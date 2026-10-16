using CertSentinel.Core.Notifications;
using CertSentinel.Core.Probing;
using CertSentinel.Core.Status;
using Microsoft.Extensions.Logging;

namespace CertSentinel.Core.Checks;

public sealed class CheckOptions
{
    public required StatusThresholds Thresholds { get; init; }

    public required NotificationRules Rules { get; init; }

    public int MaxConcurrency { get; init; } = 5;
}

public sealed record RoundSummary(int Checked, int Errors, int NotificationsSent, int NotificationsFailed);

/// <summary>
/// 一輪檢查的編排:依並行上限檢查所有啟用目標、寫入結果、依 <see cref="NotificationPolicy"/> 寄信。
/// 單一目標的任何失敗只影響該目標。
/// </summary>
public sealed partial class CheckCoordinator(
    ITargetRepository targets,
    ICheckStore store,
    ICertificateProbe probe,
    IEmailSender emailSender,
    TimeProvider timeProvider,
    CheckOptions options,
    ILogger<CheckCoordinator> logger)
{
    private readonly NotificationPolicy _policy = new(options.Rules);

    public async Task<RoundSummary> RunRoundAsync(CancellationToken cancellationToken)
    {
        var enabled = (await targets.ListAsync(cancellationToken)).Where(t => t.Enabled).ToList();
        LogRoundStarted(enabled.Count);

        var errors = 0;
        var sent = 0;
        var failed = 0;
        await Parallel.ForEachAsync(
            enabled,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.MaxConcurrency), CancellationToken = cancellationToken },
            async (target, ct) =>
            {
                var outcome = await ProcessTargetAsync(target, ct);
                if (outcome.IsError)
                {
                    Interlocked.Increment(ref errors);
                }

                if (outcome.Notification == NotificationResult.Sent)
                {
                    Interlocked.Increment(ref sent);
                }
                else if (outcome.Notification == NotificationResult.Failed)
                {
                    Interlocked.Increment(ref failed);
                }
            });

        var summary = new RoundSummary(enabled.Count, errors, sent, failed);
        LogRoundFinished(summary.Checked, summary.Errors, summary.NotificationsSent, summary.NotificationsFailed);
        return summary;
    }

    /// <summary>CLI 的 check 指令:立即檢查並寫入歷史,但不更新通知進度、不寄信。</summary>
    public async Task<CheckRecord> CheckOnceAsync(MonitoredTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        var record = await CheckAsync(target, CheckSource.Cli, cancellationToken);
        await store.AddResultAsync(record, notificationState: null, cancellationToken);
        LogChecked(target.Address.ToString(), record.Status, record.DaysRemaining, record.ErrorMessage);
        return record;
    }

    /// <summary>刪除超過保留期限的歷史(每個目標最新一筆與通知進度不受影響)。</summary>
    public async Task<int> PurgeHistoryAsync(TimeSpan retention, CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow() - retention;
        var deleted = await store.PurgeHistoryAsync(cutoff, cancellationToken);
        LogPurged(deleted, cutoff);
        return deleted;
    }

    private async Task<TargetOutcome> ProcessTargetAsync(MonitoredTarget target, CancellationToken cancellationToken)
    {
        var address = target.Address.ToString();
        try
        {
            var record = await CheckAsync(target, CheckSource.Service, cancellationToken);
            var state = await store.GetNotificationStateAsync(target.Id, cancellationToken);
            var decision = _policy.Evaluate(state, record.Status, record.CheckedUtc);

            if (!await store.AddResultAsync(record, decision.StateWithoutSend, cancellationToken))
            {
                LogTargetRemoved(address);
                return new TargetOutcome(false, NotificationResult.None);
            }

            LogChecked(address, record.Status, record.DaysRemaining, record.ErrorMessage);
            var isError = record.Status == CertificateStatus.Error;
            if (decision.Kind is not { } kind)
            {
                return new TargetOutcome(isError, NotificationResult.None);
            }

            var message = NotificationComposer.Compose(kind, target, record, decision.StateWithoutSend.ConsecutiveErrors, timeProvider.LocalTimeZone);
            var result = await SendAsync(target, kind, message, decision.StateAfterSent, cancellationToken);
            return new TargetOutcome(isError, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // 單一目標的任何失敗都不得影響同一輪的其他目標。
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogTargetFailed(ex, address);
            return new TargetOutcome(true, NotificationResult.None);
        }
    }

    private async Task<NotificationResult> SendAsync(
        MonitoredTarget target,
        NotificationKind kind,
        EmailMessage message,
        NotificationState stateAfterSent,
        CancellationToken cancellationToken)
    {
        var address = target.Address.ToString();
        try
        {
            await emailSender.SendAsync(message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // 寄信失敗不視為已通知,保留未前進的通知進度,下一輪重試。
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogNotificationFailed(ex, kind, address, message.Subject);
            return NotificationResult.Failed;
        }

        // 寄出後才推進通知進度;此處若程序中斷,下一輪可能重寄一次(at-least-once)。
        await store.SaveNotificationStateAsync(target.Id, stateAfterSent, CancellationToken.None);
        LogNotificationSent(kind, address, message.Subject);
        return NotificationResult.Sent;
    }

    private async Task<CheckRecord> CheckAsync(MonitoredTarget target, CheckSource source, CancellationToken cancellationToken)
    {
        ProbeResult result;
        try
        {
            result = await probe.ProbeAsync(target.Address, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // 探測器的非預期例外記為該目標的 Error,而非中斷整輪。
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogProbeCrashed(ex, target.Address.ToString());
            result = ProbeResult.Failure(ProbeErrorKind.NetworkError, $"檢查時發生非預期錯誤:{ex.Message}");
        }

        var now = timeProvider.GetUtcNow();
        if (result.Certificate is { } cert)
        {
            var evaluation = StatusClassifier.Classify(cert.NotAfterUtc, now, options.Thresholds);
            return new CheckRecord(0, target.Id, now, source, evaluation.Status, evaluation.DaysRemaining, cert, result.ErrorKind, result.ErrorMessage);
        }

        return new CheckRecord(0, target.Id, now, source, CertificateStatus.Error, null, null, result.ErrorKind, result.ErrorMessage);
    }

    private enum NotificationResult
    {
        None,
        Sent,
        Failed,
    }

    private readonly record struct TargetOutcome(bool IsError, NotificationResult Notification);

    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "開始一輪檢查,共 {Count} 個啟用目標")]
    private partial void LogRoundStarted(int count);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "本輪檢查結束:檢查 {Checked} 個、無法檢查 {Errors} 個、寄出通知 {Sent} 封、寄送失敗 {Failed} 封")]
    private partial void LogRoundFinished(int @checked, int errors, int sent, int failed);

    [LoggerMessage(EventId = 1100, Level = LogLevel.Information, Message = "檢查 {Target}:狀態 {Status},剩餘 {Days} 天,錯誤 {Error}")]
    private partial void LogChecked(string target, CertificateStatus status, int? days, string? error);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Warning, Message = "目標 {Target} 在檢查期間已被刪除,結果不寫入")]
    private partial void LogTargetRemoved(string target);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Error, Message = "處理目標 {Target} 時發生非預期錯誤")]
    private partial void LogTargetFailed(Exception exception, string target);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Error, Message = "探測目標 {Target} 時發生非預期錯誤")]
    private partial void LogProbeCrashed(Exception exception, string target);

    [LoggerMessage(EventId = 1200, Level = LogLevel.Information, Message = "已寄出 {Kind} 通知({Target}):{Subject}")]
    private partial void LogNotificationSent(NotificationKind kind, string target, string subject);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Warning, Message = "{Kind} 通知寄送失敗({Target}):{Subject};下一輪重試")]
    private partial void LogNotificationFailed(Exception exception, NotificationKind kind, string target, string subject);

    [LoggerMessage(EventId = 1300, Level = LogLevel.Information, Message = "已清理 {Deleted} 筆早於 {Cutoff:O} 的歷史紀錄")]
    private partial void LogPurged(int deleted, DateTimeOffset cutoff);
}
