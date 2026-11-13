using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Services.Sets;
using SLSKDONET.Utils;

namespace SLSKDONET.ViewModels.Sets;

/// <summary>A row in the Sets track pool.</summary>
public sealed class PoolTrackViewModel
{
    public SetPoolTrack Track { get; }
    public string TrackUniqueHash => Track.TrackUniqueHash;
    public string Title => Track.Title;
    public string Artist => Track.Artist;
    public string Bpm => NumFormat.Bpm(Track.Bpm);
    public string Key => Track.CamelotKey ?? "—";

    public PoolTrackViewModel(SetPoolTrack track) => Track = track;
}

/// <summary>
/// The Sets page track pool (P2.5): searches the library and filters relative to the selected
/// slot ("fits key", "BPM close"). Adding to the set is the page's job; this only finds tracks.
/// </summary>
public sealed class SetPoolViewModel : ReactiveObject, IDisposable
{
    /// <summary>"BPM close" window around the reference slot, as a fraction of its BPM.</summary>
    public const double BpmWindow = 0.04;

    private readonly ISetProjectService _sets;
    private readonly IDisposable _refresh;
    private int _generation;
    private SetSlotInfo? _reference;

    public ObservableCollection<PoolTrackViewModel> Items { get; } = new();

    private string _searchText = string.Empty;
    public string SearchText { get => _searchText; set => this.RaiseAndSetIfChanged(ref _searchText, value); }

    private bool _analysedOnly;
    public bool AnalysedOnly { get => _analysedOnly; set => this.RaiseAndSetIfChanged(ref _analysedOnly, value); }

    private bool _fitsKey;
    /// <summary>Only tracks in the selected slot's key or a Camelot neighbour.</summary>
    public bool FitsKey { get => _fitsKey; set => this.RaiseAndSetIfChanged(ref _fitsKey, value); }

    private bool _bpmClose;
    /// <summary>Only tracks within ±4% of the selected slot's BPM.</summary>
    public bool BpmClose { get => _bpmClose; set => this.RaiseAndSetIfChanged(ref _bpmClose, value); }

    /// <summary>Key/BPM chips need an analysed selected slot to compare against.</summary>
    public bool CanFilterByKey => _reference?.CamelotKey is not null;
    public bool CanFilterByBpm => _reference?.Bpm is > 0;

    /// <summary>"Relative to: Dead Limit (172.0 · 4A)" or a hint when nothing is selected.</summary>
    public string ReferenceText => _reference is { } r
        ? $"Relative to {r.Title} ({NumFormat.Bpm(r.Bpm)} · {r.CamelotKey ?? "—"})"
        : "Select a slot to filter by key or BPM";

    public bool HasActiveFilters => AnalysedOnly || FitsKey || BpmClose || !string.IsNullOrWhiteSpace(SearchText);

    private bool _isLoading = true;
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isLoading, value);
            this.RaisePropertyChanged(nameof(IsEmpty));
        }
    }

    public bool IsEmpty => !IsLoading && ErrorMessage is null && Items.Count == 0;

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            this.RaiseAndSetIfChanged(ref _errorMessage, value);
            this.RaisePropertyChanged(nameof(IsEmpty));
        }
    }

    public SetPoolViewModel(ISetProjectService sets)
    {
        _sets = sets;
        _refresh = this.WhenAnyValue(x => x.SearchText, x => x.AnalysedOnly, x => x.FitsKey, x => x.BpmClose)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(250))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(state => { _ = RefreshAsync(); });
    }

    /// <summary>Clears every filter (the "Clear" chip).</summary>
    public void ClearFilters()
    {
        SearchText = string.Empty;
        AnalysedOnly = FitsKey = BpmClose = false;
    }

    /// <summary>The slot the key/BPM chips compare against (the lane's selection).</summary>
    public void SetReference(SetSlotInfo? slot)
    {
        if (_reference?.SlotId == slot?.SlotId && _reference?.Bpm == slot?.Bpm && _reference?.CamelotKey == slot?.CamelotKey)
            return;
        _reference = slot;
        if (!CanFilterByKey) FitsKey = false;
        if (!CanFilterByBpm) BpmClose = false;
        this.RaisePropertyChanged(nameof(CanFilterByKey));
        this.RaisePropertyChanged(nameof(CanFilterByBpm));
        this.RaisePropertyChanged(nameof(ReferenceText));
        if (FitsKey || BpmClose)
            _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        var generation = ++_generation;
        IsLoading = true;
        ErrorMessage = null;
        this.RaisePropertyChanged(nameof(HasActiveFilters));
        try
        {
            var query = new SetPoolQuery(
                Text: SearchText,
                AnalysedOnly: AnalysedOnly,
                BpmMin: BpmClose && _reference?.Bpm is double lo ? lo * (1 - BpmWindow) : null,
                BpmMax: BpmClose && _reference?.Bpm is double hi ? hi * (1 + BpmWindow) : null,
                CamelotKeys: FitsKey && _reference?.CamelotKey is { } key ? CamelotNeighbours(key) : null);

            var tracks = await _sets.SearchPoolAsync(query);
            if (generation != _generation)
                return;

            Items.Clear();
            foreach (var track in tracks)
                Items.Add(new PoolTrackViewModel(track));
        }
        catch (Exception ex)
        {
            if (generation != _generation)
                return;
            Items.Clear();
            ErrorMessage = $"Couldn't search the library: {ex.Message}";
        }
        finally
        {
            if (generation == _generation)
                IsLoading = false;
        }
    }

    /// <summary>
    /// The key itself plus its harmonic neighbours on the Camelot wheel: ±1 on the same ring and
    /// the relative major/minor. Unparseable keys return just themselves.
    /// </summary>
    public static IReadOnlyCollection<string> CamelotNeighbours(string key)
    {
        var trimmed = key.Trim().ToUpperInvariant();
        if (trimmed.Length < 2
            || trimmed[^1] is not ('A' or 'B')
            || !int.TryParse(trimmed[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            || number is < 1 or > 12)
            return new[] { key };

        var ring = trimmed[^1];
        string Key(int n, char r) => $"{((n + 11) % 12) + 1}{r}";
        return new HashSet<string>
        {
            Key(number, ring),
            Key(number + 1, ring),
            Key(number - 1, ring),
            Key(number, ring == 'A' ? 'B' : 'A'),
        };
    }

    public void Dispose() => _refresh.Dispose();
}
