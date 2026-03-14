using System;
using System.Linq;
using SLSKDONET.Services;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.ComponentModel;
using System.Collections.Specialized;
using DynamicData;
using ReactiveUI;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels;

public class SearchFilterViewModel : ReactiveObject
{
    // Throttled Bitrate
    private int _minBitrate = 128;
    public int MinBitrate 
    { 
        get => _minBitrate; 
        set
        {
            if (_minBitrate == value) return;
            this.RaiseAndSetIfChanged(ref _minBitrate, value);
            
            // Phase 12.6: Sync bitrate as "320+" style token
            if (!_isSyncingFromQuery)
                OnTokenSyncRequested?.Invoke($"{value}+", true);
        }
    }

    // Formats
    public ObservableCollection<string> SelectedFormats { get; } = new ObservableCollection<string>(new[] { "MP3", "FLAC", "WAV" });
    
    // Reliability
    private bool _useHighReliability;
    public bool UseHighReliability 
    { 
        get => _useHighReliability; 
        set => this.RaiseAndSetIfChanged(ref _useHighReliability, value); 
    }

    // Phase 12.6: Curation Assistant
    private bool _hideSuspects = true;
    public bool HideSuspects
    {
        get => _hideSuspects;
        set => this.RaiseAndSetIfChanged(ref _hideSuspects, value);
    }

    // Phase 19: The Bouncer
    private BouncerMode _bouncerMode = BouncerMode.Standard;
    public BouncerMode BouncerMode
    {
        get => _bouncerMode;
        set => this.RaiseAndSetIfChanged(ref _bouncerMode, value);
    }

    // Phase 12.6: Bi-directional sync infrastructure
    private bool _isSyncingFromQuery;
    
    /// <summary>
    /// Callback to notify parent ViewModel when a token should be injected/removed from search bar.
    /// Args: (token, shouldAdd)
    /// </summary>
    public Action<string, bool>? OnTokenSyncRequested { get; set; }

    /// <summary>
    /// Execute filter changes without triggering reverse sync to search bar.
    /// Use when parsing tokens from query.
    /// </summary>
    public void SetFromQueryParsing(Action action)
    {
        _isSyncingFromQuery = true;
        try { action(); }
        finally { _isSyncingFromQuery = false; }
    }

    // Format Toggles (Helpers for UI binding)
    public bool FilterMp3
    {
        get => SelectedFormats.Contains("MP3");
        set => ToggleFormat("MP3", value);
    }

    public bool FilterFlac
    {
        get => SelectedFormats.Contains("FLAC");
        set => ToggleFormat("FLAC", value);
    }

    public bool FilterWav
    {
        get => SelectedFormats.Contains("WAV");
        set => ToggleFormat("WAV", value);
    }

    public SearchFilterViewModel()
    {
        // React to collection changes to trigger UI updates for toggle properties
        SelectedFormats.CollectionChanged += (s, e) => 
        {
            this.RaisePropertyChanged(nameof(FilterMp3));
            this.RaisePropertyChanged(nameof(FilterFlac));
            this.RaisePropertyChanged(nameof(FilterWav));
        };
    }

    private void ToggleFormat(string format, bool isSelected)
    {
        if (isSelected && !SelectedFormats.Contains(format))
            SelectedFormats.Add(format);
        else if (!isSelected && SelectedFormats.Contains(format))
            SelectedFormats.Remove(format);
        
        // Phase 12.6: Bi-directional sync - notify search bar
        if (!_isSyncingFromQuery)
            OnTokenSyncRequested?.Invoke(format.ToLowerInvariant(), isSelected);
    }

    public IObservable<Func<SearchResult, bool>> FilterChanged => 
        this.WhenAnyValue(
            x => x.MinBitrate,
            x => x.UseHighReliability,
            x => x.HideSuspects,
            x => x.BouncerMode) // Phase 19
            .Throttle(TimeSpan.FromMilliseconds(200), RxApp.MainThreadScheduler)
            .Merge(
                Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                    h => SelectedFormats.CollectionChanged += h, 
                    h => SelectedFormats.CollectionChanged -= h)
                .Select(_ => System.Reactive.Unit.Default)
                .Select(_ => (MinBitrate, UseHighReliability, HideSuspects, BouncerMode)) // Updated tuple
            )
            .Select(_ => GetFilterPredicate());


    public Func<SearchResult, bool> GetFilterPredicate()
    {
        // Capture current state values to avoid closure issues if they change during evaluation (though usually strictly sequential)
        var minBitrate = MinBitrate;
        var formats = SelectedFormats.Select(f => f.ToUpperInvariant()).ToHashSet(); // HashSet for O(1)
        var highReliability = UseHighReliability;
        var hideSuspects = HideSuspects; // Phase 12.6: Curation quality
        var bouncerMode = BouncerMode; // Phase 19: The Bouncer
        
        // Return a single optimized function
        return result => 
        {
            if (result.Model == null) return false;

            // Phase 19: The Bouncer Logic
            // Calculate Tier for Bouncer checks
            var tier = Services.MetadataForensicService.CalculateTier(result.Model);

            if (bouncerMode == BouncerMode.Strict)
            {
                // Strict: Only Gold or Platinum
                if (tier != SearchTier.Platinum && tier != SearchTier.Gold) return false;
            }
            else if (bouncerMode == BouncerMode.Standard)
            {
                // Standard: Block Garbage
                if (tier == SearchTier.Garbage) return false;
            }
            
            // 1. Bitrate Check with "Bucket Logic" for VBR
            // If user asks for 320, we allow V0 (~240+)
            // If user asks for 256, we allow ~220
            int effectiveMin = minBitrate;
            if (minBitrate >= 320) effectiveMin = 240;      // Allow V0
            else if (minBitrate >= 256) effectiveMin = 220; // Allow V1
            else if (minBitrate >= 192) effectiveMin = 180; // Allow V2

            if (result.Bitrate < effectiveMin) return false;

            // 2. Format
            // Normalize extension
            var ext = System.IO.Path.GetExtension(result.Model.Filename)?.TrimStart('.')?.ToUpperInvariant() ?? "";
            
            // Map "MPEG Layer 3" etc if needed, but usually extension is "mp3"
            if (!formats.Contains(ext)) return false; 

            // 3. Reliability (Queue Length)
            // If High Reliability is ON, reject queues > 50
            if (highReliability && result.QueueLength > 50) return false;

            // Phase 12.6: Hide potential fakes/upscales (Operational Hardening)
            if (hideSuspects)
            {
                if (result.IntegrityStatus == "Suspect" || SLSKDONET.Services.MetadataForensicService.IsFake(result.Model)) 
                    return false;

                // 4. Manual Forensic Size Gate (Phase 11.5)
                if (result.Size > 0 && result.Length > 0 && result.Bitrate > 0)
                {
                    double expectedBytes = (result.Bitrate * 1000.0 / 8.0) * result.Length.Value;
                    // Formula: ±15% variance + 10% buffer = ±25% total allowance
                    if (result.Size < (expectedBytes * 0.75) || result.Size > (expectedBytes * 1.25))
                    {
                        return false;
                    }
                }
            }

            return true;
        };
    }

    public bool IsMatch(SearchResult result) 
    {
            if (result.Model == null) return false;

            // Phase 19: The Bouncer Logic
            var tier = Services.MetadataForensicService.CalculateTier(result.Model);

            if (BouncerMode == BouncerMode.Strict)
            {
                if (tier != SearchTier.Platinum && tier != SearchTier.Gold) return false;
            }
            else if (BouncerMode == BouncerMode.Standard)
            {
                if (tier == SearchTier.Garbage) return false;
            }

            // 1. Bitrate Check
            int effectiveMin = MinBitrate;
            if (MinBitrate >= 320) effectiveMin = 240;      
            else if (MinBitrate >= 256) effectiveMin = 220; 
            else if (MinBitrate >= 192) effectiveMin = 180; 

            if (result.Bitrate < effectiveMin) return false;

            // 2. Format
            var ext = System.IO.Path.GetExtension(result.Model.Filename)?.TrimStart('.')?.ToUpperInvariant() ?? "";
            
            if (!SelectedFormats.Contains(ext)) return false; 

            // 3. Reliability
            if (UseHighReliability && result.QueueLength > 50) return false;

            // Phase 12.6: Hide potential fakes/upscales
            if (HideSuspects && (result.IntegrityStatus == "Suspect" || Services.MetadataForensicService.IsFake(result.Model))) 
                return false;

            return true;
    }

    public void Reset()
    {
        MinBitrate = 320;
        UseHighReliability = false;
        
        // Reset formats (avoid triggering too many updates)
        if (SelectedFormats.Count != 3) 
        {
             SelectedFormats.Clear();
             SelectedFormats.Add("MP3");
             SelectedFormats.Add("FLAC");
             SelectedFormats.Add("WAV");
        }
    }
}
