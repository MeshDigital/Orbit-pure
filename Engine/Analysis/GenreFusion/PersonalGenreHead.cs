using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace SLSKDONET.Engine.Analysis.GenreFusion;

/// <summary>One labelled track for the personal genre head (GA11 #256).</summary>
/// <param name="Weight">Label trust: the user's genre 1.0, a clean file tag 0.7, Beatport/Discogs or unknown origin 0.5.</param>
/// <param name="BaseScores">The audio fusion's bucket scores for this track (the "base" the head must beat), or null.</param>
public sealed record PersonalGenreSample(
    string Hash,
    string? Artist,
    float[] Embedding,
    GenreBucket Label,
    float Weight,
    IReadOnlyDictionary<GenreBucket, double>? BaseScores);

/// <summary>
/// The trained head as persisted in <c>models/personal-genre-head.json</c>: a multinomial logistic regression over the
/// centred, L2-normalised 1,280-D EffNet embedding, plus the cross-validation numbers that decided whether it is used.
/// </summary>
public sealed class PersonalGenreModel
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public DateTime TrainedAtUtc { get; init; }
    public string? EmbeddingModelTag { get; init; }
    /// <summary>Stable digest of the labels it was trained on; a different digest means "retrain".</summary>
    public string LabelFingerprint { get; init; } = string.Empty;
    public int LabelCount { get; init; }
    /// <summary>Labels per bucket name, for the Settings readout.</summary>
    public Dictionary<string, int> LabelsPerClass { get; init; } = new();

    /// <summary>Blended into fusion only when cross-validation showed it helps (≥ <see cref="PersonalGenreTrainer.MinGain"/> macro-F1).</summary>
    public bool Enabled { get; init; }
    /// <summary>Why it is (not) enabled, in plain words.</summary>
    public string Reason { get; init; } = string.Empty;
    public float BlendWeight { get; init; }
    public double BaseMacroF1 { get; init; }
    public double BlendMacroF1 { get; init; }

    public string[] Classes { get; init; } = Array.Empty<string>();
    public float[] Mean { get; init; } = Array.Empty<float>();
    public float[][] Weights { get; init; } = Array.Empty<float[]>();
    public float[] Bias { get; init; } = Array.Empty<float>();

    /// <summary>Identifies this training run in the genre evidence (GA10 staleness).</summary>
    [JsonIgnore]
    public string Id => TrainedAtUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>True when the weights line up with an embedding of <paramref name="dimension"/>.</summary>
    public bool CanPredict(int dimension) =>
        Classes.Length >= 2 && Weights.Length == Classes.Length && Bias.Length == Classes.Length &&
        Mean.Length == dimension && Weights.All(w => w.Length == dimension);

    /// <summary>Class probabilities for one raw embedding, or null when the model can't score it.</summary>
    public IReadOnlyDictionary<GenreBucket, float>? Predict(float[]? embedding)
    {
        if (embedding is null || !CanPredict(embedding.Length)) return null;
        var x = PersonalGenreTrainer.Prepare(embedding, Mean);
        var probs = PersonalGenreTrainer.Softmax(Weights, Bias, x);
        var result = new Dictionary<GenreBucket, float>();
        for (var c = 0; c < Classes.Length; c++)
            if (Enum.TryParse<GenreBucket>(Classes[c], out var bucket)) result[bucket] = probs[c];
        return result;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
    public static PersonalGenreModel? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var model = JsonSerializer.Deserialize<PersonalGenreModel>(json, JsonOptions);
            return model is { Version: CurrentVersion } ? model : null;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// Trains the personal genre head (GA11 #256). Pure and deterministic: hand-rolled softmax regression (Adam, L2,
/// class-balanced weights, SIMD dot products) rather than ML.NET's SDCA, because the model is a handful of weight
/// vectors that serialise straight to JSON, there's no IDataView/pipeline to version, and a seeded loop gives
/// identical results on every run. Gated on evidence: 5-fold cross-validation grouped by artist (so one artist's
/// tracks never sit in both train and test) must show the blend beating the base fusion by
/// <see cref="MinGain"/> macro-F1, or the head stays disabled.
/// </summary>
public static class PersonalGenreTrainer
{
    public const int MinLabelsPerClass = 20;
    public const double MinGain = 0.02;
    public const int Folds = 5;
    public static readonly float[] BlendWeights = { 0.2f, 0.35f, 0.5f };

    private const int Epochs = 25;
    private const int BatchSize = 64;
    private const float LearningRate = 0.02f;
    private const float L2 = 1e-4f;
    private const int Seed = 1234;

    /// <summary>Blend of the base fusion (as shares of its total) and the head's probabilities. Used by fusion and CV alike.</summary>
    public static Dictionary<GenreBucket, double> Blend(IReadOnlyDictionary<GenreBucket, double>? baseScores,
        IReadOnlyDictionary<GenreBucket, float> head, float weight)
    {
        var result = new Dictionary<GenreBucket, double>();
        var total = baseScores?.Where(kv => kv.Value > 0).Sum(kv => kv.Value) ?? 0;
        if (baseScores is not null && total > 0)
            foreach (var (bucket, score) in baseScores)
                if (score > 0) result[bucket] = (1 - weight) * score / total;
        foreach (var (bucket, p) in head)
            result[bucket] = result.GetValueOrDefault(bucket) + weight * p;
        return result;
    }

    /// <summary>A stable digest of the labels and embedding tag (order-independent).</summary>
    public static string Fingerprint(IEnumerable<(string Hash, GenreBucket Label, float Weight)> labels, string? embeddingTag)
    {
        ulong h = 1469598103934665603UL;
        void Mix(string s) { foreach (var ch in s) { h ^= ch; h *= 1099511628211UL; } h ^= 0xFF; h *= 1099511628211UL; }
        Mix(embeddingTag ?? "-");
        foreach (var l in labels.OrderBy(l => l.Hash, StringComparer.Ordinal))
            Mix($"{l.Hash}:{l.Label}:{l.Weight:0.##}");
        return h.ToString("x16");
    }

    public static PersonalGenreModel Train(IReadOnlyList<PersonalGenreSample> samples, string? embeddingTag, string fingerprint,
        DateTime? nowUtc = null, CancellationToken ct = default)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var perClass = samples.GroupBy(s => s.Label).ToDictionary(g => g.Key, g => g.Count());
        var labelsPerClass = perClass.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
        var classes = perClass.Where(kv => kv.Value >= MinLabelsPerClass).Select(kv => kv.Key).OrderBy(b => b).ToArray();
        var dim = samples.Count > 0 ? samples[0].Embedding.Length : 0;

        PersonalGenreModel Disabled(string reason, double baseF1 = 0, double blendF1 = 0, float weight = 0) => new()
        {
            TrainedAtUtc = now, EmbeddingModelTag = embeddingTag, LabelFingerprint = fingerprint, LabelCount = samples.Count,
            LabelsPerClass = labelsPerClass, Enabled = false, Reason = reason,
            BaseMacroF1 = baseF1, BlendMacroF1 = blendF1, BlendWeight = weight,
        };

        if (classes.Length < 2)
            return Disabled($"Needs at least {MinLabelsPerClass} labelled tracks in two genres; has {DescribeCounts(perClass)}.");

        var usable = samples.Where(s => classes.Contains(s.Label) && s.Embedding.Length == dim).ToList();

        // Cross-validation, folds grouped by artist.
        var folds = usable.Select(s => (int)(StableHash(GroupKey(s)) % Folds)).ToArray();
        var predictions = new List<(GenreBucket Truth, IReadOnlyDictionary<GenreBucket, double>? Base, IReadOnlyDictionary<GenreBucket, float> Head)>();
        for (var fold = 0; fold < Folds; fold++)
        {
            ct.ThrowIfCancellationRequested();
            var train = usable.Where((_, i) => folds[i] != fold).ToList();
            var test = usable.Where((_, i) => folds[i] == fold).ToList();
            if (test.Count == 0 || train.Select(s => s.Label).Distinct().Count() < 2) continue;
            var model = Fit(train, classes, ct);
            foreach (var s in test)
                predictions.Add((s.Label, s.BaseScores, model.Predict(s.Embedding)!));
        }
        if (predictions.Count == 0)
            return Disabled("Not enough distinct artists to cross-validate.");

        var baseF1 = MacroF1(classes, predictions.Select(p => (p.Truth, Argmax(p.Base))));
        var best = BlendWeights
            .Select(w => (Weight: w, F1: MacroF1(classes, predictions.Select(p => (p.Truth, Argmax(Blend(p.Base, p.Head, w)))))))
            .OrderByDescending(x => x.F1).ThenBy(x => x.Weight).First();

        if (best.F1 - baseF1 < MinGain)
            return Disabled(SLSKDONET.Utils.NumFormat.Ui($"Doesn't beat the audio model on your tracks yet ({best.F1:P0} vs {baseF1:P0} macro-F1); not used."),
                baseF1, best.F1, best.Weight);

        ct.ThrowIfCancellationRequested();
        var final = Fit(usable, classes, ct);
        return new PersonalGenreModel
        {
            TrainedAtUtc = now, EmbeddingModelTag = embeddingTag, LabelFingerprint = fingerprint, LabelCount = samples.Count,
            LabelsPerClass = labelsPerClass, Enabled = true,
            Reason = SLSKDONET.Utils.NumFormat.Ui($"Improves genre on your tracks: {baseF1:P0} → {best.F1:P0} macro-F1 (cross-validated)."),
            BlendWeight = best.Weight, BaseMacroF1 = baseF1, BlendMacroF1 = best.F1,
            Classes = final.Classes, Mean = final.Mean, Weights = final.Weights, Bias = final.Bias,
        };
    }

    /// <summary>Fits the softmax regression on <paramref name="train"/>. Public for tests.</summary>
    public static PersonalGenreModel Fit(IReadOnlyList<PersonalGenreSample> train, IReadOnlyList<GenreBucket> classes, CancellationToken ct = default)
    {
        var dim = train[0].Embedding.Length;
        var c = classes.Count;
        var classIndex = classes.Select((b, i) => (b, i)).ToDictionary(x => x.b, x => x.i);

        // Centre on the training mean, then L2-normalise: mean-pooled embeddings share a large common offset.
        var mean = new float[dim];
        foreach (var s in train) Axpy(1f, s.Embedding, mean);
        for (var d = 0; d < dim; d++) mean[d] /= train.Count;
        var xs = train.Select(s => Prepare(s.Embedding, mean)).ToArray();
        var ys = train.Select(s => classIndex[s.Label]).ToArray();

        // Class-balanced sample weights times label trust.
        var counts = new double[c];
        foreach (var y in ys) counts[y]++;
        var sampleWeight = train.Select((s, i) => (float)(s.Weight * train.Count / (c * Math.Max(1, counts[ys[i]])))).ToArray();

        var w = new float[c][];
        var mW = new float[c][];
        var vW = new float[c][];
        for (var k = 0; k < c; k++) { w[k] = new float[dim]; mW[k] = new float[dim]; vW[k] = new float[dim]; }
        var b = new float[c];
        var mB = new float[c];
        var vB = new float[c];
        var grad = new float[c][];
        for (var k = 0; k < c; k++) grad[k] = new float[dim];
        var gradB = new float[c];

        const float beta1 = 0.9f, beta2 = 0.999f, eps = 1e-8f;
        var order = Enumerable.Range(0, xs.Length).ToArray();
        var rng = new Random(Seed);
        var step = 0;
        for (var epoch = 0; epoch < Epochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();
            rng.Shuffle(order);
            for (var start = 0; start < order.Length; start += BatchSize)
            {
                var end = Math.Min(order.Length, start + BatchSize);
                for (var k = 0; k < c; k++) { Array.Clear(grad[k]); gradB[k] = 0; }
                float weightSum = 0;
                for (var j = start; j < end; j++)
                {
                    var i = order[j];
                    var p = Softmax(w, b, xs[i]);
                    for (var k = 0; k < c; k++)
                    {
                        var err = (p[k] - (ys[i] == k ? 1f : 0f)) * sampleWeight[i];
                        Axpy(err, xs[i], grad[k]);
                        gradB[k] += err;
                    }
                    weightSum += sampleWeight[i];
                }

                step++;
                var scale = 1f / Math.Max(1e-6f, weightSum);
                var lrT = LearningRate * MathF.Sqrt(1 - MathF.Pow(beta2, step)) / (1 - MathF.Pow(beta1, step));
                for (var k = 0; k < c; k++)
                {
                    var wk = w[k]; var gk = grad[k]; var mk = mW[k]; var vk = vW[k];
                    for (var d = 0; d < dim; d++)
                    {
                        var g = gk[d] * scale + L2 * wk[d];
                        mk[d] = beta1 * mk[d] + (1 - beta1) * g;
                        vk[d] = beta2 * vk[d] + (1 - beta2) * g * g;
                        wk[d] -= lrT * mk[d] / (MathF.Sqrt(vk[d]) + eps);
                    }
                    var gb = gradB[k] * scale;
                    mB[k] = beta1 * mB[k] + (1 - beta1) * gb;
                    vB[k] = beta2 * vB[k] + (1 - beta2) * gb * gb;
                    b[k] -= lrT * mB[k] / (MathF.Sqrt(vB[k]) + eps);
                }
            }
        }

        return new PersonalGenreModel
        {
            Classes = classes.Select(x => x.ToString()).ToArray(),
            Mean = mean,
            Weights = w,
            Bias = b,
        };
    }

    /// <summary>Centre and L2-normalise one embedding.</summary>
    internal static float[] Prepare(float[] embedding, float[] mean)
    {
        var x = new float[embedding.Length];
        double norm = 0;
        for (var d = 0; d < x.Length; d++)
        {
            var v = embedding[d] - mean[d];
            if (float.IsNaN(v)) v = 0;
            x[d] = v;
            norm += v * v;
        }
        var inv = norm > 1e-12 ? (float)(1 / Math.Sqrt(norm)) : 0f;
        for (var d = 0; d < x.Length; d++) x[d] *= inv * 10f; // unit length × 10: well-scaled logits for Adam
        return x;
    }

    internal static float[] Softmax(float[][] w, float[] b, float[] x)
    {
        var c = b.Length;
        var z = new float[c];
        var max = float.NegativeInfinity;
        for (var k = 0; k < c; k++) { z[k] = Dot(w[k], x) + b[k]; if (z[k] > max) max = z[k]; }
        float sum = 0;
        for (var k = 0; k < c; k++) { z[k] = MathF.Exp(z[k] - max); sum += z[k]; }
        for (var k = 0; k < c; k++) z[k] /= sum;
        return z;
    }

    private static float Dot(float[] a, float[] x)
    {
        var acc = Vector<float>.Zero;
        var n = Vector<float>.Count;
        var i = 0;
        for (; i <= a.Length - n; i += n) acc += new Vector<float>(a, i) * new Vector<float>(x, i);
        var sum = Vector.Dot(acc, Vector<float>.One);
        for (; i < a.Length; i++) sum += a[i] * x[i];
        return sum;
    }

    private static void Axpy(float alpha, float[] x, float[] y)
    {
        var n = Vector<float>.Count;
        var va = new Vector<float>(alpha);
        var i = 0;
        for (; i <= x.Length - n; i += n) (new Vector<float>(y, i) + va * new Vector<float>(x, i)).CopyTo(y, i);
        for (; i < x.Length; i++) y[i] += alpha * x[i];
    }

    private static GenreBucket? Argmax<T>(IReadOnlyDictionary<GenreBucket, T>? scores) where T : struct, IComparable<T>
    {
        if (scores is null || scores.Count == 0) return null;
        return scores.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
    }

    private static GenreBucket? Argmax(Dictionary<GenreBucket, double> scores) => Argmax<double>(scores);

    /// <summary>Macro-F1 over <paramref name="classes"/>; a prediction outside them (or none) is a miss.</summary>
    internal static double MacroF1(IReadOnlyList<GenreBucket> classes, IEnumerable<(GenreBucket Truth, GenreBucket? Predicted)> pairs)
    {
        var list = pairs.ToList();
        double total = 0;
        foreach (var cls in classes)
        {
            var tp = list.Count(p => p.Truth == cls && p.Predicted == cls);
            var fp = list.Count(p => p.Truth != cls && p.Predicted == cls);
            var fn = list.Count(p => p.Truth == cls && p.Predicted != cls);
            total += tp == 0 ? 0 : 2.0 * tp / (2.0 * tp + fp + fn);
        }
        return classes.Count == 0 ? 0 : total / classes.Count;
    }

    private static string GroupKey(PersonalGenreSample s) =>
        string.IsNullOrWhiteSpace(s.Artist) ? "#" + s.Hash : s.Artist.Trim().ToLowerInvariant();

    private static ulong StableHash(string s)
    {
        ulong h = 1469598103934665603UL;
        foreach (var ch in s) { h ^= ch; h *= 1099511628211UL; }
        return h;
    }

    private static string DescribeCounts(Dictionary<GenreBucket, int> perClass) =>
        perClass.Count == 0 ? "none" : string.Join(", ", perClass.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"));
}
