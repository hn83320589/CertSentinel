namespace CertSentinel.Core.Status;

public sealed record StatusEvaluation(CertificateStatus Status, int DaysRemaining);

/// <summary>
/// 剩餘時間 = 到期時間 − 目前 UTC;剩餘天數 = 剩餘時間的天數向下取整。
/// 剩餘時間 ≤ 0 → Expired;天數 ≤ 緊急門檻 → Critical;天數 ≤ 警告門檻 → Warning;其餘 OK。
/// </summary>
public static class StatusClassifier
{
    public static StatusEvaluation Classify(DateTimeOffset notAfterUtc, DateTimeOffset nowUtc, StatusThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);

        var remaining = notAfterUtc - nowUtc;
        var days = (int)Math.Floor(remaining.TotalDays);

        var status = remaining <= TimeSpan.Zero ? CertificateStatus.Expired
            : days <= thresholds.CriticalDays ? CertificateStatus.Critical
            : days <= thresholds.WarningDays ? CertificateStatus.Warning
            : CertificateStatus.OK;

        return new StatusEvaluation(status, days);
    }
}
