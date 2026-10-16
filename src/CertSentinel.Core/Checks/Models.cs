using CertSentinel.Core.Status;
using CertSentinel.Core.Targets;

namespace CertSentinel.Core.Checks;

public sealed record MonitoredTarget(long Id, TargetAddress Address, string Name, bool Enabled, DateTimeOffset CreatedUtc);

/// <summary>伺服器送出的末端(leaf)憑證資訊。</summary>
public sealed record CertificateInfo(
    DateTimeOffset NotBeforeUtc,
    DateTimeOffset NotAfterUtc,
    string Subject,
    string Issuer,
    string ThumbprintSha256,
    IReadOnlyList<string> SubjectAlternativeNames,
    bool NameMatches,
    bool ChainTrusted,
    string? ChainErrors);

public enum ProbeErrorKind
{
    Timeout,
    ConnectionRefused,
    DnsFailure,
    HandshakeFailed,
    NetworkError,
}

/// <summary>
/// 一次 TLS 探測的結果。<see cref="Certificate"/> 與 <see cref="ErrorKind"/> 可能同時存在:
/// 已取得憑證但握手後續失敗時,仍保留憑證資訊。
/// </summary>
public sealed record ProbeResult(CertificateInfo? Certificate, ProbeErrorKind? ErrorKind, string? ErrorMessage)
{
    public static ProbeResult Success(CertificateInfo certificate) => new(certificate, null, null);

    public static ProbeResult Failure(ProbeErrorKind kind, string message) => new(null, kind, message);
}

public enum CheckSource
{
    Service,
    Cli,
}

/// <summary>一筆檢查歷史紀錄。</summary>
public sealed record CheckRecord(
    long Id,
    long TargetId,
    DateTimeOffset CheckedUtc,
    CheckSource Source,
    CertificateStatus Status,
    int? DaysRemaining,
    CertificateInfo? Certificate,
    ProbeErrorKind? ErrorKind,
    string? ErrorMessage);

/// <summary>status 指令用:目標與其最新一筆檢查結果。</summary>
public sealed record TargetStatus(MonitoredTarget Target, CheckRecord? Latest);
