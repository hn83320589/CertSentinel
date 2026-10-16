using System.Collections.Concurrent;
using CertSentinel.Core.Checks;
using CertSentinel.Core.Notifications;
using CertSentinel.Core.Probing;
using CertSentinel.Core.Targets;

namespace CertSentinel.Tests.Core;

/// <summary>依主機名稱回傳預先設定的結果;可設定延遲以觀察並行數。</summary>
internal sealed class FakeProbe : ICertificateProbe
{
    private readonly ConcurrentDictionary<string, Func<ProbeResult>> _results = new(StringComparer.OrdinalIgnoreCase);
    private int _current;
    private int _max;

    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public int MaxConcurrency => _max;

    public void Set(string host, ProbeResult result) => _results[host] = () => result;

    public void Throw(string host, Exception exception) => _results[host] = () => throw exception;

    /// <summary>設定憑證在指定時間到期的成功結果。</summary>
    public void SetExpiry(string host, DateTimeOffset notAfter, string thumbprint = "AA") =>
        Set(host, ProbeResult.Success(Certificate(notAfter, thumbprint)));

    public void SetError(string host, ProbeErrorKind kind = ProbeErrorKind.ConnectionRefused) =>
        Set(host, ProbeResult.Failure(kind, "連線被拒絕"));

    public static CertificateInfo Certificate(DateTimeOffset notAfter, string thumbprint = "AA", bool nameMatches = true, bool trusted = true) =>
        new(notAfter.AddDays(-90), notAfter, "CN=example.com", "CN=Test CA", thumbprint, ["example.com"], nameMatches, trusted,
            trusted ? null : "UntrustedRoot: 自簽憑證");

    public async Task<ProbeResult> ProbeAsync(TargetAddress target, CancellationToken cancellationToken)
    {
        var now = Interlocked.Increment(ref _current);
        InterlockedMax(ref _max, now);
        try
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            return _results.TryGetValue(target.Host, out var result)
                ? result()
                : throw new InvalidOperationException($"FakeProbe 沒有設定 {target.Host}");
        }
        finally
        {
            Interlocked.Decrement(ref _current);
        }
    }

    private static void InterlockedMax(ref int location, int value)
    {
        int initial;
        do
        {
            initial = location;
            if (value <= initial)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref location, value, initial) != initial);
    }
}

internal sealed class FakeEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    public bool Fail { get; set; }

    public int Attempts { get; private set; }

    public IReadOnlyList<EmailMessage> Sent => [.. _sent];

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        Attempts++;
        if (Fail)
        {
            throw new InvalidOperationException("SMTP 伺服器無回應(測試模擬)");
        }

        _sent.Enqueue(message);
        return Task.CompletedTask;
    }

    public void Clear()
    {
        _sent.Clear();
        Attempts = 0;
    }
}
