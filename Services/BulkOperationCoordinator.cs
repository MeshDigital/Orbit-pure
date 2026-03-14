using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services
{
    public interface IBulkOperationCoordinator
    {
        Task<BulkOperationResult> RunOperationAsync<T>(
            IEnumerable<T> items, 
            Func<T, CancellationToken, Task<bool>> operation, 
            string operationName,
            CancellationToken cancellationToken = default);

        bool IsRunning { get; }
    }

    public class BulkOperationCoordinator : IBulkOperationCoordinator
    {
        private readonly ILogger<BulkOperationCoordinator> _logger;
        private readonly IEventBus _eventBus;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private bool _isRunning;

        public bool IsRunning => _isRunning;

        public BulkOperationCoordinator(
            ILogger<BulkOperationCoordinator> logger,
            IEventBus eventBus)
        {
            _logger = logger;
            _eventBus = eventBus;
        }

        public async Task<BulkOperationResult> RunOperationAsync<T>(
            IEnumerable<T> items, 
            Func<T, CancellationToken, Task<bool>> operation, 
            string operationName,
            CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (_isRunning)
                {
                    throw new InvalidOperationException("Another bulk operation is already running.");
                }
                _isRunning = true;
            }
            finally
            {
                _lock.Release();
            }

            var itemList = items as IList<T> ?? new List<T>(items);
            int total = itemList.Count;
            int processed = 0;
            int success = 0;
            int failed = 0;
            var errors = new List<string>();

            _logger.LogInformation("Starting Bulk Operation '{Operation}' on {Count} items", operationName, total);
            
            // Publish Start Event
            _eventBus.Publish(new BulkOperationStartedEvent(operationName, total));

            var result = new BulkOperationResult { Total = total };

            try
            {
                foreach (var item in itemList)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        bool outcome = await operation(item, cancellationToken);
                        if (outcome) success++; else failed++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        errors.Add($"Item failed: {ex.Message}");
                        _logger.LogError(ex, "Bulk op item failure");
                    }

                    processed++;
                    
                    // Publish Progress Event
                    _eventBus.Publish(new BulkOperationProgressEvent(operationName, processed, total));
                }
            }
            finally
            {
                _isRunning = false;
                result.SuccessCount = success;
                result.FailCount = failed;
                result.Errors = errors;

                _logger.LogInformation("Bulk Operation '{Operation}' Completed. Success={Success}, Failed={Failed}", 
                    operationName, success, failed);

                // Publish Complete Event
                _eventBus.Publish(new BulkOperationCompletedEvent(operationName, result));
            }

            return result;
        }
    }

    public class BulkOperationResult
    {
        public int Total { get; set; }
        public int SuccessCount { get; set; }
        public int FailCount { get; set; }
        public List<string> Errors { get; set; } = new();
    }

    public class BulkOperationStartedEvent
    {
        public string Title { get; }
        public int TotalCount { get; }
        public BulkOperationStartedEvent(string title, int total) 
        { 
            Title = title; 
            TotalCount = total; 
        }
    }

    public class BulkOperationProgressEvent
    {
        public string Title { get; }
        public int Processed { get; }
        public int Total { get; }
        public int Percentage => Total > 0 ? (int)((double)Processed / Total * 100) : 0;
        
        public BulkOperationProgressEvent(string title, int processed, int total)
        {
            Title = title;
            Processed = processed;
            Total = total;
        }
    }

    public class BulkOperationCompletedEvent
    {
        public string Title { get; }
        public BulkOperationResult Result { get; }
        public BulkOperationCompletedEvent(string title, BulkOperationResult result)
        {
            Title = title;
            Result = result;
        }
    }
}
