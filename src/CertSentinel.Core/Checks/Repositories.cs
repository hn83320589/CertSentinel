using CertSentinel.Core.Notifications;
using CertSentinel.Core.Targets;

namespace CertSentinel.Core.Checks;

public enum AddTargetOutcome
{
    Added,
    Duplicate,
}

public sealed record AddTargetResult(AddTargetOutcome Outcome, MonitoredTarget Target);

public interface ITargetRepository
{
    /// <summary>新增目標;同一主機與埠號已存在時回傳 <see cref="AddTargetOutcome.Duplicate"/> 與既有目標。</summary>
    Task<AddTargetResult> AddAsync(TargetAddress address, string? name, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MonitoredTarget>> ListAsync(CancellationToken cancellationToken = default);

    Task<MonitoredTarget?> FindAsync(TargetAddress address, CancellationToken cancellationToken = default);

    /// <summary>刪除目標及其歷史與通知進度。找不到時回傳 false。</summary>
    Task<bool> RemoveAsync(TargetAddress address, CancellationToken cancellationToken = default);

    /// <summary>找不到時回傳 false。</summary>
    Task<bool> SetEnabledAsync(TargetAddress address, bool enabled, CancellationToken cancellationToken = default);
}

public interface ICheckStore
{
    /// <summary>
    /// 寫入一筆檢查結果;若 <paramref name="notificationState"/> 不為 null,在同一個交易中一併更新通知進度。
    /// 目標在檢查期間已被刪除時不寫入並回傳 false。
    /// </summary>
    Task<bool> AddResultAsync(CheckRecord record, NotificationState? notificationState, CancellationToken cancellationToken = default);

    Task<NotificationState> GetNotificationStateAsync(long targetId, CancellationToken cancellationToken = default);

    Task SaveNotificationStateAsync(long targetId, NotificationState state, CancellationToken cancellationToken = default);

    /// <summary>依時間由新到舊回傳 <paramref name="sinceUtc"/> 之後的紀錄。</summary>
    Task<IReadOnlyList<CheckRecord>> GetHistoryAsync(long targetId, DateTimeOffset sinceUtc, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TargetStatus>> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>刪除早於 <paramref name="cutoffUtc"/> 的歷史,但永遠保留每個目標最新一筆;不影響通知進度。回傳刪除筆數。</summary>
    Task<int> PurgeHistoryAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default);
}
