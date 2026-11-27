using System;
using System.IO;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Collections.ObjectModel;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.ViewModels.Library;
using SLSKDONET.Services.Models;
using SLSKDONET.Services;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Views;
using SLSKDONET.Events;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using SLSKDONET.Services.Playlist;
using SLSKDONET.Services.Library;
using SLSKDONET.Services.Library.Files;
using SLSKDONET.Services.Similarity;
using SLSKDONET.Models.Musical;
using SLSKDONET.Utils;

namespace SLSKDONET.ViewModels;


/// <summary>
/// Rekordbox/M3U export, smart escape, bulk rename, deduplication, and maintenance commands (#307).
/// </summary>
public partial class LibraryViewModel
{
    private async Task<bool> ConfirmRekordboxOverwriteAsync(
        Services.Library.Rekordbox.RekordboxExportConflictReport report)
    {
        var details = string.Join(Environment.NewLine, report.Tracks.Take(5).Select(track =>
            $"• {track.TrackLabel}: {string.Join(", ", track.Fields)}"));
        var remainder = report.Tracks.Count > 5
            ? $"{Environment.NewLine}• …and {report.Tracks.Count - 5} more track(s)."
            : string.Empty;
        var rekordboxEdits = report.TracksWithRekordboxCueEdits > 0
            ? $"{Environment.NewLine}{Environment.NewLine}Warning: {report.TracksWithRekordboxCueEdits} track(s) also have cue changes made in Rekordbox since ORBIT's last export. Replacing them will discard those changes from this XML."
            : string.Empty;
        var message =
            $"ORBIT has different values for {report.Tracks.Count} track(s) already present in this Rekordbox XML.{Environment.NewLine}{Environment.NewLine}" +
            $"{details}{remainder}{rekordboxEdits}{Environment.NewLine}{Environment.NewLine}" +
            "Choose ‘Replace with ORBIT data’ only if you intend to change those existing Rekordbox values when this XML is imported. " +
            "Choose ‘Keep Rekordbox data’ to preserve every existing value and export only new tracks and empty fields.";
        return await _dialogService.ConfirmAsync(
            "Update existing Rekordbox data?", message, "Replace with ORBIT data", "Keep Rekordbox data");
    }

    private async Task ExecuteExportPlaylistAsync(object? param)
    {
        if (param is not PlaylistJob project) return;

        try
        {
            var tracks = (await _libraryService.LoadPlaylistTracksAsync(project.Id)).ToList();

            // Ask user: copy files to USB/folder (with optional LF7 folder layout & tree/size preview),
            // XML only, or cancel entirely. Dismissing this dialog (X / Alt+F4 / Escape) returns null
            // here, which is handled the same as an explicit Cancel below.
            var options = await _dialogService.ShowExportPlaylistOptionsAsync(project.SourceTitle, tracks);
            if (options is null || options.Choice == Views.Avalonia.Dialogs.ExportPlaylistChoice.Cancelled)
                return;

            IsLoading = true;

            if (options.Choice == Views.Avalonia.Dialogs.ExportPlaylistChoice.CopyFilesAndXml)
            {
                var folder = await _dialogService.OpenFolderDialogAsync("Choose export folder or USB drive root");
                if (string.IsNullOrEmpty(folder)) return;

                int total = tracks.Count;
                Services.Export.ExportProgress? lastProgress = null;
                var progressHandler = new Progress<Services.Export.ExportProgress>(p =>
                {
                    lastProgress = p;
                    if (!p.IsComplete)
                        _notificationService.Show("Exporting…",
                            $"[{p.Copied}/{total}] {p.CurrentFile}" +
                            (p.Skipped > 0 ? $" · {p.Skipped} skipped" : ""),
                            NotificationType.Information);
                });

                await _exportOrchestrator.ExportAsync(
                    project.SourceTitle, tracks, folder,
                    Services.Export.ExportMode.FilesAndXml,
                    progressHandler, folderId: project.FolderId, confirmOverwrite: ConfirmRekordboxOverwriteAsync,
                    layoutMode: options.LayoutMode);

                // Use the orchestrator's own final tally rather than re-deriving "skipped" by
                // re-checking File.Exists here — that only ever caught missing-file skips, silently
                // missing every other reason a track can end up left out (copy verification
                // failure, exceeding FAT32's 4GB single-file limit, disk-full mid-export). Those
                // are exactly the failure modes a USB export needs to surface, not hide.
                int skipped = lastProgress?.Skipped ?? 0;
                var xmlPath = System.IO.Path.Combine(folder, "PIONEER", "rekordbox.xml");
                // A drive with only files + rekordbox.xml is not yet CDJ-ready — CDJs read
                // Rekordbox's own native device database (PDB+ANLZ), not this XML directly. The
                // XML has to go through Rekordbox desktop first (Preferences → Advanced →
                // rekordbox xml → import), and only Rekordbox's own device-export from there
                // actually prepares the drive for CDJ hardware. Spelling this out here so it isn't
                // discovered the hard way at a gig.
                _notificationService.Show("Export Complete",
                    $"{xmlPath}" +
                    (skipped > 0 ? $" · {skipped} track(s) skipped — see logs for why" : "") +
                    " · Next: import this XML into Rekordbox desktop, then use Rekordbox's own USB export to make this drive CDJ-ready.",
                    skipped > 0 ? NotificationType.Warning : NotificationType.Success);
            }
            else
            {
                var defaultName = $"{Utils.FilenameNormalizer.GetSafeFilename(project.SourceTitle)}.xml";
                var path = await _dialogService.SaveFileAsync("Export Rekordbox XML", defaultName, "xml");
                if (string.IsNullOrEmpty(path)) return;

                _notificationService.Show("Exporting", $"Saving '{project.SourceTitle}' to Rekordbox XML…", NotificationType.Information);
                await _exportOrchestrator.ExportAsync(
                    project.SourceTitle, tracks, path,
                    Services.Export.ExportMode.XmlOnly, folderId: project.FolderId, confirmOverwrite: ConfirmRekordboxOverwriteAsync);
                _notificationService.Show("Export Successful",
                    $"Playlist exported to {Path.GetFileName(path)}", NotificationType.Success);
            }
        }
        catch (OperationCanceledException)
        {
            _notificationService.Show("Export Cancelled", "Export was cancelled.", NotificationType.Warning);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export failed");
            _notificationService.Show("Export Failed", ex.Message, NotificationType.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ExecuteExportPlaylistM3uAsync(object? param)
    {
        if (param is not PlaylistJob project) return;
        try
        {
            var safeName = Utils.FilenameNormalizer.GetSafeFilename(project.SourceTitle);
            var path = await _dialogService.SaveFileAsync("Export Rekordbox M3U8 Playlist", $"{safeName}.m3u8", "m3u8");
            if (string.IsNullOrEmpty(path)) return;

            IsLoading = true;
            var tracks = (await _libraryService.LoadPlaylistTracksAsync(project.Id)).ToList();
            var result = await _exportService.ExportToM3uAsync(project.SourceTitle, tracks, path);
            var details = result.SkippedTrackProblems.Count == 0
                ? string.Empty
                : " Unavailable tracks were omitted: "
                    + string.Join("; ", result.SkippedTrackProblems.Take(3))
                    + (result.SkippedTrackProblems.Count > 3 ? "; …" : "");
            _notificationService.Show("M3U Export Complete",
                $"{result.Exported} track(s) exported to {Path.GetFileName(path)}" +
                (result.SkippedDuplicates > 0 ? $" · {result.SkippedDuplicates} duplicate path(s) skipped" : "") +
                (result.SkippedTrackProblems.Count > 0
                    ? $" · {result.SkippedTrackProblems.Count} unavailable track(s) skipped.{details}"
                    : ""),
                result.Skipped > 0 ? NotificationType.Warning : NotificationType.Success);
        }
        catch (OperationCanceledException)
        {
            _notificationService.Show("Export Cancelled", "Export was cancelled.", NotificationType.Warning);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "M3U export failed");
            _notificationService.Show("Export Failed", ex.Message, NotificationType.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ExecuteResetView()
    {
        // Reset view settings
        CurrentWorkspace = ActiveWorkspace.Selector;
        // Reset other view states
    }

    private void ExecuteSwitchWorkspace(ActiveWorkspace workspace)
    {
        CurrentWorkspace = workspace;
    }

    private void ExecuteSmartEscape()
    {
        // Close any open overlays in priority order
        if (IsOrphanedTracksVisible)
        {
            IsOrphanedTracksVisible = false;
        }
        else if (IsRemovalHistoryVisible)
        {
            IsRemovalHistoryVisible = false;
        }
        else if (IsSourcesOpen)
        {
            IsSourcesOpen = false;
        }
    }

    // ── Batch Action FAB implementations (Task 10.5) ────────────────────────

    private async Task ExecuteBatchTagEditAsync()
    {
        var selected = Tracks.SelectedTracks.ToList();
        if (selected.Count == 0) return;

        string? initialFileName = null;
        if (selected.Count == 1)
        {
            var singlePath = selected[0].Model?.ResolvedFilePath;
            if (!string.IsNullOrEmpty(singlePath))
                initialFileName = System.IO.Path.GetFileNameWithoutExtension(singlePath);
        }

        // Prefill the dialog with the selection's actual current values instead of leaving every
        // field blank — a field comes back null (shown as "multiple values") only when the
        // selected tracks disagree on it, which is impossible for a single track.
        string? CommonOrNull(Func<SLSKDONET.Models.PlaylistTrack, string?> selector)
        {
            var values = selected.Select(t => selector(t.Model) ?? string.Empty).Distinct().ToList();
            return values.Count == 1 ? values[0] : null;
        }

        var seed = new BatchTagEditSeed
        {
            Artist = CommonOrNull(m => m.Artist),
            Title = CommonOrNull(m => m.Title),
            Album = CommonOrNull(m => m.Album),
            Genre = CommonOrNull(m => m.PrimaryGenre),
            Year = CommonOrNull(m => m.ReleaseDate?.Year.ToString()),
            Bpm = CommonOrNull(m => m.BPM is > 0 ? m.BPM.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : null),
            Key = CommonOrNull(m => m.MusicalKey),
            Comments = CommonOrNull(m => m.Comments),
            Mood = CommonOrNull(m => m.MoodTag),
            TrackNumber = CommonOrNull(m => m.TrackNumber > 0 ? m.TrackNumber.ToString() : null),
            Rating = CommonOrNull(m => m.Rating > 0 ? m.Rating.ToString() : null),
        };

        var result = await _dialogService.ShowBatchTagEditDialogAsync(initialFileName, seed);
        if (result == null || !result.IsConfirmed) return;

        _logger.LogInformation("Batch tag edit for {Count} tracks starting.", selected.Count);

        DateTime? releaseDate = null;
        if (!string.IsNullOrWhiteSpace(result.Year) && int.TryParse(result.Year, out var year))
        {
            releaseDate = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        double? bpm = null;
        if (!string.IsNullOrWhiteSpace(result.Bpm) && SLSKDONET.Utils.NumFormat.TryParse(result.Bpm, out var parsedBpm) && parsedBpm > 0)
        {
            bpm = parsedBpm;
        }

        int? trackNumber = null;
        if (!string.IsNullOrWhiteSpace(result.TrackNumber) && int.TryParse(result.TrackNumber, out var parsedTrackNumber) && parsedTrackNumber > 0)
        {
            trackNumber = parsedTrackNumber;
        }

        int? rating = null;
        if (!string.IsNullOrWhiteSpace(result.Rating) && int.TryParse(result.Rating, out var parsedRating) && parsedRating is >= 0 and <= 5)
        {
            rating = parsedRating;
        }

        int tagUpdateSuccessCount = 0;
        int dbUpdateSuccessCount = 0;
        int renameFailedCount = 0;
        var failedTagWriteTracks = new List<string>();
        var failedRenameTracks = new List<string>();

        await Task.Run(async () =>
        {
            await using var context = _dbFactory.CreateDbContext();

            // Preload both lookups in two batched queries instead of one FirstOrDefaultAsync per
            // track per table (2N round-trips for an N-track selection).
            var trackIds = selected.Select(t => t.Model.Id).ToList();
            var trackHashes = selected.Select(t => t.Model.TrackUniqueHash).Where(h => !string.IsNullOrEmpty(h)).ToList();

            var dbTracksById = await context.PlaylistTracks
                .Where(t => trackIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id);
            var dbEntriesByHash = await context.LibraryEntries
                .Where(e => trackHashes.Contains(e.UniqueHash))
                .ToDictionaryAsync(e => e.UniqueHash);

            foreach (var trackVm in selected)
            {
                var track = trackVm.Model;
                bool fileUpdated = false;

                // 1. Update physical file tags — routed through the atomic, verified ITaggerService
                // (temp-file write + post-write format check) instead of writing TagLib directly in
                // place, so a bad write can't leave a half-tagged or corrupted file on disk. As a
                // bonus this also writes Key/TrackNumber, which the old inline path explicitly
                // skipped (neither has a slot in TagLib's plain Tag API, but ITaggerService writes
                // Key via InitialKey and TrackNumber via the Metadata dictionary).
                if (track.Status == TrackStatus.Downloaded && !string.IsNullOrEmpty(track.ResolvedFilePath) && System.IO.File.Exists(track.ResolvedFilePath))
                {
                    try
                    {
                        var taggerTrack = new Track
                        {
                            Title = result.Title,
                            Artist = result.Artist,
                            Album = result.Album,
                            Metadata = new Dictionary<string, object>(),
                        };
                        if (!string.IsNullOrWhiteSpace(result.Genre)) taggerTrack.Metadata["Genre"] = result.Genre;
                        if (!string.IsNullOrWhiteSpace(result.Year)) taggerTrack.Metadata["Year"] = result.Year;
                        if (bpm.HasValue) taggerTrack.Metadata["BPM"] = bpm.Value;
                        if (!string.IsNullOrWhiteSpace(result.Key)) taggerTrack.Metadata["MusicalKey"] = result.Key;
                        if (!string.IsNullOrWhiteSpace(result.Comments)) taggerTrack.Metadata["Comment"] = result.Comments;
                        if (!string.IsNullOrWhiteSpace(result.TrackNumber)) taggerTrack.Metadata["TrackNumber"] = result.TrackNumber;
                        // Mood is intentionally not written to the physical file — it has no
                        // standard, widely-supported ID3/tag slot via TagLib's high-level Tag API,
                        // so it stays database-only.

                        fileUpdated = await _taggerService.TagFileAsync(taggerTrack, track.ResolvedFilePath);
                        if (fileUpdated)
                        {
                            tagUpdateSuccessCount++;
                        }
                        else
                        {
                            failedTagWriteTracks.Add($"{track.Artist} - {track.Title}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to write tag to physical file: {Path}", track.ResolvedFilePath);
                        failedTagWriteTracks.Add($"{track.Artist} - {track.Title}");
                    }
                }

                // 2. Update Database Entity (PlaylistTrackEntity)
                try
                {
                    // Tag fields (Artist/Title/Album/Genre) route through the shared
                    // TrackMetadataWriter so the three denormalized tables stay in sync; the
                    // analysis/manual fields (BPM, key, mood, …) stay inline below.
                    var tagChanges = new List<Services.Library.Maintenance.TrackMetadataWriter.Change>();
                    if (!string.IsNullOrWhiteSpace(result.Artist)) tagChanges.Add(new(TagField.Artist, result.Artist));
                    if (!string.IsNullOrWhiteSpace(result.Title)) tagChanges.Add(new(TagField.Title, result.Title));
                    if (!string.IsNullOrWhiteSpace(result.Album)) tagChanges.Add(new(TagField.Album, result.Album));
                    if (!string.IsNullOrWhiteSpace(result.Genre)) tagChanges.Add(new(TagField.Genre, result.Genre));
                    var useWriter = _metadataWriter != null && !string.IsNullOrEmpty(track.TrackUniqueHash) && tagChanges.Count > 0;
                    if (useWriter)
                        await _metadataWriter!.ApplyAsync(context, track.TrackUniqueHash, tagChanges);

                    dbTracksById.TryGetValue(track.Id, out var dbTrack);
                    if (dbTrack != null)
                    {
                        if (!useWriter)
                        {
                            if (!string.IsNullOrWhiteSpace(result.Artist)) dbTrack.Artist = result.Artist;
                            if (!string.IsNullOrWhiteSpace(result.Title)) dbTrack.Title = result.Title;
                            if (!string.IsNullOrWhiteSpace(result.Album)) dbTrack.Album = result.Album;
                            if (!string.IsNullOrWhiteSpace(result.Genre))
                            {
                                dbTrack.PrimaryGenre = result.Genre;
                                dbTrack.Genres = System.Text.Json.JsonSerializer.Serialize(new List<string> { result.Genre });
                            }
                        }
                        if (releaseDate.HasValue) dbTrack.ReleaseDate = releaseDate;
                        if (bpm.HasValue) dbTrack.BPM = bpm;
                        if (!string.IsNullOrWhiteSpace(result.Key)) dbTrack.MusicalKey = result.Key;
                        if (!string.IsNullOrWhiteSpace(result.Comments)) dbTrack.Comments = result.Comments;
                        if (!string.IsNullOrWhiteSpace(result.Mood)) dbTrack.MoodTag = result.Mood;
                        if (trackNumber.HasValue) dbTrack.TrackNumber = trackNumber.Value;

                        context.PlaylistTracks.Update(dbTrack);
                    }

                    // 2b. Rating is global-by-hash (shared across every row for this track), so it
                    // goes through the existing single-track rating path instead of the two entity
                    // updates above.
                    if (rating.HasValue && !string.IsNullOrEmpty(track.TrackUniqueHash))
                    {
                        await _libraryService.UpdateRatingAsync(track.TrackUniqueHash, rating.Value);
                    }

                    // 3. Update Library Entry if exists
                    if (!string.IsNullOrEmpty(track.TrackUniqueHash))
                    {
                        dbEntriesByHash.TryGetValue(track.TrackUniqueHash, out var dbLibraryEntry);
                        if (dbLibraryEntry != null)
                        {
                            if (!useWriter)
                            {
                                if (!string.IsNullOrWhiteSpace(result.Artist)) dbLibraryEntry.Artist = result.Artist;
                                if (!string.IsNullOrWhiteSpace(result.Title)) dbLibraryEntry.Title = result.Title;
                                if (!string.IsNullOrWhiteSpace(result.Album)) dbLibraryEntry.Album = result.Album;
                                if (!string.IsNullOrWhiteSpace(result.Genre))
                                {
                                    dbLibraryEntry.PrimaryGenre = result.Genre;
                                    dbLibraryEntry.Genres = System.Text.Json.JsonSerializer.Serialize(new List<string> { result.Genre });
                                }
                            }
                            if (releaseDate.HasValue) dbLibraryEntry.ReleaseDate = releaseDate;
                            if (bpm.HasValue) dbLibraryEntry.BPM = bpm;
                            if (!string.IsNullOrWhiteSpace(result.Key)) dbLibraryEntry.MusicalKey = result.Key;
                            if (!string.IsNullOrWhiteSpace(result.Comments)) dbLibraryEntry.Comments = result.Comments;
                            if (!string.IsNullOrWhiteSpace(result.Mood)) dbLibraryEntry.MoodTag = result.Mood;

                            context.LibraryEntries.Update(dbLibraryEntry);
                        }
                    }

                    // 3b. File rename (single-track only, only when NewFileName differs from current)
                    if (selected.Count == 1 && !string.IsNullOrWhiteSpace(result.NewFileName))
                    {
                        var sourcePath = track.ResolvedFilePath;
                        if (!string.IsNullOrEmpty(sourcePath) && System.IO.File.Exists(sourcePath))
                        {
                            var dir = System.IO.Path.GetDirectoryName(sourcePath)!;
                            var ext = System.IO.Path.GetExtension(sourcePath);
                            var safeName = result.NewFileName
                                .Replace('/', '_').Replace('\\', '_').Replace(':', '_').Trim();
                            if (!string.Equals(safeName,
                                    System.IO.Path.GetFileNameWithoutExtension(sourcePath),
                                    StringComparison.OrdinalIgnoreCase)
                                && !string.IsNullOrWhiteSpace(safeName))
                            {
                                var destPath = System.IO.Path.Combine(dir, safeName + ext);
                                if (!System.IO.File.Exists(destPath))
                                {
                                    try
                                    {
                                        System.IO.File.Move(sourcePath, destPath);
                                        track.ResolvedFilePath = destPath;

                                        var dbTrackRename = await context.PlaylistTracks.FirstOrDefaultAsync(t => t.Id == track.Id);
                                        if (dbTrackRename != null)
                                        {
                                            dbTrackRename.ResolvedFilePath = destPath;
                                            context.PlaylistTracks.Update(dbTrackRename);
                                        }

                                        if (!string.IsNullOrEmpty(track.TrackUniqueHash))
                                        {
                                            var dbEntryRename = await context.LibraryEntries.FirstOrDefaultAsync(e => e.UniqueHash == track.TrackUniqueHash);
                                            if (dbEntryRename != null)
                                            {
                                                dbEntryRename.FilePath = destPath;
                                                context.LibraryEntries.Update(dbEntryRename);
                                            }
                                        }

                                        _logger.LogInformation("Renamed file: {Old} -> {New}", sourcePath, destPath);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, "Failed to rename file: {Path}", sourcePath);
                                        renameFailedCount++;
                                        failedRenameTracks.Add($"{track.Artist} - {track.Title}");
                                    }
                                }
                                else
                                {
                                    _logger.LogWarning("Rename skipped: target file already exists: {Path}", destPath);
                                }
                            }
                        }
                    }

                    dbUpdateSuccessCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to update tags in database for track ID: {Id}", track.Id);
                    // Don't apply the edit to the visible row below — its DB write failed, so
                    // showing it as "applied" would just silently revert on the next reload.
                    continue;
                }

                // 4. Update the ViewModels dynamically in the UI thread
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!string.IsNullOrWhiteSpace(result.Artist))
                    {
                        trackVm.Artist = result.Artist;
                    }
                    if (!string.IsNullOrWhiteSpace(result.Title))
                    {
                        trackVm.Title = result.Title;
                    }
                    if (!string.IsNullOrWhiteSpace(result.Album))
                    {
                        trackVm.Album = result.Album;
                    }
                    if (!string.IsNullOrWhiteSpace(result.Genre))
                    {
                        trackVm.Model.PrimaryGenre = result.Genre;
                        trackVm.Model.Genres = System.Text.Json.JsonSerializer.Serialize(new List<string> { result.Genre });
                    }
                    if (releaseDate.HasValue)
                    {
                        trackVm.Model.ReleaseDate = releaseDate;
                    }
                    if (bpm.HasValue)
                    {
                        trackVm.Model.BPM = bpm;
                    }
                    if (!string.IsNullOrWhiteSpace(result.Key))
                    {
                        trackVm.Model.MusicalKey = result.Key;
                    }
                    if (!string.IsNullOrWhiteSpace(result.Comments))
                    {
                        trackVm.Model.Comments = result.Comments;
                    }
                    if (!string.IsNullOrWhiteSpace(result.Mood))
                    {
                        trackVm.Model.MoodTag = result.Mood;
                    }
                    if (trackNumber.HasValue)
                    {
                        trackVm.Model.TrackNumber = trackNumber.Value;
                    }
                    if (rating.HasValue)
                    {
                        // Set the model directly rather than trackVm.Rating — that setter fires its
                        // own UpdateRatingAsync call, which would double-write the DB update already
                        // done above.
                        trackVm.Model.Rating = rating.Value;
                    }
                    trackVm.NotifyMetadataChanged();
                });
            }

            await context.SaveChangesAsync();
        });

        await MarkUserEditedForEnrichmentAsync(selected.Select(t => t.Model.TrackUniqueHash), result.Artist, result.Title,
            result.Album, result.Genre, releaseDate.HasValue);

        var failedCount = selected.Count - dbUpdateSuccessCount;
        var message = $"Successfully edited metadata tags for {dbUpdateSuccessCount} track(s) in DB (and {tagUpdateSuccessCount} physical files).";
        if (failedCount > 0)
            message += $" {failedCount} track(s) failed and were not changed — see logs for details.";
        if (failedTagWriteTracks.Count > 0)
            message += $" File tag write failed for: {FormatFailedTrackList(failedTagWriteTracks)} — the DB was still updated, so it may no longer match the file's tags.";
        if (renameFailedCount > 0)
            message += $" File rename failed for: {FormatFailedTrackList(failedRenameTracks)} — tags were still updated, but the file(s) on disk keep their old name.";

        _notificationService.Show(
            "Tags Updated",
            message,
            failedCount > 0 || renameFailedCount > 0 || failedTagWriteTracks.Count > 0 ? NotificationType.Warning : NotificationType.Success);
    }

    /// <summary>Renders up to 5 failed-track names for a notification, collapsing the rest into a count.</summary>
    private static string FormatFailedTrackList(List<string> names)
    {
        const int max = 5;
        var shown = string.Join(", ", names.Take(max));
        return names.Count > max ? $"{shown} (+{names.Count - max} more)" : shown;
    }

    /// <summary>
    /// Renames every selected track's physical file according to a user-supplied pattern (e.g.
    /// "{artist} - {title}") — unlike the single-track exact-filename rename in the tag-edit
    /// dialog, this applies across the whole selection. Same collision rule as that rename: skip +
    /// report rather than overwrite.
    /// </summary>
    /// <summary>
    /// Records the fields the user just edited as user-owned (MetadataProvenance source "user"), so
    /// library enrichment never overwrites them. A failure is logged and surfaced; the edit itself
    /// already succeeded.
    /// </summary>
    private async Task MarkUserEditedForEnrichmentAsync(IEnumerable<string?> hashes, string? artist, string? title, string? album, string? genre, bool year)
    {
        var fields = new List<string>();
        if (!string.IsNullOrWhiteSpace(artist)) fields.Add(Services.Enrichment.MetadataMerger.Fields.Artist);
        if (!string.IsNullOrWhiteSpace(title)) fields.Add(Services.Enrichment.MetadataMerger.Fields.Title);
        if (!string.IsNullOrWhiteSpace(album)) fields.Add(Services.Enrichment.MetadataMerger.Fields.Album);
        if (!string.IsNullOrWhiteSpace(genre))
        {
            fields.Add(Services.Enrichment.MetadataMerger.Fields.Genre);
            _eventBus?.Publish(new Services.Genre.GenreLabelsChangedEvent()); // the personal genre head retrains (GA11 #256)
        }
        if (year) fields.Add(Services.Enrichment.MetadataMerger.Fields.ReleaseDate);
        var list = hashes.Where(h => !string.IsNullOrEmpty(h)).Select(h => h!).Distinct().ToList();
        if (fields.Count == 0 || list.Count == 0) return;
        if (_serviceProvider?.GetService(typeof(Services.Enrichment.EnrichmentStore)) is not Services.Enrichment.EnrichmentStore store) return;
        try
        {
            await store.MarkUserEditedAsync(list, fields);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record user-edited fields for {Count} tracks", list.Count);
            _notificationService.Show("Metadata edit", "Your edit was saved, but enrichment may refill these fields later: " + ex.Message, NotificationType.Warning);
        }
    }

    private async Task ExecuteBulkRenameAsync()
    {
        var selected = Tracks.SelectedTracks.ToList();
        if (selected.Count == 0) return;

        var previewTracks = selected.Take(3).Select(t => new BulkRenamePreviewTrack
        {
            Artist = t.Model.Artist ?? "",
            Title = t.Model.Title ?? "",
            Album = t.Model.Album ?? "",
            TrackNumber = t.Model.TrackNumber > 0 ? t.Model.TrackNumber.ToString() : "",
            Year = t.Model.ReleaseDate?.Year.ToString() ?? "",
            Genre = t.Model.Genres ?? "",
            Extension = string.IsNullOrEmpty(t.Model.ResolvedFilePath) ? "" : System.IO.Path.GetExtension(t.Model.ResolvedFilePath),
        }).ToList();

        var result = await _dialogService.ShowBulkRenameDialogAsync(selected.Count, previewTracks);
        if (result == null || !result.IsConfirmed || string.IsNullOrWhiteSpace(result.Pattern)) return;

        _logger.LogInformation("Bulk rename for {Count} tracks starting with pattern '{Pattern}'.", selected.Count, result.Pattern);

        // Build the relocation requests + a hash→label map for reporting (#344: single move path).
        var requests = new List<RelocationRequest>();
        var labelByHash = new Dictionary<string, string>(StringComparer.Ordinal);
        var skippedTracks = new List<string>();

        foreach (var trackVm in selected)
        {
            var track = trackVm.Model;
            var label = $"{track.Artist} - {track.Title}";
            if (string.IsNullOrEmpty(track.ResolvedFilePath) || !System.IO.File.Exists(track.ResolvedFilePath))
                continue;
            if (string.IsNullOrEmpty(track.TrackUniqueHash))
                continue;

            var resolvedBase = BulkRenameViewModel.Resolve(
                result.Pattern,
                track.Artist ?? "", track.Title ?? "", track.Album ?? "",
                track.TrackNumber > 0 ? track.TrackNumber.ToString() : "",
                track.ReleaseDate?.Year.ToString() ?? "",
                track.Genres ?? "");

            var safeName = resolvedBase.Replace('/', '_').Replace('\\', '_').Replace(':', '_').Trim();
            if (string.IsNullOrWhiteSpace(safeName))
            {
                skippedTracks.Add(label);
                continue;
            }

            var dir = System.IO.Path.GetDirectoryName(track.ResolvedFilePath)!;
            var ext = System.IO.Path.GetExtension(track.ResolvedFilePath);
            var destPath = System.IO.Path.Combine(dir, safeName + ext);

            if (string.Equals(destPath, track.ResolvedFilePath, StringComparison.OrdinalIgnoreCase))
                continue; // already matches the pattern — nothing to do

            requests.Add(new RelocationRequest(track.TrackUniqueHash, track.ResolvedFilePath, destPath));
            labelByHash[track.TrackUniqueHash] = label;
        }

        var svc = _fileRelocationService;
        if (svc == null)
        {
            _logger.LogWarning("Bulk rename skipped: FileRelocationService is not available.");
            return;
        }

        if (requests.Count == 0)
        {
            _notificationService.Show("Bulk Rename",
                skippedTracks.Count > 0 ? $"No files renamed. Skipped: {FormatFailedTrackList(skippedTracks)}." : "No files to rename.",
                skippedTracks.Count > 0 ? NotificationType.Warning : NotificationType.Success);
            return;
        }

        // Plan (validates + LF4 lock/linked guard) then execute (moves + journals + refreshes via event).
        var plan = await svc.PlanAsync(requests);
        foreach (var problem in plan.Problems)
            skippedTracks.Add(labelByHash.TryGetValue(problem.Hash, out var lbl) ? lbl : problem.Hash);

        var outcome = await svc.ExecuteAsync(plan);
        foreach (var failure in outcome.Failures)
            skippedTracks.Add(labelByHash.TryGetValue(failure.Hash, out var lbl) ? lbl : failure.Hash);

        var message = $"Renamed {outcome.Moved} file(s).";
        if (skippedTracks.Count > 0)
            message += $" Skipped: {FormatFailedTrackList(skippedTracks)} — destination already existed, was locked, or the rename failed. See logs for details.";

        _notificationService.Show(
            "Bulk Rename",
            message,
            skippedTracks.Count > 0 ? NotificationType.Warning : NotificationType.Success);
    }

    /// <summary>
    /// Moves or copies every selected track's physical file into a chosen destination folder.
    /// Move relocates the file and updates ORBIT's stored path (the reorganization case); Copy
    /// duplicates the file for external use (e.g. staging a USB stick) and leaves the DB untouched
    /// — a linked-library "copy" has no single unambiguous meaning otherwise, so this deliberately
    /// doesn't create a second tracked library entry for the copy.
    /// </summary>
    private async Task ExecuteBulkMoveOrCopyAsync()
    {
        var selected = Tracks.SelectedTracks.ToList();
        if (selected.Count == 0) return;

        var modeResult = await _dialogService.ShowBulkMoveOrCopyDialogAsync(selected.Count);
        if (modeResult == null || !modeResult.IsConfirmed) return;

        var destinationFolder = await _dialogService.OpenFolderDialogAsync(
            modeResult.IsCopy ? "Copy Selected Tracks To…" : "Move Selected Tracks To…");
        if (string.IsNullOrWhiteSpace(destinationFolder)) return;

        _logger.LogInformation(
            "Bulk {Mode} for {Count} tracks to '{Destination}' starting.",
            modeResult.IsCopy ? "copy" : "move", selected.Count, destinationFolder);

        var skippedTracks = new List<string>();

        if (modeResult.IsCopy)
        {
            // Copy duplicates the file for external use and leaves the DB untouched (#344: a linked-
            // library "copy" has no single unambiguous meaning, so no second entry is created).
            int copied = 0;
            foreach (var trackVm in selected)
            {
                var track = trackVm.Model;
                if (string.IsNullOrEmpty(track.ResolvedFilePath) || !System.IO.File.Exists(track.ResolvedFilePath))
                    continue;
                var destPath = System.IO.Path.Combine(destinationFolder, System.IO.Path.GetFileName(track.ResolvedFilePath));
                if (string.Equals(System.IO.Path.GetFullPath(destPath), System.IO.Path.GetFullPath(track.ResolvedFilePath), StringComparison.OrdinalIgnoreCase))
                    continue; // already there
                try
                {
                    System.IO.File.Copy(track.ResolvedFilePath, destPath);
                    copied++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Bulk copy failed for {Path}", track.ResolvedFilePath);
                    skippedTracks.Add($"{track.Artist} - {track.Title}");
                }
            }

            var copyMsg = $"Copied {copied} file(s) to {destinationFolder}.";
            if (skippedTracks.Count > 0)
                copyMsg += $" Skipped: {FormatFailedTrackList(skippedTracks)} — the operation failed. See logs for details.";
            _notificationService.Show("Bulk Copy", copyMsg, skippedTracks.Count > 0 ? NotificationType.Warning : NotificationType.Success);
            return;
        }

        // Move: single path via FileRelocationService (#344) — moves + updates all three path
        // columns + journals + publishes the refresh event.
        var svc = _fileRelocationService;
        if (svc == null)
        {
            _logger.LogWarning("Bulk move skipped: FileRelocationService is not available.");
            return;
        }

        var requests = new List<RelocationRequest>();
        var labelByHash = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var trackVm in selected)
        {
            var track = trackVm.Model;
            var label = $"{track.Artist} - {track.Title}";
            if (string.IsNullOrEmpty(track.ResolvedFilePath) || !System.IO.File.Exists(track.ResolvedFilePath))
                continue;
            if (string.IsNullOrEmpty(track.TrackUniqueHash))
                continue;
            var destPath = System.IO.Path.Combine(destinationFolder, System.IO.Path.GetFileName(track.ResolvedFilePath));
            if (string.Equals(System.IO.Path.GetFullPath(destPath), System.IO.Path.GetFullPath(track.ResolvedFilePath), StringComparison.OrdinalIgnoreCase))
                continue; // already there
            requests.Add(new RelocationRequest(track.TrackUniqueHash, track.ResolvedFilePath, destPath));
            labelByHash[track.TrackUniqueHash] = label;
        }

        if (requests.Count == 0)
        {
            _notificationService.Show("Bulk Move", "No files to move.", NotificationType.Success);
            return;
        }

        var plan = await svc.PlanAsync(requests);
        foreach (var problem in plan.Problems)
            skippedTracks.Add(labelByHash.TryGetValue(problem.Hash, out var lbl) ? lbl : problem.Hash);
        var outcome = await svc.ExecuteAsync(plan);
        foreach (var failure in outcome.Failures)
            skippedTracks.Add(labelByHash.TryGetValue(failure.Hash, out var lbl) ? lbl : failure.Hash);

        var msg = $"Moved {outcome.Moved} file(s) to {destinationFolder}.";
        if (skippedTracks.Count > 0)
            msg += $" Skipped: {FormatFailedTrackList(skippedTracks)} — destination already existed, was locked, or the move failed. See logs for details.";
        _notificationService.Show("Bulk Move", msg, skippedTracks.Count > 0 ? NotificationType.Warning : NotificationType.Success);
    }

    /// <summary>
    /// Finds and permanently removes every "ID" placeholder track (DJ-tracklist shorthand for an
    /// unidentified track, e.g. "Basstripper - ID") — <see cref="Utils.CommentTracklistParser"/>
    /// drops these at import time now, but that can't retroactively clean tracklists imported
    /// before the filter existed, so leftovers can still be sitting in the library.
    /// </summary>
    private async Task ExecuteRemoveUnidentifiedTracksAsync()
    {
        var cleanupService = _serviceProvider?.GetService(typeof(Services.UnidentifiedTrackCleanupService))
            as Services.UnidentifiedTrackCleanupService;
        if (cleanupService == null)
        {
            _notificationService.Show("Remove Unidentified Tracks", "Cleanup service unavailable.", NotificationType.Error);
            return;
        }

        bool confirm = await _dialogService.ConfirmAsync(
            "Remove Unidentified (\"ID\") Tracks",
            "This permanently deletes every track titled \"ID\" — a DJ-tracklist placeholder meaning the real track was never identified — from every playlist and the library, including any downloaded file on disk. This cannot be undone. Continue?");
        if (!confirm) return;

        try
        {
            var result = await cleanupService.RemoveAllAsync();
            _notificationService.Show(
                "Remove Unidentified Tracks",
                result.TracksRemoved == 0 ? "No unidentified (\"ID\") tracks found." : result.Summary,
                result.HasErrors ? NotificationType.Warning : NotificationType.Success);

            if (result.TracksRemoved > 0)
            {
                await ExecuteRefreshLibraryAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove unidentified tracks");
            _notificationService.Show("Remove Unidentified Tracks", $"Failed: {ex.Message}", NotificationType.Error);
        }
    }

    /// <summary>
    /// Cleans up two confirmed duplicate-data bugs: the same track inserted multiple times into
    /// the same playlist, and the same file registered twice in the library under two different
    /// hash formats (legacy vs. current). See <see cref="Services.DuplicateTrackCleanupService"/>.
    /// Fresh imports and AddTracksToProjectAsync no longer create new duplicates going forward —
    /// this is the one-off correction for rows already affected.
    /// </summary>
    private async Task ExecuteRemoveDuplicateTracksAsync()
    {
        var cleanupService = _serviceProvider?.GetService(typeof(Services.DuplicateTrackCleanupService))
            as Services.DuplicateTrackCleanupService;
        if (cleanupService == null)
        {
            _notificationService.Show("Remove Duplicate Tracks", "Cleanup service unavailable.", NotificationType.Error);
            return;
        }

        bool confirm = await _dialogService.ConfirmAsync(
            "Remove Duplicate Tracks",
            "This merges duplicate library entries and removes duplicate track rows within playlists (keeping the downloaded/earliest copy of each). A safety backup of the database is taken first. This cannot be undone otherwise. Continue?");
        if (!confirm) return;

        try
        {
            var result = await cleanupService.RunAsync();
            var noneFound = !result.Aborted && result.LibraryEntriesMerged == 0 && result.PlaylistRowsRemoved == 0;
            _notificationService.Show(
                "Remove Duplicate Tracks",
                noneFound ? "No duplicate tracks found." : result.Summary,
                result.Aborted || result.HasErrors ? NotificationType.Warning : NotificationType.Success);

            if (result.PlaylistRowsRemoved > 0 || result.LibraryEntriesMerged > 0)
            {
                await ExecuteRefreshLibraryAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove duplicate tracks");
            _notificationService.Show("Remove Duplicate Tracks", $"Failed: {ex.Message}", NotificationType.Error);
        }
    }

    /// <summary>
    /// Fixes tracks stuck showing "FILE MISSING" in the Track Inspector even though the file
    /// genuinely exists on disk (and is often already fully analyzed — waveform, BPM, etc. all
    /// present) — reported directly by the user from a screenshot. Root cause: some "file already
    /// exists, skip the actual download" fast paths in DownloadManager didn't always advance
    /// AvailabilityState past Ghost the way a full download completion does, leaving existing rows
    /// stuck (now fixed going forward — this is the one-off correction for rows already affected).
    /// </summary>
    private async Task ExecuteFixMissingFileFlagsAsync()
    {
        var reconcileService = _serviceProvider?.GetService(typeof(Services.AvailabilityStateReconciliationService))
            as Services.AvailabilityStateReconciliationService;
        if (reconcileService == null)
        {
            _notificationService.Show("Fix Missing-File Flags", "Reconciliation service unavailable.", NotificationType.Error);
            return;
        }

        try
        {
            var result = await reconcileService.ReconcileAsync();
            _notificationService.Show(
                "Fix Missing-File Flags",
                result.Summary,
                result.TotalFixed > 0 ? NotificationType.Success : NotificationType.Information);

            if (result.TotalFixed > 0)
            {
                await ExecuteRefreshLibraryAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reconcile missing-file flags");
            _notificationService.Show("Fix Missing-File Flags", $"Failed: {ex.Message}", NotificationType.Error);
        }
    }

    /// <summary>
    /// One-time sweep fixing two stacked gaps behind the Inspector's TRACK DETAILS section showing
    /// "—"/"UNKNOWN" even on already-analysed tracks: (1) a TagLib duration probe for tracks that
    /// predate PostDownloadDurationCaptureService — most Soulseek downloads never had their real
    /// duration read, only Bitrate/Format; (2) Bitrate/Format on PlaylistTracks rows that never
    /// inherited them from the matching LibraryEntries row (DatabaseService.SavePlaylistJobWithTracksAsync
    /// was missing that field entirely). Reported directly by the user via screenshot.
    /// </summary>
    /// <summary>
    /// Starts the shared loudness backfill (the same job as the Analysis page's "Measure loudness"
    /// card) and opens the Analysis page, where its progress and Cancel live.
    /// </summary>
    private void ExecuteMeasureMissingLoudness()
    {
        if (_serviceProvider?.GetService(typeof(LoudnessBackfillViewModel)) is not LoudnessBackfillViewModel backfill)
        {
            _notificationService.Show("Measure loudness", "The loudness backfill is unavailable.", NotificationType.Error);
            return;
        }

        backfill.Start();
        _navigationService.NavigateTo("Analysis");
    }

    /// <summary>
    /// Starts library metadata enrichment (the Analysis page's Enrichment card) and opens the
    /// Analysis page, where its progress, per-source status and Pause/Cancel live.
    /// </summary>
    private async Task ExecuteEnrichLibraryMetadataAsync()
    {
        if (_serviceProvider?.GetService(typeof(EnrichmentViewModel)) is not EnrichmentViewModel enrichment)
        {
            _notificationService.Show("Enrich library", "Library enrichment is unavailable.", NotificationType.Error);
            return;
        }
        try
        {
            await enrichment.StartAsync();
            _navigationService.NavigateTo("Analysis");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not start library enrichment");
            _notificationService.Show("Enrich library", "Could not start: " + ex.Message, NotificationType.Error);
        }
    }

    private async Task ExecuteBackfillDurationsAsync()
    {
        var backfillService = _serviceProvider?.GetService(typeof(Services.DurationBackfillService))
            as Services.DurationBackfillService;
        if (backfillService == null)
        {
            _notificationService.Show("Backfill Missing Durations", "Backfill service unavailable.", NotificationType.Error);
            return;
        }

        try
        {
            var result = await backfillService.BackfillAsync();
            _notificationService.Show(
                "Backfill Missing Track Metadata",
                result.Summary,
                result.TotalFixed > 0 ? NotificationType.Success : NotificationType.Information);

            if (result.TotalFixed > 0)
            {
                await ExecuteRefreshLibraryAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to backfill missing track metadata");
            _notificationService.Show("Backfill Missing Track Metadata", $"Failed: {ex.Message}", NotificationType.Error);
        }
    }

    /// <summary>
    /// Reorders every downloaded track in a playlist by similarity and "double drop" DJ-mix
    /// compatibility (close BPM + harmonically compatible key + similar drop-section sonics),
    /// then persists the new order. Reuses PlaylistIntelligenceService.ReorderAsync — the same
    /// engine Flow Builder's "Suggest Flow" already uses — with the new optional double-drop
    /// weighting so this one-click entry point, unlike Flow Builder's multi-step review UI,
    /// favors beatmatchable neighbor pairs rather than just general similarity.
    /// </summary>
    private async Task ExecuteAutoArrangePlaylistAsync(PlaylistJob? job)
    {
        if (job == null) return;

        try
        {
            var tracks = await _libraryService.LoadPlaylistTracksAsync(job.Id);
            // Not-yet-downloaded tracks have no file to derive fingerprint/BPM data from, so they
            // can't take part in similarity scoring. They aren't passed to SaveTrackOrderAsync at
            // all: a partial list is a permutation, so those rows keep the slots they already
            // occupy (and the playlist still ends up renumbered 1..N with no duplicate positions).
            var eligible = tracks
                .Where(t => !string.IsNullOrEmpty(t.ResolvedFilePath) && File.Exists(t.ResolvedFilePath))
                .ToList();
            var skippedNotDownloaded = tracks.Count - eligible.Count;

            // The same physical track can legitimately appear more than once in a set (an
            // intentional replay/reprise) — dedupe by hash for the reorder computation itself
            // (ReorderAsync also dedupes internally, but building lookups keyed by hash from a
            // list containing duplicates throws), then apply the resulting position to every
            // instance sharing that hash so repeats move together rather than being dropped.
            var distinctEligible = eligible
                .GroupBy(t => t.TrackUniqueHash, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();

            if (distinctEligible.Count < 3)
            {
                _notificationService.Show("Auto-Arrange",
                    "Need at least 3 distinct downloaded tracks in this playlist to arrange.",
                    NotificationType.Warning);
                return;
            }
            if (distinctEligible.Count > PlaylistIntelligenceService.MaxReorderTracks)
            {
                _notificationService.Show("Auto-Arrange",
                    $"Playlist has {distinctEligible.Count} distinct tracks — auto-arrange supports up to {PlaylistIntelligenceService.MaxReorderTracks}.",
                    NotificationType.Warning);
                return;
            }

            bool confirm = await _dialogService.ConfirmAsync(
                "Auto-Arrange Playlist",
                $"Reorder {distinctEligible.Count} downloaded tracks in '{job.SourceTitle}' by similarity and double-drop compatibility?" +
                (skippedNotDownloaded > 0 ? $" {skippedNotDownloaded} not-yet-downloaded track(s) will be left in place." : "") +
                " This replaces the current track order.");
            if (!confirm) return;

            var intelligenceService = _serviceProvider?.GetService(typeof(PlaylistIntelligenceService))
                as PlaylistIntelligenceService;
            if (intelligenceService == null)
            {
                _notificationService.Show("Auto-Arrange", "Reorder service unavailable.", NotificationType.Error);
                return;
            }

            var bpmByHash = distinctEligible
                .Where(t => t.BPM is > 0)
                .ToDictionary(t => t.TrackUniqueHash, t => (float)t.BPM!.Value, StringComparer.Ordinal);

            var metadataByHash = distinctEligible
                .ToDictionary(t => t.TrackUniqueHash, t => new ReorderTrackMetadata(t.Artist, t.DetectedSubGenre), StringComparer.Ordinal);

            var result = await intelligenceService.ReorderAsync(
                distinctEligible.Select(t => t.TrackUniqueHash),
                energyCurve: EnergyCurvePattern.Rising,
                doubleDropWeight: 0.35,
                bpmByHash: bpmByHash,
                metadataByHash: metadataByHash);

            if (result.OrderedTrackHashes.Count == 0)
            {
                _notificationService.Show("Auto-Arrange", "Could not compute an order (missing analysis data).", NotificationType.Warning);
                return;
            }

            // SaveTrackOrderAsync takes the LIST ORDER as the new sequence (it renumbers the
            // playlist itself), so this must hand over the tracks already ordered — not the
            // original list with a SortOrder written onto each item.
            var rowsByHash = eligible
                .GroupBy(t => t.TrackUniqueHash, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            var ordered = new List<PlaylistTrack>(eligible.Count);
            var added = new HashSet<Guid>();
            foreach (var hash in result.OrderedTrackHashes)
            {
                if (!rowsByHash.TryGetValue(hash, out var rows)) continue;
                foreach (var row in rows)
                {
                    // A replay of the same track: both copies move with their position.
                    if (added.Add(row.Id)) ordered.Add(row);
                }
            }
            foreach (var track in eligible)
            {
                // Defensive: a row the reorder service didn't mention keeps its place in the list
                // (it still ends up at a unique position once the playlist is renumbered).
                if (added.Add(track.Id)) ordered.Add(track);
            }

            await _libraryService.SaveTrackOrderAsync(job.Id, ordered);
            await ExecuteRefreshLibraryAsync();
            _notificationService.Show("Auto-Arrange",
                NumFormat.Ui($"Rearranged {result.OrderedTrackHashes.Count} tracks in '{job.SourceTitle}' (avg transition score {result.AverageTransitionScore:P0}).") +
                (skippedNotDownloaded > 0 ? $" {skippedNotDownloaded} not-yet-downloaded track(s) left in place." : ""),
                NotificationType.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-arrange failed for playlist {Id}", job.Id);
            _notificationService.Show("Auto-Arrange Failed", ex.Message, NotificationType.Error);
        }
    }

    private async Task ExecuteBatchQueueAnalysisAsync()
    {
        var selected = Tracks.SelectedTracks.ToList();
        if (selected.Count == 0) return;
        foreach (var track in selected)
        {
            _eventBus.Publish(new Models.TrackAnalysisRequestedEvent(track.GlobalId));
        }
        _notificationService.Show(
            "Analysis Queued",
            $"{selected.Count} track(s) queued for audio analysis.",
            NotificationType.Success);
        await Task.CompletedTask;
    }

    private async Task ExecuteBatchAddToPlaylistAsync()
    {
        var selected = Tracks.SelectedTracks.ToList();
        if (selected.Count == 0) return;

        var result = await _dialogService.ShowPlaylistPickerDialogAsync(Projects.AllProjects);
        if (result == null || !result.IsConfirmed) return;

        PlaylistJob targetPlaylist = null!;

        if (!string.IsNullOrWhiteSpace(result.NewPlaylistName))
        {
            _logger.LogInformation("Creating new playlist '{Name}' for batch add", result.NewPlaylistName);
            targetPlaylist = await _libraryService.CreateEmptyPlaylistAsync(result.NewPlaylistName);
        }
        else if (result.SelectedPlaylist != null)
        {
            targetPlaylist = result.SelectedPlaylist;
        }

        if (targetPlaylist == null)
        {
            _notificationService.Show("Error", "No playlist selected or created.", NotificationType.Error);
            return;
        }

        _logger.LogInformation("Adding {Count} tracks to playlist '{Title}'", selected.Count, targetPlaylist.SourceTitle);

        var selectedModels = selected.Select(t => t.Model).ToList();
        await _libraryService.AddTracksToProjectAsync(selectedModels, targetPlaylist.Id);

        _notificationService.Show(
            "Tracks Added",
            $"Added {selected.Count} track(s) to playlist '{targetPlaylist.SourceTitle}'.",
            NotificationType.Success);

        // Clear selection to hide the FAB
        Tracks.ClearSelection();
    }

    private async Task ExecuteBatchExportRekordboxAsync()
    {
        var selected = Tracks.SelectedTracks.ToList();
        if (selected.Count == 0) return;
        try
        {
            var outputPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                $"orbit-rekordbox-{DateTime.Now:yyyyMMdd-HHmmss}.xml");
            // The real track models: cues are looked up by TrackUniqueHash, so a hand-built copy
            // without it exported every track with no cues at all (and no album/rating/comments).
            var tracks = selected.Select(t => t.Model).ToList();
            await Services.Export.TrackExportPreflight.ValidateAsync(tracks);
            await _exportService.ExportToRekordboxXmlAsync("ORBIT Batch Export", tracks, outputPath);
            _notificationService.Show(
                "Rekordbox Export Complete",
                $"{selected.Count} track(s) exported to {outputPath}",
                NotificationType.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch Rekordbox export failed");
            _notificationService.Show("Export Failed", ex.Message, NotificationType.Error);
        }
    }

    private async Task ExecuteBatchExportM3uAsync()
    {
        var selected = Tracks.SelectedTracks.ToList();
        if (selected.Count == 0) return;
        try
        {
            var outputPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                $"orbit-playlist-{DateTime.Now:yyyyMMdd-HHmmss}.m3u8");
            var tracks = selected.Select(t => new Models.PlaylistTrack
            {
                Id               = t.Id,
                Title            = t.Title,
                Artist           = t.Artist,
                ResolvedFilePath = t.Model.ResolvedFilePath ?? string.Empty,
                BPM              = t.BPM > 0 ? t.BPM : null,
                MusicalKey       = t.MusicalKey,
            });
            var result = await _exportService.ExportToM3uAsync("ORBIT Batch Export", tracks, outputPath);
            var details = result.SkippedTrackProblems.Count == 0
                ? string.Empty
                : " Unavailable tracks were omitted: "
                    + string.Join("; ", result.SkippedTrackProblems.Take(3))
                    + (result.SkippedTrackProblems.Count > 3 ? "; …" : "");
            _notificationService.Show(
                "M3U Export Complete",
                $"{result.Exported} track(s) exported to {outputPath}" +
                (result.SkippedDuplicates > 0 ? $" · {result.SkippedDuplicates} duplicate path(s) skipped" : "") +
                (result.SkippedTrackProblems.Count > 0
                    ? $" · {result.SkippedTrackProblems.Count} unavailable track(s) skipped.{details}"
                    : ""),
                result.Skipped > 0 ? NotificationType.Warning : NotificationType.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch M3U export failed");
            _notificationService.Show("Export Failed", ex.Message, NotificationType.Error);
        }
    }
}
