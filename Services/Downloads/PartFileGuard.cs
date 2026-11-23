using System;
using System.Collections.Generic;
using System.IO;

namespace SLSKDONET.Services.Downloads;

/// <summary>Whether an existing <c>.part</c> file may be continued (#328).</summary>
/// <param name="Resume">True: continue from <paramref name="ConfirmedBytes"/>. False: delete the .part and start from 0.</param>
/// <param name="Reason">Why, for the log.</param>
public readonly record struct PartResumeDecision(bool Resume, long ConfirmedBytes, string Reason);

/// <summary>
/// Rules for <c>.part</c> files (#328). A partial download may only be continued from the <b>same</b> source: the
/// same peer, the same remote file and the same size. Bytes from another peer's file (a different encode, edit or
/// remix of the "same" track) must never be glued onto it, so without journal proof of origin the .part is discarded.
/// Also the single place that knows where a track's .part file actually is, so every cleanup path deletes the real
/// file instead of rebuilding a path from Artist/Album/Title (which never existed and created empty folders).
/// </summary>
public static class PartFileGuard
{
    public const string PartSuffix = ".part";

    /// <summary>Decides whether to continue a .part, from the crash journal's record of the previous attempt.</summary>
    /// <param name="journalUser">Peer of the previous attempt (from the journal); null when there is no record.</param>
    public static PartResumeDecision DecideResume(
        string? journalUser, string? journalFile, long journalSize, long journalConfirmedBytes,
        string newUser, string newFile, long newSize)
    {
        if (string.IsNullOrEmpty(journalUser) || string.IsNullOrEmpty(journalFile))
            return new(false, 0, "no record of which source wrote the partial file");
        if (!string.Equals(journalUser, newUser, StringComparison.OrdinalIgnoreCase))
            return new(false, 0, $"partial file came from peer '{journalUser}', the new source is '{newUser}'");
        if (!string.Equals(journalFile, newFile, StringComparison.Ordinal))
            return new(false, 0, "partial file came from a different remote file");
        if (journalSize > 0 && newSize > 0 && journalSize != newSize)
            return new(false, 0, $"partial file was for {journalSize} bytes, the new source has {newSize}");
        return new(true, Math.Max(0, journalConfirmedBytes), "same peer, file and size");
    }

    /// <summary>
    /// Every place the track's .part can be: the path recorded when the download started, and the resolved final
    /// path + ".part" (persisted with the track, so it survives a restart). Never builds a path from tags.
    /// </summary>
    public static IReadOnlyList<string> CandidatePartPaths(string? recordedPartPath, string? resolvedFilePath)
    {
        var paths = new List<string>(2);
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            foreach (var existing in paths)
                if (string.Equals(existing, p, StringComparison.OrdinalIgnoreCase)) return;
            paths.Add(p);
        }
        Add(recordedPartPath);
        if (!string.IsNullOrWhiteSpace(resolvedFilePath))
            Add(resolvedFilePath.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase) ? resolvedFilePath : resolvedFilePath + PartSuffix);
        return paths;
    }

    /// <summary>Deletes whichever candidate .part files exist; returns the paths deleted.</summary>
    public static IReadOnlyList<string> DeletePartFiles(string? recordedPartPath, string? resolvedFilePath)
    {
        var deleted = new List<string>();
        foreach (var path in CandidatePartPaths(recordedPartPath, resolvedFilePath))
        {
            if (!File.Exists(path)) continue;
            File.Delete(path);
            deleted.Add(path);
        }
        return deleted;
    }
}
