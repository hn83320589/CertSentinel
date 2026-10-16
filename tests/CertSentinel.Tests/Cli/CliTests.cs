using System.Text.Json;
using CertSentinel.Cli;
using CertSentinel.Core.Checks;
using CertSentinel.Core.Targets;
using CertSentinel.Infrastructure.Storage;
using CertSentinel.Tests.Core;
using CertSentinel.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace CertSentinel.Tests.Cli;

/// <summary>以 CliApp 執行完整指令流程(使用暫存資料庫與假的探測器),驗收 #2 #12。</summary>
public sealed class CliTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 1, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeProbe _probe = new();
    private TempDatabase _db = null!;

    public async Task InitializeAsync() => _db = await TempDatabase.CreateAsync();

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] args)
    {
        var dependencies = new CliDependencies
        {
            Configuration = new ConfigurationBuilder().Build(),
            Time = _time,
            ProbeFactory = () => _probe,
        };
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await CliApp.RunAsync([.. args, "--db", _db.Path], output, error, dependencies, CancellationToken.None);
        return (exitCode, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task add_三種輸入格式皆正規化為主機加埠號()
    {
        Assert.Equal(0, (await RunAsync("add", "example.com")).ExitCode);
        Assert.Equal(0, (await RunAsync("add", "example.com:8443", "--name", "管理介面")).ExitCode);
        var (code, output, _) = await RunAsync("add", "https://shop.example.com:9443/path?q=1");

        Assert.Equal(0, code);
        Assert.Contains("shop.example.com:9443", output, StringComparison.Ordinal);
        var list = (await RunAsync("list")).Output;
        Assert.Contains("example.com:443", list, StringComparison.Ordinal);
        Assert.Contains("example.com:8443", list, StringComparison.Ordinal);
        Assert.Contains("管理介面", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task add_重複目標_拒絕並說明原因()
    {
        await RunAsync("add", "example.com", "--name", "官網");

        var (code, _, error) = await RunAsync("add", "https://EXAMPLE.com/");

        Assert.Equal(1, code);
        Assert.Contains("example.com:443 已存在", error, StringComparison.Ordinal);
        Assert.Contains("官網", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("example.com:99999")]
    [InlineData("bad host")]
    public async Task add_格式不合法_顯示錯誤而不崩潰(string input)
    {
        var (code, _, error) = await RunAsync("add", input);

        Assert.Equal(1, code);
        Assert.StartsWith("錯誤:", error, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task remove_enable_disable_找不到目標時回傳錯誤()
    {
        foreach (var command in new[] { "remove", "enable", "disable" })
        {
            var (code, _, error) = await RunAsync(command, "missing.example");
            Assert.Equal(1, code);
            Assert.Contains("找不到目標 missing.example:443", error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task disable_enable_remove_正常流程()
    {
        await RunAsync("add", "example.com");

        Assert.Equal(0, (await RunAsync("disable", "example.com")).ExitCode);
        Assert.Contains("停用", (await RunAsync("list")).Output, StringComparison.Ordinal);
        Assert.Equal(0, (await RunAsync("enable", "https://example.com")).ExitCode);
        Assert.Equal(0, (await RunAsync("remove", "example.com:443")).ExitCode);
        Assert.Contains("尚未", (await RunAsync("list")).Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task status_表格含必要欄位_到期日為本機日期()
    {
        await AddAndCheckAsync("example.com", Now.AddDays(20));

        var (code, output, _) = await RunAsync("status");

        Assert.Equal(0, code);
        foreach (var header in new[] { "名稱", "主機:埠號", "狀態", "剩餘天數", "到期日", "最後檢查" })
        {
            Assert.Contains(header, output, StringComparison.Ordinal);
        }

        var localExpiry = TimeZoneInfo.ConvertTime(Now.AddDays(20), _time.LocalTimeZone).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains(localExpiry, output, StringComparison.Ordinal);
        Assert.Contains("Warning", output, StringComparison.Ordinal);
        Assert.Contains("20", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task status_json_為合法JSON且包含狀態欄位()
    {
        await AddAndCheckAsync("example.com", Now.AddDays(3));
        await RunAsync("add", "never-checked.example");

        var (code, output, _) = await RunAsync("status", "--json");

        Assert.Equal(0, code);
        using var json = JsonDocument.Parse(output);
        var rows = json.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        var checkedRow = rows.Single(r => r.GetProperty("host").GetString() == "example.com");
        Assert.Equal("Critical", checkedRow.GetProperty("status").GetString());
        Assert.Equal(3, checkedRow.GetProperty("daysRemaining").GetInt32());
        Assert.Equal(443, checkedRow.GetProperty("port").GetInt32());
        Assert.Equal(JsonValueKind.Null, rows.Single(r => r.GetProperty("host").GetString() == "never-checked.example").GetProperty("status").ValueKind);
    }

    [Fact]
    public async Task check_立即檢查並寫入歷史_來源為Cli()
    {
        await RunAsync("add", "example.com");
        _probe.SetExpiry("example.com", Now.AddDays(100), "ABCDEF0123456789ABCDEF");

        var (code, output, _) = await RunAsync("check", "example.com");

        Assert.Equal(0, code);
        Assert.Contains("OK", output, StringComparison.Ordinal);
        Assert.Contains("ABCDEF0123456789ABCDEF", output, StringComparison.Ordinal);
        var target = await new SqliteTargetRepository(_db.Database).FindAsync(new TargetAddress("example.com", 443));
        var record = Assert.Single(await new SqliteCheckStore(_db.Database).GetHistoryAsync(target!.Id, DateTimeOffset.MinValue));
        Assert.Equal(CheckSource.Cli, record.Source);
    }

    [Fact]
    public async Task check_無法檢查時顯示原因()
    {
        await RunAsync("add", "down.example");
        _probe.SetError("down.example");

        var (code, output, _) = await RunAsync("check", "down.example");

        Assert.Equal(0, code);
        Assert.Contains("Error", output, StringComparison.Ordinal);
        Assert.Contains("連線被拒絕", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task check_json_為合法JSON()
    {
        await AddAndCheckAsync("example.com", Now.AddDays(50));
        _probe.SetExpiry("example.com", Now.AddDays(50));

        var (_, output, _) = await RunAsync("check", "example.com", "--json");

        using var json = JsonDocument.Parse(output);
        Assert.Equal("OK", json.RootElement.GetProperty("status").GetString());
        Assert.True(json.RootElement.GetProperty("certificate").GetProperty("nameMatches").GetBoolean());
    }

    [Fact]
    public async Task check_目標不在清單中_回傳錯誤()
    {
        var (code, _, error) = await RunAsync("check", "missing.example");

        Assert.Equal(1, code);
        Assert.Contains("certsentinel add", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task history_指紋變動時標示憑證已更新_並依天數篩選()
    {
        await AddAndCheckAsync("example.com", Now.AddDays(5), "OLDTHUMBPRINT000");
        _time.Advance(TimeSpan.FromDays(1));
        _probe.SetExpiry("example.com", Now.AddDays(95), "NEWTHUMBPRINT000");
        await RunAsync("check", "example.com");
        _time.Advance(TimeSpan.FromDays(10));
        await RunAsync("check", "example.com");

        var recent = await RunAsync("history", "example.com", "--days", "5");
        var all = await RunAsync("history", "example.com", "--days", "30");

        Assert.Equal(0, all.ExitCode);
        Assert.DoesNotContain("憑證已更新", recent.Output, StringComparison.Ordinal);
        Assert.Single(all.Output.Split(Environment.NewLine), l => l.Contains("憑證已更新", StringComparison.Ordinal));
        Assert.Equal(3 + 1, all.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task history_json_含certificateRenewed欄位()
    {
        await AddAndCheckAsync("example.com", Now.AddDays(5), "OLD");
        _probe.SetExpiry("example.com", Now.AddDays(95), "NEW");
        await RunAsync("check", "example.com");

        var (_, output, _) = await RunAsync("history", "example.com", "--json");

        using var json = JsonDocument.Parse(output);
        var rows = json.RootElement.EnumerateArray().ToList();
        Assert.True(rows[0].GetProperty("certificateRenewed").GetBoolean());
        Assert.False(rows[1].GetProperty("certificateRenewed").GetBoolean());
        Assert.Equal("NEW", rows[0].GetProperty("thumbprintSha256").GetString());
    }

    [Fact]
    public async Task history_天數不合法_回傳錯誤()
    {
        await RunAsync("add", "example.com");

        var (code, _, error) = await RunAsync("history", "example.com", "--days", "0");

        Assert.Equal(1, code);
        Assert.Contains("--days", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 未知指令_顯示錯誤而不崩潰()
    {
        var (code, _, error) = await RunAsync("frobnicate");

        Assert.NotEqual(0, code);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    private async Task AddAndCheckAsync(string host, DateTimeOffset notAfter, string thumbprint = "AA")
    {
        await RunAsync("add", host);
        _probe.SetExpiry(host, notAfter, thumbprint);
        Assert.Equal(0, (await RunAsync("check", host)).ExitCode);
    }
}
