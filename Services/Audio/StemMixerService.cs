using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SLSKDONET.Models.Stem;

namespace SLSKDONET.Services.Audio;

/// <summary>
/// Mixes multiple per-stem <see cref="ISampleProvider"/> chains with individual
/// gain, mute and solo controls.  Drop-in replacement for the raw audio player
/// when working with separated stems inside the Timeline editor.
///
/// Usage:
///   var mixer = new StemMixerService(waveFormat);
///   mixer.AddStem(StemType.Drums,  drumsProvider);
///   mixer.AddStem(StemType.Vocals, vocalsProvider);
///   mixer.SetGain(StemType.Drums, gainDb: -3f);
///   mixer.SetMute(StemType.Vocals, muted: true);
///   // mixer itself is an ISampleProvider – feed to AudioPlayerService
/// </summary>
public sealed class StemMixerService : ISampleProvider
{
    private readonly WaveFormat _waveFormat;
    private readonly Dictionary<StemType, StemChannel> _channels = new();
    private readonly MixingSampleProvider _mixBus;

    public StemMixerService(WaveFormat waveFormat)
    {
        _waveFormat = waveFormat;
        _mixBus = new MixingSampleProvider(waveFormat)
        {
            ReadFully = true // pad with silence when inputs are exhausted
        };
    }

    /// <inheritdoc />
    public WaveFormat WaveFormat => _waveFormat;

    // ──────────────────────────────────── stem management ─────────────────

    /// <summary>
    /// Adds a stem channel.  Replaces any existing channel for the same <see cref="StemType"/>.
    /// </summary>
    public void AddStem(StemType type, ISampleProvider source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (_channels.TryGetValue(type, out var old))
            _mixBus.RemoveMixerInput(old.Fader);

        var fader = new VolumeSampleProvider(source) { Volume = 1f };
        _channels[type] = new StemChannel(fader);
        _mixBus.AddMixerInput(fader);
    }

    /// <summary>Removes and silences a stem channel.</summary>
    public void RemoveStem(StemType type)
    {
        if (!_channels.TryGetValue(type, out var ch)) return;
        _mixBus.RemoveMixerInput(ch.Fader);
        _channels.Remove(type);
    }

    // ──────────────────────────────────── per-stem controls ───────────────

    /// <summary>Sets stem gain in dBFS.  0 dB = unity gain.  Clamped to [-60, +12] dB.</summary>
    public void SetGain(StemType type, float gainDb)
    {
        if (!_channels.TryGetValue(type, out var ch)) return;
        gainDb = Math.Clamp(gainDb, -60f, 12f);
        ch.GainDb = gainDb;
        ApplyFaderVolume(ch);
    }

    /// <returns>Current gain in dBFS for the stem, or 0 if not present.</returns>
    public float GetGain(StemType type)
        => _channels.TryGetValue(type, out var ch) ? ch.GainDb : 0f;

    /// <summary>Mutes or unmutes a stem.  Muted stems produce silence.</summary>
    public void SetMute(StemType type, bool muted)
    {
        if (!_channels.TryGetValue(type, out var ch)) return;
        ch.IsMuted = muted;
        ApplyFaderVolume(ch);
    }

    /// <returns>True if the stem is muted.</returns>
    public bool IsMuted(StemType type)
        => _channels.TryGetValue(type, out var ch) && ch.IsMuted;

    /// <summary>
    /// Solos a stem.  When at least one stem is soloed, all non-soloed stems
    /// are silenced.  Call with <paramref name="soloed"/>=false to unsolo.
    /// </summary>
    public void SetSolo(StemType type, bool soloed)
    {
        if (!_channels.ContainsKey(type)) return;
        _channels[type].IsSoloed = soloed;
        RefreshAllFaders();
    }

    /// <returns>True if the stem is soloed.</returns>
    public bool IsSoloed(StemType type)
        => _channels.TryGetValue(type, out var ch) && ch.IsSoloed;

    /// <summary>Returns all currently registered stem types.</summary>
    public IReadOnlyList<StemType> RegisteredStems => _channels.Keys.ToList();

    /// <summary>
    /// Sets the stereo pan for a stem. -1.0 = full left, 0.0 = center, +1.0 = full right.
    /// Uses linear (balance) panning: amplitude of one channel scales from 1→0 as the
    /// pan moves to the opposite side.
    /// </summary>
    public void SetPan(StemType type, float pan)
    {
        if (!_channels.TryGetValue(type, out var ch)) return;
        ch.Pan = Math.Clamp(pan, -1f, 1f);
        ApplyFaderVolume(ch);
    }

    /// <returns>Current pan value [-1, +1] for the stem, or 0 if not present.</returns>
    public float GetPan(StemType type)
        => _channels.TryGetValue(type, out var ch) ? ch.Pan : 0f;

    // ──────────────────────────────────── ISampleProvider ─────────────────

    /// <inheritdoc />
    public int Read(float[] buffer, int offset, int count)
    {
        int read = _mixBus.Read(buffer, offset, count);

        // Apply per-stem pan as a stereo balance post-process on the mixed output.
        // Because the mix bus already summed all stems, we recompute net L/R scale
        // by iterating over active channels and computing the weighted sum.
        float netLeft  = 0f;
        float netRight = 0f;
        int   active   = 0;

        lock (_channels)
        {
            foreach (var ch in _channels.Values)
            {
                if (ch.IsMuted) continue;
                bool anySoloed = _channels.Values.Any(c => c.IsSoloed);
                if (anySoloed && !ch.IsSoloed) continue;

                float p = ch.Pan; // [-1, +1]
                netLeft  += (p <= 0f) ? 1f : (1f - p);
                netRight += (p >= 0f) ? 1f : (1f + p);
                active++;
            }
        }

        if (active > 1)
        {
            netLeft  /= active;
            netRight /= active;
        }
        else if (active == 0)
        {
            netLeft = netRight = 1f;
        }

        // Only touch buffer if pan is non-trivial
        if (Math.Abs(netLeft - 1f) > 0.001f || Math.Abs(netRight - 1f) > 0.001f)
        {
            for (int i = offset; i < offset + read; i += 2)
            {
                buffer[i]     *= netLeft;
                if (i + 1 < offset + read)
                    buffer[i + 1] *= netRight;
            }
        }

        return read;
    }

    // ──────────────────────────────────── helpers ─────────────────────────

    private static float DbToLinear(float db) => MathF.Pow(10f, db / 20f);

    private void ApplyFaderVolume(StemChannel ch)
    {
        bool anySoloed = _channels.Values.Any(c => c.IsSoloed);

        if (ch.IsMuted || (anySoloed && !ch.IsSoloed))
        {
            ch.Fader.Volume = 0f;
        }
        else
        {
            // Apply gain.  Pan is handled per-sample in Read() via PanFilterSampleProvider
            ch.Fader.Volume = DbToLinear(ch.GainDb);
        }
    }

    private void RefreshAllFaders()
    {
        foreach (var ch in _channels.Values)
            ApplyFaderVolume(ch);
    }

    // ──────────────────────────────────── inner types ─────────────────────

    private sealed class StemChannel
    {
        public VolumeSampleProvider Fader { get; }
        public float GainDb   { get; set; } = 0f;
        public float Pan       { get; set; } = 0f;   // -1 left, 0 center, +1 right
        public bool  IsMuted  { get; set; } = false;
        public bool  IsSoloed { get; set; } = false;

        public StemChannel(VolumeSampleProvider fader) => Fader = fader;
    }
}
