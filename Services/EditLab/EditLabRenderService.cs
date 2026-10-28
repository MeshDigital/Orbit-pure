using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Models.EditLab;

namespace SLSKDONET.Services.EditLab;

/// <summary>What to render.</summary>
public sealed record EditRenderRequest(EditProject Project, EditRenderFormat Format, bool LoopRegionOnly, bool WriteCues);

/// <summary>What the render produced and where it went in the library.</summary>
public sealed record EditRenderOutcome(string TrackHash, string Title, string FilePath, EditRenderResult Result, int CuesWritten, IReadOnlyList<string> Warnings);

/// <summary>
/// Renders an Edit Lab project offline and adds the file to the library as a new track: title
/// "&lt;base title&gt; (ORBIT Edit)", artist from the base track, BPM/key = the project's, provenance
/// (project id + source track hashes) in <c>SourceProvenance</c>, analysis queued, and optionally a
/// memory cue at every clip start. Any failure throws (the partial file is removed), so the caller
/// never reports a success that didn't happen.
/// </summary>
public class EditLabRenderService
{
    public const string ProvenancePrefix = "orbit-edit-lab";
    private const string CueColour = "#4EC9B0";

    private readonly EditLabSourceService _sources;
    private readonly AppConfig? _config;
    private readonly IEventBus? _eventBus;
    private readonly ICuePointService? _cues;
    private readonly ILogger<EditLabRenderService>? _logger;
    private readonly string? _outputDirectoryOverride;

    public EditLabRenderService(EditLabSourceService sources, AppConfig? config, IEventBus? eventBus, ICuePointService? cues,
        ILogger<EditLabRenderService>? logger = null, string? outputDirectoryOverride = null)
    {
        _sources = sources;
        _config = config;
        _eventBus = eventBus;
        _cues = cues;
        _logger = logger;
        _outputDirectoryOverride = outputDirectoryOverride;
    }

    /// <summary>
    /// The configured Edit Lab output folder ([EditLab] OutputDirectory, set in Settings or the
    /// setup wizard); otherwise &lt;download folder&gt;/ORBIT Edits, or &lt;library dir&gt;/EditLab/Renders
    /// when no download folder is set.
    /// </summary>
    public string OutputDirectory =>
        _outputDirectoryOverride
        ?? (!string.IsNullOrWhiteSpace(_config?.EditLabOutputDirectory) ? _config!.EditLabOutputDirectory! : null)
        ?? (!string.IsNullOrWhiteSpace(_config?.DownloadDirectory) && Directory.Exists(_config!.DownloadDirectory)
            ? Path.Combine(_config.DownloadDirectory!, "ORBIT Edits")
            : Path.Combine(EditProjectStore.DefaultRoot(), "Renders"));

    public virtual async Task<EditRenderOutcome> RenderToLibraryAsync(EditRenderRequest request, IProgress<double>? progress, CancellationToken ct)
    {
        var project = request.Project;
        var plan = await _sources.CompilePlanAsync(project, ct).ConfigureAwait(false);
        if (plan.Problems.Count > 0) throw new InvalidOperationException("Can't render yet: " + string.Join(" ", plan.Problems));
        if (plan.Clips.Count == 0) throw new InvalidOperationException("Nothing to render: every clip is muted or the timeline is empty.");

        long start = 0, end = plan.TotalFrames;
        if (request.LoopRegionOnly)
        {
            if (!project.HasLoopRegion) throw new InvalidOperationException("Set a loop region first (drag across the bar ruler).");
            start = plan.BarToFrame(project.LoopStartBar!.Value);
            end = plan.BarToFrame(project.LoopEndBar!.Value);
        }

        var baseSource = project.BaseSource;
        string artist = string.IsNullOrWhiteSpace(baseSource?.Artist) ? "ORBIT Edit Lab" : baseSource!.Artist;
        string baseTitle = string.IsNullOrWhiteSpace(baseSource?.Title) ? project.Name : baseSource!.Title;
        string suffix = request.LoopRegionOnly ? "(ORBIT Edit loop)" : "(ORBIT Edit)";
        string ext = request.Format == EditRenderFormat.Flac ? ".flac" : ".wav";
        var (title, hash, path) = await ChooseIdentityAsync(artist, $"{baseTitle} {suffix}", ext, ct).ConfigureAwait(false);

        var result = await EditOfflineRenderer.RenderAsync(plan, path, request.Format, start, end,
            progress == null ? null : new Progress<double>(p => progress.Report(p * 0.95)), ct).ConfigureAwait(false);

        var warnings = new List<string>();
        if (result.ClippedSamples > 0)
            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"The mix clipped (peak {20 * Math.Log10(result.Peak):+0.0} dBFS): lower some clip gains and render again."));

        try
        {
            WriteTags(path, project, title, artist, warnings);
            await AddToLibraryAsync(project, hash, title, artist, path, result, ct).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(path);
            throw;
        }

        int cues = 0;
        if (request.WriteCues)
        {
            try
            {
                cues = await WriteCuesAsync(project, plan, hash, start, end, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or IOException)
            {
                _logger?.LogWarning(ex, "Edit Lab: rendered {Hash} but couldn't write its cues", hash);
                warnings.Add($"The track was added, but its cues couldn't be written: {ex.Message}");
            }
        }

        _eventBus?.Publish(new TrackAnalysisRequestedEvent(hash, AnalysisTier.Tier1, IsHighPriority: true));
        // The Library page doesn't watch the DB; tell it a new track exists so the edit shows up without a manual refresh.
        _eventBus?.Publish(new SLSKDONET.Events.LibraryUpdatedEvent($"Edit Lab render: {title}"));
        progress?.Report(1.0);
        _logger?.LogInformation("Edit Lab: rendered {Title} ({Seconds:0.0}s) to {Path}", title, result.DurationSeconds, path);
        return new EditRenderOutcome(hash, title, path, result, cues, warnings);
    }

    /// <summary>A title, library hash and file path no existing track or file uses ("…", "… 2", "… 3").</summary>
    private async Task<(string Title, string Hash, string Path)> ChooseIdentityAsync(string artist, string title, string ext, CancellationToken ct)
    {
        Directory.CreateDirectory(OutputDirectory);
        await using var db = new AppDbContext();
        for (int n = 1; n < 1000; n++)
        {
            string candidate = n == 1 ? title : $"{title} {n}";
            string hash = LibraryHash(artist, candidate);
            string path = Path.Combine(OutputDirectory, SafeFileName($"{artist} - {candidate}") + ext);
            if (File.Exists(path)) continue;
            if (await db.LibraryEntries.AsNoTracking().AnyAsync(e => e.UniqueHash == hash, ct).ConfigureAwait(false)) continue;
            return (candidate, hash, path);
        }
        throw new InvalidOperationException("Couldn't find a free file name for the render.");
    }

    /// <summary>Same identity format the folder scanner uses (lower-case artist-title without spaces).</summary>
    public static string LibraryHash(string artist, string title)
    {
        static string Token(string v) => new((v ?? "").ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray());
        return $"{Token(artist)}-{Token(title)}".Trim('-');
    }

    public static string Provenance(EditProject project) =>
        $"{ProvenancePrefix};project={project.Id:N};sources={string.Join(",", project.AllClips.Select(c => c.SourceHash).Distinct())}";

    private void WriteTags(string path, EditProject project, string title, string artist, List<string> warnings)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            file.Tag.Title = title;
            file.Tag.Performers = new[] { artist };
            file.Tag.Album = "ORBIT Edits";
            file.Tag.BeatsPerMinute = (uint)Math.Round(project.Bpm);
            if (project.Key != null) file.Tag.InitialKey = project.Key;
            file.Tag.Comment = $"ORBIT Edit Lab: {project.Name}";
            file.Save();
        }
        catch (Exception ex) when (ex is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or IOException or NotImplementedException)
        {
            _logger?.LogWarning(ex, "Edit Lab: couldn't write tags to {Path}", path);
            warnings.Add("The file's tags couldn't be written; the library entry still has the title, BPM and key.");
        }
    }

    private async Task AddToLibraryAsync(EditProject project, string hash, string title, string artist, string path, EditRenderResult result, CancellationToken ct)
    {
        var info = new FileInfo(path);
        int seconds = (int)Math.Round(result.DurationSeconds);
        string format = info.Extension.TrimStart('.').ToLowerInvariant();
        int bitrate = seconds > 0 ? (int)(info.Length * 8 / 1000 / Math.Max(1, result.DurationSeconds)) : 0;

        var entry = new LibraryEntryEntity
        {
            Id = Guid.NewGuid(),
            UniqueHash = hash,
            Artist = artist,
            Title = title,
            Album = "ORBIT Edits",
            FilePath = path,
            AddedAt = DateTime.UtcNow,
            DurationSeconds = seconds,
            CanonicalDuration = seconds,
            Format = format,
            Bitrate = bitrate,
            AvailabilityState = TrackAvailabilityState.LocalUnanalyzed,
            // The render is at the project tempo by construction: keep analysis from re-reading it
            // as half/double time (the same guard a file's own BPM tag gets).
            BPM = Math.Round(project.Bpm, 2),
            TagBPM = Math.Round(project.Bpm, 2),
            MusicalKey = project.Key,
            Comments = $"ORBIT Edit Lab · {project.Name}",
            SourceProvenance = Provenance(project),
            QualityDetails = $"edit-lab:v1;format={format};samplerate={result.SampleRate};bitdepth=24",
        };

        await using var db = new AppDbContext();
        db.LibraryEntries.Add(entry);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>A memory cue at every distinct clip start inside the rendered range (not at 0).</summary>
    private async Task<int> WriteCuesAsync(EditProject project, EditRenderPlan plan, string hash, long start, long end, CancellationToken ct)
    {
        if (_cues == null) return 0;
        var cues = new List<CuePointEntity>();
        var seen = new HashSet<long>();
        foreach (var lane in project.Lanes)
        foreach (var clip in lane.Clips.OrderBy(c => c.StartBar))
        {
            if (!EditRenderPlan.IsAudible(project, lane, clip)) continue;
            long frame = plan.BarToFrame(clip.StartBar);
            if (frame <= start || frame >= end || !seen.Add(frame)) continue;
            var source = project.FindSource(clip.SourceHash);
            string what = source == null ? "Clip" : EditProjectEditor.ShortName(source);
            if (clip.Stem != EditStem.FullMix) what += " " + EditProjectEditor.StemLabel(clip.Stem).ToLowerInvariant();
            cues.Add(new CuePointEntity
            {
                TrackUniqueHash = hash,
                TimestampInSeconds = (frame - start) / (double)plan.SampleRate,
                Type = CuePointType.PhraseBoundary,
                Label = Truncate($"{what} in", 100),
                Color = CueColour,
                IsAutoGenerated = false,
                Confidence = 1f,
                SlotIndex = -1,
            });
        }
        if (cues.Count == 0) return 0;
        await _cues.CreateManyAsync(cues.OrderBy(c => c.TimestampInSeconds), ct).ConfigureAwait(false);
        return cues.Count;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length > 150 ? cleaned[..150] : cleaned;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "Edit Lab: couldn't remove the failed render {Path}", path);
        }
    }
}
