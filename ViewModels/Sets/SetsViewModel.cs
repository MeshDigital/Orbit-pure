using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Services.Sets;

namespace SLSKDONET.ViewModels.Sets;

/// <summary>
/// P2.2: view model for the Sets page shell — lists the user's sets and offers New / Delete.
/// The set lane + track pool arrive in P2.3.
/// </summary>
public sealed class SetsViewModel : ReactiveObject, IDisposable
{
    private readonly ISetProjectService _sets;

    public ObservableCollection<SetSummary> Sets { get; } = new();

    private SetSummary? _selectedSet;
    public SetSummary? SelectedSet
    {
        get => _selectedSet;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedSet, value);
            this.RaisePropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSets => Sets.Count > 0;
    public bool HasSelection => SelectedSet is not null;

    private string? _errorMessage;
    public string? ErrorMessage { get => _errorMessage; private set => this.RaiseAndSetIfChanged(ref _errorMessage, value); }
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public ReactiveCommand<Unit, Unit> NewSetCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteSetCommand { get; }

    public SetsViewModel(ISetProjectService sets)
    {
        _sets = sets;
        NewSetCommand = ReactiveCommand.CreateFromTask(NewSetAsync);
        DeleteSetCommand = ReactiveCommand.CreateFromTask(DeleteSelectedAsync);
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

    public void Dispose()
    {
        NewSetCommand.Dispose();
        DeleteSetCommand.Dispose();
    }
}
