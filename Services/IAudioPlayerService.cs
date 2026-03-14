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

        double Pitch { get; set; }
        void Play(string uri);
        void Pause();
        void Stop();
    }

    public class AudioLevelsEventArgs : EventArgs
    {
        public float Left { get; set; }
        public float Right { get; set; }
    }
}
