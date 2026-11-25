using System.IO;
using ReactiveUI;
using SLSKDONET.Services.Library.Files;

namespace SLSKDONET.ViewModels.Maintenance;

/// <summary>One row in the LF5 "Reorganise library files" preview list.</summary>
public sealed class ReorganizeRowViewModel : ReactiveObject
{
    private bool _isSelected;

    public ReorganizeRowViewModel(ReorganizePreviewItem item)
    {
        Item = item;
        IsMove = item.Status == "Move";
        _isSelected = IsMove;
    }

    public ReorganizePreviewItem Item { get; }
    public string Hash => Item.Hash;
    public string SourcePath => Item.SourcePath;
    public string TargetPath => Item.TargetPath;
    public string Status => Item.Status;
    public bool IsMove { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, IsMove && value);
    }

    public string ChangeText => IsMove
        ? $"{Path.GetFileName(SourcePath)}  →  {TargetPath}"
        : $"{Path.GetFileName(SourcePath)}  ({Status})";
}
