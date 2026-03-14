using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SLSKDONET.Models.Stem;

namespace SLSKDONET.Services.Audio;

public enum Deck { A, B }

/// <summary>
/// A specialized audio engine that loads, processes, and mixes multiple audio stems for two decks.
/// Supports crossfading and real-time synthesis for key verification.
/// </summary>
public class RealTimeStemEngine : IDisposable
{
    private readonly Dictionary<Deck, Dictionary<StemType, StemProcessingChain>> _decks = new();
    private IWavePlayer? _outputDevice;
    private MixingSampleProvider? _mixer;
    private SignalGenerator? _pianoSynth;
    
    // Legacy support for single-deck callers
    public TimeSpan CurrentTime => GetDeckTime(Deck.A);
    public TimeSpan TotalTime => GetDeckTotalTime(Deck.A);

    private float _crossfaderValue = 0.5f; // 0.0 = Deck A, 1.0 = Deck B
    public float CrossfaderValue
    {
        get => _crossfaderValue;
        set
        {
            _crossfaderValue = Math.Clamp(value, 0f, 1f);
            UpdateEffectiveVolumes();
        }
    }

    private readonly object _lock = new();

    public RealTimeStemEngine()
    {
        _decks[Deck.A] = new();
        _decks[Deck.B] = new();
        
        // Initialize Output Device early
        _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));
        _mixer.ReadFully = true;
        
        _pianoSynth = new SignalGenerator(44100, 2)
        {
            Type = SignalGeneratorType.Sin,
            Gain = 0.0 // Silent by default
        };
        _mixer.AddMixerInput(_pianoSynth);

        // Wrap mixer with peak tracking
        var meteringProvider = new MeteringSampleProvider(_mixer);
        meteringProvider.StreamVolume += (s, e) => {
            foreach(var sample in e.MaxSampleValues) OnSampleRead(sample);
        };

        _outputDevice = new WaveOutEvent();
        _outputDevice.Init(meteringProvider);
    }

    public void LoadDeckStems(Deck deck, Dictionary<StemType, string> stemFilePaths)
    {
        lock (_lock)
        {
            try 
            {
                // Clear existing stems for this deck
                if (_decks.TryGetValue(deck, out var processors))
                {
                    foreach (var chain in processors.Values)
                    {
                        if (chain.FinalProvider != null) _mixer?.RemoveMixerInput(chain.FinalProvider);
                        chain.Reader?.Dispose();
                    }
                    processors.Clear();
                }

                var targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

                foreach (var input in stemFilePaths)
                {
                    if (!System.IO.File.Exists(input.Value)) continue;

                    var reader = new AudioFileReader(input.Value);
                    
                    // Phase 3: Resampling Support
                    ISampleProvider provider = reader;
                    if (reader.WaveFormat.SampleRate != targetFormat.SampleRate)
                    {
                        provider = new WdlResamplingSampleProvider(reader, targetFormat.SampleRate);
                    }

                    // Enforce Stereo
                    if (provider.WaveFormat.Channels != targetFormat.Channels)
                    {
                        if (provider.WaveFormat.Channels == 1)
                            provider = new MonoToStereoSampleProvider(provider);
                        // Add more channel conversion if needed
                    }

                    var volumeProvider = new VolumeSampleProvider(provider) { Volume = 1.0f };
                    var panProvider = new PanningSampleProvider(volumeProvider) { Pan = 0.0f };

                    var chain = new StemProcessingChain(input.Key)
                    {
                        Reader = reader,
                        VolumeProvider = volumeProvider,
                        PanProvider = panProvider,
                        FinalProvider = panProvider
                    };
                    
                    _decks[deck][input.Key] = chain;
                    _mixer?.AddMixerInput(panProvider);
                }

                UpdateEffectiveVolumes();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RealTimeStemEngine] Error loading stems for {deck}: {ex.Message}");
            }
        }
    }

    public void Play()
    {
        if (_outputDevice?.PlaybackState != PlaybackState.Playing)
            _outputDevice?.Play();
    }

    public void Pause()
    {
        if (_outputDevice?.PlaybackState == PlaybackState.Playing)
            _outputDevice?.Pause();
    }

    /// <summary>
    /// Synchronizes the playback position of the target deck to the source deck.
    /// Used for "Proof of Blend" to ensure the mashup is perfectly aligned.
    /// </summary>
    public void SeekToSync(Deck source, Deck target)
    {
        lock (_lock)
        {
            var sourceTime = GetDeckTime(source);
            Seek(target, sourceTime.TotalSeconds);
        }
    }

    private float _maxPeak;
    public float MasterPeakLevel 
    {
        get 
        {
            float val = _maxPeak;
            _maxPeak = 0; // Reset after read for next window
            return val;
        }
    }

    private void OnSampleRead(float sample)
    {
        float abs = Math.Abs(sample);
        if (abs > _maxPeak) _maxPeak = abs;
    }

    // Legacy support - default to Deck A
    public void LoadStems(Dictionary<StemType, string> stemFilePaths) => LoadDeckStems(Deck.A, stemFilePaths);
    public void SetVolume(StemType type, float volume) => SetVolume(Deck.A, type, volume);
    public void SetMute(StemType type, bool isMuted) => SetMute(Deck.A, type, isMuted);
    public void SetSolo(StemType type, bool isSolo) => SetSolo(Deck.A, type, isSolo);
    public void Seek(double seconds) => Seek(Deck.A, seconds);
    public void PlayTone(double frequency) => PlayRootTone(frequency);

    public void SetMute(Deck deck, StemType type, bool isMuted)
    {
        lock (_lock)
        {
            if (_decks[deck].TryGetValue(type, out var chain))
            {
                chain.IsMuted = isMuted;
                UpdateEffectiveVolumes();
            }
        }
    }

    public bool IsMuted(Deck deck, StemType type)
    {
        lock (_lock)
        {
            return _decks[deck].TryGetValue(type, out var chain) && chain.IsMuted;
        }
    }

    public bool IsMuted(StemType type) => IsMuted(Deck.A, type);

    public void SetSolo(Deck deck, StemType type, bool isSolo)
    {
        lock (_lock)
        {
            if (_decks[deck].TryGetValue(type, out var chain))
            {
                chain.IsSolo = isSolo;
                UpdateEffectiveVolumes();
            }
        }
    }

    public TimeSpan GetDeckTime(Deck deck)
    {
        lock (_lock)
        {
            var first = _decks[deck].Values.FirstOrDefault(c => c.Reader != null);
            return first?.Reader?.CurrentTime ?? TimeSpan.Zero;
        }
    }

    public TimeSpan GetDeckTotalTime(Deck deck)
    {
        lock (_lock)
        {
            var first = _decks[deck].Values.FirstOrDefault(c => c.Reader != null);
            return first?.Reader?.TotalTime ?? TimeSpan.Zero;
        }
    }

    public void SetVolume(Deck deck, StemType type, float volume)
    {
        lock (_lock)
        {
            if (_decks[deck].TryGetValue(type, out var chain))
            {
                chain.UserVolume = volume;
                UpdateEffectiveVolumes();
            }
        }
    }

    public void PlayRootTone(double frequency)
    {
        if (_pianoSynth == null) return;
        _pianoSynth.Frequency = frequency;
        _pianoSynth.Gain = 0.2;
        Task.Delay(500).ContinueWith(_ => _pianoSynth.Gain = 0.0);
    }

    public void Seek(Deck deck, double seconds)
    {
        lock (_lock)
        {
            var time = TimeSpan.FromSeconds(seconds);
            if (!_decks.TryGetValue(deck, out var processors) || !processors.Any()) return;

            // Get total time safely from any non-null reader
            var totalTime = GetDeckTotalTime(deck);
            if (time > totalTime) time = totalTime;
            if (time < TimeSpan.Zero) time = TimeSpan.Zero;

            foreach (var chain in processors.Values)
            {
                if (chain.Reader != null)
                {
                    try
                    {
                        chain.Reader.CurrentTime = time;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[RealTimeStemEngine] Seek error on {deck} - {chain.Type}: {ex.Message}");
                    }
                }
            }
        }
    }

    private void UpdateEffectiveVolumes()
    {
        lock (_lock)
        {
            // Deck A: Constant Power Curve (starts at 1.0, ends at 0.0)
            // Cos(0) = 1, Cos(PI/2) = 0
            float deckAVol = (float)Math.Cos(_crossfaderValue * Math.PI / 2);
            
            // Deck B: Constant Power Curve (starts at 0.0, ends at 1.0)
            // Sin(0) = 0, Sin(PI/2) = 1
            float deckBVol = (float)Math.Sin(_crossfaderValue * Math.PI / 2);

            ApplyDeckVolume(Deck.A, deckAVol);
            ApplyDeckVolume(Deck.B, deckBVol);
        }
    }

    private void ApplyDeckVolume(Deck deck, float masterVol)
    {
        var processors = _decks[deck];
        bool anySolo = processors.Values.Any(p => p.IsSolo);

        foreach (var chain in processors.Values)
        {
            if (chain.VolumeProvider == null) continue;

            float targetVol = chain.UserVolume * masterVol;

            if (chain.IsMuted) targetVol = 0.0f;
            else if (anySolo && !chain.IsSolo) targetVol = 0.0f;

            chain.VolumeProvider.Volume = targetVol;
        }
    }

    public void Dispose()
    {
        _outputDevice?.Stop();
        _outputDevice?.Dispose();
        foreach (var deck in _decks.Values)
        {
            foreach (var chain in deck.Values)
                chain.Reader?.Dispose();
        }
    }
}

/// <summary>
/// Helper for RealTimeStemEngine to track the NAudio processing graph for a single stem.
/// </summary>
public class StemProcessingChain
{
    public StemType Type { get; }
    public AudioFileReader? Reader { get; set; }
    public VolumeSampleProvider? VolumeProvider { get; set; }
    public PanningSampleProvider? PanProvider { get; set; }
    public ISampleProvider? FinalProvider { get; set; }

    public float UserVolume { get; set; } = 1.0f;
    public bool IsMuted { get; set; }
    public bool IsSolo { get; set; }

    public StemProcessingChain(StemType type)
    {
        Type = type;
    }
}
