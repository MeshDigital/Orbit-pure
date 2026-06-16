namespace SLSKDONET.Services.Diagnostics;

public interface ITrackAuditLogger
{
    void Log(string trackHash, string message, bool isError = false);
    void LogSearchCandidate(string trackHash, string peer, int bitrate, string format, string action, string reason);
}
