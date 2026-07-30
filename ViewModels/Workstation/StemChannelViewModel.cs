using System;
using System.Reactive;
using ReactiveUI;
using SLSKDONET.Models.Stem;
using SLSKDONET.Services.Audio;

namespace SLSKDONET.ViewModels;

/// <summary>
/// ViewModel for a single stem channel strip (Vocals / Drums / Bass / Other).
/// Exposes fader, pan, mute, and solo controls that delegate directly to
/// <see cref="StemMixerService"/> for real-time audio routing.
/// </summary>
public sealed class StemChannelViewModel : ReactiveObject
{
    private readonly StemType _stemType;
    private readonly StemMixerService _mixer;

    public string DisplayName { get; }

    // ── Gain ─────────────────────────────────────────────────────────────────

    private float _gainDb;
    public float GainDb
    {
        get => _gainDb;
        set
        {
            // Clamp here too (not just in StemMixerService.SetGain) so the bound/displayed
            // value never disagrees with the gain actually applied to the audio engine.
            float clamped = Math.Clamp(value, -60f, 12f);
            this.RaiseAndSetIfChanged(ref _gainDb, clamped);
            _mixer.SetGain(_stemType, clamped);
        }
    }

    // ── Pan ──────────────────────────────────────────────────────────────────

    private float _pan;
    public float Pan
    {
        get => _pan;
        set
        {
            float clamped = Math.Clamp(value, -1f, 1f);
            this.RaiseAndSetIfChanged(ref _pan, clamped);
            _mixer.SetPan(_stemType, clamped);
        }
    }

    // ── Mute ─────────────────────────────────────────────────────────────────

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            this.RaiseAndSetIfChanged(ref _isMuted, value);
            _mixer.SetMute(_stemType, value);
        }
    }

    // ── Solo ─────────────────────────────────────────────────────────────────

    private bool _isSoloed;
    public bool IsSoloed
    {
        get => _isSoloed;
        set
        {
            this.RaiseAndSetIfChanged(ref _isSoloed, value);
            _mixer.SetSolo(_stemType, value);
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> MuteCommand  { get; }
    public ReactiveCommand<Unit, Unit> SoloCommand  { get; }
    public ReactiveCommand<Unit, Unit> ResetCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public StemChannelViewModel(StemType stemType, StemMixerService mixer)
    {
        _stemType   = stemType;
        _mixer      = mixer;
        DisplayName = stemType.ToString().ToUpperInvariant();

        // Seed from whatever the mixer already has (e.g. restored prefs)
        _gainDb  = mixer.GetGain(stemType);
        _isMuted = mixer.IsMuted(stemType);
        _isSoloed= mixer.IsSoloed(stemType);

        MuteCommand  = ReactiveCommand.Create(() => { IsMuted  = !IsMuted; });
        SoloCommand  = ReactiveCommand.Create(() => { IsSoloed = !IsSoloed; });
        ResetCommand = ReactiveCommand.Create(() =>
        {
            GainDb   = 0f;
            Pan      = 0f;
            IsMuted  = false;
            IsSoloed = false;
        });
    }
}
