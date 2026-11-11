using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Services.Sets;

namespace SLSKDONET.ViewModels.Sets;

/// <summary>
/// P2.2/P2.3: view model for the Sets page — the set-list rail, the horizontal slot lane, and the
/// track-pool search/add drawer. The set lane + pool are P2.3.
/// </summary>
public sealed class SetsViewModel : ReactiveObject, IDisposable
{
    private readonly ISetProjectService _sets;

    public ObservableCollection<SetSummary> Sets { get; } = new();
    public ObservableCollection<SlotViewModel> Slots { get; } = new();
    public ObservableCollection<SetTrackPickerItem> SearchResults { get; } = new();

    private SetSummary? _selectedSet;
    public SetSummary? SelectedSet
    {
        get => _selectedSet;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedSet, value);
            this.RaisePropertyChanged(nameof(HasSelection));
            _ = LoadSlotsAsync(value?.Id);
        }
    }

    private SlotViewModel? _selectedSlot;
    public SlotViewModel? SelectedSlot
    {
        get => _selectedSlot;
        set => this.RaiseAndSetIfChanged(ref _selectedSlot, value);
    }

    private string _searchQuery = string.Empty;
    public string SearchQuery
    {
        get => _searchQuery;
        set => this.RaiseAndSetIfChanged(ref _searchQuery, value);
    }

    private SetTrackPickerItem? _selectedSearchResult;
    public SetTrackPickerItem? SelectedSearchResult
    {
        get => _selectedSearchResult;
        set => this.RaiseAndSetIfChanged(ref _selectedSearchResult, value);
    }

    public bool HasSets => Sets.Count > 0;
    public bool HasSelection => SelectedSet is not null;
    public bool HasSlots => Slots.Count > 0;

    private string? _errorMessage;
    public string? ErrorMessage { get => _errorMessage; private set => this.RaiseAndSetIfChanged(ref _errorMessage, value); }
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public ReactiveCommand<Unit, Unit> NewSetCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteSetCommand { get; }
    public ReactiveCommand<Unit, Unit> SearchCommand { get; }
    public ReactiveCommand<Unit, Unit> AddTrackCommand { get; }

    public SetsViewModel(ISetProjectService sets)
    {
        _sets = sets;
        NewSetCommand = ReactiveCommand.CreateFromTask(NewSetAsync);
        DeleteSetCommand = ReactiveCommand.CreateFromTask(DeleteSelectedAsync);
        SearchCommand = ReactiveCommand.CreateFromTask(SearchAsync);
        AddTrackCommand = ReactiveCommand.CreateFromTask(AddSelectedTrackAsync);
    }

    public async Task LoadAsync()
    {
        ErrorMessage = null;
        try
        {
            var sets = await _sets.GetSetsAsync();
            Sets.Clear();
            foreach (var s in sets)
                Sets.Add(s);
            this.RaisePropertyChanged(nameof(HasSets));
        }
        catch (Exception ex)
        {
            ErrorMessage = "Couldn't load sets: " + ex.Message;
        }
    }

    private async Task LoadSlotsAsync(Guid? setId)
    {
        Slots.Clear();
        SelectedSlot = null;
        if (setId is not Guid id)
        {
            this.RaisePropertyChanged(nameof(HasSlots));
            return;
        }

        try
        {
            var slots = await _sets.GetSlotsAsync(id);
            var labels = await _sets.GetTrackLabelsAsync(slots.Select(s => s.TrackUniqueHash));
            foreach (var s in slots)
                Slots.Add(new SlotViewModel(
                    s.Id,
                    labels.TryGetValue(s.TrackUniqueHash, out var label) ? label : s.TrackUniqueHash,
                    s.SlotRole,
                    s.EnergyTarget));
        }
        catch (Exception ex)
        {
            ErrorMessage = "Couldn't load the set's tracks: " + ex.Message;
        }
        this.RaisePropertyChanged(nameof(HasSlots));
    }

    private async Task NewSetAsync()
    {
        var created = await _sets.CreateSetAsync();
        await LoadAsync();
        SelectedSet = Sets.FirstOrDefault(s => s.Id == created.Id);
    }

    private async Task DeleteSelectedAsync()
    {
        if (SelectedSet is null)
            return;
        await _sets.DeleteSetAsync(SelectedSet.Id);
        SelectedSet = null;
        await LoadAsync();
    }

    private async Task SearchAsync()
    {
        SearchResults.Clear();
        var results = await _sets.SearchLibraryAsync(SearchQuery);
        foreach (var r in results)
            SearchResults.Add(r);
    }

    private async Task AddSelectedTrackAsync()
    {
        if (SelectedSet is null || SelectedSearchResult is null)
            return;
        await _sets.AppendTrackAsync(SelectedSet.Id, SelectedSearchResult.TrackUniqueHash);
        await LoadSlotsAsync(SelectedSet.Id);
    }

    public void Dispose()
    {
        NewSetCommand.Dispose();
        DeleteSetCommand.Dispose();
        SearchCommand.Dispose();
        AddTrackCommand.Dispose();
    }
}
