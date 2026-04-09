using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ReactiveUI;
using SLSKDONET.Models.Stem;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Audio.Separation;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Manages the four-stem audio mixer for a single workstation deck.
/// Owns the <see cref="StemMixerService"/> mix bus, orchestrates ONNX separation via
/// <see cref="CachedStemSeparator"/>, and exposes per-channel ViewModels for the UI.
/// </summary>
public sealed class StemMixerViewModel : ReactiveObject, IDisposable
{
    private readonly CachedStemSeparator _separator;

    /// <summary>The NAudio mix-bus that feeds into the deck audio chain.</summary>
    public StemMixerService Mixer { get; }

    // ── Per-stem channel ViewModels ───────────────────────────────────────────

    public StemChannelViewModel Vocals { get; }
    public StemChannelViewModel Drums  { get; }
    public StemChannelViewModel Bass   { get; }
    public StemChannelViewModel Other  { get; }

    // ── Separation state ──────────────────────────────────────────────────────

    private bool _isSeparating;
    public bool IsSeparating
    {
        get => _isSeparating;
        private set => this.RaiseAndSetIfChanged(ref _isSeparating, value);
    }

    private float _separationProgress;
    public float SeparationProgress
    {
        get => _separationProgress;
        private set => this.RaiseAndSetIfChanged(ref _separationProgress, value);
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    // ── Stem paths (populated after successful separation) ────────────────────

    public string? VocalsWavPath { get; private set; }
    public string? DrumsWavPath  { get; private set; }
    public string? BassWavPath   { get; private set; }
    public string? OtherWavPath  { get; private set; }

    // ── Commands ──────────────────────────────────────────────────────────────

    /// <summary>Cancels an in-progress separation.</summary>
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>
    /// Triggers stem separation on a source file path.
    /// Called by <see cref="WorkstationDeckViewModel.SeparateStemsCommand"/>.
    /// </summary>
    public ReactiveCommand<string, Unit> LoadAndSeparateCommand { get; }

    // ── Internal ──────────────────────────────────────────────────────────────

    private CancellationTokenSource? _cts;
    private readonly List<IDisposable> _audioProviders = new();

    // ── Constructor ───────────────────────────────────────────────────────────

    public StemMixerViewModel(CachedStemSeparator separator)
    {
        _separator = separator;

        // Stereo 44100 hz — matches Orbit's standard audio pipeline format.
        Mixer = new StemMixerService(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));

        Vocals = new StemChannelViewModel(StemType.Vocals, Mixer);
        Drums  = new StemChannelViewModel(StemType.Drums,  Mixer);
        Bass   = new StemChannelViewModel(StemType.Bass,   Mixer);
        Other  = new StemChannelViewModel(StemType.Other,  Mixer);

        CancelCommand = ReactiveCommand.Create(Cancel,
            this.WhenAnyValue(x => x.IsSeparating));

        LoadAndSeparateCommand = ReactiveCommand.CreateFromTask<string>(
            SeparateAsync,
            this.WhenAnyValue(x => x.IsSeparating, separating => !separating));
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs ONNX stem separation on <paramref name="sourceFilePath"/> and loads
    /// the resulting WAV files into the mixer.
    /// </summary>
    public async Task SeparateAsync(string sourceFilePath)
    {
        if (IsSeparating) return;

        _cts = new CancellationTokenSource();
        IsSeparating      = true;
        SeparationProgress = 0f;
        ErrorMessage       = null;
        ClearStemAudio();

        try
        {
            string outputDir = Path.Combine(
                Path.GetTempPath(), "orbit_stems",
                Path.GetFileNameWithoutExtension(sourceFilePath));
            Directory.CreateDirectory(outputDir);

            var progress = new Progress<float>(p =>
                Dispatcher.UIThread.Post(() => SeparationProgress = p));

            var stems = await _separator.SeparateWithProgressAsync(
                sourceFilePath, outputDir, progress, _cts.Token);

            LoadStemAudio(stems);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SeparationProgress = 1f;
                IsSeparating       = false;
            });
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsSeparating       = false;
                SeparationProgress = 0f;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsSeparating = false;
                ErrorMessage = ex.Message;
            });
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Cancels an in-progress separation.
    /// </summary>
    public void Cancel() => _cts?.Cancel();

    // ── Private helpers ───────────────────────────────────────────────────────

    private void LoadStemAudio(Dictionary<StemType, string> stemPaths)
    {
        // NAudio operations on the audio thread are fine; we are not on UIThread here.
        foreach (var (type, path) in stemPaths)
        {
            try
            {
                var reader   = new AudioFileReader(path);
                var provider = new SampleChannel(reader, forceStereo: true);
                _audioProviders.Add(reader);
                Mixer.AddStem(type, provider);

                switch (type)
                {
                    case StemType.Vocals: VocalsWavPath = path; break;
                    case StemType.Drums:  DrumsWavPath  = path; break;
                    case StemType.Bass:   BassWavPath   = path; break;
                    case StemType.Other:  OtherWavPath  = path; break;
                }
            }
            catch
            {
                // Skip single failed stem — degraded mode is better than a crash.
            }
        }
    }

    private void ClearStemAudio()
    {
        Mixer.RemoveStem(StemType.Vocals);
        Mixer.RemoveStem(StemType.Drums);
        Mixer.RemoveStem(StemType.Bass);
        Mixer.RemoveStem(StemType.Other);
        foreach (var d in _audioProviders) d.Dispose();
        _audioProviders.Clear();
        VocalsWavPath = null;
        DrumsWavPath  = null;
        BassWavPath   = null;
        OtherWavPath  = null;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        ClearStemAudio();
    }
}
