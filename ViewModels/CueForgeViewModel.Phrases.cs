using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels;

/// <summary>
/// The phrase-strip editor (#225): select a phrase, drag a boundary (bar snap 1/8/16/32), set its type,
/// split at the playhead, merge with the next, reset to the analysed map. Every edit is stored as the
/// track's "User" phrase rows (which re-analysis and Rekordbox never overwrite) and is one step on the
/// session's undo history.
/// </summary>
public sealed partial class CueForgeViewModel
{
    public const string UserPhraseSource = "User";

    /// <summary>Snap choices for a dragged boundary, in bars from Bar 1.</summary>
    public static IReadOnlyList<int> PhraseSnapOptions { get; } = new[] { 1, 8, 16, 32 };

    /// <summary>Types a phrase can be set to (the labels the cue generator understands).</summary>
    public static IReadOnlyList<string> PhraseTypeOptions { get; } = new[] { "Intro", "Build", "Drop", "Breakdown", "Outro", "Verse", "Chorus", "Bridge" };

    private int _phraseSnapBars = 1;
    public int PhraseSnapBars
    {
        get => _phraseSnapBars;
        set => this.RaiseAndSetIfChanged(ref _phraseSnapBars, PhraseSnapOptions.Contains(value) ? value : 1);
    }

    private int _selectedPhraseIndex = -1;
    public int SelectedPhraseIndex
    {
        get => _selectedPhraseIndex;
        private set
        {
            this.RaiseAndSetIfChanged(ref _selectedPhraseIndex, value);
            this.RaisePropertyChanged(nameof(HasSelectedPhrase));
            this.RaisePropertyChanged(nameof(SelectedPhraseDisplay));
        }
    }

    public bool HasSelectedPhrase => _selectedPhraseIndex >= 0 && _selectedPhraseIndex < PhraseSegments.Count;

    /// <summary>"Drop · bars 33–48 (16 bars)".</summary>
    public string SelectedPhraseDisplay
    {
        get
        {
            if (!HasSelectedPhrase) return "";
            var p = PhraseSegments[_selectedPhraseIndex];
            if (GridBpm <= 0) return p.Label;
            double bar = 240.0 / GridBpm;
            int first = (int)Math.Round((p.Start - _downbeat) / bar) + 1;
            int count = Math.Max(1, (int)Math.Round(p.Duration / bar));
            return $"{p.Label} · bars {first}–{first + count - 1} ({count} bars)";
        }
    }

    private bool _hasUserPhrases;
    /// <summary>The phrase map is the user's own edit (shows "Reset to analysed").</summary>
    public bool HasUserPhrases
    {
        get => _hasUserPhrases;
        private set => this.RaiseAndSetIfChanged(ref _hasUserPhrases, value);
    }

    public ReactiveCommand<double, Unit> SelectPhraseAtCommand { get; private set; } = null!;
    public ReactiveCommand<PhraseBoundaryMove, Unit> MovePhraseBoundaryCommand { get; private set; } = null!;
    public ReactiveCommand<string, Unit> SetPhraseTypeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> SplitPhraseAtPlayheadCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> MergePhraseWithNextCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ResetPhrasesCommand { get; private set; } = null!;

    private void InitPhraseCommands(IObservable<bool> hasTrack)
    {
        var hasSelection = this.WhenAnyValue(x => x.SelectedPhraseIndex).Select(_ => HasSelectedPhrase);
        SelectPhraseAtCommand = ReactiveCommand.Create<double>(SelectPhraseAt, hasTrack);
        MovePhraseBoundaryCommand = ReactiveCommand.CreateFromTask<PhraseBoundaryMove>(MovePhraseBoundaryAsync, hasTrack);
        SetPhraseTypeCommand = ReactiveCommand.CreateFromTask<string>(SetPhraseTypeAsync, hasSelection);
        SplitPhraseAtPlayheadCommand = ReactiveCommand.CreateFromTask(SplitPhraseAtPlayheadAsync, hasTrack);
        MergePhraseWithNextCommand = ReactiveCommand.CreateFromTask(MergePhraseWithNextAsync,
            hasSelection.Select(has => has && _selectedPhraseIndex + 1 < PhraseSegments.Count));
        ResetPhrasesCommand = ReactiveCommand.CreateFromTask(ResetPhrasesAsync, this.WhenAnyValue(x => x.HasUserPhrases));
    }

    internal void SelectPhraseAt(double seconds)
    {
        int index = -1;
        for (int i = 0; i < PhraseSegments.Count; i++)
        {
            var p = PhraseSegments[i];
            if (seconds >= p.Start && seconds < p.Start + p.Duration) { index = i; break; }
        }
        SelectedPhraseIndex = index;
        if (index >= 0) Session.SetStatus($"{SelectedPhraseDisplay} · drag a boundary to move it, right-click for phrase actions.");
    }

    private List<PhraseSegment> EditablePhrases() =>
        PhraseSegments.Select(p => new PhraseSegment { Label = p.Label, Start = p.Start, Duration = p.Duration, Confidence = p.Confidence, Bars = p.Bars, Beats = p.Beats, Color = p.Color }).ToList();

    private double BarLength => GridBpm > 0 ? 240.0 / GridBpm : 1.0;

    /// <summary>The boundary moves to the bar grid (multiples of <see cref="PhraseSnapBars"/>) unless it was placed off-grid with Shift.</summary>
    internal async Task MovePhraseBoundaryAsync(PhraseBoundaryMove move)
    {
        var phrases = EditablePhrases();
        int i = move.PhraseIndex;
        if (i < 1 || i >= phrases.Count) return;
        var prev = phrases[i - 1];
        var cur = phrases[i];
        double curEnd = cur.Start + cur.Duration;
        double t = Math.Clamp(move.Seconds, prev.Start + BarLength, curEnd - BarLength);
        if (Math.Abs(t - cur.Start) < 1e-3) return;
        prev.Duration = (float)(t - prev.Start);
        cur.Start = (float)t;
        cur.Duration = (float)(curEnd - t);
        await CommitPhrasesAsync(phrases, $"phrase boundary move", selectIndex: i);
    }

    internal async Task SetPhraseTypeAsync(string type)
    {
        if (!HasSelectedPhrase || string.IsNullOrWhiteSpace(type)) return;
        var phrases = EditablePhrases();
        phrases[_selectedPhraseIndex].Label = type;
        await CommitPhrasesAsync(phrases, $"phrase type → {type}", selectIndex: _selectedPhraseIndex);
    }

    /// <summary>Splits the phrase under the playhead at the nearest bar line; both halves keep its type.</summary>
    internal async Task SplitPhraseAtPlayheadAsync()
    {
        var phrases = EditablePhrases();
        double bar = BarLength;
        double t = GridBpm > 0 ? _downbeat + Math.Round((CurrentPlayPosition - _downbeat) / bar) * bar : CurrentPlayPosition;
        int i = phrases.FindIndex(p => t > p.Start + bar * 0.5 && t < p.Start + p.Duration - bar * 0.5);
        if (i < 0)
        {
            Session.SetStatus("Put the playhead inside a phrase, at least a bar from its edges, to split it.", isError: true);
            return;
        }
        var p = phrases[i];
        double end = p.Start + p.Duration;
        p.Duration = (float)(t - p.Start);
        phrases.Insert(i + 1, new PhraseSegment { Label = p.Label, Start = (float)t, Duration = (float)(end - t), Confidence = p.Confidence });
        await CommitPhrasesAsync(phrases, "phrase split", selectIndex: i + 1);
    }

    internal async Task MergePhraseWithNextAsync()
    {
        int i = _selectedPhraseIndex;
        var phrases = EditablePhrases();
        if (i < 0 || i + 1 >= phrases.Count) return;
        var next = phrases[i + 1];
        phrases[i].Duration = (float)(next.Start + next.Duration - phrases[i].Start);
        phrases.RemoveAt(i + 1);
        await CommitPhrasesAsync(phrases, "phrase merge", selectIndex: i);
    }

    /// <summary>Drops the user's phrase map: the analysed map (Rekordbox or heuristic) shows again.</summary>
    internal async Task ResetPhrasesAsync()
    {
        if (TrackHash is not { } hash || _libraryService is null) return;
        await WritePhrasesUndoableAsync(hash, "phrase map reset",
            () => _libraryService.ReplacePhrasesForSourceAsync(hash, UserPhraseSource, Array.Empty<TrackPhraseEntity>()), selectIndex: -1);
        Session.SetStatus("Phrase map back to the analysed one. Ctrl+Z restores your edits.");
    }

    private async Task CommitPhrasesAsync(List<PhraseSegment> phrases, string description, int selectIndex)
    {
        if (TrackHash is not { } hash || _libraryService is null) return;
        var rows = PhraseSourceResolver.ToTrackPhrases(hash, phrases, UserPhraseSource, GridBpm, _downbeat);
        bool ok = await WritePhrasesUndoableAsync(hash, description,
            () => _libraryService.ReplacePhrasesForSourceAsync(hash, UserPhraseSource, rows), selectIndex);
        if (ok) Session.SetStatus($"Phrase map updated ({description}). Your map is kept on re-analysis; Ctrl+Z undoes.");
    }

    /// <summary>Snapshot → write → snapshot → push an undo step → refresh the map. False (and an error status) on failure.</summary>
    private async Task<bool> WritePhrasesUndoableAsync(string hash, string description, Func<Task> write, int selectIndex)
    {
        try
        {
            var before = CloneRows(await _libraryService!.GetPhrasesByHashAsync(hash));
            await write();
            var after = CloneRows(await _libraryService.GetPhrasesByHashAsync(hash));
            await _ui(() => Session.PushExternalEdit(new ExternalEdit(description,
                () => RestorePhraseRowsAsync(hash, before),
                () => RestorePhraseRowsAsync(hash, after))));
            await ReloadPhrasesAsync(hash, selectIndex);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CueForge: saving the phrase map failed for {Hash}", hash);
            Session.SetStatus("Couldn't save the phrase map; nothing was changed. See the log for details.", isError: true);
            return false;
        }
    }

    private async Task<bool> RestorePhraseRowsAsync(string hash, IReadOnlyList<TrackPhraseEntity> rows)
    {
        if (_libraryService is null || TrackHash != hash) return false;
        await _libraryService.ReplaceAllPhrasesAsync(hash, CloneRows(rows));
        await ReloadPhrasesAsync(hash, _selectedPhraseIndex);
        return true;
    }

    /// <summary>Re-resolves the phrase map from the store (User > RekordboxPSSI > Heuristic) into the editor.</summary>
    private async Task ReloadPhrasesAsync(string hash, int selectIndex)
    {
        if (_libraryService is null) return;
        var rows = await _libraryService.GetPhrasesByHashAsync(hash);
        var phrases = PhraseSourceResolver.Resolve(rows, TrackDuration);
        bool user = rows.Count(r => string.Equals(r.Source, UserPhraseSource, StringComparison.Ordinal)) >= 2;
        await _ui(() =>
        {
            if (TrackHash != hash) return;
            _resolvedPhrases = phrases;
            PhraseSegments.Clear();
            foreach (var p in phrases) PhraseSegments.Add(p);
            Session.SetStructureHints(PhraseSegments.ToList());
            HasUserPhrases = user;
            SelectedPhraseIndex = selectIndex < PhraseSegments.Count ? selectIndex : -1;
        });
    }

    private static readonly PropertyInfo[] PhraseRowProperties = typeof(TrackPhraseEntity)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
        .ToArray();

    /// <summary>Detached copies of phrase rows (Id cleared) — undo snapshots must not share tracked instances.</summary>
    private static List<TrackPhraseEntity> CloneRows(IEnumerable<TrackPhraseEntity> rows) =>
        rows.Select(r =>
        {
            var copy = new TrackPhraseEntity();
            foreach (var prop in PhraseRowProperties) prop.SetValue(copy, prop.GetValue(r));
            copy.Id = 0;
            return copy;
        }).ToList();
}
