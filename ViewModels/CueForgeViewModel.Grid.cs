using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Models;
using SLSKDONET.Services.AudioAnalysis;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Cue Forge beat-grid behaviour added for the grid epic (#219): undoable grid edits (#222),
/// variable-tempo sections (#226) and the suggested-grid compare overlay (#221).
/// </summary>
public sealed partial class CueForgeViewModel
{
    // ── Undoable grid edits (#222) ─────────────────────────────────────────

    /// <summary>
    /// Runs a grid edit through the edit service and, when it succeeds, records it on the session's
    /// undo history (snapshot before/after), so Ctrl+Z / Ctrl+Y step through grid and cue edits in order.
    /// </summary>
    private async Task<GridEditResult> EditGridAsync(string hash, string description, Func<IBeatGridEditService, Task<GridEditResult>> edit)
    {
        var service = _gridEditService!;
        GridSnapshot? before = null;
        try { before = await service.GetSnapshotAsync(hash, CancellationToken.None); }
        catch (Exception ex) { _logger.LogWarning(ex, "CueForge: couldn't snapshot the grid before '{Edit}'; the edit won't be undoable", description); }

        var result = await edit(service);
        if (!result.Success || before is null) return result;

        GridSnapshot? after = null;
        try { after = await service.GetSnapshotAsync(hash, CancellationToken.None); }
        catch (Exception ex) { _logger.LogWarning(ex, "CueForge: couldn't snapshot the grid after '{Edit}'; the edit won't be undoable", description); }
        if (after is not null)
        {
            await _ui(() => Session.PushExternalEdit(new ExternalEdit(description,
                () => RestoreGridSnapshotAsync(hash, before),
                () => RestoreGridSnapshotAsync(hash, after))));
        }
        return result;
    }

    private async Task<bool> RestoreGridSnapshotAsync(string hash, GridSnapshot snapshot)
    {
        if (_gridEditService is null || TrackHash != hash) return false;
        var result = await _gridEditService.RestoreSnapshotAsync(hash, snapshot, CancellationToken.None);
        if (!result.Success)
        {
            _logger.LogWarning("CueForge: restoring a grid snapshot failed for {Hash}: {Reason}", hash, result.Reason);
            return false;
        }
        await ReloadGridFromStoreAsync(hash);
        return true;
    }

    /// <summary>
    /// Re-reads the stored grid (BPM, beats, Bar 1, provenance, suggestion, sections) and the phrase map
    /// into the editor — after undo/redo, a section edit or using a suggestion. Keeps the session (and so
    /// its undo history), unlike a full track reload.
    /// </summary>
    internal async Task ReloadGridFromStoreAsync(string hash)
    {
        if (_libraryService is null) return;
        AudioFeaturesEntity? features;
        List<PhraseSegment> phrases;
        try
        {
            features = await _libraryService.GetAudioFeaturesByHashAsync(hash);
            if (features is null || TrackHash != hash) return;
            phrases = PhraseSourceResolver.Resolve(await _libraryService.GetPhrasesByHashAsync(hash), features.TrackDuration);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CueForge: reloading the grid failed for {Hash}", hash);
            Session.SetStatus("Couldn't reload the beat grid; reopen the track to see the stored grid.", isError: true);
            return;
        }

        await _ui(() =>
        {
            if (TrackHash != hash) return;
            _preciseBpm = features.Bpm > 0 ? features.Bpm : _preciseBpm;
            Bpm = (int)Math.Round(Math.Max(60, _preciseBpm));
            _downbeat = Math.Max(0, features.DownbeatOffsetSeconds);
            ManualBpmInput = _preciseBpm.ToString("0.#", CultureInfo.InvariantCulture);
            IsGridEdited = Math.Abs(_preciseBpm - _analysedBpm) > 1e-4 || Math.Abs(_downbeat - _analysedDownbeat) > 1e-4;
            _gridSource = features.GridSource;
            _gridConfidence = features.GridConfidence;
            var suggestion = GridWriteRules.ParseSuggestion(features.AnalysedGridJson);
            _hasGridSuggestion = suggestion is not null;
            _gridSuggestionSource = suggestion?.Source ?? "";
            _beatGrid = BeatGridFitter.ParseTicks(features.BeatGridJson);
            ApplyDerivedGridState(features);
            if (_metronomeEnabled) _transport.SetMetronome(_beatGrid, _downbeat, true);

            _resolvedPhrases = phrases;
            PhraseSegments.Clear();
            foreach (var p in phrases) PhraseSegments.Add(p);
            Session.SetStructureHints(PhraseSegments.ToList());

            RaiseGridChanged();
            Session.UpdateGrid(_downbeat);
            this.RaisePropertyChanged(nameof(GridStatusDisplay));
            this.RaisePropertyChanged(nameof(HasGridSuggestion));
            this.RaisePropertyChanged(nameof(GridSuggestionDisplay));
        });
    }

    // ── Variable tempo (#226) ──────────────────────────────────────────────

    private IReadOnlyList<BeatGridFitter.TempoSection> _tempoSections = Array.Empty<BeatGridFitter.TempoSection>();
    private bool _isVariableTempo;

    /// <summary>The grid has more than one tempo section (drawn from the beat list, not one BPM).</summary>
    public bool IsVariableTempo
    {
        get => _isVariableTempo;
        private set => this.RaiseAndSetIfChanged(ref _isVariableTempo, value);
    }

    /// <summary>The beat list for the waveform when the grid is variable; null = draw from GridBpm.</summary>
    public IReadOnlyList<double>? VariableBeatTimes => _isVariableTempo && _beatGrid.Count > 1 ? _beatGrid : null;

    /// <summary>"3 tempo sections: 108 → 128 → 108 BPM" (mono font in the view).</summary>
    public string TempoSectionsDisplay => _tempoSections.Count < 2 ? ""
        : $"{_tempoSections.Count} tempo sections: " + string.Join(" → ", _tempoSections.Select(s => SLSKDONET.Utils.NumFormat.F(s.Bpm, 2))) + " BPM";

    private string _sectionBpmInput = "";
    /// <summary>BPM typed for "Tempo from here" / "Set section BPM".</summary>
    public string SectionBpmInput
    {
        get => _sectionBpmInput;
        set => this.RaiseAndSetIfChanged(ref _sectionBpmInput, value ?? "");
    }

    public ReactiveCommand<Unit, Unit> AddTempoChangeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> SetSectionBpmCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> RemoveTempoChangeCommand { get; private set; } = null!;

    // ── Suggested-grid compare overlay (#221) ──────────────────────────────

    private double[]? _suggestedBeats;
    private double _suggestedDownbeat;
    private bool _isGridCompareOn;

    /// <summary>Overlays the suggested grid's bar lines on the waveform, to compare before choosing.</summary>
    public bool IsGridCompareOn
    {
        get => _isGridCompareOn;
        set
        {
            this.RaiseAndSetIfChanged(ref _isGridCompareOn, value);
            this.RaisePropertyChanged(nameof(CompareBeatTimes));
        }
    }

    public IReadOnlyList<double>? CompareBeatTimes => _isGridCompareOn && _hasGridSuggestion ? _suggestedBeats : null;
    public double CompareDownbeat => _suggestedDownbeat;

    private void InitGridCommands(IObservable<bool> hasTrack)
    {
        AddTempoChangeCommand = ReactiveCommand.CreateFromTask(() => SetTempoAtAsync(fromSectionStart: false), hasTrack);
        SetSectionBpmCommand = ReactiveCommand.CreateFromTask(() => SetTempoAtAsync(fromSectionStart: true), hasTrack);
        RemoveTempoChangeCommand = ReactiveCommand.CreateFromTask(RemoveTempoChangeAtPlayheadAsync, this.WhenAnyValue(x => x.IsVariableTempo));
    }

    /// <summary>Sections, the variable flag and the suggestion's beats from a stored grid (UI thread).</summary>
    private void ApplyDerivedGridState(AudioFeaturesEntity f)
    {
        bool clean = string.Equals(f.GridSource, GridWriteRules.SourceUser, StringComparison.Ordinal)
                     || string.Equals(f.GridSource, GridWriteRules.SourceRekordbox, StringComparison.Ordinal);
        _tempoSections = f.IsVariableTempo && _beatGrid.Count > 1
            ? (clean ? BeatGridFitter.ExactSections(_beatGrid) : BeatGridFitter.FitSections(_beatGrid))
            : Array.Empty<BeatGridFitter.TempoSection>();
        IsVariableTempo = _tempoSections.Count > 1;

        var suggestion = GridWriteRules.ParseSuggestion(f.AnalysedGridJson);
        _suggestedBeats = suggestion?.Beats;
        _suggestedDownbeat = suggestion?.Downbeat ?? 0;
        if (suggestion is null) _isGridCompareOn = false;

        this.RaisePropertyChanged(nameof(VariableBeatTimes));
        this.RaisePropertyChanged(nameof(TempoSectionsDisplay));
        this.RaisePropertyChanged(nameof(IsGridCompareOn));
        this.RaisePropertyChanged(nameof(CompareBeatTimes));
        this.RaisePropertyChanged(nameof(CompareDownbeat));
    }

    private async Task SetTempoAtAsync(bool fromSectionStart)
    {
        if (TrackHash is not { } hash) return;
        if (_gridEditService is null) { Session.SetStatus("The beat-grid editor is unavailable.", isError: true); return; }
        string input = (SectionBpmInput ?? "").Trim().Replace(',', '.');
        if (!double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out double bpm) || bpm is < 40 or > 400)
        {
            Session.SetStatus("Type a BPM between 40 and 400 first.", isError: true);
            return;
        }

        double at = CurrentPlayPosition;
        if (fromSectionStart && _tempoSections.Count > 0)
            at = _tempoSections.LastOrDefault(s => s.StartSeconds <= CurrentPlayPosition + 1e-3)?.StartSeconds ?? _tempoSections[0].StartSeconds;

        string what = fromSectionStart ? "section tempo" : "tempo change";
        var result = await EditGridAsync(hash, what, s => s.SetTempoFromAsync(hash, at, bpm, CancellationToken.None));
        if (!result.Success) { Session.SetStatus(result.Reason ?? "Couldn't change the tempo; nothing was changed.", isError: true); return; }
        await ReloadGridFromStoreAsync(hash);
        Session.SetStatus($"{SLSKDONET.Utils.NumFormat.F(bpm, 2)} BPM from {CueForgeGrid.FormatTime(at)}. Re-run Auto-generate to re-place the auto cues.");
    }

    private async Task RemoveTempoChangeAtPlayheadAsync()
    {
        if (TrackHash is not { } hash) return;
        if (_gridEditService is null) { Session.SetStatus("The beat-grid editor is unavailable.", isError: true); return; }
        var result = await EditGridAsync(hash, "tempo change removal", s => s.RemoveTempoChangeAsync(hash, CurrentPlayPosition, CancellationToken.None));
        if (!result.Success) { Session.SetStatus(result.Reason ?? "Couldn't remove the tempo change; nothing was changed.", isError: true); return; }
        await ReloadGridFromStoreAsync(hash);
        Session.SetStatus("Tempo change removed.");
    }
}
