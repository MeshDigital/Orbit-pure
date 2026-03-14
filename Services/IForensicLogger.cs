using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Interface for forensic logging with correlation tracking.
/// </summary>
public interface IForensicLogger
{
    void Debug(string correlationId, string stage, string message, string? trackId = null, object? data = null);
    void Info(string correlationId, string stage, string message, string? trackId = null, object? data = null);
    void Warning(string correlationId, string stage, string message, string? trackId = null, object? data = null);
    void Error(string correlationId, string stage, string message, string? trackId = null, Exception? ex = null, object? data = null);
    
    /// <summary>
    /// Starts a timed operation scope. Disposing the return value ends the scope and logs duration.
    /// </summary>
    IDisposable TimedOperation(string correlationId, string stage, string operation, string? trackId = null);

    /// <summary>
    /// Logs a decision matrix of search candidates to explain why a specific one was chosen.
    /// </summary>
    void LogSelectionDecision(string correlationId, string trackId, string decision, object candidates);

    /// <summary>
    /// Logs a message with a specific level (convenience overload).
    /// </summary>
    void Log(string trackId, string stage, string message, ForensicLevel level);

    void LogSearchSummary(string correlationId, string trackId, string summary, object stats);
}

