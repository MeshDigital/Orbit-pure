using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services.Library.Files;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Services.LibraryActions;

/// <summary>
/// LF3 (#345): re-files the selected Inbox tracks to their template path ("File to library") via the
/// single move path (LF2). Each track's final path is resolved from its metadata, then the move is
/// planned and executed through <see cref="FileRelocationService"/> (lock/linked guard, journal, undo).
/// </summary>
public class FileToLibraryAction : ILibraryAction
{
    private readonly PathProviderService _pathProvider;
    private readonly FileRelocationService _relocation;
    private readonly ILogger<FileToLibraryAction> _logger;

    public string Name => "File to library";
    public string IconGlyph => "📂";
    public string Category => "File";

    public FileToLibraryAction(
        PathProviderService pathProvider,
        FileRelocationService relocation,
        ILogger<FileToLibraryAction> logger)
    {
        _pathProvider = pathProvider;
        _relocation = relocation;
        _logger = logger;
    }

    public bool CanExecute(LibraryContext context) =>
        context.SelectedTracks.Any(t =>
            !string.IsNullOrEmpty(t.Model.TrackUniqueHash) &&
            !string.IsNullOrEmpty(t.Model.ResolvedFilePath));

    public async Task ExecuteAsync(LibraryContext context)
    {
        var requests = new List<RelocationRequest>();
        foreach (var vm in context.SelectedTracks)
        {
            var model = vm.Model;
            if (string.IsNullOrEmpty(model.TrackUniqueHash) || string.IsNullOrEmpty(model.ResolvedFilePath))
                continue;

            var track = new Track
            {
                Artist = model.Artist,
                Title = model.Title,
                Album = model.Album,
                BPM = model.BPM,
                MusicalKey = model.MusicalKey,
                Energy = model.Energy,
                Filename = Path.GetFileName(model.ResolvedFilePath),
                Length = model.CanonicalDuration,
                Bitrate = model.Bitrate ?? 0,
                Genres = model.Genres,
                ReleaseDate = model.ReleaseDate,
            };

            var templatePath = _pathProvider.GetTrackPath(track);
            if (string.Equals(templatePath, model.ResolvedFilePath, StringComparison.OrdinalIgnoreCase))
                continue; // already at the template path

            requests.Add(new RelocationRequest(model.TrackUniqueHash, model.ResolvedFilePath, templatePath));
        }

        if (requests.Count == 0)
        {
            _logger.LogInformation("File-to-library: no tracks to re-file.");
            return;
        }

        var plan = await _relocation.PlanAsync(requests).ConfigureAwait(false);
        var outcome = await _relocation.ExecuteAsync(plan).ConfigureAwait(false);
        _logger.LogInformation("File-to-library: moved {Moved}/{Total} track(s) ({Skipped} problems).",
            outcome.Moved, requests.Count, plan.Problems.Count + outcome.Failures.Count);
    }
}
