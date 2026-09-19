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

        /// <summary>
        /// Preset-aware, analysis-driven preview — routes through the real TransitionDsp chain
        /// (instead of a fixed triangular crossfade), starting each track at its own suggested or
        /// saved trigger point rather than "near the literal end of A / literal start of B". See
        /// <see cref="ISurgicalProcessingService"/>'s TransitionModel overload.
        /// </summary>
        Task StartTransitionPreviewAsync(
            string trackATitle, string trackAFilePath, double sourceTriggerSeconds,
            string trackBTitle, string trackBFilePath, double targetTriggerSeconds,
            SLSKDONET.Models.Timeline.TransitionModel model, double projectBpm,
            CancellationToken ct = default);

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

            await PlayRenderedPreviewAsync(previewPath, ct).ConfigureAwait(false);
        }

        public async Task StartTransitionPreviewAsync(
            string trackATitle, string trackAFilePath, double sourceTriggerSeconds,
            string trackBTitle, string trackBFilePath, double targetTriggerSeconds,
            SLSKDONET.Models.Timeline.TransitionModel model, double projectBpm,
            CancellationToken ct = default)
        {
            _logger.LogInformation("🎧 Starting preset-aware Transition Preview: {TrackA} -> {TrackB} ({Preset}), A@{SourceT}s B@{TargetT}s",
                trackATitle, trackBTitle, model.Type, sourceTriggerSeconds, targetTriggerSeconds);

            string previewPath = await _surgicalService.RenderTransitionPreviewAsync(
                trackAFilePath, sourceTriggerSeconds, trackBFilePath, targetTriggerSeconds, model, projectBpm, ct).ConfigureAwait(false);

            await PlayRenderedPreviewAsync(previewPath, ct).ConfigureAwait(false);
        }

        private async Task PlayRenderedPreviewAsync(string previewPath, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                DisposePlaybackResources(deleteRenderedFile: true, stopOutput: true);

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
            // Runs off the calling thread — StopPreview is invoked directly from
            // MixTransitionViewModel.PauseCommand on the UI thread, and WasapiOut.Stop() blocks
            // until its internal audio-render thread acknowledges the stop. If that thread was
            // *already* mid-shutdown (natural end-of-file racing the user's Pause click) and
            // OnPlaybackStopped below was holding _gate at that exact moment, the UI thread's
            // synchronous _gate.Wait() froze the whole app waiting for a gate release that could
            // itself be waiting on a NAudio thread transition. Task.Run keeps any blocking here
            // off the UI thread entirely, whichever side wins the race.
            Task.Run(() =>
            {
                _gate.Wait();
                try
                {
                    DisposePlaybackResources(deleteRenderedFile: true, stopOutput: true);
                }
                finally
                {
                    _gate.Release();
                }
                PreviewStopped?.Invoke(this, EventArgs.Empty);
            });
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
                _logger.LogWarning(e.Exception, "Transition preview playback stopped with error");

            _gate.Wait();
            try
            {
                // stopOutput: false — playback has already stopped (that's why this event fired),
                // and this callback can itself run on WasapiOut's own audio-render thread. Calling
                // Stop() again here would make that thread join itself and hang forever; Dispose()
                // alone is enough to release the device.
                DisposePlaybackResources(deleteRenderedFile: true, stopOutput: false);
            }
            finally
            {
                _gate.Release();
            }
            PreviewStopped?.Invoke(this, EventArgs.Empty);
        }

        private void DisposePlaybackResources(bool deleteRenderedFile, bool stopOutput)
        {
            if (stopOutput)
            {
                try { _output?.Stop(); } catch { /* already stopped/disposed */ }
            }
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
                DisposePlaybackResources(deleteRenderedFile: true, stopOutput: true);
            }
            finally
            {
                _gate.Release();
            }
            _gate.Dispose();
        }
    }
}
