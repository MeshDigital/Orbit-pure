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
}

public sealed class BatchTagEditViewModel : ReactiveObject
{
    private string _artist = string.Empty;
    private string _title = string.Empty;
    private string _album = string.Empty;
    private string _genre = string.Empty;
    private string _year = string.Empty;
    private string _newFileName = string.Empty;

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

    public bool CanSave =>
        !string.IsNullOrWhiteSpace(Artist) ||
        !string.IsNullOrWhiteSpace(Title) ||
        !string.IsNullOrWhiteSpace(Album) ||
        !string.IsNullOrWhiteSpace(Genre) ||
        !string.IsNullOrWhiteSpace(Year) ||
        (IsSingleTrack && !string.IsNullOrWhiteSpace(NewFileName));
}
