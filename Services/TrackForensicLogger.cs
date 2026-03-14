using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Track-scoped forensic logger that writes correlation-based logs.
/// Wraps standard ILogger with automatic CorrelationId prefixing and database persistence.
/// </summary>
public class TrackForensicLogger : IForensicLogger, IDisposable
{
    private readonly ILogger<TrackForensicLogger> _logger;
    private readonly Channel<ForensicLogEntry> _logChannel;
    
    public TrackForensicLogger(ILogger<TrackForensicLogger> logger)
    {
        _logger = logger;
        
        // Producer-Consumer channel for async log persistence (non-blocking)
        _logChannel = Channel.CreateUnbounded<ForensicLogEntry>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true // Note: SingleReader=true is optimal if only ConsumerAsync reads. We have event for UI.
        });
        
        // Start background consumer
        _ = Task.Run(ConsumerAsync);
    }
    
    /// <summary>
    /// Logs a debug message with correlation context
    /// </summary>
    public void Debug(string correlationId, string stage, string message, string? trackId = null, object? data = null)
    {
        LogInternal(correlationId, stage, ForensicLevel.Debug, message, trackId, data);
    }
    
    /// <summary>
    /// Logs an info message with correlation context
    /// </summary>
    public void Info(string correlationId, string stage, string message, string? trackId = null, object? data = null)
    {
        LogInternal(correlationId, stage, ForensicLevel.Info, message, trackId, data);
    }
    
    /// <summary>
    /// Logs a warning with correlation context
    /// </summary>
    public void Warning(string correlationId, string stage, string message, string? trackId = null, object? data = null)
    {
        LogInternal(correlationId, stage, ForensicLevel.Warning, message, trackId, data);
    }
    
    /// <summary>
    /// Logs an error with correlation context
    /// </summary>
    public void Error(string correlationId, string stage, string message, string? trackId = null, Exception? ex = null, object? data = null)
    {
        var errorData = data != null ? data : (ex != null ? new { Exception = ex.Message, StackTrace = ex.StackTrace } : null);
        LogInternal(correlationId, stage, ForensicLevel.Error, message, trackId, errorData);
    }
    
    /// <summary>
    /// Logs a rejected search candidate with specific reason and technical details.
    /// Phase 14: Search Audit Trail
    /// </summary>
    public void LogRejection(string trackId, string filename, string reason, string details)
    {
        var data = new { Filename = filename, Reason = reason, TechnicalDetails = details };
        LogInternal(trackId, "Discovery", ForensicLevel.Warning, $"Rejected candidate: {reason}", trackId, data);
    }
    
    /// <summary>
    /// Logs a timed operation (auto-calculates duration)
    /// </summary>
    public IDisposable TimedOperation(string correlationId, string stage, string operation, string? trackId = null)
    {
        return new TimedLogScope(this, correlationId, stage, operation, trackId);
    }
    
    private void LogInternal(string correlationId, string stage, ForensicLevel level, string message, string? trackId, object? data)
    {
        // Standard console log with correlation prefix
        var enrichedMessage = $"[CID: {correlationId[..8]}] [{stage}] {(trackId != null ? $"[T: {trackId[..6]}] " : "")}{message}";
        
        switch (level)
        {
            case ForensicLevel.Debug:
                _logger.LogDebug(enrichedMessage);
                break;
            case ForensicLevel.Info:
                _logger.LogInformation(enrichedMessage);
                break;
            case ForensicLevel.Warning:
                _logger.LogWarning(enrichedMessage);
                break;
            case ForensicLevel.Error:
                _logger.LogError(enrichedMessage);
                break;
        }
        
        // Queue for database persistence (non-blocking)
        var entry = new ForensicLogEntry
        {
            CorrelationId = correlationId,
            TrackIdentifier = trackId,
            Stage = stage,
            Level = level,
            Message = message,
            Data = data != null ? JsonSerializer.Serialize(data) : null,
            Timestamp = DateTime.UtcNow
        };
        
        // Notify live listeners (e.g. Mission Control UI)
        LogGenerated?.Invoke(this, entry);
        
        _logChannel.Writer.TryWrite(entry);
    }
    
    /// <summary>
    /// Logs a decision matrix of search candidates to explain why a specific one was chosen.
    /// </summary>
    public void LogSelectionDecision(string correlationId, string trackId, string decision, object candidates)
    {
        LogInternal(correlationId, ForensicStage.Matching, ForensicLevel.Info, $"Selection: {decision}", trackId, candidates);
    }

    public void Log(string trackId, string stage, string message, ForensicLevel level)
    {
        LogInternal(trackId, stage, level, message, trackId, null);
    }

    /// <summary>
    /// Logs a summary of search attempt statistics.
    /// </summary>
    public void LogSearchSummary(string correlationId, string trackId, string summary, object stats)
    {
        LogInternal(correlationId, ForensicStage.Discovery, ForensicLevel.Info, summary, trackId, stats);
    }

    /// <summary>
    /// Event fired when a new log entry is generated. Safe for UI subscription (but marshaling required).

    /// </summary>
    public event EventHandler<ForensicLogEntry>? LogGenerated;
    
    /// <summary>
    /// Signals the consumer to stop and waits for remaining logs to be persisted
    /// </summary>
    public void Dispose()
    {
        _logger.LogInformation("Forensic logger shutting down, flushing remaining entries...");
        
        // Complete the channel to signal consumer to stop
        _logChannel.Writer.Complete();
        
        // Give consumer up to 5 seconds to finish writing remaining entries
        Task.Delay(5000).Wait();
        
        _logger.LogInformation("Forensic logger shutdown complete");
    }
    
    /// <summary>
    /// Background consumer that persists logs to database
    /// </summary>
    private async Task ConsumerAsync()
    {
        await foreach (var entry in _logChannel.Reader.ReadAllAsync())
        {
            try
            {
                // Use Task.Run to avoid blocking the consumer thread
                await Task.Run(async () =>
                {
                    using var db = new Data.AppDbContext();
                    db.ForensicLogs.Add(entry);
                    await db.SaveChangesAsync();
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist forensic log entry");
            }
        }
    }
    
    /// <summary>
    /// Helper for automatic duration tracking
    /// </summary>
    private class TimedLogScope : IDisposable
    {
        private readonly TrackForensicLogger _logger;
        private readonly string _correlationId;
        private readonly string _stage;
        private readonly string _operation;
        private readonly string? _trackId;
        private readonly Stopwatch _stopwatch;
        
        public TimedLogScope(TrackForensicLogger logger, string correlationId, string stage, string operation, string? trackId = null)
        {
            _logger = logger;
            _correlationId = correlationId;
            _stage = stage;
            _operation = operation;
            _trackId = trackId;
            _stopwatch = Stopwatch.StartNew();
            
            _logger.Debug(correlationId, stage, $"{operation} started", trackId);
        }
        
        public void Dispose()
        {
            _stopwatch.Stop();
            _logger.Info(_correlationId, _stage, $"{_operation} completed in {_stopwatch.ElapsedMilliseconds}ms", _trackId);
        }
    }
}

/// <summary>
/// Extension methods for creating correlation IDs
/// </summary>
public static class CorrelationIdExtensions
{
    /// <summary>
    /// Generates a new correlation ID (short GUID)
    /// </summary>
    public static string NewCorrelationId()
    {
        return Guid.NewGuid().ToString("N"); // 32 chars, no dashes
    }
}
