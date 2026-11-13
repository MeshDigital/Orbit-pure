using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.Similarity;

/// <summary>One Discogs style label: <c>Key</c> is the full <c>Parent---Name</c> key.</summary>
public sealed record DiscogsStyle(string Key, string Parent, string Name);

/// <summary>A style label with its model score (0–1).</summary>
public sealed record DiscogsStyleScore(string Key, string Parent, string Name, float Score);

/// <summary>
/// Lazily loads the 400 Discogs style labels (<c>Parent---Style</c>) from the shipped model
/// metadata JSON. Degrades gracefully: a missing or malformed file logs a warning and yields an
/// empty list, and a length mismatch against the model's output disables style persistence. It
/// never throws from its constructor, because a throwing constructor poisons the DI singleton.
/// </summary>
public sealed class DiscogsStyleLabels
{
    public const int ExpectedClassCount = 400;

    private static readonly string DefaultLabelsRelativePath =
        Path.Combine("Tools", "Essentia", "models", "genre_discogs400-discogs-effnet-1.json");

    private readonly string _labelsPath;
    private readonly ILogger<DiscogsStyleLabels> _logger;
    private readonly Lazy<IReadOnlyList<DiscogsStyle>> _all;

    public DiscogsStyleLabels(ILogger<DiscogsStyleLabels> logger, string? labelsPath = null)
    {
        _logger = logger;
        _labelsPath = labelsPath ?? Path.Combine(AppContext.BaseDirectory, DefaultLabelsRelativePath);
        _all = new Lazy<IReadOnlyList<DiscogsStyle>>(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IReadOnlyList<DiscogsStyle> All => _all.Value;

    /// <summary>
    /// True when the labels loaded successfully and match the model output length, so the style
    /// scores can be persisted and read back meaningfully.
    /// </summary>
    public bool IsUsable => All.Count == ExpectedClassCount;

    /// <summary>
    /// The top <paramref name="n"/> styles by score. Returns an empty list when the scores length
    /// does not match the label count (model/labels mismatch) — this disables style persistence.
    /// </summary>
    public IReadOnlyList<DiscogsStyleScore> TopN(float[] scores, int n)
    {
        if (scores == null || scores.Length == 0 || All.Count == 0) return Array.Empty<DiscogsStyleScore>();
        if (scores.Length != All.Count)
        {
            _logger.LogError(
                "[DiscogsStyleLabels] Style score length {Scores} does not match label count {Labels}; style persistence disabled.",
                scores.Length, All.Count);
            return Array.Empty<DiscogsStyleScore>();
        }

        return scores
            .Select((score, i) => new DiscogsStyleScore(All[i].Key, All[i].Parent, All[i].Name, score))
            .OrderByDescending(s => s.Score)
            .Take(n)
            .ToList();
    }

    private IReadOnlyList<DiscogsStyle> Load()
    {
        try
        {
            if (!File.Exists(_labelsPath))
            {
                _logger.LogWarning("[DiscogsStyleLabels] Style labels file not found at {Path}; style scores will not be persisted.", _labelsPath);
                return Array.Empty<DiscogsStyle>();
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(_labelsPath));
            if (!doc.RootElement.TryGetProperty("classes", out var classes) || classes.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("[DiscogsStyleLabels] Style labels file at {Path} has no \"classes\" array.", _labelsPath);
                return Array.Empty<DiscogsStyle>();
            }

            var list = new List<DiscogsStyle>(classes.GetArrayLength());
            foreach (var entry in classes.EnumerateArray())
            {
                var key = entry.GetString();
                if (string.IsNullOrWhiteSpace(key)) continue;
                var sep = key.IndexOf("---", StringComparison.Ordinal);
                if (sep <= 0 || sep + 3 >= key.Length)
                {
                    list.Add(new DiscogsStyle(key, string.Empty, key));
                    continue;
                }
                list.Add(new DiscogsStyle(key, key[..sep], key[(sep + 3)..]));
            }

            if (list.Count != ExpectedClassCount)
                _logger.LogWarning("[DiscogsStyleLabels] Expected {Expected} labels, loaded {Actual} from {Path}.", ExpectedClassCount, list.Count, _labelsPath);
            else
                _logger.LogInformation("[DiscogsStyleLabels] Loaded {Count} Discogs style labels.", list.Count);

            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DiscogsStyleLabels] Failed to load style labels from {Path}.", _labelsPath);
            return Array.Empty<DiscogsStyle>();
        }
    }
}
