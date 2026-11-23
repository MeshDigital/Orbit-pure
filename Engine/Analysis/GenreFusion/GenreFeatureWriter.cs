using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Engine.Analysis.GenreFusion;

/// <summary>What <see cref="GenreFeatureWriter.Apply"/> fused and resolved.</summary>
/// <param name="Fused">The audio fusion result; null when no model voted and the rhythm couldn't guess.</param>
/// <param name="Resolved">The metadata-first genre (GA8), always set.</param>
public sealed record GenreWriteResult(GenreFusionResult? Fused, ResolvedGenre Resolved);

/// <summary>
/// Fuses the audio genre votes and writes them onto a features row, plus the metadata-first evidence. Shared by
/// analysis (fresh ONNX output) and the genre refresh (GA10 #255, stored scores, no audio), so both write the
/// exact same columns. Touches only the genre columns: never the embedding, style scores, BPM or file tags.
/// </summary>
public static class GenreFeatureWriter
{
    public static GenreWriteResult Apply(
        AudioFeaturesEntity features,
        IReadOnlyList<GenreLabelScore> jamendo,
        IReadOnlyList<GenreLabelScore>? discogs,
        RhythmSignature? rhythm,
        TrackGenreContext? genreContext,
        PersonalGenreModel? personal = null)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(jamendo);

        var head = PersonalInput(personal, features);
        var fused = GenreFusionEngine.Fuse(new GenreFusionInput(jamendo, features.Bpm, discogs, rhythm, head));
        if (fused is null)
        {
            // No audio opinion: the stored labels stay as they are, and a trusted library genre still gets its
            // evidence row (stamped with this fusion version, so a refresh doesn't pick the row up again).
            var alone = TrackGenreResolver.Resolve(genreContext,
                new AudioGenreOpinion(null, 0f, rhythm, FusionVersion: GenreFusionEngine.FusionVersion, PersonalModelId: head?.ModelId));
            features.GenreEvidenceJson = alone.Evidence.ToJson();
            return new GenreWriteResult(null, alone);
        }

        features.ElectronicSubgenre = fused.WinnerLabel;
        features.ElectronicSubgenreConfidence = fused.Confidence;
        features.DetectedSubGenre = fused.WinnerLabel;
        features.SubGenreConfidence = fused.Confidence;
        features.PredictedVibe = fused.WinnerLabel;
        features.PredictionConfidence = fused.Confidence;
        features.GenreDistributionJson = JsonSerializer.Serialize(fused.Distribution);

        // The audio label above stays the audio opinion; the resolved genre (GA8 #253) is what analysis acts on.
        var resolved = TrackGenreResolver.Resolve(genreContext, new AudioGenreOpinion(
            fused.WinnerLabel, fused.Confidence, rhythm, fused.Distribution, fused.Gate, GenreFusionEngine.FusionVersion,
            head?.Probabilities.ToDictionary(kv => GenreTaxonomy.CanonicalDisplay(kv.Key, null), kv => Math.Round((double)kv.Value, 4)),
            head?.ModelId));
        features.GenreEvidenceJson = resolved.Evidence.ToJson();
        return new GenreWriteResult(fused, resolved);
    }

    /// <summary>The personal head's input for this row (GA11 #256): only an enabled model, on an embedding from the same
    /// EffNet model it was trained on. Null otherwise — and then the evidence records no head.</summary>
    public static PersonalHeadInput? PersonalInput(PersonalGenreModel? model, AudioFeaturesEntity features)
    {
        if (model is not { Enabled: true } || model.BlendWeight <= 0) return null;
        if (!string.Equals(model.EmbeddingModelTag, features.EmbeddingModelTag, StringComparison.Ordinal)) return null;
        var probabilities = model.Predict(features.Embedding);
        return probabilities is null ? null : new PersonalHeadInput(probabilities, model.BlendWeight, model.Id);
    }

    /// <summary>The stored 400 Discogs style scores as votes, or null when they don't line up with the labels.</summary>
    public static IReadOnlyList<GenreLabelScore>? DiscogsVotes(float[]? scores, IReadOnlyList<string>? keys)
    {
        if (scores is null || keys is null || scores.Length == 0 || scores.Length != keys.Count) return null;
        var votes = new GenreLabelScore[scores.Length];
        for (var i = 0; i < votes.Length; i++) votes[i] = new GenreLabelScore(keys[i], scores[i]);
        return votes;
    }
}
