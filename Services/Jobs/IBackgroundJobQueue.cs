using System;
using System.Threading;
using System.Threading.Tasks;

namespace SLSKDONET.Services.Jobs;

/// <summary>
/// A unit of work that can be queued for background execution.
/// </summary>
public sealed class BackgroundJob
{
    /// <summary>Unique identifier for this job instance.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Human-readable description shown in progress UI.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Category tag for filtering and logging (e.g., "Analysis", "Stems", "Video").</summary>
    public string Category { get; init; } = "General";

    /// <summary>Work to execute. Receives a progress reporter and the cancellation token.</summary>
    public required Func<IProgress<JobProgress>, CancellationToken, Task> Work { get; init; }
}

/// <summary>
/// Progress report emitted by a running <see cref="BackgroundJob"/>.
/// </summary>
public sealed class JobProgress
{
    public Guid JobId { get; init; }
    public string Description { get; init; } = string.Empty;

    /// <summary>Value in [0, 1]. Negative means indeterminate.</summary>
    public double Fraction { get; init; } = -1;

    public bool IsCompleted { get; init; }
    public bool IsFailed { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Contract for enqueueing background jobs.
/// </summary>
public interface IBackgroundJobQueue
{
    /// <summary>
    /// Enqueues <paramref name="job"/> for background execution.
    /// Returns immediately; the job runs on the worker thread pool.
    /// </summary>
    void Enqueue(BackgroundJob job);

    /// <summary>Subscribe to all progress reports from all enqueued jobs.</summary>
    event EventHandler<JobProgress> JobProgressChanged;

    /// <summary>Current number of jobs waiting or running.</summary>
    int PendingCount { get; }
}
