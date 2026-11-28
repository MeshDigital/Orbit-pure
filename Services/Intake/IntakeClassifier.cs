using System;
using System.Globalization;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Library.Maintenance;

namespace SLSKDONET.Services.Intake;

/// <summary>What the checks found about one incoming file (the input to <see cref="IntakeClassifier"/>).</summary>
public sealed record IntakeFacts(
    string Extension,
    bool Readable,
    string? Artist,
    string? Title,
    int DurationSeconds,
    int BitrateKbps,
    int DecodeErrorCount,
    AudioAuthenticityVerdict? SpectralVerdict,
    double SpectralConfidence,
    double? SpectralCutoffHz);

/// <summary>The library track an incoming file matched, if any.</summary>
public sealed record IntakeLibraryMatch(
    string TrackHash,
    string FilePath,
    string Format,
    int Bitrate,
    IntakeMatchKind Kind);

/// <summary>A file's lane, the action Apply takes unless the user changes it, and one line on why.</summary>
public readonly record struct IntakeVerdict(IntakeLane Lane, IntakeAction Action, string Reason);

/// <summary>
/// Decides each incoming file's review lane and pre-ticked action (epic #632). Pure: the caller
/// gathers the facts and the library match. Order matters: a file that can't be trusted to play
/// is rejected before anything else, an exact copy is a duplicate whatever its quality, and only
/// then is quality compared against the copy already owned.
/// </summary>
public static class IntakeClassifier
{
    /// <summary>Decode errors at or above this count make a file a Reject (a few isolated frame errors don't).</summary>
    public const int RejectDecodeErrors = 5;

    /// <summary>Shorter than this (seconds) and the file is most likely a store preview.</summary>
    public const int PreviewMaxSeconds = 45;

    /// <summary>Lossy files below this bitrate (kbps) are worth upgrading.</summary>
    public const int LowBitrateKbps = 256;

    /// <summary>A spectral "transcoded" verdict needs at least this confidence to flag a file.</summary>
    public const double SpectralFlagConfidence = 0.6;

    public static IntakeVerdict Classify(IntakeFacts facts, IntakeLibraryMatch? match)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (!facts.Readable)
            return new(IntakeLane.Reject, IntakeAction.Skip, "Can't be read as audio");

        if (facts.DecodeErrorCount >= RejectDecodeErrors)
            return new(IntakeLane.Reject, IntakeAction.Skip, $"Doesn't decode cleanly ({facts.DecodeErrorCount} errors)");

        if (facts.DurationSeconds > 0 && facts.DurationSeconds < PreviewMaxSeconds)
            return new(IntakeLane.Reject, IntakeAction.Skip, $"Only {facts.DurationSeconds} s long, likely a preview");

        if (match is { Kind: IntakeMatchKind.ContentHash })
            return new(IntakeLane.Duplicate, IntakeAction.Skip, $"Same audio as {Describe(match)} in your library");

        var format = Format(facts.Extension);
        bool fakeLossless = IsFlaggedBySpectral(facts) && AudioFormats.LosslessTokenSet.Contains(format);
        bool lowBitrate = !AudioFormats.LosslessTokenSet.Contains(format) && facts.BitrateKbps > 0 && facts.BitrateKbps < LowBitrateKbps;
        bool upscaledLossy = IsFlaggedBySpectral(facts) && !fakeLossless;
        bool suspicious = fakeLossless || lowBitrate || upscaledLossy;

        if (match != null)
        {
            // A suspicious file is never better than what's owned: its label can't be trusted.
            int incomingRank = suspicious ? 1 : ExactDuplicateKeeper.QualityRank(format, facts.BitrateKbps);
            int ownedRank = ExactDuplicateKeeper.QualityRank(match.Format, match.Bitrate);
            if (incomingRank > ownedRank)
                return new(IntakeLane.BetterCopy, IntakeAction.Replace,
                    $"{QualityText(format, facts.BitrateKbps)} copy of your {Describe(match)}");
            return new(IntakeLane.Duplicate, IntakeAction.Skip, $"You already have it ({Describe(match)})");
        }

        // A search needs a name: an untagged weak file stays on the drive unless the user adds it.
        bool searchable = !string.IsNullOrWhiteSpace(facts.Artist) && !string.IsNullOrWhiteSpace(facts.Title);
        if (suspicious && !searchable)
            return new(IntakeLane.Suspicious, IntakeAction.Skip,
                (fakeLossless ? $"Labelled {format.ToUpperInvariant()} but sounds like a lossy file{CutoffText(facts)}"
                    : upscaledLossy ? $"{facts.BitrateKbps} kbps label, but encoded from a lower bitrate{CutoffText(facts)}"
                    : $"Low bitrate ({facts.BitrateKbps} kbps)")
                + "; no artist or title to search for a better copy");

        if (fakeLossless)
            return new(IntakeLane.Suspicious, IntakeAction.Upgrade,
                $"Labelled {format.ToUpperInvariant()} but sounds like a lossy file{CutoffText(facts)}");
        if (upscaledLossy)
            return new(IntakeLane.Suspicious, IntakeAction.Upgrade,
                $"{facts.BitrateKbps} kbps label, but encoded from a lower bitrate{CutoffText(facts)}");
        if (lowBitrate)
            return new(IntakeLane.Suspicious, IntakeAction.Upgrade, $"Low bitrate ({facts.BitrateKbps} kbps)");

        if (string.IsNullOrWhiteSpace(facts.Artist) || string.IsNullOrWhiteSpace(facts.Title))
            return new(IntakeLane.Good, IntakeAction.Keep, $"{QualityText(format, facts.BitrateKbps)}, not in your library; missing artist or title");

        return new(IntakeLane.Good, IntakeAction.Keep, $"{QualityText(format, facts.BitrateKbps)}, not in your library");
    }

    /// <summary>The format token for an extension (".flac" → "flac").</summary>
    public static string Format(string? extension) =>
        (extension ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();

    private static bool IsFlaggedBySpectral(IntakeFacts facts) =>
        facts.SpectralVerdict is AudioAuthenticityVerdict.TranscodedLowBitrate
            or AudioAuthenticityVerdict.TranscodedMediumBitrate
            or AudioAuthenticityVerdict.TranscodedHighBitrate
        && facts.SpectralConfidence >= SpectralFlagConfidence;

    private static string QualityText(string format, int bitrate) =>
        AudioFormats.LosslessTokenSet.Contains(format)
            ? $"Lossless {format.ToUpperInvariant()}"
            : bitrate > 0 ? $"{bitrate} kbps {format.ToUpperInvariant()}" : format.ToUpperInvariant();

    private static string Describe(IntakeLibraryMatch match) =>
        QualityText(Format(match.Format), match.Bitrate);

    private static string CutoffText(IntakeFacts facts) =>
        facts.SpectralCutoffHz is double hz && hz > 0
            ? $" (nothing above {(hz / 1000).ToString("0.#", CultureInfo.InvariantCulture)} kHz)"
            : string.Empty;
}
