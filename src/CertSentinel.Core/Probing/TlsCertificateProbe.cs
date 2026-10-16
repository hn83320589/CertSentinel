using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using CertSentinel.Core.Certificates;
using CertSentinel.Core.Checks;
using CertSentinel.Core.Targets;

namespace CertSentinel.Core.Probing;

/// <summary>
/// 以 <see cref="SslStream"/> 取得伺服器憑證。驗證 callback 一律接受,
/// 讓過期、自簽、名稱不符的憑證仍能完成握手並被記錄;信任度與名稱比對另外計算後保存。
/// </summary>
public sealed class TlsCertificateProbe(IEndpointResolver resolver, TlsProbeOptions options) : ICertificateProbe
{
    public async Task<ProbeResult> ProbeAsync(TargetAddress target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        using var timeout = new CancellationTokenSource(options.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        CertificateInfo? captured = null;

        try
        {
            IPAddress[] addresses;
            try
            {
                addresses = await resolver.ResolveAsync(target.Host, linked.Token);
            }
            catch (SocketException ex)
            {
                return ProbeResult.Failure(ProbeErrorKind.DnsFailure, $"無法解析主機名稱 {target.Host}:{ex.Message}");
            }

            if (addresses.Length == 0)
            {
                return ProbeResult.Failure(ProbeErrorKind.DnsFailure, $"主機名稱 {target.Host} 沒有任何位址。");
            }

            using var client = await ConnectAsync(addresses, target.Port, linked.Token);
            await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = target.Host, // 送出 SNI
                    CertificateChainPolicy = CreateChainPolicy(),
#pragma warning disable CA5359 // 刻意接受所有憑證:本工具的目的是「記錄」憑證(含過期/自簽/名稱不符),連線後不傳送任何資料。
                    RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                    {
                        if (certificate is not null)
                        {
                            captured = Capture(certificate, chain, errors, target.Host);
                        }

                        return true;
                    },
#pragma warning restore CA5359
                },
                linked.Token);

            return captured is null
                ? ProbeResult.Failure(ProbeErrorKind.HandshakeFailed, "握手完成但伺服器未提供憑證。")
                : ProbeResult.Success(captured);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Fail(ProbeErrorKind.Timeout, $"連線或握手超過 {options.Timeout.TotalSeconds:0.#} 秒未完成。");
        }
        catch (SocketException ex)
        {
            return Fail(ClassifySocketError(ex.SocketErrorCode), DescribeSocketError(ex));
        }
        catch (AuthenticationException ex)
        {
            return Fail(ProbeErrorKind.HandshakeFailed, $"TLS 握手失敗:{InnermostMessage(ex)}");
        }
        catch (IOException ex)
        {
            return Fail(ProbeErrorKind.HandshakeFailed, $"TLS 握手過程中連線中斷:{InnermostMessage(ex)}");
        }

        // 只要已取得憑證,即使握手後續失敗也保留憑證資訊。
        ProbeResult Fail(ProbeErrorKind kind, string message) => new(captured, kind, message);
    }

    private static async Task<TcpClient> ConnectAsync(IPAddress[] addresses, int port, CancellationToken cancellationToken)
    {
        SocketException? last = null;
        foreach (var address in addresses)
        {
            var client = new TcpClient(address.AddressFamily);
            try
            {
                await client.ConnectAsync(address, port, cancellationToken);
                return client;
            }
            catch (SocketException ex)
            {
                client.Dispose();
                last = ex;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        throw last!;
    }

    private X509ChainPolicy CreateChainPolicy()
    {
        var policy = new X509ChainPolicy
        {
            // 不查 OCSP/CRL、不從 AIA 下載中繼憑證:避免呼叫目標以外的外部網路服務。
            RevocationMode = X509RevocationMode.NoCheck,
            DisableCertificateDownloads = true,
        };

        if (options.CustomTrustRoots is { Count: > 0 } roots)
        {
            policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            policy.CustomTrustStore.AddRange(roots);
        }

        return policy;
    }

    private static CertificateInfo Capture(X509Certificate certificate, X509Chain? chain, SslPolicyErrors errors, string host)
    {
        using var leaf = new X509Certificate2(certificate);
        var names = CertificateNames.Read(leaf);
        var trusted = !errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors);

        return new CertificateInfo(
            new DateTimeOffset(leaf.NotBefore.ToUniversalTime()),
            new DateTimeOffset(leaf.NotAfter.ToUniversalTime()),
            leaf.Subject,
            leaf.Issuer,
            leaf.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256),
            names.DisplayNames,
            names.Covers(host),
            trusted,
            trusted ? null : DescribeChainErrors(chain));
    }

    private static string DescribeChainErrors(X509Chain? chain)
    {
        var statuses = chain?.ChainStatus
            .Where(s => s.Status != X509ChainStatusFlags.NoError)
            .Select(s => $"{s.Status}: {s.StatusInformation.Trim()}")
            .Distinct()
            .ToList();

        return statuses is { Count: > 0 } ? string.Join("; ", statuses) : "憑證鏈不受信任。";
    }

    private static ProbeErrorKind ClassifySocketError(SocketError error) => error switch
    {
        SocketError.ConnectionRefused => ProbeErrorKind.ConnectionRefused,
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => ProbeErrorKind.DnsFailure,
        SocketError.TimedOut => ProbeErrorKind.Timeout,
        _ => ProbeErrorKind.NetworkError,
    };

    private static string DescribeSocketError(SocketException ex) => ex.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => "連線被拒絕(該埠號沒有服務在聽)。",
        SocketError.TimedOut => "TCP 連線逾時。",
        _ => $"網路錯誤({ex.SocketErrorCode}):{ex.Message}",
    };

    private static string InnermostMessage(Exception ex)
    {
        while (ex.InnerException is not null)
        {
            ex = ex.InnerException;
        }

        return ex.Message;
    }
}
