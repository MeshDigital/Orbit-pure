using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Services;
using SLSKDONET.Services.Copilot;
using SLSKDONET.Services.Sets;
using SLSKDONET.Utils;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.Sets;

// "Draft a set" (rebuild plan P4.5, #205): the Copilot's SetBuilder picks a spine for a target BPM
// and length (and optional seed / event curve); it always opens as a NEW Draft set the DJ then edits.
// It never changes an existing set.
public sealed partial class SetsViewModel
{
    /// <summary>BPM band around the target, as wide as Suggest next's.</summary>
    public const double DraftBpmVariance = NextTrackRanker.BpmWindow;

    private readonly IOrbitCopilot? _copilot;

    /// <summary>Draft a set is only offered when the Copilot is available (it is in the app).</summary>
    public bool CanDraftSet => _copilot is not null;

    private decimal? _draftBpm;
    public decimal? DraftBpm { get => _draftBpm; set => this.RaiseAndSetIfChanged(ref _draftBpm, value); }

    private decimal? _draftMinutes = 60;
    public decimal? DraftMinutes { get => _draftMinutes; set => this.RaiseAndSetIfChanged(ref _draftMinutes, value); }

    private EventPresetOption _draftPreset = EventPresetOption.None;
    /// <summary>The event whose energy curve shapes the draft ("No event" = flow only).</summary>
    public EventPresetOption DraftPreset { get => _draftPreset; set => this.RaiseAndSetIfChanged(ref _draftPreset, value ?? EventPresetOption.None); }

    private bool _draftFromSelected;
    /// <summary>Open the draft with the selected slot's track.</summary>
    public bool DraftFromSelected { get => _draftFromSelected; set => this.RaiseAndSetIfChanged(ref _draftFromSelected, value); }

    private bool _isDrafting;
    public bool IsDrafting { get => _isDrafting; private set => this.RaiseAndSetIfChanged(ref _isDrafting, value); }

    private string? _draftStatus;
    /// <summary>What the last draft produced, or why it couldn't.</summary>
    public string? DraftStatus { get => _draftStatus; private set => this.RaiseAndSetIfChanged(ref _draftStatus, value); }

    /// <summary>Prefills the form when it opens: BPM from the selected slot, else the set's median.</summary>
    public void PrepareDraft()
    {
        DraftStatus = null;
        DraftFromSelected = DraftFromSelected && SelectedSlot is not null;
        if (DraftBpm is not null)
            return;
        var bpm = SelectedSlot?.Slot.Bpm is > 0
            ? SelectedSlot.Slot.Bpm
            : Slots.Select(s => s.Slot.Bpm).Where(b => b is > 0).Select(b => b!.Value).OrderBy(b => b).ToList() is { Count: > 0 } list
                ? list[list.Count / 2]
                : null;
        if (bpm is double b)
            DraftBpm = Math.Round((decimal)b, 1);
    }

    /// <summary>Builds a spine and opens it as a new Draft set.</summary>
    public async Task DraftSetAsync()
    {
        if (_copilot is null || IsDrafting)
            return;
        var seed = DraftFromSelected ? SelectedSlot?.Slot : null;
        var bpm = DraftBpm is decimal d && d > 0 ? (double)d : seed?.Bpm;
        if (bpm is not > 0)
        {
            DraftStatus = "Enter a target BPM (or seed it from an analysed slot).";
            return;
        }
        var minutes = DraftMinutes is decimal m && m >= 5 ? (double)m : 60;

        IsDrafting = true;
        DraftStatus = null;
        try
        {
            var preset = DraftPreset.Preset;
            var profile = preset is null ? null : new SLSKDONET.Data.Entities.EventProfileEntity
            {
                Name = EventPresets.NamePrefix + preset.Key,
                TargetEnergyCurveJson = System.Text.Json.JsonSerializer.Serialize(preset.EnergyCurve),
            };
            var result = await _copilot.BuildSetAsync(new SetBuildRequest(
                minutes, profile, DraftBpmVariance, seed?.TrackUniqueHash, bpm));

            var bpmText = bpm.Value.ToString("0", CultureInfo.InvariantCulture);
            if (result.SpineTrackHashes.Count < 2)
            {
                DraftStatus = $"Not enough analysed tracks within {DraftBpmVariance * 100:0} % of {bpmText} BPM to draft a set.";
                return;
            }

            var name = seed is not null
                ? $"Draft from {seed.Title}"
                : $"Draft · {bpmText} BPM · {minutes.ToString("0", CultureInfo.InvariantCulture)} min";
            var created = await _sets.CreateDraftSetAsync(name, result.SpineTrackHashes, preset?.Key);

            var got = result.DurationMinutes.ToString("0", CultureInfo.InvariantCulture);
            var summary = $"{CountFormat.Of(result.SpineTrackHashes.Count, "track")}, {got} min";
            DraftStatus = result.DurationMinutes < minutes * 0.9
                ? $"Drafted {summary}: the {bpmText} BPM pool ran out before {minutes:0} min."
                : $"Drafted {summary}.";
            _notifications?.Show("Sets", $"{DraftStatus} It opens as a new draft: edit it freely.", NotificationType.Success);
            await LoadAsync(created.Id);
        }
        catch (Exception ex)
        {
            ReportFailure("Draft a set failed", ex);
        }
        finally
        {
            IsDrafting = false;
        }
    }
}
