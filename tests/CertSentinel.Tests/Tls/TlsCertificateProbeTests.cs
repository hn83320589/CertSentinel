using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CertSentinel.Core.Checks;
using CertSentinel.Core.Probing;
using CertSentinel.Core.Targets;

namespace CertSentinel.Tests.Tls;

/// <summary>以 loopback TLS 伺服器驗證探測行為(驗收 #3 #4 #7)。</summary>
public sealed class TlsCertificateProbeTests : IDisposable
{
    private readonly X509Certificate2 _ca = TestCertificates.CreateRootCa();

    public void Dispose() => _ca.Dispose();

    /// <summary>把所有主機名稱解析到 127.0.0.1,SNI 仍送出原本的主機名稱。</summary>
    internal sealed class LoopbackResolver : IEndpointResolver
    {
        public ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new[] { IPAddress.Loopback });
    }

    private sealed class FailingResolver : IEndpointResolver
    {
        public ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            throw new SocketException((int)SocketError.HostNotFound);
    }

    private TlsCertificateProbe CreateProbe(TimeSpan? timeout = null, bool trustTestCa = true, IEndpointResolver? resolver = null) =>
        new(resolver ?? new LoopbackResolver(), new TlsProbeOptions
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
            CustomTrustRoots = trustTestCa ? [_ca] : null,
        });

    private static string Sha256(X509Certificate2 cert) => cert.GetCertHashString(HashAlgorithmName.SHA256);

    [Fact]
    public async Task 取得到期時間_指紋_簽發者_且鏈受信任()
    {
        using var cert = TestCertificates.Issue(_ca, "a.test", "a.test");
        await using var server = LoopbackTlsServer.StartTls(cert);

        var result = await CreateProbe().ProbeAsync(new TargetAddress("a.test", server.Port), CancellationToken.None);

        Assert.Null(result.ErrorKind);
        var info = Assert.IsType<CertificateInfo>(result.Certificate);
        Assert.Equal(new DateTimeOffset(cert.NotAfter.ToUniversalTime()), info.NotAfterUtc);
        Assert.Equal(TimeSpan.Zero, info.NotAfterUtc.Offset);
        Assert.Equal(Sha256(cert), info.ThumbprintSha256);
        Assert.Equal(_ca.Subject, info.Issuer);
        Assert.Equal("CN=a.test", info.Subject);
        Assert.Contains("a.test", info.SubjectAlternativeNames);
        Assert.True(info.NameMatches);
        Assert.True(info.ChainTrusted, info.ChainErrors);
    }

    [Fact]
    public async Task 伺服器依SNI回傳不同憑證_取得的是對應主機的那張()
    {
        using var certA = TestCertificates.Issue(_ca, "a.test", "a.test");
        using var certB = TestCertificates.Issue(_ca, "b.test", "b.test");
        await using var server = LoopbackTlsServer.StartTlsWithSni(new Dictionary<string, X509Certificate2>
        {
            ["a.test"] = certA,
            ["b.test"] = certB,
        });
        var probe = CreateProbe();

        var a = await probe.ProbeAsync(new TargetAddress("a.test", server.Port), CancellationToken.None);
        var b = await probe.ProbeAsync(new TargetAddress("b.test", server.Port), CancellationToken.None);

        Assert.Equal(Sha256(certA), a.Certificate!.ThumbprintSha256);
        Assert.Equal(Sha256(certB), b.Certificate!.ThumbprintSha256);
        Assert.True(a.Certificate.NameMatches);
        Assert.True(b.Certificate.NameMatches);
    }

    [Fact]
    public async Task 萬用字元憑證_涵蓋單一層級子網域()
    {
        using var cert = TestCertificates.Issue(_ca, "*.example.test", "*.example.test");
        await using var server = LoopbackTlsServer.StartTls(cert);
        var probe = CreateProbe();

        var www = await probe.ProbeAsync(new TargetAddress("www.example.test", server.Port), CancellationToken.None);
        var deep = await probe.ProbeAsync(new TargetAddress("a.b.example.test", server.Port), CancellationToken.None);

        Assert.True(www.Certificate!.NameMatches);
        Assert.False(deep.Certificate!.NameMatches);
    }

    [Fact]
    public async Task 已過期憑證_仍記錄憑證資訊_並附上原因()
    {
        using var cert = TestCertificates.Issue(_ca, "old.test", ["old.test"],
            DateTimeOffset.UtcNow.AddDays(-100), DateTimeOffset.UtcNow.AddDays(-10));
        await using var server = LoopbackTlsServer.StartTls(cert);

        var result = await CreateProbe().ProbeAsync(new TargetAddress("old.test", server.Port), CancellationToken.None);

        var info = Assert.IsType<CertificateInfo>(result.Certificate);
        Assert.True(info.NotAfterUtc < DateTimeOffset.UtcNow);
        Assert.Equal(Sha256(cert), info.ThumbprintSha256);
        Assert.False(info.ChainTrusted);
        Assert.Contains("NotTimeValid", info.ChainErrors, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 自簽憑證_系統信任庫不受信任_仍記錄憑證資訊與原因()
    {
        using var cert = TestCertificates.CreateSelfSigned("self.test", "self.test");
        await using var server = LoopbackTlsServer.StartTls(cert);

        var result = await CreateProbe(trustTestCa: false).ProbeAsync(new TargetAddress("self.test", server.Port), CancellationToken.None);

        var info = Assert.IsType<CertificateInfo>(result.Certificate);
        Assert.Null(result.ErrorKind);
        Assert.Equal(Sha256(cert), info.ThumbprintSha256);
        Assert.True(info.NameMatches);
        Assert.False(info.ChainTrusted);
        Assert.False(string.IsNullOrWhiteSpace(info.ChainErrors));
    }

    [Fact]
    public async Task 名稱不符_仍記錄憑證資訊_NameMatches為false()
    {
        using var cert = TestCertificates.Issue(_ca, "other.test", "other.test");
        await using var server = LoopbackTlsServer.StartTls(cert);

        var result = await CreateProbe().ProbeAsync(new TargetAddress("a.test", server.Port), CancellationToken.None);

        var info = Assert.IsType<CertificateInfo>(result.Certificate);
        Assert.False(info.NameMatches);
        Assert.Equal(["other.test"], info.SubjectAlternativeNames);
        Assert.True(info.ChainTrusted, info.ChainErrors);
    }

    [Fact]
    public async Task 憑證沒有SAN時_退回比對CN()
    {
        using var cert = TestCertificates.Issue(_ca, "legacy.test");
        await using var server = LoopbackTlsServer.StartTls(cert);

        var result = await CreateProbe().ProbeAsync(new TargetAddress("legacy.test", server.Port), CancellationToken.None);

        Assert.True(result.Certificate!.NameMatches);
    }

    [Fact]
    public async Task 伺服器不回應_記為逾時()
    {
        await using var server = LoopbackTlsServer.Start(LoopbackTlsServer.Behavior.Silent);
        var stopwatch = Stopwatch.StartNew();

        var result = await CreateProbe(TimeSpan.FromSeconds(1)).ProbeAsync(new TargetAddress("slow.test", server.Port), CancellationToken.None);

        Assert.Equal(ProbeErrorKind.Timeout, result.ErrorKind);
        Assert.Null(result.Certificate);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"逾時花了 {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task 沒有程式在聽的埠號_記為拒絕連線()
    {
        var port = LoopbackTlsServer.GetClosedPort();

        var result = await CreateProbe().ProbeAsync(new TargetAddress("closed.test", port), CancellationToken.None);

        Assert.Equal(ProbeErrorKind.ConnectionRefused, result.ErrorKind);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }

    [Fact]
    public async Task 對方不是TLS_記為握手失敗()
    {
        await using var server = LoopbackTlsServer.Start(LoopbackTlsServer.Behavior.Garbage);

        var result = await CreateProbe().ProbeAsync(new TargetAddress("garbage.test", server.Port), CancellationToken.None);

        Assert.Equal(ProbeErrorKind.HandshakeFailed, result.ErrorKind);
        Assert.Null(result.Certificate);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }

    [Fact]
    public async Task DNS解析失敗_記為DnsFailure()
    {
        var result = await CreateProbe(resolver: new FailingResolver()).ProbeAsync(new TargetAddress("missing.test", 443), CancellationToken.None);

        Assert.Equal(ProbeErrorKind.DnsFailure, result.ErrorKind);
    }

    [Fact]
    public async Task 外部取消_丟出OperationCanceledException而非記為錯誤()
    {
        await using var server = LoopbackTlsServer.Start(LoopbackTlsServer.Behavior.Silent);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateProbe().ProbeAsync(new TargetAddress("slow.test", server.Port), cts.Token));
    }
}
