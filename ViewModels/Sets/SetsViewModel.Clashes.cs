using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Services.Sets;

namespace SLSKDONET.ViewModels.Sets;

// "Check clashes" (rebuild plan P4.4, #205): an on-demand sweep whose findings join the Set Health
// issues (each jumps to its slot). Results belong to the exact set they were checked on: any change
// to the set's order drops them and asks for a re-check, so stale clashes are never shown.
public sealed partial class SetsViewModel
{
    private IReadOnlyList<SetClash> _clashes = Array.Empty<SetClash>();
    private string? _clashSignature;

    private bool _isCheckingClashes;
    public bool IsCheckingClashes { get => _isCheckingClashes; private set => this.RaiseAndSetIfChanged(ref _isCheckingClashes, value); }

    private string? _clashStatus;
    /// <summary>What the last "Check clashes" covered, or that the set changed since.</summary>
    public string? ClashStatus { get => _clashStatus; private set => this.RaiseAndSetIfChanged(ref _clashStatus, value); }

    private string SlotSignature() =>
        (SelectedSet?.Id.ToString() ?? string.Empty) + "|" + string.Join(",", Slots.Select(s => s.SlotId));

    /// <summary>Set Health plus the clash findings, when they still describe the current set.</summary>
    private SetHealthSummary BuildHealth()
    {
        if (_clashSignature is not null && _clashSignature != SlotSignature())
        {
            _clashes = Array.Empty<SetClash>();
            _clashSignature = null;
            ClashStatus = Slots.Count >= 2 ? "The set changed: check clashes again." : null;
        }
        var extra = _clashes.Select(c => new HealthIssue(c.Text, c.ToSlotId)).ToList();
        return new SetHealthSummary(Slots.ToList(), LaneItems.OfType<TransitionChipViewModel>().ToList(),
            SelectedEventPreset.Preset, extra);
    }

    /// <summary>
    /// Checks every transition for section, vocal and artist clashes (<see cref="SetClashCheck"/>) and
    /// adds what it finds to the Set Health issues. Changes nothing in the set.
    /// </summary>
    public async Task CheckClashesAsync()
    {
        if (SelectedSet is null || Slots.Count < 2 || IsCheckingClashes)
            return;
        IsCheckingClashes = true;
        var signature = SlotSignature();
        try
        {
            var slots = Slots.Select(s => new ClashSlot(s.SlotId, s.Number, s.Slot.TrackUniqueHash, s.Title, s.Artist, s.Slot.Bpm)).ToList();
            var mix = await _sets.GetMixDataAsync(slots.Select(s => s.TrackUniqueHash).ToList());

            var fits = new Dictionary<(string, string), double>();
            if (_sectionVectors is not null)
                for (var i = 1; i < slots.Count; i++)
                {
                    var (from, to) = (slots[i - 1].TrackUniqueHash, slots[i].TrackUniqueHash);
                    foreach (var (_, score) in await _sectionVectors.FindNextCandidatesAsync(from, new[] { to }))
                        fits[(from, to)] = score;
                }

            if (signature != SlotSignature())
            {
                ClashStatus = "The set changed while checking: check clashes again.";
                return;
            }

            var result = SetClashCheck.Run(slots, mix, (a, b) => fits.TryGetValue((a, b), out var f) ? f : null);
            _clashes = result.Clashes;
            _clashSignature = signature;
            ClashStatus = result.Summary;
            Health = BuildHealth();
        }
        catch (Exception ex)
        {
            ReportFailure("Check clashes failed", ex);
        }
        finally
        {
            IsCheckingClashes = false;
        }
    }
}
