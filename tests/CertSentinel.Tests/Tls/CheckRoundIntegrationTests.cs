using CertSentinel.Core.Checks;
using CertSentinel.Core.Notifications;
using CertSentinel.Core.Probing;
using CertSentinel.Core.Status;
using CertSentinel.Core.Targets;
using CertSentinel.Infrastructure.Storage;
using CertSentinel.Tests.Core;
using CertSentinel.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace CertSentinel.Tests.Tls;

/// <summary>驗收 #7:同一輪中逾時、拒絕連線、握手失敗各自記為無法檢查,且不影響其他目標。</summary>
public sealed class CheckRoundIntegrationTests
{
    [Fact]
    public async Task 同一輪混合成功與各種失敗_各自記錄且互不影響()
    {
        using var ca = TestCertificates.CreateRootCa();
        using var cert = TestCertificates.Issue(ca, "good.test", "good.test");
        await using var good = LoopbackTlsServer.StartTls(cert);
        await using var silent = LoopbackTlsServer.Start(LoopbackTlsServer.Behavior.Silent);
        await using var garbage = LoopbackTlsServer.Start(LoopbackTlsServer.Behavior.Garbage);
        var closedPort = LoopbackTlsServer.GetClosedPort();

        await using var db = await TempDatabase.CreateAsync();
        var targets = new SqliteTargetRepository(db.Database);
        var store = new SqliteCheckStore(db.Database);
        var now = DateTimeOffset.UtcNow;
        var goodTarget = (await targets.AddAsync(new TargetAddress("good.test", good.Port), null, now)).Target;
        var silentTarget = (await targets.AddAsync(new TargetAddress("silent.test", silent.Port), null, now)).Target;
        var garbageTarget = (await targets.AddAsync(new TargetAddress("garbage.test", garbage.Port), null, now)).Target;
        var closedTarget = (await targets.AddAsync(new TargetAddress("closed.test", closedPort), null, now)).Target;

        var probe = new TlsCertificateProbe(new TlsCertificateProbeTests.LoopbackResolver(), new TlsProbeOptions
        {
            // Windows 對 loopback 被拒連線會重試約 2 秒才回報,逾時需大於此值。
            Timeout = TimeSpan.FromSeconds(5),
            CustomTrustRoots = [ca],
        });
        var coordinator = new CheckCoordinator(targets, store, probe, new FakeEmailSender(), TimeProvider.System,
            new CheckOptions { Thresholds = new StatusThresholds(30, 7), Rules = NotificationRules.Default, MaxConcurrency = 4 },
            NullLogger<CheckCoordinator>.Instance);

        var summary = await coordinator.RunRoundAsync(CancellationToken.None);

        Assert.Equal(4, summary.Checked);
        async Task<CheckRecord> Latest(MonitoredTarget t) => Assert.Single(await store.GetHistoryAsync(t.Id, DateTimeOffset.MinValue));

        var okRecord = await Latest(goodTarget);
        Assert.Equal(CertificateStatus.OK, okRecord.Status);
        Assert.True(okRecord.Certificate!.ChainTrusted);

        Assert.Equal((CertificateStatus.Error, ProbeErrorKind.Timeout), Describe(await Latest(silentTarget)));
        Assert.Equal((CertificateStatus.Error, ProbeErrorKind.HandshakeFailed), Describe(await Latest(garbageTarget)));
        Assert.Equal((CertificateStatus.Error, ProbeErrorKind.ConnectionRefused), Describe(await Latest(closedTarget)));
    }

    private static (CertificateStatus, ProbeErrorKind?) Describe(CheckRecord record)
    {
        Assert.False(string.IsNullOrWhiteSpace(record.ErrorMessage));
        return (record.Status, record.ErrorKind);
    }
}
