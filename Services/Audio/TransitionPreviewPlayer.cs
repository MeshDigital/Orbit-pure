using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data;
using SLSKDONET.Services.Audio;

namespace SLSKDONET.Services.Audio
{
    public interface ITransitionPreviewPlayer
    {
        Task StartTransitionPreviewAsync(LibraryEntryEntity trackA, LibraryEntryEntity trackB, double overlapSeconds, CancellationToken ct = default);
        void StopPreview();
    }

    public class TransitionPreviewPlayer : ITransitionPreviewPlayer
    {
        private readonly ILogger<TransitionPreviewPlayer> _logger;
        private readonly ISurgicalProcessingService _surgicalService;
        private readonly IAudioPlayerService _audioPlayer;

        public TransitionPreviewPlayer(ILogger<TransitionPreviewPlayer> logger, ISurgicalProcessingService surgicalService, IAudioPlayerService audioPlayer)
        {
            _logger = logger;
            _surgicalService = surgicalService;
            _audioPlayer = audioPlayer;
        }

        public async Task StartTransitionPreviewAsync(LibraryEntryEntity trackA, LibraryEntryEntity trackB, double overlapSeconds, CancellationToken ct = default)
        {
            _logger.LogInformation("🎧 Starting Transition Preview: {TrackA} -> {TrackB} (Overlap: {Overlap}s)", trackA.Title, trackB.Title, overlapSeconds);

            double trackADuration = trackA.DurationSeconds ?? trackA.CanonicalDuration ?? 0;
            double tailStart = Math.Max(0, trackADuration - overlapSeconds);

            string previewPath = await _surgicalService.RenderTransitionPreviewAsync(
                trackA.FilePath, tailStart,
                trackB.FilePath, overlapSeconds,
                overlapSeconds, ct).ConfigureAwait(false);

            _audioPlayer.Play(previewPath);
        }

        public void StopPreview()
        {
            _logger.LogInformation("⏹️ Stopping Transition Preview");
            _audioPlayer.Stop();
        }
    }
}
