using CertSentinel.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;

namespace CertSentinel.Tests.Infrastructure;

public class SettingsTests
{
    private static CertSentinelSettings Valid() => new()
    {
        DatabasePath = @"C:\data\certsentinel.db",
        Smtp = new SmtpSettings { Host = "smtp.example.test", From = "a@example.test", To = ["b@example.test"] },
    };

    [Fact]
    public void 預設值符合題目需求()
    {
        var settings = new CertSentinelSettings();

        Assert.Equal(30, settings.WarningDays);
        Assert.Equal(7, settings.CriticalDays);
        Assert.Equal(3, settings.ErrorNotifyThreshold);
        Assert.Equal(24, settings.ReminderIntervalHours);
        Assert.Contains("CertSentinel", settings.DatabasePath, StringComparison.Ordinal);
    }

    [Fact]
    public void 合法設定_沒有錯誤() => Assert.Empty(Valid().Validate());

    [Theory]
    [InlineData(nameof(CertSentinelSettings.CheckIntervalMinutes))]
    [InlineData(nameof(CertSentinelSettings.MaxConcurrency))]
    [InlineData(nameof(CertSentinelSettings.RetentionDays))]
    [InlineData(nameof(CertSentinelSettings.ErrorNotifyThreshold))]
    public void 數值必須為正(string property)
    {
        var settings = Valid();
        typeof(CertSentinelSettings).GetProperty(property)!.SetValue(settings, 0);

        Assert.Contains(settings.Validate(), e => e.Contains(property, StringComparison.Ordinal));
    }

    [Fact]
    public void 緊急門檻必須小於警告門檻()
    {
        var settings = Valid();
        settings.CriticalDays = 30;

        Assert.Contains(settings.Validate(), e => e.Contains("CriticalDays", StringComparison.Ordinal));
    }

    [Fact]
    public void 收件人與寄件人必填()
    {
        var settings = Valid();
        settings.Smtp.To = [];
        settings.Smtp.From = "";

        var errors = settings.Validate();
        Assert.Contains(errors, e => e.Contains("Smtp:To", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Smtp:From", StringComparison.Ordinal));
    }

    [Fact]
    public void 可由設定檔與環境變數格式繫結()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CertSentinel:CheckIntervalMinutes"] = "15",
                ["CertSentinel:Smtp:Security"] = "SslOnConnect",
                ["CertSentinel:Smtp:To:0"] = "x@example.test",
            })
            .Build();

        var settings = CertSentinelSettings.Bind(configuration);

        Assert.Equal(15, settings.CheckIntervalMinutes);
        Assert.Equal(SmtpSecurity.SslOnConnect, settings.Smtp.Security);
        Assert.Equal(["x@example.test"], settings.Smtp.To);
    }
}
