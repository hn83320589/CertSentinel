using CertSentinel.Core.Checks;
using CertSentinel.Core.Notifications;
using CertSentinel.Core.Status;
using CertSentinel.Core.Targets;
using CertSentinel.Infrastructure.Storage;

namespace CertSentinel.Tests.Infrastructure;

public sealed class SqliteStoreTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TargetAddress Example = new("example.com", 443);

    private TempDatabase _db = null!;
    private SqliteTargetRepository _targets = null!;
    private SqliteCheckStore _checks = null!;

    public async Task InitializeAsync()
    {
        _db = await TempDatabase.CreateAsync();
        _targets = new SqliteTargetRepository(_db.Database);
        _checks = new SqliteCheckStore(_db.Database);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    internal static CheckRecord Ok(long targetId, DateTimeOffset at, string thumbprint = "AA", CheckSource source = CheckSource.Service) =>
        new(0, targetId, at, source, CertificateStatus.OK, 60,
            new CertificateInfo(at.AddDays(-30), at.AddDays(60), "CN=example.com", "CN=Test CA", thumbprint,
                ["example.com", "www.example.com"], NameMatches: true, ChainTrusted: false, ChainErrors: "UntrustedRoot"),
            null, null);

    internal static CheckRecord Error(long targetId, DateTimeOffset at) =>
        new(0, targetId, at, CheckSource.Service, CertificateStatus.Error, null, null, ProbeErrorKind.ConnectionRefused, "連線被拒絕");

    [Fact]
    public async Task 新增目標_預設名稱為主機加埠號_且為啟用()
    {
        var result = await _targets.AddAsync(Example, null, T0);

        Assert.Equal(AddTargetOutcome.Added, result.Outcome);
        Assert.Equal("example.com:443", result.Target.Name);
        Assert.True(result.Target.Enabled);
        Assert.Single(await _targets.ListAsync());
    }

    [Fact]
    public async Task 同一主機與埠號重複新增_回傳Duplicate與既有目標()
    {
        await _targets.AddAsync(Example, "官網", T0);

        var result = await _targets.AddAsync(Example, "另一個名稱", T0);

        Assert.Equal(AddTargetOutcome.Duplicate, result.Outcome);
        Assert.Equal("官網", result.Target.Name);
        Assert.Single(await _targets.ListAsync());
    }

    [Fact]
    public async Task 同一主機不同埠號_視為不同目標()
    {
        await _targets.AddAsync(Example, null, T0);
        var result = await _targets.AddAsync(Example with { Port = 8443 }, null, T0);

        Assert.Equal(AddTargetOutcome.Added, result.Outcome);
        Assert.Equal(2, (await _targets.ListAsync()).Count);
    }

    [Fact]
    public async Task 啟用停用與找不到目標()
    {
        await _targets.AddAsync(Example, null, T0);

        Assert.True(await _targets.SetEnabledAsync(Example, false));
        Assert.False((await _targets.FindAsync(Example))!.Enabled);
        Assert.False(await _targets.SetEnabledAsync(new TargetAddress("nope.example", 443), true));
    }

    [Fact]
    public async Task 刪除目標_一併刪除歷史與通知進度()
    {
        var target = (await _targets.AddAsync(Example, null, T0)).Target;
        await _checks.AddResultAsync(Ok(target.Id, T0), new NotificationState(0, CertificateStatus.OK, T0));

        Assert.True(await _targets.RemoveAsync(Example));
        Assert.False(await _targets.RemoveAsync(Example));
        Assert.Empty(await _checks.GetHistoryAsync(target.Id, DateTimeOffset.MinValue));
        Assert.Equal(NotificationState.Initial, await _checks.GetNotificationStateAsync(target.Id));
    }

    [Fact]
    public async Task 檢查結果與通知進度_完整寫入與讀回()
    {
        var target = (await _targets.AddAsync(Example, null, T0)).Target;
        var state = new NotificationState(2, CertificateStatus.Critical, T0.AddHours(1));

        Assert.True(await _checks.AddResultAsync(Ok(target.Id, T0), state));

        var history = await _checks.GetHistoryAsync(target.Id, T0.AddDays(-1));
        var record = Assert.Single(history);
        Assert.Equal(T0, record.CheckedUtc);
        Assert.Equal(CertificateStatus.OK, record.Status);
        Assert.Equal(60, record.DaysRemaining);
        Assert.Equal(T0.AddDays(60), record.Certificate!.NotAfterUtc);
        Assert.Equal(["example.com", "www.example.com"], record.Certificate.SubjectAlternativeNames);
        Assert.Equal("UntrustedRoot", record.Certificate.ChainErrors);
        Assert.Equal(state, await _checks.GetNotificationStateAsync(target.Id));
    }

    [Fact]
    public async Task 錯誤結果_保存錯誤種類與原因()
    {
        var target = (await _targets.AddAsync(Example, null, T0)).Target;
        await _checks.AddResultAsync(Error(target.Id, T0), null);

        var record = Assert.Single(await _checks.GetHistoryAsync(target.Id, DateTimeOffset.MinValue));
        Assert.Null(record.Certificate);
        Assert.Equal(ProbeErrorKind.ConnectionRefused, record.ErrorKind);
        Assert.Equal("連線被拒絕", record.ErrorMessage);
    }

    [Fact]
    public async Task 不帶通知進度寫入_不改變通知進度()
    {
        var target = (await _targets.AddAsync(Example, null, T0)).Target;
        var state = new NotificationState(0, CertificateStatus.Warning, T0);
        await _checks.SaveNotificationStateAsync(target.Id, state);

        await _checks.AddResultAsync(Error(target.Id, T0.AddHours(1)), null);

        Assert.Equal(state, await _checks.GetNotificationStateAsync(target.Id));
    }

    [Fact]
    public async Task 目標在檢查期間被刪除_寫入回傳false而不丟例外()
    {
        var target = (await _targets.AddAsync(Example, null, T0)).Target;
        await _targets.RemoveAsync(Example);

        Assert.False(await _checks.AddResultAsync(Ok(target.Id, T0), NotificationState.Initial));
    }

    [Fact]
    public async Task 歷史依時間由新到舊_並依起始時間篩選()
    {
        var target = (await _targets.AddAsync(Example, null, T0)).Target;
        for (var i = 0; i < 5; i++)
        {
            await _checks.AddResultAsync(Ok(target.Id, T0.AddDays(i)), null);
        }

        var history = await _checks.GetHistoryAsync(target.Id, T0.AddDays(2));

        Assert.Equal([T0.AddDays(4), T0.AddDays(3), T0.AddDays(2)], history.Select(r => r.CheckedUtc));
    }

    [Fact]
    public async Task 狀態查詢_回傳每個目標最新一筆_沒有紀錄者為null()
    {
        var a = (await _targets.AddAsync(Example, "A", T0)).Target;
        await _targets.AddAsync(new TargetAddress("b.example", 443), "B", T0);
        await _checks.AddResultAsync(Ok(a.Id, T0), null);
        await _checks.AddResultAsync(Error(a.Id, T0.AddHours(1)), null);

        var status = await _checks.GetStatusAsync();

        Assert.Equal(2, status.Count);
        Assert.Equal(CertificateStatus.Error, status.Single(s => s.Target.Name == "A").Latest!.Status);
        Assert.Null(status.Single(s => s.Target.Name == "B").Latest);
    }

    [Fact]
    public async Task 保留清理_只刪過期歷史_保留每個目標最新一筆與通知進度()
    {
        var active = (await _targets.AddAsync(Example, null, T0)).Target;
        var idle = (await _targets.AddAsync(new TargetAddress("idle.example", 443), null, T0)).Target;
        var activeState = new NotificationState(0, CertificateStatus.Critical, T0.AddDays(9));
        var idleState = new NotificationState(5, CertificateStatus.Error, T0.AddDays(1));

        for (var i = 0; i < 10; i++)
        {
            await _checks.AddResultAsync(Ok(active.Id, T0.AddDays(i)), activeState);
        }

        await _checks.AddResultAsync(Error(idle.Id, T0), idleState);
        await _checks.AddResultAsync(Error(idle.Id, T0.AddDays(1)), idleState);

        var deleted = await _checks.PurgeHistoryAsync(T0.AddDays(5));

        Assert.Equal(5 + 1, deleted);
        Assert.Equal(5, (await _checks.GetHistoryAsync(active.Id, DateTimeOffset.MinValue)).Count);
        var idleLeft = Assert.Single(await _checks.GetHistoryAsync(idle.Id, DateTimeOffset.MinValue));
        Assert.Equal(T0.AddDays(1), idleLeft.CheckedUtc);
        Assert.Equal(activeState, await _checks.GetNotificationStateAsync(active.Id));
        Assert.Equal(idleState, await _checks.GetNotificationStateAsync(idle.Id));
        Assert.Equal(2, (await _targets.ListAsync()).Count);
    }

    [Fact]
    public async Task 初始化可重複執行_且資料庫為WAL模式()
    {
        await _db.Reopen().InitializeAsync();

        Assert.Equal("wal", await _db.Database.GetJournalModeAsync());
    }

    [Fact]
    public async Task 兩個獨立連線同時讀寫_不丟出鎖定錯誤()
    {
        var target = (await _targets.AddAsync(Example, null, T0)).Target;
        var serviceStore = new SqliteCheckStore(_db.Reopen());
        var cliStore = new SqliteCheckStore(_db.Reopen());
        var cliTargets = new SqliteTargetRepository(_db.Reopen());

        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                await serviceStore.AddResultAsync(Ok(target.Id, T0.AddMinutes(i)), new NotificationState(0, CertificateStatus.OK, T0));
            }
        });
        var reader = Task.Run(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                await cliStore.GetStatusAsync();
                await cliStore.AddResultAsync(Ok(target.Id, T0.AddMinutes(i), source: CheckSource.Cli), null);
                await cliTargets.SetEnabledAsync(Example, i % 2 == 0);
            }
        });

        await Task.WhenAll(writer, reader);

        Assert.Equal(400, (await _checks.GetHistoryAsync(target.Id, DateTimeOffset.MinValue)).Count);
    }
}
