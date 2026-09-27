using ReactiveUI;

namespace SLSKDONET.ViewModels.Library;

public class BulkMoveOrCopyResult
{
    public bool IsConfirmed { get; set; }
    public bool IsCopy { get; set; }
}

public sealed class BulkMoveOrCopyViewModel : ReactiveObject
{
    private bool _isCopy;

    public int TrackCount { get; }

    public BulkMoveOrCopyViewModel(int trackCount)
    {
        TrackCount = trackCount;
    }

    /// <summary>False = Move (relocates the file, updates ORBIT's library path). True = Copy
    /// (duplicates the file for external use, e.g. staging a USB stick — ORBIT's library entry
    /// keeps pointing at the original).</summary>
    public bool IsCopy
    {
        get => _isCopy;
        set => this.RaiseAndSetIfChanged(ref _isCopy, value);
    }
}
