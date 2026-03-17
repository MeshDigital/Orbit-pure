using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;

namespace SLSKDONET.ViewModels.Downloads;

/// <summary>
/// Beta 2026 — Lane Dashboard.
/// Represents a single Soulseek peer's contribution lane in the Download Center.
/// Groups all tracks currently being served (or queued from) one peer, giving a
/// "bottleneck at a glance" view: one peer serving 8/10 tracks is visible immediately.
/// </summary>
public class PeerLaneViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _cleanUp = new();

    public string PeerName { get; }

    private readonly ReadOnlyObservableCollection<UnifiedTrackViewModel> _tracks;
    public ReadOnlyObservableCollection<UnifiedTrackViewModel> Tracks => _tracks;

    private double _totalSpeed;
    public double TotalSpeed
    {
        get => _totalSpeed;
        set => this.RaiseAndSetIfChanged(ref _totalSpeed, value);
    }

    public int TrackCount => _tracks.Count;

    public string SpeedDisplay => TotalSpeed > 1_048_576
        ? $"{TotalSpeed / 1_048_576.0:F1} MB/s"
        : TotalSpeed > 1024
            ? $"{TotalSpeed / 1024.0:F0} KB/s"
            : "—";

    public string TrackCountDisplay => TrackCount == 1 ? "1 track" : $"{TrackCount} tracks";

    /// <summary>Accent color shifts from green (fast/sole provider) to amber (many-peer spread).</summary>
    public string LaneAccentColor => TotalSpeed > 2_097_152 ? "#1DB954"
        : TotalSpeed > 524_288 ? "#4EC9B0"
        : "#888888";

    public PeerLaneViewModel(IGroup<UnifiedTrackViewModel, string, string> group)
    {
        PeerName = string.IsNullOrEmpty(group.Key) ? "Unknown Peer" : group.Key;

        group.Cache.Connect()
            .SortAndBind(out _tracks,
                SortExpressionComparer<UnifiedTrackViewModel>.Descending(x => x.CurrentSpeedBytes))
            .Subscribe()
            .DisposeWith(_cleanUp);

        // Aggregate speed and raise derived property notifications on each change
        _tracks.ToObservableChangeSet()
            .AutoRefresh(x => x.CurrentSpeedBytes)
            .ToCollection()
            .Select(col => col.Sum(x => x.CurrentSpeedBytes))
            .Subscribe(speed =>
            {
                TotalSpeed = speed;
                this.RaisePropertyChanged(nameof(SpeedDisplay));
                this.RaisePropertyChanged(nameof(TrackCount));
                this.RaisePropertyChanged(nameof(TrackCountDisplay));
                this.RaisePropertyChanged(nameof(LaneAccentColor));
            })
            .DisposeWith(_cleanUp);
    }

    public void Dispose() => _cleanUp.Dispose();
}
