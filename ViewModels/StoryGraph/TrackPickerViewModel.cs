using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Services.Sets;

namespace SLSKDONET.ViewModels.StoryGraph;

/// <summary>
/// Single-track picker for the Story Graph "Add Track" flow. Searches the whole library by title or
/// artist (debounced) and raises <see cref="Selected"/> with the chosen hash on confirm.
/// </summary>
public sealed class TrackPickerViewModel : ReactiveObject, IDisposable
{
    private const int ResultLimit = 200;

    private readonly ISetProjectService _sets;
    private readonly IDisposable _searchSubscription;
    private int _searchGeneration;

    public ObservableCollection<SetTrackPickerItem> Items { get; } = new();

    private SetTrackPickerItem? _selectedItem;
    public SetTrackPickerItem? SelectedItem
    {
        get => _selectedItem;
        set => this.RaiseAndSetIfChanged(ref _selectedItem, value);
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set => this.RaiseAndSetIfChanged(ref _searchText, value);
    }

    private bool _isLoading = true; // until the first search finishes, show "Searching…" not "No matches"
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isLoading, value);
            this.RaisePropertyChanged(nameof(IsEmpty));
        }
    }

    /// <summary>True when a finished search returned nothing.</summary>
    public bool IsEmpty => !IsLoading && Items.Count == 0;

    private string? _errorMessage;
    /// <summary>Set when the last search failed; shown in place of the list.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    public ReactiveCommand<Unit, Unit> AddCommand { get; }

    /// <summary>Raised with the chosen track's UniqueHash; the window closes on this.</summary>
    public event EventHandler<string>? Selected;

    public TrackPickerViewModel(ISetProjectService sets)
    {
        _sets = sets;
        var canAdd = this.WhenAnyValue(x => x.SelectedItem).Select(item => item is not null);
        AddCommand = ReactiveCommand.Create(Add, canAdd);

        _searchSubscription = this.WhenAnyValue(x => x.SearchText)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(250))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(term => { _ = LoadAsync(); });
    }

    public async Task LoadAsync()
    {
        var generation = ++_searchGeneration;
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var results = await _sets.SearchLibraryAsync(SearchText, ResultLimit);
            if (generation != _searchGeneration)
                return; // a newer search started while this one ran

            Items.Clear();
            foreach (var item in results)
                Items.Add(item);
        }
        catch (Exception ex)
        {
            if (generation != _searchGeneration)
                return;
            Items.Clear();
            ErrorMessage = $"Search failed: {ex.Message}";
        }
        finally
        {
            if (generation == _searchGeneration)
                IsLoading = false;
        }
    }

    private void Add()
    {
        if (SelectedItem is null)
            return;
        Selected?.Invoke(this, SelectedItem.TrackUniqueHash);
    }

    public void Dispose()
    {
        Selected = null;
        _searchSubscription.Dispose();
        AddCommand.Dispose();
    }
}
