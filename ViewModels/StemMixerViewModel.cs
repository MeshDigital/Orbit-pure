using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ReactiveUI;
using SLSKDONET.Models.Stem;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Audio.Separation;

namespace SLSKDONET.ViewModels;

// ─── StemChannelViewModel ─────────────────────────────────────────────────────

/// <summary>
/// Bindable state for one stem channel (vocals / drums / bass / other).
/// Pushes changes through to <see cref="StemMixerService"/>.
/// </summary>
public sealed class StemChannelViewModel : ReactiveObject
{
    private readonly StemMixerService _mixer;

    public StemType StemType { get; }

    /// <summary>Display name, e.g. "Vocals".</summary>
    public string DisplayName { get; }

    /// <summary>Accent color hex string for the waveform strip (matches per-stem color convention).</summary>
    public string AccentColor { get; }

    // ── Gain ──────────────────────────────────────────────────────────────

    private float _gainDb = 0f;
    /// <summary>Fader level in dBFS.  Range [-60, +12].  0 = unity.</summary>
    public float GainDb
    {
        get => _gainDb;
        set
        {
            this.RaiseAndSetIfChanged(ref _gainDb, Math.Clamp(value, -60f, 12f));
            _mixer.SetGain(StemType, _gainDb);
        }
    }

    // ── Pan ───────────────────────────────────────────────────────────────

    private float _pan = 0f;
    /// <summary>Stereo pan.  -1 = full left, 0 = center, +1 = full right.</summary>
    public float Pan
    {
        get => _pan;
        set
        {
            this.RaiseAndSetIfChanged(ref _pan, Math.Clamp(value, -1f, 1f));
            _mixer.SetPan(StemType, _pan);
        }
    }

    // ── Mute / Solo ───────────────────────────────────────────────────────

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            this.RaiseAndSetIfChanged(ref _isMuted, value);
            _mixer.SetMute(StemType, value);
        }
    }

    private bool _isSoloed;
    public bool IsSoloed
    {
        get => _isSoloed;
        set
        {
            this.RaiseAndSetIfChanged(ref _isSoloed, value);
            _mixer.SetSolo(StemType, value);
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> MuteCommand  { get; }
    public ReactiveCommand<Unit, Unit> SoloCommand  { get; }
    public ReactiveCommand<Unit, Unit> ResetCommand { get; }

    public StemChannelViewModel(StemType stemType, StemMixerService mixer)
    {
        StemType = stemType;
        _mixer   = mixer;

        (DisplayName, AccentColor) = stemType switch
        {
            StemType.Vocals => ("Vocals", "#00CFFF"),   // cyan
            StemType.Drums  => ("Drums",  "#FF8C00"),   // orange
            StemType.Bass   => ("Bass",   "#44FF88"),   // green
            StemType.Other  => ("Other",  "#BB88FF"),   // purple
            _               => (stemType.ToString(), "#FFFFFF"),
        };

        MuteCommand  = ReactiveCommand.Create(() => { IsMuted  = !IsMuted; });
        SoloCommand  = ReactiveCommand.Create(() => { IsSoloed = !IsSoloed; });
        ResetCommand = ReactiveCommand.Create(() => { GainDb = 0f; Pan = 0f; IsMuted = false; IsSoloed = false; });
    }
}

// ─── StemMixerViewModel ───────────────────────────────────────────────────────

/// <summary>
/// Owns four <see cref="StemChannelViewModel"/>s and manages the separation lifecycle:
/// load a track → separate → populate mixer → expose waveform data.
///
/// Register as <c>AddSingleton&lt;StemMixerViewModel&gt;</c> in DI.
/// </summary>
public sealed class StemMixerViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable     _disposables   = new();
    private readonly CachedStemSeparator     _separator;
    private readonly StemMixerService        _mixer;
    private CancellationTokenSource?         _cts;
    private bool                             _isDisposed;

    public StemChannelViewModel Vocals { get; }
    public StemChannelViewModel Drums  { get; }
    public StemChannelViewModel Bass   { get; }
    public StemChannelViewModel Other  { get; }

    /// <summary>Underlying mixer service (ISampleProvider for wiring into NAudio chain).</summary>
    public StemMixerService MixerService => _mixer;

    // ── Separation state ──────────────────────────────────────────────────

    private bool _isSeparating;
    public bool  IsSeparating
    {
        get => _isSeparating;
        private set => this.RaiseAndSetIfChanged(ref _isSeparating, value);
    }

    private float _separationProgress;
    /// <summary>0.0 → 1.0 as each stem completes its cache-write.</summary>
    public float SeparationProgress
    {
        get => _separationProgress;
        private set => this.RaiseAndSetIfChanged(ref _separationProgress, value);
    }

    private string? _loadedFilePath;
    public string?  LoadedFilePath
    {
        get => _loadedFilePath;
        private set => this.RaiseAndSetIfChanged(ref _loadedFilePath, value);
    }

    private string? _errorMessage;
    public string?  ErrorMessage
    {
        get => _errorMessage;
        private set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    // ── Stem WAV paths (set after separation completes) ───────────────────

    private string? _vocalsPath;
    private string? _drumsPath;
    private string? _bassPath;
    private string? _otherPath;

    public string? VocalsWavPath => _vocalsPath;
    public string? DrumsWavPath  => _drumsPath;
    public string? BassWavPath   => _bassPath;
    public string? OtherWavPath  => _otherPath;

    // ── Commands ──────────────────────────────────────────────────────────

    public ReactiveCommand<string, Unit>  LoadAndSeparateCommand { get; }
    public ReactiveCommand<Unit, Unit>    CancelCommand          { get; }

    public StemMixerViewModel(CachedStemSeparator separator)
    {
        _separator = separator;
        _mixer     = new StemMixerService(NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));

        Vocals = new StemChannelViewModel(StemType.Vocals, _mixer);
        Drums  = new StemChannelViewModel(StemType.Drums,  _mixer);
        Bass   = new StemChannelViewModel(StemType.Bass,   _mixer);
        Other  = new StemChannelViewModel(StemType.Other,  _mixer);

        var isSeparatingObs = this.WhenAnyValue(x => x.IsSeparating);

        LoadAndSeparateCommand = ReactiveCommand.CreateFromTask<string>(
            filePath => LoadAndSeparateAsync(filePath),
            isSeparatingObs.Select(s => !s));

        CancelCommand = ReactiveCommand.Create(() => { _cts?.Cancel(); },
            isSeparatingObs);
    }

    private async Task LoadAndSeparateAsync(string filePath)
    {
        if (!_separator.IsAvailable)
        {
            ErrorMessage = "Demucs-4s ONNX model not found. Check Tools/Essentia/models/demucs-4s.onnx.";
            return;
        }

        _cts = new CancellationTokenSource();
        IsSeparating      = true;
        SeparationProgress = 0f;
        ErrorMessage       = null;

        try
        {
            string outDir = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(filePath)!,
                ".stems",
                System.IO.Path.GetFileNameWithoutExtension(filePath));

            var progress = new Progress<float>(p =>
                Dispatcher.UIThread.Post(() => SeparationProgress = p));

            var stems = await _separator.SeparateWithProgressAsync(
                filePath, outDir, progress, _cts.Token);

            // Wire stem WAV files into the mixer
            foreach (var (stemType, stemPath) in stems)
            {
                var reader = new NAudio.Wave.AudioFileReader(stemPath);
                _mixer.AddStem(stemType, reader);
            }

            stems.TryGetValue(StemType.Vocals, out _vocalsPath);
            stems.TryGetValue(StemType.Drums,  out _drumsPath);
            stems.TryGetValue(StemType.Bass,   out _bassPath);
            stems.TryGetValue(StemType.Other,  out _otherPath);

            LoadedFilePath = filePath;

            // Notify waveform subscribers
            this.RaisePropertyChanged(nameof(VocalsWavPath));
            this.RaisePropertyChanged(nameof(DrumsWavPath));
            this.RaisePropertyChanged(nameof(BassWavPath));
            this.RaisePropertyChanged(nameof(OtherWavPath));
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Separation cancelled.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Separation failed: {ex.Message}";
        }
        finally
        {
            IsSeparating = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _cts?.Cancel();
        _disposables.Dispose();
        _isDisposed = true;
    }
}
