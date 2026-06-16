using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Events;

namespace SLSKDONET.Services.Diagnostics;

public record AuditLogEntry(string TrackHash, string Message, bool IsError = false, DateTime? Timestamp = null);

public class TrackAuditLogger : ITrackAuditLogger, IDisposable
{
    private readonly string _baseLogsDirectory;
    private readonly ILogger<TrackAuditLogger> _logger;
    private readonly Channel<AuditLogEntry> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerTask;

    public TrackAuditLogger(IEventBus eventBus, ILogger<TrackAuditLogger> logger)
    {
        _logger = logger;
        _baseLogsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ORBIT",
            "TrackLogs"
        );

        // Single-reader, multi-writer channel for high-performance non-blocking log ingestion
        _channel = Channel.CreateUnbounded<AuditLogEntry>(new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false
        });

        // Start background writer loop
        _writerTask = Task.Run(ProcessQueueAsync);

        // Auto-subscribe to all detailed status events to capture them in the persistent log file!
        eventBus.GetEvent<TrackDetailedStatusEvent>().Subscribe(evt =>
        {
            var correlationSuffix = string.IsNullOrWhiteSpace(evt.CorrelationId)
                ? string.Empty
                : $" [corr:{evt.CorrelationId[..Math.Min(8, evt.CorrelationId.Length)]}]";
            Log(evt.TrackHash, $"{evt.Message}{correlationSuffix}", evt.IsError);
        });
    }

    public void Log(string trackHash, string message, bool isError = false)
    {
        if (string.IsNullOrWhiteSpace(trackHash)) return;

        // Non-blocking write to the Channel
        _channel.Writer.TryWrite(new AuditLogEntry(trackHash, message, isError, DateTime.Now));
    }

    public void LogSearchCandidate(string trackHash, string peer, int bitrate, string format, string action, string reason)
    {
        var formattedMsg = $"[CANDIDATE {action}] Peer: '{peer}' | Bitrate: {bitrate}kbps | Format: {format} | Reason: {reason}";
        Log(trackHash, formattedMsg, action == "REJECTED" || action == "IGNORED");
    }

    private async Task ProcessQueueAsync()
    {
        var reader = _channel.Reader;
        var token = _cts.Token;

        try
        {
            while (await reader.WaitToReadAsync(token))
            {
                while (reader.TryRead(out var entry))
                {
                    await WriteToFileAsync(entry);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in TrackAuditLogger background writer");
        }
    }

    private async Task WriteToFileAsync(AuditLogEntry entry)
    {
        try
        {
            // Monthly subfolder partitioning to prevent OS directory bloat: TrackLogs/YYYY-MM/[Hash]_audit.log
            var monthFolder = DateTime.Now.ToString("yyyy-MM");
            var directory = Path.Combine(_baseLogsDirectory, monthFolder);

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var logFile = Path.Combine(directory, $"{entry.TrackHash}_audit.log");
            var timestamp = (entry.Timestamp ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm:ss.fff");
            var prefix = entry.IsError ? "❌" : "ℹ️";
            var logLine = $"[{timestamp}] {prefix} {entry.Message}{Environment.NewLine}";

            await File.AppendAllTextAsync(logFile, logLine, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            // Protect background thread from crashes
            _logger.LogWarning(ex, "Failed to write audit log entry to disk for {Hash}", entry.TrackHash);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
