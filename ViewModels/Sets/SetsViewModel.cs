using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.Services.Sets;
using SLSKDONET.Utils;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.Sets;

/// <summary>A set in the Sets rail.</summary>
public sealed class SetListItemViewModel : ReactiveObject
{
    public Guid Id { get; }

    private string _name;
    public string Name { get => _name; set => this.RaiseAndSetIfChanged(ref _name, value); }

    public SetProjectStatus Status { get; }
    public int SlotCount { get; }

    /// <summary>"12 tracks · Draft".</summary>
    public string Summary => $"{CountFormat.Of(SlotCount, "track")} · {Status}";

    public SetListItemViewModel(SetSummary summary)
    {
        Id = summary.Id;
        _name = summary.Name;
        Status = summary.Status;
        SlotCount = summary.SlotCount;
    }
}

/// <summary>One slot row of the selected set (interim list until the set lane, P2.4).</summary>
public sealed class SetSlotRowViewModel
{
    public SetSlotInfo Slot { get; }

    public string Number => (Slot.Position + 1).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
    public string Role => Slot.Role.ToString().ToUpperInvariant();
    public string Title => Slot.Title;
    public string Artist => Slot.Artist;
    public string Bpm => NumFormat.Bpm(Slot.Bpm);
    public string Key => Slot.CamelotKey ?? "—";
    public bool IsMissing => !Slot.IsInLibrary;

    public SetSlotRowViewModel(SetSlotInfo slot) => Slot = slot;
}

/// <summary>
/// The Sets page (rebuild plan P2.2/P2.3): a rail of every SetProject with create / rename /
/// duplicate / delete, and the selected set's slots. All writes go through
/// <see cref="ISetProjectService"/>; failures are logged and toasted, never swallowed.
/// </summary>
public sealed class SetsViewModel : ReactiveObject, IDisposable
{
    private readonly ISetProjectService _sets;
    private readonly IDialogService _dialogs;
    private readonly INotificationService? _notifications;
    private readonly ILogger<SetsViewModel>? _logger;
    private int _slotLoadGeneration;

    public ObservableCollection<SetListItemViewModel> Sets { get; } = new();
    public ObservableCollection<SetSlotRowViewModel> Slots { get; } = new();

    private SetListItemViewModel? _selectedSet;
    public SetListItemViewModel? SelectedSet
    {
        get => _selectedSet;
        set
        {
            if (ReferenceEquals(_selectedSet, value))
                return;
            this.RaiseAndSetIfChanged(ref _selectedSet, value);
            this.RaisePropertyChanged(nameof(HasSelection));
            this.RaisePropertyChanged(nameof(ShowNoSelection));
            PendingSlotLoad = LoadSlotsAsync();
        }
    }

    /// <summary>The slot load started by the last selection change (awaited by tests).</summary>
    internal Task PendingSlotLoad { get; private set; } = Task.CompletedTask;

    public bool HasSelection => SelectedSet is not null;

    private bool _isLoadingSets;
    public bool IsLoadingSets { get => _isLoadingSets; private set => this.RaiseAndSetIfChanged(ref _isLoadingSets, value); }

    private bool _setsLoaded;
    private bool _isLoadingSlots;
    public bool IsLoadingSlots
    {
        get => _isLoadingSlots;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isLoadingSlots, value);
            this.RaisePropertyChanged(nameof(IsSetEmpty));
            this.RaisePropertyChanged(nameof(HasSlots));
        }
    }

    private string? _errorMessage;
    /// <summary>Set when loading failed; shown inline instead of an empty page.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            this.RaiseAndSetIfChanged(ref _errorMessage, value);
            this.RaisePropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => ErrorMessage is not null;

    /// <summary>No sets exist at all (rail + page empty state).</summary>
    public bool HasNoSets => _setsLoaded && Sets.Count == 0;

    /// <summary>Sets exist but none is selected.</summary>
    public bool ShowNoSelection => _setsLoaded && Sets.Count > 0 && SelectedSet is null;

    /// <summary>The selected set has no slots yet.</summary>
    public bool IsSetEmpty => HasSelection && !IsLoadingSlots && !HasError && Slots.Count == 0;

    public bool HasSlots => HasSelection && !IsLoadingSlots && Slots.Count > 0;

    /// <summary>"12 tracks · Draft" for the selected set's header.</summary>
    public string SelectedSummary => SelectedSet is { } set ? $"{CountFormat.Of(Slots.Count, "track")} · {set.Status}" : string.Empty;

    public ReactiveCommand<Unit, Unit> NewSetCommand { get; }
    public ReactiveCommand<Unit, Unit> RenameSetCommand { get; }
    public ReactiveCommand<Unit, Unit> DuplicateSetCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteSetCommand { get; }
    public ReactiveCommand<Unit, Unit> RetryCommand { get; }

    public SetsViewModel(
        ISetProjectService sets,
        IDialogService dialogs,
        INotificationService? notifications = null,
        ILogger<SetsViewModel>? logger = null)
    {
        _sets = sets;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;

        var hasSelection = this.WhenAnyValue(x => x.SelectedSet).Select(s => s is not null);
        NewSetCommand = ReactiveCommand.CreateFromTask(NewSetAsync);
        RenameSetCommand = ReactiveCommand.CreateFromTask(RenameSetAsync, hasSelection);
        DuplicateSetCommand = ReactiveCommand.CreateFromTask(DuplicateSetAsync, hasSelection);
        DeleteSetCommand = ReactiveCommand.CreateFromTask(DeleteSetAsync, hasSelection);
        RetryCommand = ReactiveCommand.CreateFromTask(() => LoadAsync());
    }

    /// <summary>Reloads the rail, keeping (or switching to) a selection when it still exists.</summary>
    public async Task LoadAsync(Guid? select = null)
    {
        var keep = select ?? SelectedSet?.Id;
        IsLoadingSets = true;
        ErrorMessage = null;
        try
        {
            var sets = await _sets.GetSetsAsync();

            Sets.Clear();
            foreach (var summary in sets)
                Sets.Add(new SetListItemViewModel(summary));

            _setsLoaded = true;
            var target = Sets.FirstOrDefault(s => s.Id == keep);
            if (target is not null && !ReferenceEquals(target, SelectedSet))
                SelectedSet = target;
            else if (target is null)
                SelectedSet = null;
            else
                PendingSlotLoad = LoadSlotsAsync(); // same set re-selected: refresh its slots
        }
        catch (Exception ex)
        {
            ReportFailure("Couldn't load your sets", ex);
        }
        finally
        {
            IsLoadingSets = false;
            RaiseStateChanged();
        }
    }

    /// <summary>Opens the page on a specific set (used when another surface routes here).</summary>
    public Task OpenSetAsync(Guid setId) => LoadAsync(setId);

    private async Task LoadSlotsAsync()
    {
        var generation = ++_slotLoadGeneration;
        Slots.Clear();
        ErrorMessage = null;

        if (SelectedSet is not { } set)
        {
            RaiseStateChanged();
            return;
        }

        IsLoadingSlots = true;
        try
        {
            var slots = await _sets.GetSlotsAsync(set.Id);
            if (generation != _slotLoadGeneration)
                return; // the user picked another set meanwhile

            foreach (var slot in slots)
                Slots.Add(new SetSlotRowViewModel(slot));
        }
        catch (Exception ex)
        {
            if (generation == _slotLoadGeneration)
                ReportFailure($"Couldn't open \"{set.Name}\"", ex);
        }
        finally
        {
            if (generation == _slotLoadGeneration)
            {
                IsLoadingSlots = false;
                RaiseStateChanged();
            }
        }
    }

    private async Task NewSetAsync()
    {
        try
        {
            var name = await _dialogs.ShowPromptAsync("New set", "Name the set", $"Set {DateTime.Now:MMM d}");
            if (name is null)
                return; // cancelled
            var created = await _sets.CreateSetAsync(name);
            await LoadAsync(created.Id);
        }
        catch (Exception ex)
        {
            ReportFailure("Couldn't create the set", ex);
        }
    }

    private async Task RenameSetAsync()
    {
        if (SelectedSet is not { } set)
            return;
        try
        {
            var name = await _dialogs.ShowPromptAsync("Rename set", "Set name", set.Name);
            if (string.IsNullOrWhiteSpace(name) || name.Trim() == set.Name)
                return;
            if (!await _sets.RenameSetAsync(set.Id, name))
            {
                _notifications?.Show("Sets", $"\"{set.Name}\" no longer exists.", NotificationType.Warning);
                await LoadAsync();
                return;
            }
            set.Name = name.Trim();
            await LoadAsync(set.Id); // re-sort: renaming bumps LastModifiedAt
        }
        catch (Exception ex)
        {
            ReportFailure("Couldn't rename the set", ex);
        }
    }

    private async Task DuplicateSetAsync()
    {
        if (SelectedSet is not { } set)
            return;
        try
        {
            var copy = await _sets.DuplicateSetAsync(set.Id);
            if (copy is null)
            {
                _notifications?.Show("Sets", $"\"{set.Name}\" no longer exists.", NotificationType.Warning);
                await LoadAsync();
                return;
            }
            await LoadAsync(copy.Id);
            _notifications?.Show("Sets", $"Duplicated as \"{copy.Name}\".", NotificationType.Success);
        }
        catch (Exception ex)
        {
            ReportFailure("Couldn't duplicate the set", ex);
        }
    }

    private async Task DeleteSetAsync()
    {
        if (SelectedSet is not { } set)
            return;
        try
        {
            var confirmed = await _dialogs.ConfirmAsync(
                "Delete set",
                $"Delete \"{set.Name}\" and its {CountFormat.Of(set.SlotCount, "slot")}? Tracks stay in your library. This can't be undone.",
                "Delete",
                "Cancel");
            if (!confirmed)
                return;

            if (!await _sets.DeleteSetAsync(set.Id))
                _notifications?.Show("Sets", $"\"{set.Name}\" was already deleted.", NotificationType.Warning);

            SelectedSet = null;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ReportFailure("Couldn't delete the set", ex);
        }
    }

    private void ReportFailure(string message, Exception ex)
    {
        _logger?.LogError(ex, "[Sets] {Message}", message);
        ErrorMessage = $"{message}: {ex.Message}";
        _notifications?.Show("Sets", ErrorMessage, NotificationType.Error);
    }

    private void RaiseStateChanged()
    {
        this.RaisePropertyChanged(nameof(HasNoSets));
        this.RaisePropertyChanged(nameof(ShowNoSelection));
        this.RaisePropertyChanged(nameof(IsSetEmpty));
        this.RaisePropertyChanged(nameof(HasSlots));
        this.RaisePropertyChanged(nameof(SelectedSummary));
    }

    public void Dispose()
    {
        NewSetCommand.Dispose();
        RenameSetCommand.Dispose();
        DuplicateSetCommand.Dispose();
        DeleteSetCommand.Dispose();
        RetryCommand.Dispose();
    }
}
