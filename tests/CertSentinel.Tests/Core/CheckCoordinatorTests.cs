using CertSentinel.Core.Checks;
using CertSentinel.Core.Notifications;
using CertSentinel.Core.Status;
using CertSentinel.Core.Targets;
using CertSentinel.Infrastructure.Storage;
using CertSentinel.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CertSentinel.Tests.Core;

public sealed class CheckCoordinatorTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Start);
    private readonly FakeProbe _probe = new();
    private readonly FakeEmailSender _email = new();
    private TempDatabase _db = null!;

    public async Task InitializeAsync() => _db = await TempDatabase.CreateAsync();

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private CheckCoordinator CreateCoordinator(SqliteDatabase? database = null, int maxConcurrency = 4)
    {
        var db = database ?? _db.Database;
        return new CheckCoordinator(
            new SqliteTargetRepository(db),
            new SqliteCheckStore(db),
            _probe,
            _email,
            _time,
            new CheckOptions
            {
                Thresholds = new StatusThresholds(30, 7),
                Rules = NotificationRules.Default,
                MaxConcurrency = maxConcurrency,
            },
            NullLogger<CheckCoordinator>.Instance);
    }

    private async Task<MonitoredTarget> AddTargetAsync(string host)
    {
        var result = await new SqliteTargetRepository(_db.Database).AddAsync(new TargetAddress(host, 443), null, _time.GetUtcNow());
        return result.Target;
    }

    [Fact]
    public async Task 一輪檢查_每個啟用目標寫入一筆歷史_停用目標略過()
    {
        var a = await AddTargetAsync("a.example");
        var b = await AddTargetAsync("b.example");
        await new SqliteTargetRepository(_db.Database).SetEnabledAsync(b.Address, false);
        _probe.SetExpiry("a.example", Start.AddDays(100));

        var summary = await CreateCoordinator().RunRoundAsync(CancellationToken.None);

        Assert.Equal(1, summary.Checked);
        var store = new SqliteCheckStore(_db.Database);
        var record = Assert.Single(await store.GetHistoryAsync(a.Id, DateTimeOffset.MinValue));
        Assert.Equal(CertificateStatus.OK, record.Status);
        Assert.Equal(100, record.DaysRemaining);
        Assert.Equal(CheckSource.Service, record.Source);
        Assert.Empty(await store.GetHistoryAsync(b.Id, DateTimeOffset.MinValue));
    }

    [Fact]
    public async Task 單一目標發生非預期例外_不影響其他目標()
    {
        var good = await AddTargetAsync("good.example");
        var bad = await AddTargetAsync("bad.example");
        _probe.SetExpiry("good.example", Start.AddDays(100));
        _probe.Throw("bad.example", new InvalidOperationException("boom"));

        var summary = await CreateCoordinator().RunRoundAsync(CancellationToken.None);

        Assert.Equal(2, summary.Checked);
        var store = new SqliteCheckStore(_db.Database);
        Assert.Equal(CertificateStatus.OK, Assert.Single(await store.GetHistoryAsync(good.Id, DateTimeOffset.MinValue)).Status);
        var failed = Assert.Single(await store.GetHistoryAsync(bad.Id, DateTimeOffset.MinValue));
        Assert.Equal(CertificateStatus.Error, failed.Status);
        Assert.Contains("boom", failed.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 遵守並行上限()
    {
        for (var i = 0; i < 8; i++)
        {
            await AddTargetAsync($"t{i}.example");
            _probe.SetExpiry($"t{i}.example", Start.AddDays(100));
        }

        _probe.Delay = TimeSpan.FromMilliseconds(50);

        await CreateCoordinator(maxConcurrency: 2).RunRoundAsync(CancellationToken.None);

        Assert.Equal(2, _probe.MaxConcurrency);
    }

    [Fact]
    public async Task 狀態惡化寄一封_內文含到期日與失敗原因_狀態不變不重寄()
    {
        await AddTargetAsync("shop.example");
        _probe.Set("shop.example", ProbeResult.Success(FakeProbe.Certificate(Start.AddDays(5), nameMatches: false, trusted: false)));
        var coordinator = CreateCoordinator();

        await coordinator.RunRoundAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(6));
        await coordinator.RunRoundAsync(CancellationToken.None);

        var mail = Assert.Single(_email.Sent);
        Assert.Contains("Critical", mail.Subject, StringComparison.Ordinal);
        Assert.Contains("shop.example:443", mail.Subject, StringComparison.Ordinal);
        Assert.Contains("名稱不符", mail.Body, StringComparison.Ordinal);
        Assert.Contains("UntrustedRoot", mail.Body, StringComparison.Ordinal);
        Assert.Contains("2026-10-21", mail.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 模擬服務重啟_沿用同一資料庫_不重複寄出已通知過的狀態()
    {
        await AddTargetAsync("shop.example");
        _probe.SetExpiry("shop.example", Start.AddDays(5));

        await CreateCoordinator().RunRoundAsync(CancellationToken.None);
        Assert.Single(_email.Sent);

        // 重建所有物件(含資料庫物件),只沿用同一個資料庫檔。
        _time.Advance(TimeSpan.FromHours(1));
        await CreateCoordinator(_db.Reopen()).RunRoundAsync(CancellationToken.None);

        Assert.Single(_email.Sent);
    }

    [Fact]
    public async Task 模擬服務重啟_連續錯誤次數延續_第三次才寄()
    {
        await AddTargetAsync("down.example");
        _probe.SetError("down.example");

        await CreateCoordinator().RunRoundAsync(CancellationToken.None);
        await CreateCoordinator(_db.Reopen()).RunRoundAsync(CancellationToken.None);
        Assert.Empty(_email.Sent);

        await CreateCoordinator(_db.Reopen()).RunRoundAsync(CancellationToken.None);
        var mail = Assert.Single(_email.Sent);
        Assert.Contains("連線被拒絕", mail.Body, StringComparison.Ordinal);

        await CreateCoordinator(_db.Reopen()).RunRoundAsync(CancellationToken.None);
        Assert.Single(_email.Sent);
    }

    [Fact]
    public async Task 寄信失敗_下一輪重試_成功後才視為已通知()
    {
        var target = await AddTargetAsync("shop.example");
        _probe.SetExpiry("shop.example", Start.AddDays(5));
        _email.Fail = true;
        var coordinator = CreateCoordinator();

        var first = await coordinator.RunRoundAsync(CancellationToken.None);
        Assert.Equal(1, first.NotificationsFailed);
        Assert.Null((await new SqliteCheckStore(_db.Database).GetNotificationStateAsync(target.Id)).NotifiedStatus);

        _email.Fail = false;
        _time.Advance(TimeSpan.FromHours(1));
        var second = await coordinator.RunRoundAsync(CancellationToken.None);
        Assert.Equal(1, second.NotificationsSent);
        Assert.Single(_email.Sent);

        _time.Advance(TimeSpan.FromHours(1));
        await coordinator.RunRoundAsync(CancellationToken.None);
        Assert.Single(_email.Sent);
        Assert.Equal(CertificateStatus.Critical, (await new SqliteCheckStore(_db.Database).GetNotificationStateAsync(target.Id)).NotifiedStatus);
    }

    [Fact]
    public async Task 換新憑證回到OK_寄恢復通知()
    {
        await AddTargetAsync("shop.example");
        _probe.SetExpiry("shop.example", Start.AddDays(5), thumbprint: "OLD");
        var coordinator = CreateCoordinator();
        await coordinator.RunRoundAsync(CancellationToken.None);

        _probe.SetExpiry("shop.example", Start.AddDays(90), thumbprint: "NEW");
        _time.Advance(TimeSpan.FromHours(1));
        await coordinator.RunRoundAsync(CancellationToken.None);

        Assert.Equal(2, _email.Sent.Count);
        Assert.Contains("恢復", _email.Sent[1].Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 持續Critical_24小時後提醒()
    {
        await AddTargetAsync("shop.example");
        _probe.SetExpiry("shop.example", Start.AddDays(6));
        var coordinator = CreateCoordinator();

        await coordinator.RunRoundAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(12));
        await coordinator.RunRoundAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(12));
        await coordinator.RunRoundAsync(CancellationToken.None);

        Assert.Equal(2, _email.Sent.Count);
        Assert.Contains("提醒", _email.Sent[1].Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 手動檢查_寫入歷史但不寄信也不改變通知進度()
    {
        var target = await AddTargetAsync("shop.example");
        _probe.SetExpiry("shop.example", Start.AddDays(3));
        var store = new SqliteCheckStore(_db.Database);

        var record = await CreateCoordinator().CheckOnceAsync(target, CancellationToken.None);

        Assert.Equal(CertificateStatus.Critical, record.Status);
        Assert.Equal(CheckSource.Cli, Assert.Single(await store.GetHistoryAsync(target.Id, DateTimeOffset.MinValue)).Source);
        Assert.Empty(_email.Sent);
        Assert.Equal(NotificationState.Initial, await store.GetNotificationStateAsync(target.Id));
    }

    [Fact]
    public async Task 取得憑證但握手後續失敗_仍依憑證分級並保留錯誤原因()
    {
        var target = await AddTargetAsync("odd.example");
        _probe.Set("odd.example", new ProbeResult(FakeProbe.Certificate(Start.AddDays(100)), ProbeErrorKind.HandshakeFailed, "伺服器要求用戶端憑證"));

        var record = await CreateCoordinator().CheckOnceAsync(target, CancellationToken.None);

        Assert.Equal(CertificateStatus.OK, record.Status);
        Assert.NotNull(record.Certificate);
        Assert.Equal("伺服器要求用戶端憑證", record.ErrorMessage);
    }

    [Fact]
    public async Task 停止要求_進行中的檢查取消_不寫入殘缺紀錄()
    {
        var target = await AddTargetAsync("slow.example");
        _probe.SetExpiry("slow.example", Start.AddDays(100));
        _probe.Delay = TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateCoordinator().RunRoundAsync(cts.Token));

        Assert.Empty(await new SqliteCheckStore(_db.Database).GetHistoryAsync(target.Id, DateTimeOffset.MinValue));
    }

    [Fact]
    public async Task 保留清理_刪除超過保留天數的歷史()
    {
        var target = await AddTargetAsync("a.example");
        _probe.SetExpiry("a.example", Start.AddDays(100));
        var coordinator = CreateCoordinator();
        await coordinator.RunRoundAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromDays(40));
        await coordinator.RunRoundAsync(CancellationToken.None);

        var deleted = await coordinator.PurgeHistoryAsync(TimeSpan.FromDays(30), CancellationToken.None);

        Assert.Equal(1, deleted);
        Assert.Single(await new SqliteCheckStore(_db.Database).GetHistoryAsync(target.Id, DateTimeOffset.MinValue));
    }
}
