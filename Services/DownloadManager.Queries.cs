using System;
using SLSKDONET.Views;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json; // Phase 2A: Checkpoint serialization
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Serilog.Context;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services.InputParsers;
using SLSKDONET.Services.AutoDownload;
using SLSKDONET.Utils;
using SLSKDONET.Services.Models;
using SLSKDONET.Services.Downloads;
using SLSKDONET.Data.Essentia;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Services.Repositories; // [NEW] Namespace
using SLSKDONET.Services.IO; // Added explicit using
using SLSKDONET.Events;

namespace SLSKDONET.Services;

/// <summary>Public read-only queue queries for ViewModel hydration — extracted from DownloadManager (#307).</summary>
public partial class DownloadManager
{
    /// <summary>
    /// Returns a snapshot of all current downloads for ViewModel hydration.
    /// </summary>
    public IReadOnlyList<(PlaylistTrack Model, PlaylistTrackState State)> GetAllDownloads()
    {
        lock (_collectionLock)
        {
            return _downloads.Select(ctx => (ctx.Model, ctx.State)).ToList();
        }
    }

    /// <summary>
    /// Gets the number of actively downloading or queued tracks for a specific project.
    /// Used for real-time UI updates in the library sidebar.
    /// </summary>
    public int GetActiveDownloadsCountForProject(Guid projectId)
    {
        lock (_collectionLock)
        {
            return _downloads.Count(d => d.Model.PlaylistId == projectId &&
                (d.IsActive || d.State == PlaylistTrackState.Pending || d.State == PlaylistTrackState.Stalled));
        }
    }

    /// <summary>
    /// Gets the name of the track currently being downloaded for a project.
    /// </summary>
    public string? GetCurrentlyDownloadingTrackName(Guid projectId)
    {
        lock (_collectionLock)
        {
            var active = _downloads.FirstOrDefault(d =>
                d.Model.PlaylistId == projectId &&
                d.State == PlaylistTrackState.Downloading);

            return active != null ? $"{active.Model.Artist} - {active.Model.Title}" : null;
        }
    }

    /// <summary>
    /// Checks if a track is already in the library or download queue.
    /// </summary>
    /// <summary>
    /// True when a matching track is waiting, searching or downloading right now (not finished,
    /// failed or cancelled). The import preview uses it to label rows "Queued".
    /// </summary>
    public bool IsTrackInFlight(string? spotifyTrackId, string? artist, string? title)
    {
        static bool InFlight(DownloadContext d) => d.State is PlaylistTrackState.Pending
            or PlaylistTrackState.Searching or PlaylistTrackState.Queued or PlaylistTrackState.Downloading
            or PlaylistTrackState.Converting or PlaylistTrackState.Stalled or PlaylistTrackState.WaitingForConnection;

        lock (_collectionLock)
        {
            if (!string.IsNullOrEmpty(spotifyTrackId) &&
                _downloads.Any(d => InFlight(d) && d.Model.SpotifyTrackId == spotifyTrackId))
                return true;

            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return false;

            return _downloads.Any(d => InFlight(d) &&
                string.Equals(d.Model.Artist, artist, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(d.Model.Title, title, StringComparison.OrdinalIgnoreCase));
        }
    }

    public bool IsTrackAlreadyQueued(string? spotifyTrackId, string artist, string title)
    {
        // A Paused context that isn't a genuine user-initiated pause is an OnHold track hydrated
        // at startup (EnableMp3Fallback maps OnHold -> Paused). QueueTracksAsync already knows how
        // to safely resume that in place (see the existingCtx retry branch a few hundred lines up)
        // rather than duplicate it, so it must not be treated as "already queued" here — otherwise
        // GhostAcquisitionOrchestrator (the only caller of this method) skips it every sweep,
        // forever, without ever attempting discovery.
        static bool Blocks(DownloadContext d) => !(d.State == PlaylistTrackState.Paused && !d.Model.IsUserPaused);

        lock (_collectionLock)
        {
            if (!string.IsNullOrEmpty(spotifyTrackId))
            {
                if (_downloads.Any(d => d.Model.SpotifyTrackId == spotifyTrackId && Blocks(d)))
                    return true;
            }

            return _downloads.Any(d =>
                string.Equals(d.Model.Artist, artist, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(d.Model.Title, title, StringComparison.OrdinalIgnoreCase) &&
                Blocks(d));
        }
    }
}
