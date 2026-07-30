using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace SLSKDONET.Services.Audio
{
    public interface ITransitionPreviewPlayer : IDisposable
    {
        bool IsPreviewPlaying { get; }

        /// <summary>Raised when a preview stops, whether by user request or natural end-of-file.</summary>
        event EventHandler? PreviewStopped;

        Task StartTransitionPreviewAsync(
            string trackATitle, string trackAFilePath, double trackADurationSeconds,
            string trackBTitle, string trackBFilePath,
            double overlapSeconds, CancellationToken ct = default);

        void StopPreview();
    }

    /// <summary>
    /// Renders a crossfade between the tail of one track and the head of another (via
    /// <see cref="ISurgicalProcessingService.RenderTransitionPreviewAsync"/>) and plays it back
    /// through its own isolated NAudio output — mirroring <see cref="LibraryPreviewPlayer"/> so a
    /// transition preview never hijacks the main Workstation/player-bar <c>IAudioPlayerService</c>
    /// (which would otherwise stop the user's actual playback and confuse its queue-position
    /// tracking when the preview file's own EndReached/TrackAdvanced events fired on it).
    /// </summary>
    public sealed class TransitionPreviewPlayer : ITransitionPreviewPlayer
    {
        private readonly ILogger<TransitionPreviewPlayer> _logger;
        private readonly ISurgicalProcessingService _surgicalService;

        private IWavePlayer? _output;
        private AudioFileReader? _reader;
        private string? _renderedTempPath;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public bool IsPreviewPlaying => _output?.PlaybackState == PlaybackState.Playing;

        public event EventHandler? PreviewStopped;

        public TransitionPreviewPlayer(ILogger<TransitionPreviewPlayer> logger, ISurgicalProcessingService surgicalService)
        {
            _logger = logger;
            _surgicalService = surgicalService;
        }

        public async Task StartTransitionPreviewAsync(
            string trackATitle, string trackAFilePath, double trackADurationSeconds,
            string trackBTitle, string trackBFilePath,
            double overlapSeconds, CancellationToken ct = default)
        {
            _logger.LogInformation("🎧 Starting Transition Preview: {TrackA} -> {TrackB} (Overlap: {Overlap}s)", trackATitle, trackBTitle, overlapSeconds);

            double tailStart = Math.Max(0, trackADurationSeconds - overlapSeconds);

            string previewPath = await _surgicalService.RenderTransitionPreviewAsync(
                trackAFilePath, tailStart,
                trackBFilePath, overlapSeconds,
                overlapSeconds, ct).ConfigureAwait(false);

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                DisposePlaybackResources(deleteRenderedFile: true);

                _reader = new AudioFileReader(previewPath);
                _output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 100);
                _output.Init(_reader);
                _output.PlaybackStopped += OnPlaybackStopped;
                _output.Play();
                _renderedTempPath = previewPath;
            }
            finally
            {
                _gate.Release();
            }
        }

        public void StopPreview()
        {
            _logger.LogInformation("⏹️ Stopping Transition Preview");
            _gate.Wait();
            try
            {
                DisposePlaybackResources(deleteRenderedFile: true);
            }
            finally
            {
                _gate.Release();
            }
            PreviewStopped?.Invoke(this, EventArgs.Empty);
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
                _logger.LogWarning(e.Exception, "Transition preview playback stopped with error");

            _gate.Wait();
            try
            {
                DisposePlaybackResources(deleteRenderedFile: true);
            }
            finally
            {
                _gate.Release();
            }
            PreviewStopped?.Invoke(this, EventArgs.Empty);
        }

        private void DisposePlaybackResources(bool deleteRenderedFile)
        {
            try { _output?.Stop(); } catch { /* already stopped/disposed */ }
            try { _output?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            _output = null;
            _reader = null;

            if (deleteRenderedFile && _renderedTempPath != null)
            {
                try { File.Delete(_renderedTempPath); }
                catch { /* best-effort cleanup of a one-shot preview render */ }
                _renderedTempPath = null;
            }
        }

        public void Dispose()
        {
            _gate.Wait();
            try
            {
                DisposePlaybackResources(deleteRenderedFile: true);
            }
            finally
            {
                _gate.Release();
            }
            _gate.Dispose();
        }
    }
}
