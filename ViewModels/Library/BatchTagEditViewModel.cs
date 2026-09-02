using ReactiveUI;

namespace SLSKDONET.ViewModels.Library;

public class BatchTagEditResult
{
    public bool IsConfirmed { get; set; }
    public string? Artist { get; set; }
    public string? Title { get; set; }
    public string? Album { get; set; }
    public string? Genre { get; set; }
    public string? Year { get; set; }
    public string? NewFileName { get; set; }
    public string? Bpm { get; set; }
    public string? Key { get; set; }
    public string? Comments { get; set; }
    public string? Mood { get; set; }
    public string? TrackNumber { get; set; }
    public string? Rating { get; set; }
}

public sealed class BatchTagEditViewModel : ReactiveObject
{
    private string _artist = string.Empty;
    private string _title = string.Empty;
    private string _album = string.Empty;
    private string _genre = string.Empty;
    private string _year = string.Empty;
    private string _newFileName = string.Empty;
    private string _bpm = string.Empty;
    private string _key = string.Empty;
    private string _comments = string.Empty;
    private string _mood = string.Empty;
    private string _trackNumber = string.Empty;
    private string _rating = string.Empty;

    public bool IsSingleTrack { get; }
    public string FileNameWatermark { get; }

    public BatchTagEditViewModel(string? initialFileName = null)
    {
        IsSingleTrack = initialFileName is not null;
        FileNameWatermark = IsSingleTrack
            ? initialFileName!
            : "(Multiple tracks selected — filename editing not available)";
        _newFileName = initialFileName ?? string.Empty;
    }

    public string Artist
    {
        get => _artist;
        set
        {
            this.RaiseAndSetIfChanged(ref _artist, value);
            this.RaisePropertyChanged(nameof(CanSave));
        }
    }

    public string Title
    {
        get => _title;
        set
        {
            this.RaiseAndSetIfChanged(ref _title, value);
            this.RaisePropertyChanged(nameof(CanSave));
        }
    }

    public string Album
    {
        get => _album;
        set
        {
            this.RaiseAndSetIfChanged(ref _album, value);
            this.RaisePropertyChanged(nameof(CanSave));
        }
    }

    public string Genre
    {
        get => _genre;
        set
        {
            this.RaiseAndSetIfChanged(ref _genre, value);
            this.RaisePropertyChanged(nameof(CanSave));
        }
    }

    public string Year
    {
        get => _year;
        set
        {
            this.RaiseAndSetIfChanged(ref _year, value);
            this.RaisePropertyChanged(nameof(CanSave));
        }
    }

    public string NewFileName
    {
        get => _newFileName;
        set
        {
            this.RaiseAndSetIfChanged(ref _newFileName, value);
            this.RaisePropertyChanged(nameof(CanSave));
        }
    }

    public string Bpm
    {
        get => _bpm;
        set
        {
            this.RaiseAndSetIfChanged(ref _bpm, value);
            this.RaisePropertyChanged(nameof(CanSave));
        }
    }

    public string Key
    {
        get => _key;
        set
        {
            this.RaiseAndSetIfChanged(ref _key, value);
            this.RaisePropertyChanged(nameof(CanSave));
        }
    }

    public string Comments
    {
        get => _comments;
        set
        {
            this.RaiseAndSetIfChanged(ref _comments, value);
            this.RaisePropertyChanged(nameof(CanSave));
        }
    }

    public string Mood
    {
        get => _mood;
        set
        {
            this.RaiseAndSetIfChanged(ref _mood, value);
            this.RaisePropertyChanged(nameof(CanSave));
        }
    }

    public string TrackNumber
    {
        get => _trackNumber;
        set
        {
            this.RaiseAndSetIfChanged(ref _trackNumber, value);
            this.RaisePropertyChanged(nameof(CanSave));
        }
    }

    /// <summary>Rekordbox-style 0-5 star rating, as text so a blank value means "leave unchanged".</summary>
    public string Rating
    {
        get => _rating;
        set
        {
            this.RaiseAndSetIfChanged(ref _rating, value);
            this.RaisePropertyChanged(nameof(CanSave));
        }
    }

    /// <summary>True once a BPM value is present and parses as a valid positive number — gates Save so a typo can't silently no-op or write garbage.</summary>
    public bool IsBpmValid => string.IsNullOrWhiteSpace(Bpm) || (double.TryParse(Bpm, out var bpm) && bpm > 0);

    /// <summary>True unless TrackNumber is present and doesn't parse as a positive integer.</summary>
    public bool IsTrackNumberValid => string.IsNullOrWhiteSpace(TrackNumber) || (int.TryParse(TrackNumber, out var n) && n > 0);

    /// <summary>True unless Rating is present and doesn't parse as an integer 0-5.</summary>
    public bool IsRatingValid => string.IsNullOrWhiteSpace(Rating) || (int.TryParse(Rating, out var r) && r is >= 0 and <= 5);

    public bool CanSave =>
        IsBpmValid && IsTrackNumberValid && IsRatingValid &&
        (!string.IsNullOrWhiteSpace(Artist) ||
        !string.IsNullOrWhiteSpace(Title) ||
        !string.IsNullOrWhiteSpace(Album) ||
        !string.IsNullOrWhiteSpace(Genre) ||
        !string.IsNullOrWhiteSpace(Year) ||
        !string.IsNullOrWhiteSpace(Bpm) ||
        !string.IsNullOrWhiteSpace(Key) ||
        !string.IsNullOrWhiteSpace(Comments) ||
        !string.IsNullOrWhiteSpace(Mood) ||
        !string.IsNullOrWhiteSpace(TrackNumber) ||
        !string.IsNullOrWhiteSpace(Rating) ||
        (IsSingleTrack && !string.IsNullOrWhiteSpace(NewFileName)));
}
