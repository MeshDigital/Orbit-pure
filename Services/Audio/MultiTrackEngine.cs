using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SLSKDONET.Services.Audio;

/// <summary>
/// The deterministic heart of the Orbit DAW.
/// A sample-accurate multi-track summing engine that acts as the master clock.
/// All playheads and cues synchronize to this engine's sample counter.
/// </summary>
public class MultiTrackEngine : ISampleProvider, IDisposable
{
    private readonly List<TrackLaneSampler> _lanes = new();
    private readonly object _lock = new();
    private AudioOutputProvider? _outputProvider;
    private AudioOutputSettings _outputSettings = new();
    private ISampleProvider? _outputWrapper;
    
    // The Master Clock - all time in the DAW derives from this
    private long _totalSamplesProcessed = 0;
    
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
    
    /// <summary>
    /// The absolute "DAW Time" in samples. This is the source of truth for all playheads.
    /// </summary>
    public long CurrentSamplePosition => _totalSamplesProcessed;
    
    /// <summary>
    /// Current playback time in seconds, derived from sample count.
    /// </summary>
    public double CurrentTimeSeconds => _totalSamplesProcessed / (double)WaveFormat.SampleRate;
    
    /// <summary>
    /// Current playback time as TimeSpan.
    /// </summary>
    public TimeSpan CurrentTime => TimeSpan.FromSeconds(CurrentTimeSeconds);
    
    /// <summary>
    /// Whether the engine is currently playing.
    /// </summary>
    public bool IsPlaying => _outputProvider?.IsPlaying ?? false;
    
    /// <summary>
    /// Current audio output mode.
    /// </summary>
    public AudioOutputMode OutputMode => _outputProvider?.CurrentMode ?? AudioOutputMode.WaveOut;
    
    /// <summary>
    /// Project BPM for warping calculations.
    /// </summary>
    public double ProjectBpm { get; set; } = 128.0;

    /// <summary>
    /// Master Crossfader position (0.0 = Left/DeckA, 1.0 = Right/DeckB).
    /// Default is 0.5 (Center).
    /// </summary>
    public float CrossfaderPosition { get; set; } = 0.5f;

    /// <summary>
    /// Adds a track lane to the engine.
    /// </summary>
    public void AddLane(TrackLaneSampler lane)
    {
        lock (_lock)
        {
            _lanes.Add(lane);
        }
    }
    
    /// <summary>
    /// Removes a track lane from the engine.
    /// </summary>
    public void RemoveLane(TrackLaneSampler lane)
    {
        lock (_lock)
        {
            _lanes.Remove(lane);
        }
    }
    
    /// <summary>
    /// Clears all lanes.
    /// </summary>
    public void ClearLanes()
    {
        lock (_lock)
        {
            foreach (var lane in _lanes)
            {
                lane.Dispose();
            }
            _lanes.Clear();
        }
    }

    /// <summary>
    /// Initializes the output device with default settings.
    /// </summary>
    public void Initialize()
    {
        Initialize(new AudioOutputSettings { Mode = AudioOutputMode.WasapiShared });
    }
    
    /// <summary>
    /// Initializes the output device with specified settings.
    /// Optionally provide a wrapper (like MasterBus) that processes this engine's output.
    /// </summary>
    public void Initialize(AudioOutputSettings settings, ISampleProvider? outputWrapper = null)
    {
        lock (_lock)
        {
            _outputSettings = settings;
            _outputWrapper = outputWrapper;
            _outputProvider?.Dispose();
            _outputProvider = new AudioOutputProvider();
            
            // Use wrapper if provided, otherwise use this engine directly
            ISampleProvider finalSource = outputWrapper ?? this;
            _outputProvider.Initialize(finalSource, settings.Mode, settings.DeviceName);
        }
    }
    
    /// <summary>
    /// Switches audio output mode at runtime.
    /// </summary>
    public void SetOutputMode(AudioOutputMode mode, string? deviceName = null)
    {
        lock (_lock)
        {
            bool wasPlaying = IsPlaying;
            _outputProvider?.Stop();
            
            _outputSettings.Mode = mode;
            _outputSettings.DeviceName = deviceName;
            
            _outputProvider?.Dispose();
            _outputProvider = new AudioOutputProvider();
            
            ISampleProvider finalSource = _outputWrapper ?? this;
            _outputProvider.Initialize(finalSource, mode, deviceName);
            
            if (wasPlaying)
            {
                _outputProvider.Play();
            }
        }
    }

    /// <summary>
    /// The core audio callback. Sums all active lanes and advances the master clock.
    /// </summary>
    public int Read(float[] buffer, int offset, int count)
    {
        // 1. Clear the buffer (start with silence)
        Array.Clear(buffer, offset, count);
        
        // 2. Temporary buffer for each lane
        float[] tempBuffer = new float[count];
        
        // Cache crossfader gains to avoid recalculating per sample/lane if position is stable
        float gainA = (float)Math.Cos(CrossfaderPosition * Math.PI * 0.5);
        float gainB = (float)Math.Sin(CrossfaderPosition * Math.PI * 0.5);

        lock (_lock)
        {
            // 3. Sum all active lanes into the buffer
            foreach (var lane in _lanes.Where(l => l.IsActive && !l.IsMuted))
            {
                Array.Clear(tempBuffer, 0, count);
                int samplesRead = lane.Read(tempBuffer, 0, count, _totalSamplesProcessed);
                
                // Determine crossfader gain for this lane
                float xfGain = 1.0f;
                if (lane.Assignment == LaneAssignment.DeckA) xfGain = gainA;
                else if (lane.Assignment == LaneAssignment.DeckB) xfGain = gainB;

                float finalGain = lane.Volume * xfGain;

                // Apply lane volume and sum
                for (int i = 0; i < samplesRead; i++)
                {
                    buffer[offset + i] += tempBuffer[i] * finalGain;
                }
            }
            
            // 4. Advance the Master Clock (stereo = 2 channels)
            _totalSamplesProcessed += (count / WaveFormat.Channels);
        }
        
        return count;
    }

    /// <summary>
    /// Starts playback from current position.
    /// </summary>
    public void Play()
    {
        lock (_lock)
        {
            if (_outputProvider == null) Initialize();
            _outputProvider?.Play();
        }
    }
    
    /// <summary>
    /// Pauses playback at current position.
    /// </summary>
    public void Pause()
    {
        lock (_lock)
        {
            _outputProvider?.Pause();
        }
    }
    
    /// <summary>
    /// Stops playback and resets position to zero.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            _outputProvider?.Stop();
            _totalSamplesProcessed = 0;
        }
    }
    
    /// <summary>
    /// Seeks to a specific time in seconds.
    /// </summary>
    public void Seek(double seconds)
    {
        lock (_lock)
        {
            _totalSamplesProcessed = (long)(seconds * WaveFormat.SampleRate);
            
            // Notify all lanes to seek
            foreach (var lane in _lanes)
            {
                lane.SeekToMasterPosition(_totalSamplesProcessed);
            }
        }
    }
    
    /// <summary>
    /// Seeks to a specific sample position.
    /// </summary>
    public void SeekToSample(long samplePosition)
    {
        lock (_lock)
        {
            _totalSamplesProcessed = samplePosition;
            
            foreach (var lane in _lanes)
            {
                lane.SeekToMasterPosition(_totalSamplesProcessed);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _outputProvider?.Stop();
            _outputProvider?.Dispose();
            _outputProvider = null;
            
            foreach (var lane in _lanes)
            {
                lane.Dispose();
            }
            _lanes.Clear();
        }
    }
}

public enum LaneAssignment
{
    Unassigned,
    DeckA,
    DeckB
}

/// <summary>
/// Represents a single track lane in the DAW timeline.
/// Handles clip-aware sample reading with position offsets.
/// </summary>
public class TrackLaneSampler : IDisposable
{
    public string TrackId { get; set; } = "";
    public string TrackTitle { get; set; } = "";
    
    private ISampleProvider? _source;
    private AudioFileReader? _reader;
    
    public bool IsActive { get; set; } = true;
    public bool IsMuted { get; set; } = false;
    public bool IsSolo { get; set; } = false;
    public float Volume { get; set; } = 1.0f;
    
    /// <summary>
    /// Which deck this lane is assigned to (DeckA, DeckB, or Unassigned).
    /// </summary>
    public LaneAssignment Assignment { get; set; } = LaneAssignment.Unassigned;
    
    /// <summary>
    /// Where this clip starts on the timeline (in samples).
    /// </summary>
    public long StartSampleOffset { get; set; } = 0;
    
    /// <summary>
    /// Clip end boundary (in samples). Defaults to end of file.
    /// </summary>
    public long EndSample { get; set; } = long.MaxValue;
    
    /// <summary>
    /// Internal read position within the clip.
    /// </summary>
    private long _clipPosition = 0;

    /// <summary>
    /// Loads an audio file as the source for this lane.
    /// </summary>
    public void LoadFile(string filePath)
    {
        _reader?.Dispose();
        
        if (!System.IO.File.Exists(filePath)) return;
        
        _reader = new AudioFileReader(filePath);
        _source = _reader;
        
        // Default end to file length
        EndSample = _reader.Length / (_reader.WaveFormat.BitsPerSample / 8) / _reader.WaveFormat.Channels;
    }

    /// <summary>
    /// Reads samples for this lane, respecting the master timeline position.
    /// </summary>
    public int Read(float[] buffer, int offset, int count, long masterPosition)
    {
        if (_source == null)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }
        
        // Calculate if this lane should be playing at the current master position
        long laneRelativePosition = masterPosition - StartSampleOffset;
        
        // Before the clip starts
        if (laneRelativePosition < 0)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }
        
        // After the clip ends
        if (laneRelativePosition >= EndSample - StartSampleOffset)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }
        
        // Read from source
        return _source.Read(buffer, offset, count);
    }
    
    /// <summary>
    /// Seeks the lane's internal position based on master clock.
    /// </summary>
    public void SeekToMasterPosition(long masterSamplePosition)
    {
        if (_reader == null) return;
        
        long laneRelativePosition = masterSamplePosition - StartSampleOffset;
        
        if (laneRelativePosition < 0)
        {
            _reader.Position = 0;
        }
        else
        {
            long bytePosition = laneRelativePosition * _reader.WaveFormat.BlockAlign;
            bytePosition = Math.Min(bytePosition, _reader.Length);
            _reader.Position = bytePosition;
        }
        
        _clipPosition = Math.Max(0, laneRelativePosition);
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _reader = null;
        _source = null;
    }
}
