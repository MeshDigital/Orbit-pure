using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace SLSKDONET.Tests;

/// <summary>
/// Runs async code on one dedicated thread with a posting <see cref="SynchronizationContext"/>,
/// the way the Avalonia UI thread behaves. Returns that thread's managed id, so a test can assert
/// that view-model state changes come back to it (see ViewModelConfigureAwaitGuardTests).
/// </summary>
internal sealed class SingleThreadContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public static Task<int> RunAsync(Func<Task> body)
    {
        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var context = new SingleThreadContext();
            SetSynchronizationContext(context);
            var task = body();
            task.ContinueWith(_ => context._queue.CompleteAdding(), TaskScheduler.Default);

            foreach (var (callback, state) in context._queue.GetConsumingEnumerable())
                callback(state);

            if (task.IsFaulted)
                done.SetException(task.Exception!.InnerExceptions);
            else
                done.SetResult(Environment.CurrentManagedThreadId);
        }) { IsBackground = true };
        thread.Start();
        return done.Task;
    }
}
