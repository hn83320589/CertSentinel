using CertSentinel.Core.Checks;
using CertSentinel.Core.Notifications;
using CertSentinel.Core.Status;
using Microsoft.Extensions.Configuration;

namespace CertSentinel.Infrastructure.Configuration;

/// <summary>appsettings.json 的 <c>CertSentinel</c> 區段,服務與 CLI 共用。</summary>
public sealed class CertSentinelSettings
{
    public const string SectionName = "CertSentinel";

    public string DatabasePath { get; set; } = @"%ProgramData%\CertSentinel\certsentinel.db";

    public int CheckIntervalMinutes { get; set; } = 360;

    public int MaxConcurrency { get; set; } = 5;

    public int WarningDays { get; set; } = 30;

    public int CriticalDays { get; set; } = 7;

    public int RetentionDays { get; set; } = 90;

    public int ErrorNotifyThreshold { get; set; } = 3;

    public int ReminderIntervalHours { get; set; } = 24;

    public SmtpSettings Smtp { get; set; } = new();

    public static CertSentinelSettings Bind(IConfiguration configuration)
    {
        var settings = new CertSentinelSettings();
        configuration.GetSection(SectionName).Bind(settings);
        return settings;
    }

    public CheckOptions ToCheckOptions() => new()
    {
        Thresholds = new StatusThresholds(WarningDays, CriticalDays),
        Rules = new NotificationRules(ErrorNotifyThreshold, TimeSpan.FromHours(ReminderIntervalHours)),
        MaxConcurrency = MaxConcurrency,
    };

    /// <summary>回傳所有設定錯誤(空集合代表合法)。</summary>
    public IReadOnlyList<string> Validate(bool requireSmtp = true)
    {
        var errors = new List<string>();
        void Positive(int value, string name)
        {
            if (value <= 0)
            {
                errors.Add($"{SectionName}:{name} 必須大於 0(目前為 {value})。");
            }
        }

        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            errors.Add($"{SectionName}:DatabasePath 不可為空白。");
        }

        Positive(CheckIntervalMinutes, nameof(CheckIntervalMinutes));
        Positive(MaxConcurrency, nameof(MaxConcurrency));
        Positive(RetentionDays, nameof(RetentionDays));
        Positive(ErrorNotifyThreshold, nameof(ErrorNotifyThreshold));
        Positive(ReminderIntervalHours, nameof(ReminderIntervalHours));

        if (CriticalDays < 0 || CriticalDays >= WarningDays)
        {
            errors.Add($"{SectionName}:CriticalDays 必須介於 0 與 WarningDays 之間(目前 CriticalDays={CriticalDays}、WarningDays={WarningDays})。");
        }

        if (requireSmtp)
        {
            errors.AddRange(Smtp.Validate());
        }

        return errors;
    }
}

public enum SmtpSecurity
{
    /// <summary>不加密(僅限內網中繼)。</summary>
    None,

    /// <summary>以明文連線後升級為 TLS(常見於 587 埠)。</summary>
    StartTls,

    /// <summary>連線即使用 TLS(常見於 465 埠)。</summary>
    SslOnConnect,
}

public sealed class SmtpSettings
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    /// <summary>空白代表不驗證。</summary>
    public string? UserName { get; set; }

    /// <summary>建議以環境變數 <c>CertSentinel__Smtp__Password</c> 提供。</summary>
    public string? Password { get; set; }

    public string From { get; set; } = string.Empty;

#pragma warning disable CA1819, CA2227 // 設定繫結需要可寫的陣列屬性。
    public string[] To { get; set; } = [];
#pragma warning restore CA1819, CA2227

    public int TimeoutSeconds { get; set; } = 30;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Host))
        {
            errors.Add("CertSentinel:Smtp:Host 不可為空白。");
        }

        if (Port is < 1 or > 65535)
        {
            errors.Add($"CertSentinel:Smtp:Port 必須介於 1–65535(目前為 {Port})。");
        }

        if (string.IsNullOrWhiteSpace(From))
        {
            errors.Add("CertSentinel:Smtp:From 不可為空白。");
        }

        if (To.Length == 0 || To.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("CertSentinel:Smtp:To 至少需要一個收件人,且不可有空白項目。");
        }

        if (!string.IsNullOrEmpty(UserName) && string.IsNullOrEmpty(Password))
        {
            errors.Add("CertSentinel:Smtp:Password 在設定 UserName 時不可為空白。");
        }

        return errors;
    }
}
