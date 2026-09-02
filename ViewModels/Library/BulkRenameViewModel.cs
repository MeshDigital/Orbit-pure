using System;
using System.Collections.Generic;
using System.Linq;
using ReactiveUI;

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
    private static readonly (string Token, Func<BulkRenamePreviewTrack, string> Select)[] Tokens =
    {
        ("{artist}", t => t.Artist),
        ("{title}", t => t.Title),
        ("{album}", t => t.Album),
        ("{tracknumber}", t => t.TrackNumber),
        ("{year}", t => t.Year),
        ("{genre}", t => t.Genre),
    };

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
    /// values. Shared by the dialog's live preview and the real rename execution
    /// (<c>LibraryViewModel.Commands.ExecuteBulkRenameAsync</c>) so both always agree on the
    /// resulting filename.
    /// </summary>
    public static string Resolve(string pattern, string artist, string title, string album, string trackNumber, string year, string genre) =>
        Resolve(pattern, new BulkRenamePreviewTrack
        {
            Artist = artist, Title = title, Album = album,
            TrackNumber = trackNumber, Year = year, Genre = genre,
        });

    private static string Resolve(string pattern, BulkRenamePreviewTrack t)
    {
        var result = pattern;
        foreach (var (token, select) in Tokens)
            result = result.Replace(token, select(t), StringComparison.OrdinalIgnoreCase);
        return result;
    }
}
