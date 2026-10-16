using CertSentinel.Core.Checks;
using CertSentinel.Infrastructure.Storage;

namespace CertSentinel.Service;

/// <summary>每一輪:確保資料庫已初始化 → 檢查所有啟用目標 → 清理超過保留天數的歷史。</summary>
public sealed class CoordinatorRoundRunner(SqliteDatabase database, CheckCoordinator coordinator, TimeSpan retention) : IRoundRunner
{
    private bool _initialized;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            await database.InitializeAsync(cancellationToken);
            _initialized = true;
        }

        await coordinator.RunRoundAsync(cancellationToken);
        await coordinator.PurgeHistoryAsync(retention, cancellationToken);
    }
}
