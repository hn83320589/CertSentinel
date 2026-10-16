using CertSentinel.Core.Notifications;
using CertSentinel.Core.Status;
using static CertSentinel.Core.Status.CertificateStatus;

namespace CertSentinel.Tests.Core;

public class NotificationPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
    private readonly NotificationPolicy _policy = new(NotificationRules.Default);

    /// <summary>模擬連續多輪檢查,回傳每輪寄出的通知種類(null 代表未寄)。</summary>
    private sealed class Simulation(NotificationPolicy policy)
    {
        public NotificationState State { get; private set; } = NotificationState.Initial;

        public NotificationKind? Round(CertificateStatus observed, DateTimeOffset now, bool sendSucceeds = true)
        {
            var decision = policy.Evaluate(State, observed, now);
            State = decision.Kind is not null && sendSucceeds ? decision.StateAfterSent : decision.StateWithoutSend;
            return decision.Kind;
        }
    }

    private Simulation Start(CertificateStatus first, DateTimeOffset? at = null)
    {
        var sim = new Simulation(_policy);
        sim.Round(first, at ?? T0);
        return sim;
    }

    [Fact]
    public void 新目標第一次為OK_不寄信_但建立基準()
    {
        var sim = new Simulation(_policy);

        Assert.Null(sim.Round(OK, T0));
        Assert.Equal(OK, sim.State.NotifiedStatus);
    }

    [Theory]
    [InlineData(Warning)]
    [InlineData(Critical)]
    [InlineData(Expired)]
    public void 新目標第一次就不是OK_寄惡化通知(CertificateStatus first)
    {
        Assert.Equal(NotificationKind.Deteriorated, new Simulation(_policy).Round(first, T0));
    }

    [Theory]
    [InlineData(OK, Warning)]
    [InlineData(Warning, Critical)]
    [InlineData(OK, Critical)]
    [InlineData(OK, Expired)]
    [InlineData(Warning, Expired)]
    [InlineData(Critical, Expired)]
    public void 狀態惡化_寄一封惡化通知_之後不重複(CertificateStatus from, CertificateStatus to)
    {
        var sim = Start(from);

        Assert.Equal(NotificationKind.Deteriorated, sim.Round(to, T0.AddHours(1)));
        Assert.Null(sim.Round(to, T0.AddHours(2)));
        Assert.Equal(to, sim.State.NotifiedStatus);
    }

    [Fact]
    public void 狀態不變_不重複寄送()
    {
        var sim = Start(Warning);

        for (var i = 1; i <= 10; i++)
        {
            Assert.Null(sim.Round(Warning, T0.AddDays(i)));
        }
    }

    [Fact]
    public void Error_連續三次才寄_且只寄一封()
    {
        var sim = Start(OK);

        Assert.Null(sim.Round(Error, T0.AddHours(1)));
        Assert.Null(sim.Round(Error, T0.AddHours(2)));
        Assert.Equal(NotificationKind.Unreachable, sim.Round(Error, T0.AddHours(3)));
        Assert.Null(sim.Round(Error, T0.AddHours(4)));
        Assert.Equal(4, sim.State.ConsecutiveErrors);
    }

    [Fact]
    public void Error_中間成功一次_計數歸零()
    {
        var sim = Start(OK);

        sim.Round(Error, T0.AddHours(1));
        sim.Round(Error, T0.AddHours(2));
        sim.Round(OK, T0.AddHours(3));
        Assert.Null(sim.Round(Error, T0.AddHours(4)));
        Assert.Null(sim.Round(Error, T0.AddHours(5)));
        Assert.Equal(2, sim.State.ConsecutiveErrors);
    }

    [Fact]
    public void Error未達門檻前_不影響原本的通知狀態()
    {
        var sim = Start(Warning);

        sim.Round(Error, T0.AddHours(1));
        sim.Round(Error, T0.AddHours(2));

        Assert.Equal(Warning, sim.State.NotifiedStatus);
        Assert.Null(sim.Round(Warning, T0.AddHours(3)));
    }

    [Theory]
    [InlineData(Warning)]
    [InlineData(Critical)]
    [InlineData(Expired)]
    public void 回到OK_寄一封恢復通知(CertificateStatus from)
    {
        var sim = Start(from);

        Assert.Equal(NotificationKind.Recovered, sim.Round(OK, T0.AddHours(1)));
        Assert.Null(sim.Round(OK, T0.AddHours(2)));
    }

    [Fact]
    public void Error通知後回到OK_寄恢復通知()
    {
        var sim = Start(OK);
        sim.Round(Error, T0.AddHours(1));
        sim.Round(Error, T0.AddHours(2));
        sim.Round(Error, T0.AddHours(3));

        Assert.Equal(NotificationKind.Recovered, sim.Round(OK, T0.AddHours(4)));
    }

    [Fact]
    public void Error通知後取得非OK狀態_寄狀態通知()
    {
        var sim = Start(OK);
        sim.Round(Error, T0.AddHours(1));
        sim.Round(Error, T0.AddHours(2));
        sim.Round(Error, T0.AddHours(3));

        Assert.Equal(NotificationKind.StatusAfterError, sim.Round(Critical, T0.AddHours(4)));
        Assert.Equal(Critical, sim.State.NotifiedStatus);
    }

    [Fact]
    public void Critical改善為Warning_不寄信_但不再提醒Critical()
    {
        var sim = Start(Critical);

        Assert.Null(sim.Round(Warning, T0.AddHours(1)));
        Assert.Equal(Warning, sim.State.NotifiedStatus);
        Assert.Null(sim.Round(Warning, T0.AddDays(3)));
        Assert.Equal(NotificationKind.Deteriorated, sim.Round(Critical, T0.AddDays(4)));
    }

    [Theory]
    [InlineData(Critical)]
    [InlineData(Expired)]
    public void 持續Critical或Expired_每24小時提醒一次_期間內不重複(CertificateStatus status)
    {
        var sim = Start(status);

        Assert.Null(sim.Round(status, T0.AddHours(12)));
        Assert.Null(sim.Round(status, T0.AddHours(24).AddSeconds(-1)));
        Assert.Equal(NotificationKind.Reminder, sim.Round(status, T0.AddHours(24)));
        Assert.Null(sim.Round(status, T0.AddHours(30)));
        Assert.Null(sim.Round(status, T0.AddHours(47)));
        Assert.Equal(NotificationKind.Reminder, sim.Round(status, T0.AddHours(48)));
    }

    [Fact]
    public void 持續Error_從通知時間起每24小時提醒一次()
    {
        var sim = Start(OK);
        sim.Round(Error, T0.AddHours(1));
        sim.Round(Error, T0.AddHours(2));
        sim.Round(Error, T0.AddHours(3)); // 寄出 Unreachable

        Assert.Null(sim.Round(Error, T0.AddHours(26)));
        Assert.Equal(NotificationKind.Reminder, sim.Round(Error, T0.AddHours(27)));
        Assert.Null(sim.Round(Error, T0.AddHours(28)));
    }

    [Fact]
    public void Warning持續_不提醒()
    {
        var sim = Start(Warning);

        Assert.Null(sim.Round(Warning, T0.AddDays(2)));
        Assert.Null(sim.Round(Warning, T0.AddDays(5)));
    }

    [Fact]
    public void 寄信失敗_不視為已通知_下一輪重試_成功後才不再寄()
    {
        var sim = Start(OK);

        Assert.Equal(NotificationKind.Deteriorated, sim.Round(Critical, T0.AddHours(1), sendSucceeds: false));
        Assert.Equal(OK, sim.State.NotifiedStatus);
        Assert.Equal(NotificationKind.Deteriorated, sim.Round(Critical, T0.AddHours(2), sendSucceeds: false));
        Assert.Equal(NotificationKind.Deteriorated, sim.Round(Critical, T0.AddHours(3)));
        Assert.Equal(Critical, sim.State.NotifiedStatus);
        Assert.Equal(T0.AddHours(3), sim.State.NotifiedAtUtc);
        Assert.Null(sim.Round(Critical, T0.AddHours(4)));
    }

    [Fact]
    public void Error通知寄信失敗_下一輪重試_連續錯誤數持續累計()
    {
        var sim = Start(OK);
        sim.Round(Error, T0.AddHours(1));
        sim.Round(Error, T0.AddHours(2));

        Assert.Equal(NotificationKind.Unreachable, sim.Round(Error, T0.AddHours(3), sendSucceeds: false));
        Assert.Equal(NotificationKind.Unreachable, sim.Round(Error, T0.AddHours(4)));
        Assert.Equal(4, sim.State.ConsecutiveErrors);
        Assert.Null(sim.Round(Error, T0.AddHours(5)));
    }

    [Fact]
    public void 提醒寄信失敗_下一輪重試()
    {
        var sim = Start(Critical);

        Assert.Equal(NotificationKind.Reminder, sim.Round(Critical, T0.AddHours(24), sendSucceeds: false));
        Assert.Equal(NotificationKind.Reminder, sim.Round(Critical, T0.AddHours(25)));
        Assert.Null(sim.Round(Critical, T0.AddHours(26)));
    }

    [Fact]
    public void 規則可自訂門檻與提醒間隔()
    {
        var policy = new NotificationPolicy(new NotificationRules(ErrorThreshold: 1, ReminderInterval: TimeSpan.FromHours(1)));
        var state = NotificationState.Initial;

        var decision = policy.Evaluate(state, Error, T0);
        Assert.Equal(NotificationKind.Unreachable, decision.Kind);
        Assert.Equal(NotificationKind.Reminder, policy.Evaluate(decision.StateAfterSent, Error, T0.AddHours(1)).Kind);
    }
}
