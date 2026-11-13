using System;
using System.Linq;
using System.Windows.Input;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels
{
    /// <summary>Where playback was when an editor (Cue Forge) interrupted it.</summary>
    public sealed record PlaybackResumePoint(
        string FilePath, string Title, string Artist, double Fraction, bool WasPlaying, string? QueueTrackHash);

    /// <summary>
    /// "Resume playlist": when Cue Forge takes the shared player over to audition cues (of the playing
    /// track, the next one or any other), the player remembers where the playlist was. The top bar then
    /// offers one click back to it — through the queue, so the following transition is prepared again
    /// with the cues and mix-in/out points just edited. The first interruption wins: auditioning
    /// several cues never moves the return point.
    /// </summary>
    public partial class PlayerViewModel
    {
        private PlaybackResumePoint? _resumePoint;
        private bool _resuming;
        private ICommand? _resumeInterruptedCommand;

        public bool HasResumePoint => _resumePoint != null;

        /// <summary>"Valhalla · 2:14" — what the resume button goes back to.</summary>
        public string ResumeLabel
        {
            get
            {
                if (_resumePoint is not { } r) return "";
                double seconds = r.Fraction * Math.Max(0, LengthMs) / 1000.0;
                string at = string.Equals(r.FilePath, LoadedFilePath, StringComparison.OrdinalIgnoreCase) && LengthMs > 0
                    ? " · " + TimeSpan.FromSeconds(seconds).ToString(@"m\:ss")
                    : "";
                return r.Title + at;
            }
        }

        public ICommand ResumeInterruptedCommand => _resumeInterruptedCommand ??= new RelayCommand(ResumeInterrupted);

        /// <summary>
        /// Records where playback is, before an editor takes the player over. Ignored when a point is
        /// already recorded (the first interruption wins) or when nothing is loaded.
        /// </summary>
        public void MarkInterrupted()
        {
            if (_resumePoint != null || _resuming) return;
            var path = LoadedFilePath;
            if (string.IsNullOrEmpty(path)) return;
            // Only worth offering when something was playing, or a playlist is lined up.
            if (!IsPlaying && CurrentTrack == null) return;
            _resumePoint = new PlaybackResumePoint(path, TrackTitle, TrackArtist, Position, IsPlaying, CurrentTrack?.GlobalId);
            RaiseResumeChanged();
        }

        public void ClearResumePoint()
        {
            if (_resumePoint == null) return;
            _resumePoint = null;
            RaiseResumeChanged();
        }

        /// <summary>The editor that interrupted playback is done (it hands the player back).</summary>
        public void ResumeInterrupted(bool play) => Resume(_resumePoint, play);

        private void ResumeInterrupted() => Resume(_resumePoint, play: true);

        private void Resume(PlaybackResumePoint? r, bool play)
        {
            if (r == null) return;
            _resumePoint = null;
            _resuming = true;
            try
            {
                // Back through the queue when the interrupted track is a playlist track: playing it
                // by index prepares the next transition again, now from the edited cues/mix points.
                int index = r.QueueTrackHash is { Length: > 0 } hash
                    ? Queue.ToList().FindIndex(t => string.Equals(t.GlobalId, hash, StringComparison.OrdinalIgnoreCase))
                    : -1;
                bool startPlaying = play && r.WasPlaying;
                if (index >= 0 && string.Equals(Queue[index].Model?.ResolvedFilePath, r.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    PlayTrackAtIndex(index);                       // starts playing and re-arms the next transition
                    Seek((float)Math.Clamp(r.Fraction, 0, 1));
                    if (!startPlaying && IsPlaying) { _playerService.Pause(); IsPlaying = false; }
                }
                else
                {
                    LoadTrackPaused(r.FilePath, r.Title, r.Artist);
                    Seek((float)Math.Clamp(r.Fraction, 0, 1));
                    if (startPlaying) { _playerService.Pause(); IsPlaying = true; } // the toggle starts a loaded deck once the seek has landed
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[Player] Resuming the interrupted playlist failed");
                HasPlaybackError = true;
                PlaybackError = "Couldn't resume the playlist where it was interrupted.";
            }
            finally
            {
                _resuming = false;
                RaiseResumeChanged();
            }
        }

        private void RaiseResumeChanged()
        {
            OnPropertyChanged(nameof(HasResumePoint));
            OnPropertyChanged(nameof(ResumeLabel));
        }
    }
}
