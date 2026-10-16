using System.Globalization;
using System.Text;
using CertSentinel.Core.Checks;
using CertSentinel.Core.Status;

namespace CertSentinel.Core.Notifications;

/// <summary>組出純文字通知信。名稱不符、不受信任與錯誤原因都會寫進內文。</summary>
public static class NotificationComposer
{
    public static EmailMessage Compose(
        NotificationKind kind,
        MonitoredTarget target,
        CheckRecord record,
        int consecutiveErrors,
        TimeSpan reminderInterval,
        TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(localTimeZone);

        var subject = $"[CertSentinel] {Headline(kind, record)} — {target.Address}{RemainingSuffix(record)}";

        var body = new StringBuilder();
        Line(body, "通知類型", KindLabel(kind, reminderInterval));
        Line(body, "目標名稱", target.Name);
        Line(body, "主機與埠號", target.Address.ToString());
        Line(body, "目前狀態", record.Status.ToString());
        Line(body, "檢查時間", FormatTime(record.CheckedUtc, localTimeZone));

        if (record.Certificate is { } cert)
        {
            body.AppendLine();
            Line(body, "到期時間", FormatTime(cert.NotAfterUtc, localTimeZone));
            Line(body, "剩餘天數", record.DaysRemaining?.ToString(CultureInfo.InvariantCulture) ?? "-");
            Line(body, "主體", cert.Subject);
            Line(body, "簽發者", cert.Issuer);
            Line(body, "SHA-256 指紋", cert.ThumbprintSha256);
            Line(body, "涵蓋名稱", string.Join(", ", cert.SubjectAlternativeNames));
            Line(body, "名稱比對", cert.NameMatches ? "相符" : $"名稱不符:憑證未涵蓋 {target.Address.Host}");
            Line(body, "鏈信任", cert.ChainTrusted ? "受系統信任" : $"不受信任:{cert.ChainErrors}");
        }

        if (record.ErrorKind is { } errorKind)
        {
            body.AppendLine();
            Line(body, "錯誤類型", errorKind.ToString());
            Line(body, "錯誤原因", record.ErrorMessage ?? "-");
            if (record.Status == CertificateStatus.Error)
            {
                Line(body, "連續失敗次數", consecutiveErrors.ToString(CultureInfo.InvariantCulture));
            }
        }

        body.AppendLine();
        body.AppendLine("— 此信由 CertSentinel 自動寄出。");
        return new EmailMessage(subject, body.ToString());
    }

    private static string Headline(NotificationKind kind, CheckRecord record) => kind switch
    {
        NotificationKind.Recovered => "已恢復 OK",
        NotificationKind.Unreachable => "無法檢查",
        NotificationKind.Reminder => $"持續提醒 {StatusLabel(record.Status)}",
        _ => StatusLabel(record.Status),
    };

    private static string StatusLabel(CertificateStatus status) =>
        status == CertificateStatus.Error ? "無法檢查" : status.ToString();

    private static string KindLabel(NotificationKind kind, TimeSpan reminderInterval) => kind switch
    {
        NotificationKind.Deteriorated => "狀態惡化",
        NotificationKind.Unreachable => "連續無法檢查",
        NotificationKind.StatusAfterError => "恢復檢查(狀態非 OK)",
        NotificationKind.Recovered => "恢復 OK",
        NotificationKind.Reminder => string.Create(CultureInfo.InvariantCulture, $"持續狀態提醒(每 {reminderInterval.TotalHours:0.##} 小時)"),
        _ => kind.ToString(),
    };

    private static string RemainingSuffix(CheckRecord record) =>
        record.Status is CertificateStatus.Warning or CertificateStatus.Critical && record.DaysRemaining is { } days
            ? $" 剩餘 {days} 天"
            : string.Empty;

    private static string FormatTime(DateTimeOffset utc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(utc, zone);
        return string.Create(CultureInfo.InvariantCulture, $"{utc:yyyy-MM-dd HH:mm} UTC(本機 {local:yyyy-MM-dd HH:mm})");
    }

    private static void Line(StringBuilder body, string label, string value) =>
        body.Append(label).Append(':').AppendLine(value);
}
