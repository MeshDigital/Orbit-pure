using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Services.Jobs;
using Xunit;

namespace SLSKDONET.Tests.Services;

[Collection(NonParallelCollection.Name)]
public class BackgroundJobQueueTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static (BackgroundJobQueue queue, BackgroundJobWorker worker) Build(int maxConcurrency = 2)
    {
        var queue = new BackgroundJobQueue();
        var worker = new BackgroundJobWorker(queue, NullLogger<BackgroundJobWorker>.Instance)
        {
            MaxConcurrency = maxConcurrency
        };
        return (queue, worker);
    }

    /// <summary>
    /// Waits until <paramref name="condition"/> holds (or fails the test after 5 s). The worker
    /// raises progress events on its own threads, so a fixed Task.Delay was racy under full-suite
    /// parallel load — the event often hadn't arrived yet.
    /// </summary>
    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    private static BackgroundJob MakeJob(Func<IProgress<JobProgress>, CancellationToken, Task> work,
        string description = "test", string category = "Test")
        => new BackgroundJob { Description = description, Category = category, Work = work };

    // ── PendingCount ──────────────────────────────────────────────────────────

    [Fact]
    public void Enqueue_IncrementsPendingCount()
    {
        var queue = new BackgroundJobQueue();
        var job = MakeJob(async (_, ct) => await Task.Delay(1000, ct));

        queue.Enqueue(job);

        Assert.Equal(1, queue.PendingCount);
    }

    [Fact]
    public void Enqueue_MultipleJobs_PendingCountMatchesEnqueuedCount()
    {
        var queue = new BackgroundJobQueue();
        for (int i = 0; i < 5; i++)
            queue.Enqueue(MakeJob(async (_, ct) => await Task.Delay(1000, ct)));

        Assert.Equal(5, queue.PendingCount);
    }

    // ── job execution ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Worker_ExecutesJob_AndReportsCompletion()
    {
        var (queue, worker) = Build();
        var executed = new TaskCompletionSource<bool>();
        var progressEvents = new ConcurrentQueue<JobProgress>();

        queue.JobProgressChanged += (_, p) => progressEvents.Enqueue(p);

        var job = MakeJob(async (progress, ct) =>
        {
            progress.Report(new JobProgress { Description = "halfway", Fraction = 0.5 });
            await Task.Yield();
            executed.TrySetResult(true);
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var workerTask = worker.StartAsync(cts.Token);

        queue.Enqueue(job);
        await executed.Task;

        await WaitUntil(() => progressEvents.Any(p => p.IsCompleted || p.IsFailed), "completion progress event");
        cts.Cancel();
        await workerTask;

        Assert.True(executed.Task.Result);
        // At minimum: mid-progress + completion event
        Assert.Contains(progressEvents, p => p.Fraction == 0.5);
        Assert.Contains(progressEvents, p => p.IsCompleted || p.IsFailed);
    }

    [Fact]
    public async Task Worker_DecreasesPendingCount_AfterJobCompletes()
    {
        var (queue, worker) = Build();
        var done = new SemaphoreSlim(0, 1);

        var job = MakeJob(async (_, _) =>
        {
            await Task.Yield();
            done.Release();
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);
        queue.Enqueue(job);

        await done.WaitAsync(cts.Token);
        await WaitUntil(() => queue.PendingCount == 0, "pending count to drop to 0");
        cts.Cancel();

        Assert.Equal(0, queue.PendingCount);
    }

    // ── cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Worker_RespectsStoppingToken_OnShutdown()
    {
        var (queue, worker) = Build();

        // Enqueue a long-running job
        queue.Enqueue(MakeJob(async (_, ct) => await Task.Delay(10_000, ct)));

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        // Trigger graceful shutdown immediately
        cts.Cancel();

        // StopAsync should complete without hanging
        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await worker.StopAsync(stopCts.Token); // should not throw
    }

    [Fact]
    public async Task Worker_EmitsCancelled_Progress_WhenTokenCancelled()
    {
        var (queue, worker) = Build();
        var progressEvents = new ConcurrentQueue<JobProgress>();
        queue.JobProgressChanged += (_, p) => progressEvents.Enqueue(p);

        // Signal gate so we know when the job has started
        var jobStarted = new SemaphoreSlim(0, 1);

        queue.Enqueue(MakeJob(async (_, ct) =>
        {
            jobStarted.Release(); // notify test that work has begun
            await Task.Delay(30_000, ct); // long delay; will be cancelled
        }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);

        // Wait until the job is actually running before we shut down
        await jobStarted.WaitAsync(cts.Token);

        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);

        // Worker should have emitted a cancellation/failure progress event
        await WaitUntil(() => progressEvents.Any(p => p.IsFailed), "cancellation progress event");
    }

    // ── error handling ────────────────────────────────────────────────────────

    [Fact]
    public async Task Worker_EmitsFailedProgress_WhenJobThrows()
    {
        var (queue, worker) = Build();
        var progressEvents = new ConcurrentQueue<JobProgress>();
        queue.JobProgressChanged += (_, p) => progressEvents.Enqueue(p);

        var done = new TaskCompletionSource();
        var job = MakeJob(async (_, _) =>
        {
            await Task.Yield();
            done.TrySetResult();
            throw new InvalidOperationException("boom");
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);
        queue.Enqueue(job);

        await done.Task;
        await WaitUntil(() => progressEvents.Any(p => p.IsFailed && p.ErrorMessage == "boom"), "failed progress event");
        cts.Cancel();
    }

    // ── concurrency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Worker_RunsUpToMaxConcurrency_Simultaneously()
    {
        const int concurrency = 3;
        var (queue, worker) = Build(maxConcurrency: concurrency);

        var gate = new SemaphoreSlim(0);
        int peakConcurrent = 0;
        int currentConcurrent = 0;

        Func<IProgress<JobProgress>, CancellationToken, Task> work = async (_, ct) =>
        {
            int current = Interlocked.Increment(ref currentConcurrent);
            // Track peak
            int prev;
            do { prev = Volatile.Read(ref peakConcurrent); }
            while (current > prev && Interlocked.CompareExchange(ref peakConcurrent, current, prev) != prev);

            await gate.WaitAsync(ct);
            Interlocked.Decrement(ref currentConcurrent);
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);

        for (int i = 0; i < concurrency; i++)
            queue.Enqueue(MakeJob(work));

        // All jobs running at once (each is parked on the gate).
        await WaitUntil(() => Volatile.Read(ref currentConcurrent) == concurrency, "all jobs running concurrently");

        // Release all
        for (int i = 0; i < concurrency; i++)
            gate.Release();

        await WaitUntil(() => Volatile.Read(ref currentConcurrent) == 0, "all jobs finished");
        cts.Cancel();

        Assert.Equal(concurrency, peakConcurrent);
    }

    // ── dispose ───────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_CompletesChannel_NoException()
    {
        var queue = new BackgroundJobQueue();
        queue.Enqueue(MakeJob((_, _) => Task.CompletedTask));

        var ex = Record.Exception(() => queue.Dispose());

        Assert.Null(ex);
    }
}
