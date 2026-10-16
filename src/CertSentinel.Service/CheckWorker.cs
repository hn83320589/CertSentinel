namespace CertSentinel.Service;

/// <summary>一輪的工作內容(檢查 + 清理)。抽成介面讓排程行為可以用假時間測試。</summary>
public interface IRoundRunner
{
    Task RunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 啟動後立即執行第一輪,之後依間隔重複。每輪循序 await,上一輪未完成不會開始下一輪;
/// <see cref="PeriodicTimer"/> 不累積錯過的 tick,因此一輪超時後只會立即補跑一次。
/// </summary>
public sealed partial class CheckWorker(
    IRoundRunner runner,
    TimeProvider timeProvider,
    TimeSpan interval,
    ILogger<CheckWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(interval.TotalMinutes);
        using var timer = new PeriodicTimer(interval, timeProvider);
        try
        {
            do
            {
                await RunRoundSafelyAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            LogStopping();
        }
    }

    private async Task RunRoundSafelyAsync(CancellationToken stoppingToken)
    {
        try
        {
            await runner.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // 單輪失敗(例如資料庫暫時無法存取)只記錄,服務持續於下一輪重試。
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRoundFailed(ex);
        }
    }

    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "CertSentinel 服務已啟動,檢查間隔 {IntervalMinutes} 分鐘")]
    private partial void LogStarted(double intervalMinutes);

    [LoggerMessage(EventId = 101, Level = LogLevel.Information, Message = "收到停止要求,已取消進行中的檢查")]
    private partial void LogStopping();

    [LoggerMessage(EventId = 102, Level = LogLevel.Error, Message = "本輪檢查失敗,將於下一輪重試")]
    private partial void LogRoundFailed(Exception exception);
}
