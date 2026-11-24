using System;
using System.Collections.Generic;
using System.Linq;
using ReactiveUI;
using SLSKDONET.Services.Library.Files;

namespace SLSKDONET.ViewModels.Library;

public class BulkRenameResult
{
    public bool IsConfirmed { get; set; }
    public string Pattern { get; set; } = string.Empty;
}

/// <summary>Read-only sample used to render a live filename preview as the user edits the pattern.</summary>
public sealed class BulkRenamePreviewTrack
{
    public string Artist { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Album { get; init; } = string.Empty;
    public string TrackNumber { get; init; } = string.Empty;
    public string Year { get; init; } = string.Empty;
    public string Genre { get; init; } = string.Empty;
    public string Extension { get; init; } = string.Empty;
}

public sealed class BulkRenameViewModel : ReactiveObject
{
    private string _pattern = "{artist} - {title}";

    public int TrackCount { get; }
    private readonly IReadOnlyList<BulkRenamePreviewTrack> _previewTracks;

    public BulkRenameViewModel(int trackCount, IReadOnlyList<BulkRenamePreviewTrack> previewTracks)
    {
        TrackCount = trackCount;
        _previewTracks = previewTracks;
    }

    public string Pattern
    {
        get => _pattern;
        set
        {
            this.RaiseAndSetIfChanged(ref _pattern, value);
            this.RaisePropertyChanged(nameof(PreviewLines));
            this.RaisePropertyChanged(nameof(CanSave));
        }
    }

    public IReadOnlyList<string> PreviewLines =>
        _previewTracks.Select(t => Resolve(Pattern, t) + t.Extension).ToList();

    public bool HasPreview => _previewTracks.Count > 0;

    public bool CanSave => !string.IsNullOrWhiteSpace(Pattern);

    /// <summary>
    /// Resolves every <c>{token}</c> in <paramref name="pattern"/> against one track's field
    /// values via <see cref="PathTemplateEngine"/> (#343, LF1), so the live preview and the real
    /// rename always agree on the resulting filename — and share the same sanitisation.
    /// </summary>
    public static string Resolve(string pattern, string artist, string title, string album, string trackNumber, string year, string genre) =>
        Resolve(pattern, new BulkRenamePreviewTrack
        {
            Artist = artist, Title = title, Album = album,
            TrackNumber = trackNumber, Year = year, Genre = genre,
        });

    private static string Resolve(string pattern, BulkRenamePreviewTrack t) =>
        PathTemplateEngine.Render(pattern, ToFacts(t)).Leaf;

    private static TrackFacts ToFacts(BulkRenamePreviewTrack t) => new(
        Artist: t.Artist,
        Title: t.Title,
        Album: t.Album,
        TrackNumber: ParseInt(t.TrackNumber),
        Year: ParseInt(t.Year),
        Genre: t.Genre);

    private static int? ParseInt(string? value) => int.TryParse(value, out var n) ? n : null;
}
