using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SLSKDONET.Services.Audio;

// ─── Supporting types ─────────────────────────────────────────────────────────

/// <summary>CDJ-style playback state for a single deck.</summary>
public enum DeckState { Stopped, Playing, Paused, Cued }

/// <summary>Pitch-range modes matching hardware DJ fader throw.</summary>
public enum PitchRange
{
    /// <summary>±8% — fine control, typical EDM mixing.</summary>
    Narrow = 8,
    /// <summary>±16% — medium range.</summary>
    Medium = 16,
    /// <summary>±50% — wide range for large BPM bridging.</summary>
    Wide = 50
}

/// <summary>One of the 8 hot cue slots per deck.</summary>
public sealed class HotCue
{
    public int    Slot            { get; init; }
    public double PositionSeconds { get; set; }
    public string Label           { get; set; } = string.Empty;
    /// <summary>Hex color string, e.g. "#FF4444". Matches CuePointEntity.Color.</summary>
    public string Color           { get; set; } = "#FFFFFF";
}

/// <summary>An active loop region with optional loop-roll behaviour.</summary>
public sealed class LoopRegion
{
    public double InSeconds         { get; set; }
    public double OutSeconds        { get; set; }
    public bool   IsActive          { get; set; }
    /// <summary>
    /// Loop roll: while active, loops normally; on ExitLoop(), jumps the playhead back to
    /// where it would have been had the loop never triggered.
    /// </summary>
    public bool   IsRoll            { get; set; }
    /// <summary>Playhead at the moment ActivateLoopRoll() was called.</summary>
    public double RollEntrySeconds  { get; set; }
}

// ─── RateSampleProvider ───────────────────────────────────────────────────────

/// <summary>
/// Linear-interpolation rate-change sample provider (vinyl-mode: pitch tracks tempo).
/// For key-lock, wrap this in <see cref="SmbPitchShiftingSampleProvider"/> with
/// <c>PitchFactor = 1 / PlaybackRate</c> to restore the original key.
/// Always outputs 44100 Hz stereo IEEE float, resampling and up-mixing from source as needed.
/// </summary>
internal sealed class RateSampleProvider : ISampleProvider, IDisposable
{
    private readonly AudioFileReader _fileReader;
    private readonly ISampleProvider _source; // normalized 44100 Hz stereo chain

    // Ring-buffer of decoded stereo frames from _source
    private float[] _buf = new float[65536]; // samples (2 per frame)
    private int     _bufFrames;              // frames currently buffered
    private int     _bufFrame;              // next frame to read from _buf
    private double  _frac;                  // sub-frame interpolation position

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

    /// <summary>
    /// Playback rate. 1.0 = normal; 1.08 = +8% (faster + higher pitch); 0.92 = -8%.
    /// </summary>
    public float  PlaybackRate    { get; set; } = 1.0f;
    public double DurationSeconds => _fileReader.TotalTime.TotalSeconds;

    public double CurrentPositionSeconds
    {
        // Subtract buffered-but-not-yet-output frames for display accuracy
        get => Math.Max(0, _fileReader.CurrentTime.TotalSeconds
                           - (double)(_bufFrames - _bufFrame) / 44100.0);
        set
        {
            _fileReader.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(value, 0, DurationSeconds));
            _bufFrames = 0;
            _bufFrame  = 0;
            _frac      = 0;
        }
    }

    public RateSampleProvider(AudioFileReader fileReader)
    {
        _fileReader = fileReader;
        ISampleProvider src = fileReader;

        // Normalise to stereo
        if (fileReader.WaveFormat.Channels == 1)
            src = new MonoToStereoSampleProvider(src);

        // Normalise to 44100 Hz
        if (src.WaveFormat.SampleRate != 44100)
            src = new WdlResamplingSampleProvider(src, 44100);

        _source = src;
    }

    /// <summary>Compact ring buffer and fill from source.</summary>
    private void Refill()
    {
        int remaining = _bufFrames - _bufFrame;
        if (remaining > 0 && _bufFrame > 0)
            Array.Copy(_buf, _bufFrame * 2, _buf, 0, remaining * 2);
        _bufFrames = remaining;
        _bufFrame  = 0;

        int freeSamples = _buf.Length - _bufFrames * 2;
        if (freeSamples <= 0) return;

        int read = _source.Read(_buf, _bufFrames * 2, freeSamples);
        _bufFrames += read / 2;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int framesWanted = count / 2;
        int framesOut    = 0;

        while (framesOut < framesWanted)
        {
            if (_bufFrames - _bufFrame < 2)
            {
                Refill();
                if (_bufFrames - _bufFrame < 1) break; // EOF
            }

            int   i0      = _bufFrame * 2;
            bool  hasNext = (_bufFrame + 1) < _bufFrames;
            float fr      = (float)_frac;
            float l0 = _buf[i0],     r0 = _buf[i0 + 1];
            float l1 = hasNext ? _buf[i0 + 2] : l0;
            float r1 = hasNext ? _buf[i0 + 3] : r0;

            buffer[offset + framesOut * 2]     = l0 + fr * (l1 - l0);
            buffer[offset + framesOut * 2 + 1] = r0 + fr * (r1 - r0);
            framesOut++;

            _frac += PlaybackRate;
            int advance = (int)_frac;
            _frac      -= advance;
            _bufFrame  += advance;
        }

        return framesOut * 2;
    }

    public void Dispose() => _fileReader.Dispose();
}

// ─── DeckEngine ───────────────────────────────────────────────────────────────

/// <summary>
/// CDJ-style single-deck playback engine implementing <see cref="ISampleProvider"/>.
/// Handles: play/pause/cue, seek, tempo control (±50%), key-lock,
/// beat-accurate loops (set/exit/half/double/move/roll), and 8 hot cues.
/// Output: 44100 Hz stereo IEEE float at any volume 0–2× (applies to MixingSampleProvider).
/// </summary>
public sealed class DeckEngine : ISampleProvider, IDisposable
{
    private readonly object _lock = new();

    // Provider chain: AudioFileReader → RateSampleProvider [→ SmbPitchShiftingSampleProvider] → VolumeSampleProvider
    private AudioFileReader?                  _fileReader;
    private RateSampleProvider?               _rate;
    private SmbPitchShiftingSampleProvider?   _pitchCorrect; // only present when key-lock is on
    private VolumeSampleProvider?             _volumeProvider;
    private bool                              _isDisposed;

    // Playback state
    private DeckState _state            = DeckState.Stopped;
    private double    _cuePositionSecs  = 0;
    private double    _tempoPercent     = 0;
    private bool      _keyLock          = false;
    private int       _semitoneShift    = 0;

    // Loop & hot cues
    private LoopRegion? _loop;
    private readonly HotCue?[] _hotCues = new HotCue?[8];

    // ─── Public contract ──────────────────────────────────────────────────────

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

    public DeckState State              => _state;
    public bool      IsLoaded           => _rate != null;
    public double    DurationSeconds    => _rate?.DurationSeconds ?? 0;
    public double    PositionSeconds    => _rate?.CurrentPositionSeconds ?? 0;
    public double    CuePositionSeconds => _cuePositionSecs;
    public LoopRegion? Loop             => _loop;
    public HotCue?[] HotCues           => _hotCues;

    /// <summary>Tempo adjustment in percent. Clamped to [-50, +50].</summary>
    public double TempoPercent
    {
        get => _tempoPercent;
        set { lock (_lock) { _tempoPercent = Math.Clamp(value, -50, 50); ApplyTempo(); } }
    }

    /// <summary>
    /// Key-lock: keep original pitch while changing tempo.
    /// Implemented via <see cref="SmbPitchShiftingSampleProvider"/> with inverse pitch factor.
    /// </summary>
    public bool KeyLock
    {
        get => _keyLock;
        set { lock (_lock) { _keyLock = value; RebuildChain(); } }
    }

    /// <summary>
    /// Semitone shift applied independently of key-lock (−12 to +12).
    /// 0 = no shift. Combined with key-lock pitch factor when both are active.
    /// </summary>
    public int SemitoneShift
    {
        get => _semitoneShift;
        set { lock (_lock) { _semitoneShift = Math.Clamp(value, -12, 12); RebuildChain(); } }
    }

    // Volume persists across chain rebuilds (e.g., when key-lock toggles)
    private float _requestedVolume = 1f;

    /// <summary>Output volume multiplier. 0 = mute, 1 = unity, 2 = +6 dB boost.</summary>
    public float VolumeLevel
    {
        get => _volumeProvider?.Volume ?? _requestedVolume;
        set
        {
            _requestedVolume = Math.Clamp(value, 0f, 2f);
            if (_volumeProvider != null) _volumeProvider.Volume = _requestedVolume;
        }
    }

    // ─── Events ───────────────────────────────────────────────────────────────

    public event EventHandler?    StateChanged;
    public event EventHandler?    LoopChanged;
    public event EventHandler<int>? HotCueChanged;

    // ─── File loading ─────────────────────────────────────────────────────────

    public void LoadFile(string filePath)
    {
        if (!System.IO.File.Exists(filePath))
            throw new System.IO.FileNotFoundException(
                $"Track file not found. It may have been moved or not yet downloaded: {System.IO.Path.GetFileName(filePath)}",
                filePath);

        lock (_lock)
        {
            DisposeChain();
            _fileReader = new AudioFileReader(filePath);
            _rate       = new RateSampleProvider(_fileReader);
            _cuePositionSecs = 0;
            _state      = DeckState.Cued;
            RebuildChain();
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ─── Playback controls ────────────────────────────────────────────────────

    public void Play()
    {
        lock (_lock)
        {
            if (_rate == null) return;
            _state = DeckState.Playing;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_state != DeckState.Playing) return;
            _state = DeckState.Paused;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// CDJ-style cue logic:
    /// • If playing → stop and return to the stored cue point.
    /// • If stopped/paused → store current position as the cue point.
    /// </summary>
    public void Cue()
    {
        lock (_lock)
        {
            if (_rate == null) return;
            if (_state == DeckState.Playing)
            {
                _state = DeckState.Cued;
                _rate.CurrentPositionSeconds = _cuePositionSecs;
            }
            else
            {
                _cuePositionSecs = _rate.CurrentPositionSeconds;
                _state = DeckState.Cued;
            }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Seeks to an absolute time in seconds.</summary>
    public void Seek(double positionSeconds)
    {
        lock (_lock)
        {
            if (_rate == null) return;
            _rate.CurrentPositionSeconds = positionSeconds;
        }
    }

    // ─── Loop controls ────────────────────────────────────────────────────────

    /// <summary>Sets an active loop starting at the current playhead.</summary>
    /// <param name="beatLengthSeconds">Loop length in seconds (beats × 60 / effectiveBpm).</param>
    public void SetLoop(double beatLengthSeconds)
    {
        lock (_lock)
        {
            if (_rate == null) return;
            double pos = _rate.CurrentPositionSeconds;
            _loop = new LoopRegion { InSeconds = pos, OutSeconds = pos + beatLengthSeconds, IsActive = true };
        }
        LoopChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deactivates the loop; for loop rolls, returns to the natural playhead position.</summary>
    public void ExitLoop()
    {
        lock (_lock)
        {
            if (_loop == null) return;
            if (_loop.IsRoll && _rate != null)
            {
                // Return to where the playhead would be without the loop
                double loopLen = _loop.OutSeconds - _loop.InSeconds;
                double elapsed = _rate.CurrentPositionSeconds - _loop.InSeconds;
                int    cycles  = (int)(elapsed / loopLen);
                _rate.CurrentPositionSeconds = _loop.RollEntrySeconds + cycles * loopLen + (elapsed % loopLen);
            }
            _loop = null;
        }
        LoopChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shifts the entire loop region forward (+1) or backward (-1) by its own length.</summary>
    public void MoveLoop(int direction)
    {
        lock (_lock)
        {
            if (_loop == null) return;
            double len = _loop.OutSeconds - _loop.InSeconds;
            _loop.InSeconds  += direction * len;
            _loop.OutSeconds += direction * len;
        }
        LoopChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Halves the loop length (crops from the out point).</summary>
    public void HalfLoop()
    {
        lock (_lock)
        {
            if (_loop == null) return;
            _loop.OutSeconds = (_loop.InSeconds + _loop.OutSeconds) / 2.0;
        }
        LoopChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Doubles the loop length (extends the out point).</summary>
    public void DoubleLoop()
    {
        lock (_lock)
        {
            if (_loop == null) return;
            _loop.OutSeconds += _loop.OutSeconds - _loop.InSeconds;
        }
        LoopChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Activates a loop roll: loops normally while active; on ExitLoop(), snaps back to the
    /// natural playhead position (as if the loop had not been triggered).
    /// </summary>
    public void ActivateLoopRoll(double beatLengthSeconds)
    {
        lock (_lock)
        {
            if (_rate == null) return;
            double pos = _rate.CurrentPositionSeconds;
            _loop = new LoopRegion
            {
                InSeconds       = pos,
                OutSeconds      = pos + beatLengthSeconds,
                IsActive        = true,
                IsRoll          = true,
                RollEntrySeconds = pos
            };
        }
        LoopChanged?.Invoke(this, EventArgs.Empty);
    }

    // ─── Hot cues (8 pads per deck) ───────────────────────────────────────────

    /// <summary>Sets hot-cue slot (0–7) at the current playhead. Overwrites any existing cue.</summary>
    public void SetHotCue(int slot, string label = "", string color = "#FFFFFF")
    {
        if ((uint)slot >= 8) return;
        lock (_lock)
        {
            if (_rate == null) return;
            _hotCues[slot] = new HotCue { Slot = slot, PositionSeconds = _rate.CurrentPositionSeconds, Label = label, Color = color };
        }
        HotCueChanged?.Invoke(this, slot);
    }

    /// <summary>Jumps to hot-cue slot. If the slot is empty, stores the current position there.</summary>
    public void JumpToHotCue(int slot)
    {
        if ((uint)slot >= 8) return;
        lock (_lock)
        {
            if (_rate == null) return;
            if (_hotCues[slot] is HotCue cue)
                _rate.CurrentPositionSeconds = cue.PositionSeconds;
            else
                _hotCues[slot] = new HotCue { Slot = slot, PositionSeconds = _rate.CurrentPositionSeconds, Label = $"Cue {slot + 1}" };
        }
        HotCueChanged?.Invoke(this, slot);
    }

    /// <summary>Clears hot-cue slot (0–7).</summary>
    public void DeleteHotCue(int slot)
    {
        if ((uint)slot >= 8) return;
        lock (_lock) { _hotCues[slot] = null; }
        HotCueChanged?.Invoke(this, slot);
    }

    /// <summary>Loads a prepared set of hot cues, replacing any existing pad assignments.</summary>
    public void LoadHotCues(IEnumerable<HotCue>? hotCues)
    {
        lock (_lock)
        {
            Array.Clear(_hotCues, 0, _hotCues.Length);

            if (hotCues != null)
            {
                foreach (var cue in hotCues)
                {
                    if ((uint)cue.Slot >= 8) continue;
                    _hotCues[cue.Slot] = new HotCue
                    {
                        Slot = cue.Slot,
                        PositionSeconds = cue.PositionSeconds,
                        Label = cue.Label,
                        Color = cue.Color
                    };
                }
            }
        }

        for (var i = 0; i < 8; i++)
            HotCueChanged?.Invoke(this, i);
    }

    // ─── ISampleProvider ──────────────────────────────────────────────────────

    public int Read(float[] buffer, int offset, int count)
    {
        if (_state != DeckState.Playing)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        // Loop boundary enforcement (snapshot avoids holding lock during Read)
        var loop = _loop;
        if (loop?.IsActive == true && _rate != null && _rate.CurrentPositionSeconds >= loop.OutSeconds)
            lock (_lock) { if (_rate != null) _rate.CurrentPositionSeconds = loop.InSeconds; }

        // Capture volatile chain reference without blocking
        ISampleProvider? chain;
        lock (_lock) { chain = _volumeProvider; }

        if (chain == null) { Array.Clear(buffer, offset, count); return count; }

        int read = chain.Read(buffer, offset, count);
        if (read == 0)
        {
            lock (_lock) { _state = DeckState.Stopped; }
            StateChanged?.Invoke(this, EventArgs.Empty);
            Array.Clear(buffer, offset, count);
            return count;
        }
        return read;
    }

    // ─── Private helpers ──────────────────────────────────────────────────────

    /// <summary>Sets PlaybackRate on _rate; updates combined key-lock + semitone-shift pitch factor.</summary>
    private void ApplyTempo()
    {
        if (_rate == null) return;
        _rate.PlaybackRate = (float)(1.0 + _tempoPercent / 100.0);
        if (_pitchCorrect != null)
        {
            float keyLockFactor  = _keyLock ? 1.0f / _rate.PlaybackRate : 1.0f;
            float semitoneFactor = (float)Math.Pow(2.0, _semitoneShift / 12.0);
            _pitchCorrect.PitchFactor = keyLockFactor * semitoneFactor;
        }
    }

    /// <summary>
    /// Rebuilds the provider chain (call under _lock).
    /// Key-lock inserts SmbPitchShiftingSampleProvider between _rate and VolumeSampleProvider.
    /// </summary>
    private void RebuildChain()
    {
        if (_rate == null) return;

        bool needsPitch = _keyLock || _semitoneShift != 0;
        if (needsPitch)
        {
            _pitchCorrect   = new SmbPitchShiftingSampleProvider(_rate) { PitchFactor = 1.0f };
            _volumeProvider = new VolumeSampleProvider(_pitchCorrect) { Volume = _requestedVolume };
        }
        else
        {
            _pitchCorrect   = null;
            _volumeProvider = new VolumeSampleProvider(_rate) { Volume = _requestedVolume };
        }

        ApplyTempo();
    }

    private void DisposeChain()
    {
        _rate?.Dispose();      // also disposes the AudioFileReader
        _rate         = null;
        _fileReader   = null;
        _pitchCorrect = null;
        _volumeProvider = null;
        _loop         = null;
        Array.Clear(_hotCues, 0, 8);
        _state            = DeckState.Stopped;
        _cuePositionSecs  = 0;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        lock (_lock) { DisposeChain(); _isDisposed = true; }
    }
}
