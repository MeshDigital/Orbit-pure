using System;

namespace SLSKDONET.Services
{
    public interface IAudioPlayerService : IDisposable
    {
        bool IsPlaying { get; }
        bool IsInitialized { get; } // Check if LibVLC native libraries loaded successfully
        long Length { get; } // Duration in ms
        double Duration { get; } // Duration in seconds
        long Time { get; }   // Current time in ms
        float Position { get; set; } // 0.0 to 1.0
        int Volume { get; set; }     // 0 to 100
        bool IsVisualizerActive { get; set; } // Phase 2: High-Performance rendering coordination

        event EventHandler<long> TimeChanged;
        event EventHandler<float> PositionChanged;
        event EventHandler<long> LengthChanged;
        event EventHandler<AudioLevelsEventArgs> AudioLevelsChanged;
        event EventHandler<float[]> SpectrumChanged;

        event EventHandler EndReached;
        event EventHandler PausableChanged;

        /// <summary>Fired when the engine autonomously advances to a preloaded track (gapless
        /// swap or crossfade completion) rather than the caller explicitly starting playback.</summary>
        event EventHandler TrackAdvanced;

        double Pitch { get; set; }

        /// <summary>When enabled, a preloaded next track fades in while the current one fades
        /// out over <see cref="CrossfadeSeconds"/> instead of a hard gapless cut.</summary>
        bool CrossfadeEnabled { get; set; }

        /// <summary>Length of the crossfade overlap, in seconds.</summary>
        double CrossfadeSeconds { get; set; }

        /// <summary>True while an active crossfade (a saved Mix transition or the legacy fixed
        /// overlap) is in progress — previously computed internally with no external visibility
        /// at all, so no UI could show "mixing now" during real queue playback.</summary>
        bool IsCrossfading { get; }

        /// <summary>0.0-1.0 progress through the active crossfade; 0 when none is active.</summary>
        double CrossfadeProgress { get; }

        /// <summary>Raised once when a crossfade begins.</summary>
        event EventHandler<CrossfadeStartedEventArgs>? CrossfadeStarted;

        /// <summary>Raised on every engine tick while a crossfade is in progress.</summary>
        event EventHandler<double>? CrossfadeProgressChanged;

        /// <summary>Raised once when the active crossfade finishes.</summary>
        event EventHandler? CrossfadeEnded;

        /// <param name="trackLoudnessLufs">The track's already-analyzed integrated loudness (LUFS), if known — used for loudness-normalized playback when enabled in Settings. Null plays at unadjusted gain.</param>
        void Play(string uri, double? trackLoudnessLufs = null);
        /// <summary>Opens and initializes the output device without starting playback.</summary>
        void LoadWithoutPlaying(string uri, double? trackLoudnessLufs = null);

        /// <summary>Opens and initializes the output device for the next track ahead of time so
        /// the transition when the current track ends is a near-instant swap (gapless) or a
        /// timed overlap (crossfade) instead of a cold file-open that causes an audible gap.</summary>
        /// <param name="presetName">Display name of the Mix preset (e.g. "Wave"), if any — carried
        /// through purely for UI visibility (see <see cref="CrossfadeStartedEventArgs.PresetName"/>),
        /// not used by the DSP itself.</param>
        void PreloadNext(string uri, double? trackLoudnessLufs = null, SLSKDONET.Models.Timeline.TransitionModel? transition = null, double? transitionBpm = null,
            double? sourceTriggerSeconds = null, double? targetTriggerSeconds = null, string? presetName = null);
        void SetPendingTransitionForNext(string filePath, SLSKDONET.Models.Timeline.TransitionModel? transition, double? transitionBpm,
            double? sourceTriggerSeconds = null, double? targetTriggerSeconds = null, string? presetName = null);

        /// <summary>Discards any preloaded next track.</summary>
        void CancelPreload();

        void Pause();
        void Stop();
    }

    public class AudioLevelsEventArgs : EventArgs
    {
        public float Left { get; set; }
        public float Right { get; set; }
    }

    /// <summary>Raised once when AudioPlayerService begins an active crossfade.</summary>
    public class CrossfadeStartedEventArgs : EventArgs
    {
        /// <summary>Display name of the Mix preset driving this crossfade (e.g. "Wave"), or null
        /// when no saved transition was attached (the legacy fixed equal-power crossfade).</summary>
        public string? PresetName { get; init; }

        /// <summary>Total length of the crossfade, in seconds.</summary>
        public double DurationSeconds { get; init; }
    }
}
