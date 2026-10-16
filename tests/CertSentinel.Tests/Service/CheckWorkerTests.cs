using System.Diagnostics;
using CertSentinel.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CertSentinel.Tests.Service;

public sealed class CheckWorkerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    /// <summary>可控制每一輪何時結束的假執行器。</summary>
    private sealed class ControlledRunner : IRoundRunner, IDisposable
    {
        private readonly SemaphoreSlim _started = new(0);
        private int _running;

        public int Rounds { get; private set; }

        public int MaxRunning { get; private set; }

        public TaskCompletionSource? Gate { get; set; }

        public bool ObservedCancellation { get; private set; }

        public void Dispose() => _started.Dispose();

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            var running = Interlocked.Increment(ref _running);
            MaxRunning = Math.Max(MaxRunning, running);
            Rounds++;
            _started.Release();
            try
            {
                if (Gate is { } gate)
                {
                    await gate.Task.WaitAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                ObservedCancellation = true;
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        public async Task WaitForRoundAsync()
        {
            Assert.True(await _started.WaitAsync(WaitLimit), "等不到下一輪開始");
        }

        public async Task AssertNoRoundStartsAsync() =>
            Assert.False(await _started.WaitAsync(TimeSpan.FromMilliseconds(200)), "不應開始新的一輪");
    }

    private static CheckWorker CreateWorker(IRoundRunner runner, TimeProvider time) =>
        new(runner, time, Interval, NullLogger<CheckWorker>.Instance);

    [Fact]
    public async Task 啟動後立即檢查一輪_之後依間隔重複()
    {
        var time = new FakeTimeProvider();
        using var runner = new ControlledRunner();
        using var worker = CreateWorker(runner, time);

        await worker.StartAsync(CancellationToken.None);
        await runner.WaitForRoundAsync();
        Assert.Equal(1, runner.Rounds);

        time.Advance(Interval - TimeSpan.FromSeconds(1));
        await runner.AssertNoRoundStartsAsync();

        time.Advance(TimeSpan.FromSeconds(1));
        await runner.WaitForRoundAsync();
        Assert.Equal(2, runner.Rounds);

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task 上一輪未完成時_不開始下一輪_完成後只補跑一輪()
    {
        var time = new FakeTimeProvider();
        using var runner = new ControlledRunner { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var worker = CreateWorker(runner, time);

        await worker.StartAsync(CancellationToken.None);
        await runner.WaitForRoundAsync();

        time.Advance(Interval * 3);
        await runner.AssertNoRoundStartsAsync();

        runner.Gate.SetResult();
        runner.Gate = null;
        await runner.WaitForRoundAsync();
        await runner.AssertNoRoundStartsAsync();

        Assert.Equal(2, runner.Rounds);
        Assert.Equal(1, runner.MaxRunning);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task 停止要求_取消進行中的檢查並在5秒內結束()
    {
        using var runner = new ControlledRunner { Gate = new TaskCompletionSource() }; // 永不完成,只能被取消
        using var worker = CreateWorker(runner, new FakeTimeProvider());
        await worker.StartAsync(CancellationToken.None);
        await runner.WaitForRoundAsync();

        var stopwatch = Stopwatch.StartNew();
        await worker.StopAsync(CancellationToken.None);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"停止花了 {stopwatch.Elapsed}");
        Assert.True(runner.ObservedCancellation);
    }

    [Fact]
    public async Task 某一輪發生例外_服務不中止_下一輪照常執行()
    {
        var time = new FakeTimeProvider();
        var runner = new ThrowOnceRunner();
        using var worker = CreateWorker(runner, time);

        await worker.StartAsync(CancellationToken.None);
        await runner.FirstRound.Task.WaitAsync(WaitLimit);
        time.Advance(Interval);
        await runner.SecondRound.Task.WaitAsync(WaitLimit);

        await worker.StopAsync(CancellationToken.None);
    }

    private sealed class ThrowOnceRunner : IRoundRunner
    {
        private int _calls;

        public TaskCompletionSource FirstRound { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondRound { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RunAsync(CancellationToken cancellationToken)
        {
            if (++_calls == 1)
            {
                FirstRound.SetResult();
                throw new InvalidOperationException("資料庫暫時無法存取(測試模擬)");
            }

            SecondRound.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
