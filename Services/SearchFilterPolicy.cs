using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Soulseek;

namespace SLSKDONET.Services;

public enum SearchRejectionReason
{
    None,
    Format,
    Bitrate,
    SampleRate,
    ExcludedPhrase,
    Queue
}

public readonly record struct SearchFilterDecision(bool IsAccepted, SearchRejectionReason Reason);

public static class SearchFilterPolicy
{
    public static SearchFilterDecision EvaluateFile(
        Soulseek.File file,
        IReadOnlySet<string>? formatSet,
        (int? Min, int? Max) bitrateFilter,
        int preferredMaxSampleRate,
        IReadOnlyCollection<string> excludedPhrases,
        int? maxPeerQueueLength = null,
        int? peerQueueLength = null)
    {
        if (maxPeerQueueLength.HasValue && peerQueueLength.HasValue &&
            maxPeerQueueLength.Value > 0 && peerQueueLength.Value > maxPeerQueueLength.Value)
        {
            return new SearchFilterDecision(false, SearchRejectionReason.Queue);
        }

        var extension = Path.GetExtension(file.Filename)?.TrimStart('.').ToLowerInvariant() ?? string.Empty;
        if (formatSet is { Count: > 0 } && !formatSet.Contains(extension))
        {
            return new SearchFilterDecision(false, SearchRejectionReason.Format);
        }

        var bitrateAttr = file.Attributes?.FirstOrDefault(a => a.Type == FileAttributeType.BitRate);
        var rawBitrate = bitrateAttr?.Value ?? 0;
        // Only apply bitrate filter when the peer actually reported a bitrate.
        // rawBitrate == 0 means the attribute is absent; rejecting these would silently
        // eliminate the majority of Soulseek results that lack embedded metadata.
        if (bitrateFilter.Min.HasValue && rawBitrate > 0 && rawBitrate < bitrateFilter.Min.Value)
        {
            return new SearchFilterDecision(false, SearchRejectionReason.Bitrate);
        }

        if (bitrateFilter.Max.HasValue && bitrateFilter.Max.Value > 0 && rawBitrate > bitrateFilter.Max.Value)
        {
            return new SearchFilterDecision(false, SearchRejectionReason.Bitrate);
        }

        var sampleRateAttr = file.Attributes?.FirstOrDefault(a => a.Type == FileAttributeType.SampleRate);
        var rawSampleRate = sampleRateAttr?.Value ?? 0;
        if (preferredMaxSampleRate > 0 && rawSampleRate > preferredMaxSampleRate)
        {
            return new SearchFilterDecision(false, SearchRejectionReason.SampleRate);
        }

        if (excludedPhrases.Count > 0)
        {
            var lowerPath = file.Filename.ToLowerInvariant();
            foreach (var phrase in excludedPhrases)
            {
                if (lowerPath.Contains(phrase, StringComparison.Ordinal))
                {
                    return new SearchFilterDecision(false, SearchRejectionReason.ExcludedPhrase);
                }
            }
        }

        return new SearchFilterDecision(true, SearchRejectionReason.None);
    }
}