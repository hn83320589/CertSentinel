using CertSentinel.Core.Status;

namespace CertSentinel.Core.Notifications;

public enum NotificationKind
{
    /// <summary>狀態惡化(OK → Warning、Warning → Critical、任何 → Expired;新目標首次即非 OK)。</summary>
    Deteriorated,

    /// <summary>連續達門檻次數無法檢查。</summary>
    Unreachable,

    /// <summary>先前通知過無法檢查,現在恢復檢查但狀態非 OK。</summary>
    StatusAfterError,

    /// <summary>回到 OK。</summary>
    Recovered,

    /// <summary>Critical / Expired / Error 持續期間的定期提醒。</summary>
    Reminder,
}

public sealed record NotificationRules(int ErrorThreshold, TimeSpan ReminderInterval)
{
    /// <summary>
    /// 提醒判斷的容差:各輪開始時間會有排程誤差(毫秒級),差幾秒未滿間隔不應讓提醒順延一整輪。
    /// </summary>
    public TimeSpan ReminderTolerance { get; init; } = TimeSpan.FromMinutes(1);

    public static NotificationRules Default { get; } = new(3, TimeSpan.FromHours(24));
}

/// <summary>每個目標持久化的通知進度。<see cref="NotifiedStatus"/> 只有在通知成功寄出(或靜默確認)後才會改變。</summary>
public sealed record NotificationState(int ConsecutiveErrors, CertificateStatus? NotifiedStatus, DateTimeOffset? NotifiedAtUtc)
{
    public static NotificationState Initial { get; } = new(0, null, null);
}

/// <param name="Kind">要寄的通知種類;null 代表不寄。</param>
/// <param name="StateWithoutSend">不寄信、或寄信失敗時要保存的狀態(通知進度不前進,下一輪會重試)。</param>
/// <param name="StateAfterSent">寄信成功後要保存的狀態。</param>
public sealed record NotificationDecision(
    NotificationKind? Kind,
    NotificationState StateWithoutSend,
    NotificationState StateAfterSent);

/// <summary>
/// 通知規則(純函式)。比對的是「最後成功通知的狀態」而非「上一輪觀察到的狀態」,
/// 因此寄信失敗會在下一輪自動重試,服務重啟後也不會重寄已通知過的狀態。
/// </summary>
public sealed class NotificationPolicy(NotificationRules rules)
{
    public NotificationDecision Evaluate(NotificationState state, CertificateStatus observed, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);

        var errors = observed == CertificateStatus.Error ? state.ConsecutiveErrors + 1 : 0;
        var counted = state with { ConsecutiveErrors = errors };

        if (observed == CertificateStatus.Error && errors < rules.ErrorThreshold)
        {
            return NoSend(counted);
        }

        var sent = counted with { NotifiedStatus = observed, NotifiedAtUtc = nowUtc };
        var kind = Decide(state, observed, nowUtc);
        if (kind is not null)
        {
            return new NotificationDecision(kind, counted, sent);
        }

        // 不寄信時,新目標的 OK 基準與「改善但未回到 OK」都靜默記為已通知狀態。
        return state.NotifiedStatus == observed ? NoSend(counted) : NoSend(sent);
    }

    private NotificationKind? Decide(NotificationState state, CertificateStatus observed, DateTimeOffset nowUtc)
    {
        if (state.NotifiedStatus is not { } notified)
        {
            return observed == CertificateStatus.OK ? null
                : observed == CertificateStatus.Error ? NotificationKind.Unreachable
                : NotificationKind.Deteriorated;
        }

        if (observed == notified)
        {
            return IsReminderDue(state, observed, nowUtc) ? NotificationKind.Reminder : null;
        }

        if (observed == CertificateStatus.OK)
        {
            return NotificationKind.Recovered;
        }

        if (observed == CertificateStatus.Error)
        {
            return NotificationKind.Unreachable;
        }

        if (notified == CertificateStatus.Error)
        {
            return NotificationKind.StatusAfterError;
        }

        return observed.IsWorseThan(notified) ? NotificationKind.Deteriorated : null;
    }

    private bool IsReminderDue(NotificationState state, CertificateStatus status, DateTimeOffset nowUtc) =>
        status is CertificateStatus.Critical or CertificateStatus.Expired or CertificateStatus.Error
        && state.NotifiedAtUtc is { } last
        && nowUtc - last >= rules.ReminderInterval - rules.ReminderTolerance;

    private static NotificationDecision NoSend(NotificationState state) => new(null, state, state);
}
