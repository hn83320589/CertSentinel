using System.Net;
using System.Security.Cryptography.X509Certificates;
using CertSentinel.Core.Checks;
using CertSentinel.Core.Targets;

namespace CertSentinel.Core.Probing;

public interface ICertificateProbe
{
    /// <summary>
    /// 對目標建立 TLS 連線並取得 leaf 憑證。連線失敗、逾時、握手失敗以 <see cref="ProbeResult.ErrorKind"/> 表示,不丟例外;
    /// 只有 <paramref name="cancellationToken"/> 被取消時才丟出 <see cref="OperationCanceledException"/>。
    /// </summary>
    Task<ProbeResult> ProbeAsync(TargetAddress target, CancellationToken cancellationToken);
}

public interface IEndpointResolver
{
    ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class DnsEndpointResolver : IEndpointResolver
{
    public async ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        IPAddress.TryParse(host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(host, cancellationToken);
}

public sealed class TlsProbeOptions
{
    /// <summary>每個目標從 DNS 解析到握手完成的總逾時。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 為 null 時以作業系統信任庫判斷鏈信任度;測試可指定自訂信任根。
    /// </summary>
    public X509Certificate2Collection? CustomTrustRoots { get; init; }
}
