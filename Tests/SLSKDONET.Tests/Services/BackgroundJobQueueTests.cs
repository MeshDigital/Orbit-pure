using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Services.Jobs;
using Xunit;

namespace SLSKDONET.Tests.Services;

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
        var progressEvents = new List<JobProgress>();

        queue.JobProgressChanged += (_, p) => progressEvents.Add(p);

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

        // Allow the worker to emit the completion progress event
        await Task.Delay(50);
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
        await Task.Delay(50); // let counter update
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
        var progressEvents = new List<JobProgress>();
        queue.JobProgressChanged += (_, p) => progressEvents.Add(p);

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

        // Allow the failure progress event to propagate
        await Task.Delay(100);

        // Worker should have emitted a cancellation/failure progress event
        Assert.Contains(progressEvents, p => p.IsFailed);
    }

    // ── error handling ────────────────────────────────────────────────────────

    [Fact]
    public async Task Worker_EmitsFailedProgress_WhenJobThrows()
    {
        var (queue, worker) = Build();
        var progressEvents = new List<JobProgress>();
        queue.JobProgressChanged += (_, p) => progressEvents.Add(p);

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
        await Task.Delay(100); // allow progress event propagation
        cts.Cancel();

        Assert.Contains(progressEvents, p => p.IsFailed && p.ErrorMessage == "boom");
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

        // Give workers time to spin up
        await Task.Delay(200);

        // Release all
        for (int i = 0; i < concurrency; i++)
            gate.Release();

        await Task.Delay(200);
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
