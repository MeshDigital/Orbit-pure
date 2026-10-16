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

    /// <summary>The four channel strips in mixer order (Vocals, Drums, Bass, Other).</summary>
    public IReadOnlyList<StemChannelViewModel> Channels { get; }

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

    private string? _sourceFileName;
    /// <summary>File name of the track the current stems were separated from (null when empty).</summary>
    public string? SourceFileName
    {
        get => _sourceFileName;
        private set => this.RaiseAndSetIfChanged(ref _sourceFileName, value);
    }

    private bool _hasStems;
    /// <summary>True once at least one separated stem is loaded into the mixer.</summary>
    public bool HasStems
    {
        get => _hasStems;
        private set => this.RaiseAndSetIfChanged(ref _hasStems, value);
    }

    /// <summary>
    /// Optional per-stem waveform rows. Supplied only for the standalone Stems page
    /// (DI singleton); workstation decks pass null and skip the waveform render cost.
    /// </summary>
    public StemWaveformViewModel? Waveforms { get; }

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
        : this(separator, null) { }

    public StemMixerViewModel(CachedStemSeparator separator, StemWaveformViewModel? waveforms)
    {
        _separator = separator;
        Waveforms  = waveforms;

        // Stereo 44100 hz — matches Orbit's standard audio pipeline format.
        Mixer = new StemMixerService(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));

        Vocals = new StemChannelViewModel(StemType.Vocals, Mixer);
        Drums  = new StemChannelViewModel(StemType.Drums,  Mixer);
        Bass   = new StemChannelViewModel(StemType.Bass,   Mixer);
        Other  = new StemChannelViewModel(StemType.Other,  Mixer);
        Channels = new[] { Vocals, Drums, Bass, Other };

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

            int failed = LoadStemAudio(stems);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SeparationProgress = 1f;
                IsSeparating       = false;
                SourceFileName     = Path.GetFileName(sourceFilePath);
                HasStems           = stems.Count - failed > 0;
                if (failed > 0)
                    ErrorMessage = $"{failed} of {stems.Count} stems could not be loaded.";
            });

            if (Waveforms != null)
                await Waveforms.LoadStemWaveformsAsync(VocalsWavPath, DrumsWavPath, BassWavPath, OtherWavPath);
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

    /// <returns>The number of stems that failed to load.</returns>
    private int LoadStemAudio(Dictionary<StemType, string> stemPaths)
    {
        int failed = 0;
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
            catch (Exception ex)
            {
                // Skip single failed stem: degraded mode is better than a crash, but say so.
                failed++;
                Serilog.Log.Warning(ex, "Stem mixer: failed to load {Stem} stem from {Path}", type, path);
            }
        }
        return failed;
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
        if (Dispatcher.UIThread.CheckAccess())
            ResetLoadedState();
        else
            Dispatcher.UIThread.Post(ResetLoadedState);
    }

    private void ResetLoadedState()
    {
        HasStems       = false;
        SourceFileName = null;
        Waveforms?.Clear();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        ClearStemAudio();
    }
}
