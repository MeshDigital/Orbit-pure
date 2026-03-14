using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

public interface ISoulseekAdapter
{
    bool IsConnected { get; }
    Task ConnectAsync(string? password = null, CancellationToken ct = default);
    Task DisconnectAsync();
    void Disconnect();
    Task<int> SearchAsync(
        string query,
        IEnumerable<string>? formatFilter,
        (int? Min, int? Max) bitrateFilter,
        DownloadMode mode,
        Action<IEnumerable<Track>> onTracksFound,
        CancellationToken ct = default);

    IAsyncEnumerable<Track> StreamResultsAsync(
        string query,
        IEnumerable<string>? formatFilter,
        (int? Min, int? Max) bitrateFilter,
        DownloadMode mode,
        CancellationToken ct = default);

    Task<bool> DownloadAsync(
        string username,
        string filename,
        string outputPath,
        long? size = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        long startOffset = 0);

    Task<int> ProgressiveSearchAsync(
        string artist,
        string title,
        string? album,
        IEnumerable<string>? formatFilter,
        (int? Min, int? Max) bitrateFilter,
        Action<IEnumerable<Track>> onTracksFound,
        CancellationToken ct = default);

    event EventHandler<DownloadProgressEventArgs>? DownloadProgressChanged;
    event EventHandler<DownloadCompletedEventArgs>? DownloadCompleted;
}

public class DownloadProgressEventArgs : EventArgs
{
    public string Filename { get; }
    public string Username { get; }
    public double Progress { get; }
    public long BytesReceived { get; }
    public long TotalBytes { get; }

    public DownloadProgressEventArgs(string filename, string username, double progress, long bytesReceived, long totalBytes)
    {
        Filename = filename;
        Username = username;
        Progress = progress;
        BytesReceived = bytesReceived;
        TotalBytes = totalBytes;
    }
}

public class DownloadCompletedEventArgs : EventArgs
{
    public string Filename { get; }
    public string Username { get; }
    public bool Success { get; }
    public string? Error { get; }

    public DownloadCompletedEventArgs(string filename, string username, bool success, string? error = null)
    {
        Filename = filename;
        Username = username;
        Success = success;
        Error = error;
    }
}
