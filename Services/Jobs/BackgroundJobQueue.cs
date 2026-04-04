using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.Jobs;

/// <summary>
/// Channel-based implementation of <see cref="IBackgroundJobQueue"/>.
///
/// Design:
///   - Unbounded <see cref="Channel{T}"/> acts as the job queue.  Writers never block.
///   - A single <see cref="BackgroundJobWorker"/> (registered as IHostedService) drains
///     the channel concurrently up to <see cref="MaxConcurrency"/> jobs.
///   - Progress events are raised on the thread pool — callers must marshal to UI thread
///     (e.g., via Avalonia Dispatcher.UIThread.Post) if updating bound properties.
/// </summary>
public sealed class BackgroundJobQueue : IBackgroundJobQueue, IDisposable
{
    private readonly Channel<BackgroundJob> _channel;
    private int _pendingCount;

    public event EventHandler<JobProgress>? JobProgressChanged;

    public int PendingCount => _pendingCount;

    public BackgroundJobQueue()
    {
        _channel = Channel.CreateUnbounded<BackgroundJob>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });
    }

    public void Enqueue(BackgroundJob job)
    {
        Interlocked.Increment(ref _pendingCount);
        _channel.Writer.TryWrite(job); // Never blocks on unbounded channel.
    }

    internal ChannelReader<BackgroundJob> Reader => _channel.Reader;

    internal void ReportProgress(JobProgress progress)
    {
        if (progress.IsCompleted || progress.IsFailed)
            Interlocked.Decrement(ref _pendingCount);

        JobProgressChanged?.Invoke(this, progress);
    }

    public void Dispose() => _channel.Writer.TryComplete();
}

/// <summary>
/// Hosted service that consumes the <see cref="BackgroundJobQueue"/> channel and
/// executes jobs concurrently up to <see cref="MaxConcurrency"/>.
/// </summary>
public sealed class BackgroundJobWorker : BackgroundService
{
    /// <summary>
    /// Maximum number of jobs that run simultaneously.
    /// Keep at 1 for CPU-bound analysis to avoid starving the UI.
    /// Raise to 2-4 for I/O-bound jobs (metadata fetch, file copy).
    /// </summary>
    public int MaxConcurrency { get; set; } = 1;

    private readonly BackgroundJobQueue _queue;
    private readonly ILogger<BackgroundJobWorker> _logger;

    public BackgroundJobWorker(BackgroundJobQueue queue, ILogger<BackgroundJobWorker> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var semaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);

        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            await semaphore.WaitAsync(stoppingToken);

            // Fire-and-forget per job; semaphore controls concurrency.
            _ = Task.Run(async () =>
            {
                try
                {
                    _logger.LogInformation("[Jobs] Starting [{Category}] {Description} ({Id})",
                        job.Category, job.Description, job.Id);

                    var progress = new Progress<JobProgress>(p => _queue.ReportProgress(p));

                    await job.Work(progress, stoppingToken);

                    _queue.ReportProgress(new JobProgress
                    {
                        JobId = job.Id,
                        Description = job.Description,
                        Fraction = 1.0,
                        IsCompleted = true,
                    });

                    _logger.LogInformation("[Jobs] Completed [{Category}] {Description} ({Id})",
                        job.Category, job.Description, job.Id);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("[Jobs] Cancelled [{Category}] {Description} ({Id})",
                        job.Category, job.Description, job.Id);
                    _queue.ReportProgress(new JobProgress
                    {
                        JobId = job.Id,
                        Description = job.Description,
                        IsFailed = true,
                        ErrorMessage = "Cancelled",
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Jobs] Failed [{Category}] {Description} ({Id})",
                        job.Category, job.Description, job.Id);
                    _queue.ReportProgress(new JobProgress
                    {
                        JobId = job.Id,
                        Description = job.Description,
                        IsFailed = true,
                        ErrorMessage = ex.Message,
                    });
                }
                finally
                {
                    semaphore.Release();
                }
            }, stoppingToken);
        }
    }
}
