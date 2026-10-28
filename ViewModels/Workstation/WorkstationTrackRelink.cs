using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;

namespace SLSKDONET.ViewModels.Workstation;

internal enum TrackRelinkOutcome
{
    Success,
    Cancelled,
    InvalidFile,
    MissingIdentity,
}

internal sealed record TrackRelinkResult(TrackRelinkOutcome Outcome, string? FilePath = null);

/// <summary>The side-effecting part of locating a moved track, kept separate from UI status updates.</summary>
internal static class WorkstationTrackRelink
{
    public static async Task<TrackRelinkResult> RelinkAsync(
        PlaylistTrack track,
        ILibraryService library,
        IFileInteractionService fileInteraction)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(fileInteraction);

        // A hash-less row cannot be safely relinked by the shared track identity. In particular,
        // never update every row whose hash is empty.
        if (string.IsNullOrWhiteSpace(track.TrackUniqueHash))
            return new TrackRelinkResult(TrackRelinkOutcome.MissingIdentity);

        var filters = new[]
        {
            new Services.FileDialogFilter("Audio Files", AudioFormats.ImportableTokens.ToList()),
            new Services.FileDialogFilter("All Files", new List<string> { "*" }),
        };
        var newPath = await fileInteraction.OpenFileDialogAsync(
            $"Locate file for: {track.Artist} – {track.Title}", filters).ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(newPath))
            return new TrackRelinkResult(TrackRelinkOutcome.Cancelled);
        if (!File.Exists(newPath))
            return new TrackRelinkResult(TrackRelinkOutcome.InvalidFile, newPath);

        await library.UpdateTrackFilePathAsync(track.TrackUniqueHash, newPath).ConfigureAwait(true);
        return new TrackRelinkResult(TrackRelinkOutcome.Success, newPath);
    }
}
