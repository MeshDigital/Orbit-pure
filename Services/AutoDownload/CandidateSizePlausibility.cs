using System;
using System.Collections.Generic;

namespace SLSKDONET.Services.AutoDownload;

/// <summary>
/// Pure size-vs-duration check that rejects 30–90 s previews, truncated uploads and mislabelled
/// low-bitrate rips that pass a flat minimum-size floor (issue #234).
/// </summary>
public static class CandidateSizePlausibility
{
    public sealed record Result(bool IsPlausible, string Reason, double ImpliedKbps);

    private static readonly HashSet<string> LosslessFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "flac", "wav", "aiff", "aif", "ape", "alac"
    };

    public static Result Evaluate(
        long? sizeBytes,
        int declaredBitrateKbps,
        double? candidateLengthSeconds,
        double? expectedDurationSeconds,
        string? format,
        int losslessMinImpliedKbps = 400,
        int lossyMinImpliedKbps = 96,
        double lossyDeclaredBitrateRatio = 0.6)
    {
        if (sizeBytes is not { } size || size <= 0)
            return new Result(true, "size unknown", 0);

        var referenceSeconds = candidateLengthSeconds is > 0
            ? candidateLengthSeconds.Value
            : expectedDurationSeconds is > 0 ? expectedDurationSeconds.Value : 0;

        if (referenceSeconds <= 0)
            return new Result(true, "duration unknown", 0);

        var impliedKbps = size * 8.0 / 1000.0 / referenceSeconds;
        bool lossless = !string.IsNullOrWhiteSpace(format) && LosslessFormats.Contains(format.Trim().TrimStart('.'));

        if (lossless)
        {
            if (impliedKbps < losslessMinImpliedKbps)
                return new Result(false,
                    $"implied {impliedKbps:0} kbps is below the {losslessMinImpliedKbps} kbps lossless floor (possible preview/truncated file)",
                    impliedKbps);
            return new Result(true, $"implied {impliedKbps:0} kbps", impliedKbps);
        }

        if (declaredBitrateKbps > 0)
        {
            var minimum = declaredBitrateKbps * lossyDeclaredBitrateRatio;
            if (impliedKbps < minimum)
                return new Result(false,
                    $"implied {impliedKbps:0} kbps is below {minimum:0} kbps (60% of declared {declaredBitrateKbps} kbps)",
                    impliedKbps);
        }
        else if (impliedKbps < lossyMinImpliedKbps)
        {
            return new Result(false,
                $"implied {impliedKbps:0} kbps is below the {lossyMinImpliedKbps} kbps lossy floor",
                impliedKbps);
        }

        return new Result(true, $"implied {impliedKbps:0} kbps", impliedKbps);
    }
}