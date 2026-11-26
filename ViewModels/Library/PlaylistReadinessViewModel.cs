using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.Library;

/// <summary>
/// One unready track row shown inside the playlist readiness badge flyout (Epic #517 / #526).
/// </summary>
public sealed record UnreadyTrackItem(
    Guid TrackId,
    string TrackUniqueHash,
    string Artist,
    string Title,
    string MissingSummary)
{
    public string DisplayLabel =>
        string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} — {Title}";
}

/// <summary>
/// Drives the playlist header readiness badge ("Ready 42/45") and its clickable list of unready
/// tracks with "Open in Cue Forge" and "Prep for Mix" actions (Epic #517 / #526, #527).
/// Uses the shared <see cref="TrackMixReadinessEvaluator"/> so the badge, Prep for Mix, and
/// Workstation never disagree.
/// </summary>
public sealed class PlaylistReadinessViewModel : INotifyPropertyChanged
{
    private readonly IDbContextFactory<AppDbContext>? _dbFactory;
    private readonly Action<UnreadyTrackItem>? _openInCueForge;
    private readonly Func<Guid, string, Task>? _prepForMix;
    private readonly ILogger? _logger;
    private CancellationTokenSource? _refreshCts;

    private Guid _playlistId;
    private string _playlistTitle = string.Empty;
    private bool _isLoading;
    private bool _hasError;
    private string? _errorMessage;
    private int _readyCount;
    private int _totalCount;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<UnreadyTrackItem> UnreadyTracks { get; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand PrepForMixCommand { get; }
    public ICommand OpenInCueForgeCommand { get; }

    public bool IsVisible => _playlistId != Guid.Empty;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetField(ref _isLoading, value))
            {
                RaiseComputedState();
            }
        }
    }

    public bool HasError
    {
        get => _hasError;
        private set
        {
            if (SetField(ref _hasError, value))
            {
                RaiseComputedState();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetField(ref _errorMessage, value);
    }

    public int ReadyCount
    {
        get => _readyCount;
        private set
        {
            if (SetField(ref _readyCount, value))
            {
                RaiseComputedState();
            }
        }
    }

    public int TotalCount
    {
        get => _totalCount;
        private set
        {
            if (SetField(ref _totalCount, value))
            {
                RaiseComputedState();
            }
        }
    }

    public bool IsEmpty => !IsLoading && !HasError && TotalCount == 0;
    public bool AllReady => !IsLoading && !HasError && TotalCount > 0 && ReadyCount == TotalCount;
    public bool HasUnreadyTracks => !IsLoading && !HasError && UnreadyTracks.Count > 0;
    public bool CanPrepForMix => !IsLoading && !HasError && !IsEmpty && !AllReady;

    public string BadgeText
    {
        get
        {
            if (IsLoading) return "Checking…";
            if (HasError) return "Readiness error";
            if (TotalCount == 0) return "Ready 0/0";
            return $"Ready {ReadyCount}/{TotalCount}";
        }
    }

    public string BadgeTooltip
    {
        get
        {
            if (IsLoading) return "Checking BPM, beat grid, cues, and loudness…";
            if (HasError) return $"Could not check mix readiness: {ErrorMessage}";
            if (TotalCount == 0) return "No local tracks downloaded in this playlist yet.";
            if (AllReady) return $"All {TotalCount} local tracks have BPM, beat grid, cues, and loudness.";
            return $"{TotalCount - ReadyCount} of {TotalCount} track(s) still need prep — click to view or run Prep for Mix.";
        }
    }

    public PlaylistReadinessViewModel(
        IDbContextFactory<AppDbContext>? dbFactory,
        Action<UnreadyTrackItem>? openInCueForge = null,
        Func<Guid, string, Task>? prepForMix = null,
        ILogger? logger = null)
    {
        _dbFactory = dbFactory;
        _openInCueForge = openInCueForge;
        _prepForMix = prepForMix;
        _logger = logger;

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(_playlistId, _playlistTitle));
        PrepForMixCommand = new AsyncRelayCommand(ExecutePrepForMixAsync);
        OpenInCueForgeCommand = new RelayCommand<UnreadyTrackItem>(item =>
        {
            if (item is not null) _openInCueForge?.Invoke(item);
        });
    }

    private async Task ExecutePrepForMixAsync()
    {
        if (_playlistId == Guid.Empty || _prepForMix is null) return;
        await _prepForMix(_playlistId, _playlistTitle);
        await RefreshAsync(_playlistId, _playlistTitle);
    }

    public async Task RefreshAsync(Guid? playlistId, string? playlistTitle = null, CancellationToken cancellationToken = default)
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _refreshCts.Token;

        _playlistId = playlistId ?? Guid.Empty;
        _playlistTitle = playlistTitle ?? string.Empty;
        OnPropertyChanged(nameof(IsVisible));

        if (_playlistId == Guid.Empty || _dbFactory is null)
        {
            ReadyCount = 0;
            TotalCount = 0;
            HasError = false;
            ErrorMessage = null;
            UnreadyTracks.Clear();
            RaiseComputedState();
            return;
        }

        IsLoading = true;
        HasError = false;
        ErrorMessage = null;

        try
        {
            var snapshot = await ComputeSnapshotAsync(_playlistId, ct);
            if (ct.IsCancellationRequested) return;

            void Apply()
            {
                ReadyCount = snapshot.ReadyCount;
                TotalCount = snapshot.TotalCount;
                UnreadyTracks.Clear();
                foreach (var item in snapshot.Unready)
                {
                    UnreadyTracks.Add(item);
                }
                IsLoading = false;
                RaiseComputedState();
            }

            if (Avalonia.Application.Current is not null && !Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(Apply);
            }
            else
            {
                Apply();
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[PlaylistReadiness] Failed to compute readiness for playlist {PlaylistId}", _playlistId);
            void ApplyError()
            {
                HasError = true;
                ErrorMessage = ex.Message;
                IsLoading = false;
                RaiseComputedState();
            }

            if (Avalonia.Application.Current is not null && !Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(ApplyError);
            }
            else
            {
                ApplyError();
            }
        }
    }

    private async Task<(int ReadyCount, int TotalCount, List<UnreadyTrackItem> Unready)> ComputeSnapshotAsync(
        Guid playlistId,
        CancellationToken ct)
    {
        await using var db = await _dbFactory!.CreateDbContextAsync(ct);

        var tracks = await db.PlaylistTracks
            .AsNoTracking()
            .Where(t => t.PlaylistId == playlistId && t.Status == TrackStatus.Downloaded && !string.IsNullOrEmpty(t.TrackUniqueHash))
            .OrderBy(t => t.SortOrder)
            .Select(t => new { t.Id, t.TrackUniqueHash, t.Artist, t.Title })
            .ToListAsync(ct);

        if (tracks.Count == 0)
        {
            return (0, 0, new List<UnreadyTrackItem>());
        }

        var hashes = tracks
            .Select(t => t.TrackUniqueHash)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var featuresByHash = await db.AudioFeatures
            .AsNoTracking()
            .Where(f => hashes.Contains(f.TrackUniqueHash))
            .ToDictionaryAsync(f => f.TrackUniqueHash, StringComparer.OrdinalIgnoreCase, ct);

        var allCues = await db.CuePoints
            .AsNoTracking()
            .Where(c => hashes.Contains(c.TrackUniqueHash))
            .ToListAsync(ct);

        var cuesByHash = allCues
            .GroupBy(c => c.TrackUniqueHash, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<CuePointEntity>)g.ToList(), StringComparer.OrdinalIgnoreCase);

        int ready = 0;
        var unready = new List<UnreadyTrackItem>();

        foreach (var track in tracks)
        {
            featuresByHash.TryGetValue(track.TrackUniqueHash, out var features);
            cuesByHash.TryGetValue(track.TrackUniqueHash, out var cues);

            var evaluation = TrackMixReadinessEvaluator.Evaluate(features, cues);
            if (evaluation.IsReady)
            {
                ready++;
            }
            else
            {
                unready.Add(new UnreadyTrackItem(
                    TrackId: track.Id,
                    TrackUniqueHash: track.TrackUniqueHash,
                    Artist: track.Artist ?? string.Empty,
                    Title: track.Title ?? track.TrackUniqueHash,
                    MissingSummary: string.Join(" · ", evaluation.MissingItems)));
            }
        }

        return (ready, tracks.Count, unready);
    }

    private void RaiseComputedState()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(AllReady));
        OnPropertyChanged(nameof(HasUnreadyTracks));
        OnPropertyChanged(nameof(CanPrepForMix));
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(BadgeTooltip));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
