using System;
using System.Collections.Generic;
using System.Reactive;
using ReactiveUI;

namespace SLSKDONET.ViewModels.EditLab;

/// <summary>
/// Four-stem mute/preset rack brought from Workstation into Edit Lab (Epic #517 / #541 FO5a) so
/// stem auditioning and quick presets (Instrumental, Drums only, No vocals) live inside Edit Lab
/// before Workstation is retired.
/// </summary>
public sealed class EditLabStemRackViewModel : ReactiveObject
{
    private bool _vocalsMuted;
    private bool _drumsMuted;
    private bool _bassMuted;
    private bool _otherMuted;

    public Action? OnStemMuteChanged { get; set; }

    public bool VocalsMuted
    {
        get => _vocalsMuted;
        set { if (this.RaiseAndSetIfChanged(ref _vocalsMuted, value) == value) NotifyChanged(); }
    }

    public bool DrumsMuted
    {
        get => _drumsMuted;
        set { if (this.RaiseAndSetIfChanged(ref _drumsMuted, value) == value) NotifyChanged(); }
    }

    public bool BassMuted
    {
        get => _bassMuted;
        set { if (this.RaiseAndSetIfChanged(ref _bassMuted, value) == value) NotifyChanged(); }
    }

    public bool OtherMuted
    {
        get => _otherMuted;
        set { if (this.RaiseAndSetIfChanged(ref _otherMuted, value) == value) NotifyChanged(); }
    }

    public string StemControlSummary
    {
        get
        {
            if (!VocalsMuted && !DrumsMuted && !BassMuted && !OtherMuted)
                return "All stems active";
            if (VocalsMuted && !DrumsMuted && !BassMuted && !OtherMuted)
                return "Instrumental (vocals muted)";
            if (VocalsMuted && !DrumsMuted && BassMuted && OtherMuted)
                return "Drums only";

            var muted = new List<string>(4);
            if (VocalsMuted) muted.Add("Vox");
            if (DrumsMuted) muted.Add("Drum");
            if (BassMuted) muted.Add("Bass");
            if (OtherMuted) muted.Add("Other");
            return $"Muted: {string.Join(", ", muted)}";
        }
    }

    public ReactiveCommand<Unit, Unit> ToggleInstrumentalCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleDrumsOnlyCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleVocalOffCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetAllStemsCommand { get; }

    public EditLabStemRackViewModel()
    {
        ToggleInstrumentalCommand = ReactiveCommand.Create(() =>
        {
            bool alreadyInstrumental = VocalsMuted && !DrumsMuted && !BassMuted && !OtherMuted;
            SetMutes(vocals: !alreadyInstrumental, drums: false, bass: false, other: false);
        });

        ToggleDrumsOnlyCommand = ReactiveCommand.Create(() =>
        {
            bool alreadyDrumsOnly = VocalsMuted && !DrumsMuted && BassMuted && OtherMuted;
            SetMutes(vocals: !alreadyDrumsOnly, drums: false, bass: !alreadyDrumsOnly, other: !alreadyDrumsOnly);
        });

        ToggleVocalOffCommand = ReactiveCommand.Create(() =>
        {
            VocalsMuted = !VocalsMuted;
        });

        ResetAllStemsCommand = ReactiveCommand.Create(() =>
        {
            SetMutes(vocals: false, drums: false, bass: false, other: false);
        });
    }

    private void SetMutes(bool vocals, bool drums, bool bass, bool other)
    {
        _vocalsMuted = vocals;
        _drumsMuted = drums;
        _bassMuted = bass;
        _otherMuted = other;
        this.RaisePropertyChanged(nameof(VocalsMuted));
        this.RaisePropertyChanged(nameof(DrumsMuted));
        this.RaisePropertyChanged(nameof(BassMuted));
        this.RaisePropertyChanged(nameof(OtherMuted));
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        this.RaisePropertyChanged(nameof(StemControlSummary));
        OnStemMuteChanged?.Invoke();
    }
}
