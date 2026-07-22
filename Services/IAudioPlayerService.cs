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

        void Play(string uri);
        /// <summary>Opens and initializes the output device without starting playback.</summary>
        void LoadWithoutPlaying(string uri);

        /// <summary>Opens and initializes the output device for the next track ahead of time so
        /// the transition when the current track ends is a near-instant swap (gapless) or a
        /// timed overlap (crossfade) instead of a cold file-open that causes an audible gap.</summary>
        void PreloadNext(string uri);

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
}
