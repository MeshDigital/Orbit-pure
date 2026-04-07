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

    // ──────────────────────────────────── ISampleProvider ─────────────────

    /// <inheritdoc />
    public int Read(float[] buffer, int offset, int count)
        => _mixBus.Read(buffer, offset, count);

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
        public bool  IsMuted  { get; set; } = false;
        public bool  IsSoloed { get; set; } = false;

        public StemChannel(VolumeSampleProvider fader) => Fader = fader;
    }
}
