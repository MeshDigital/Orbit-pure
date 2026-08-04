using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SLSKDONET.Services.Similarity;

/// <summary>
/// Genre (MTG-Jamendo, 87-class) and mood (5 binary classifiers) predictions from the shared
/// 1280-D DiscogsEffnet embedding — the small "classifier head" models chained off
/// <see cref="DiscogsEffnetEmbeddingExtractor"/>'s output. Each head is a tiny dense network
/// (input "model/Placeholder" [1280] → Sigmoid/Softmax output), so no audio preprocessing is
/// needed here, unlike the base embedding model.
/// </summary>
public readonly record struct GenreMoodPrediction(
    IReadOnlyList<(string Label, float Probability)> JamendoGenres,
    float MoodHappy,
    float MoodSad,
    float MoodRelaxed,
    float MoodParty,
    float MoodAggressive);

public sealed class EffnetClassifierHeadService : IDisposable
{
    // MTG-Jamendo's 87-class taxonomy, in the model's output index order — confirmed against
    // the model's own published classes list (mtg_jamendo_genre-discogs-effnet-1.json).
    private static readonly string[] JamendoLabels =
    {
        "60s", "70s", "80s", "90s", "acidjazz", "alternative", "alternativerock", "ambient",
        "atmospheric", "blues", "bluesrock", "bossanova", "breakbeat", "celtic", "chanson",
        "chillout", "choir", "classical", "classicrock", "club", "contemporary", "country",
        "dance", "darkambient", "darkwave", "deephouse", "disco", "downtempo", "drumnbass",
        "dub", "dubstep", "easylistening", "edm", "electronic", "electronica", "electropop",
        "ethno", "eurodance", "experimental", "folk", "funk", "fusion", "groove", "grunge",
        "hard", "hardrock", "hiphop", "house", "idm", "improvisation", "indie", "industrial",
        "instrumentalpop", "instrumentalrock", "jazz", "jazzfusion", "latin", "lounge",
        "medieval", "metal", "minimal", "newage", "newwave", "orchestral", "pop", "popfolk",
        "poprock", "postrock", "progressive", "psychedelic", "punkrock", "rap", "reggae",
        "rnb", "rock", "rocknroll", "singersongwriter", "soul", "soundtrack", "swing",
        "symphonic", "synthpop", "techno", "trance", "triphop", "world", "worldfusion",
    };

    private readonly ILogger<EffnetClassifierHeadService> _logger;
    private readonly Head _jamendoGenre;
    private readonly Head _moodHappy;
    private readonly Head _moodSad;
    private readonly Head _moodRelaxed;
    private readonly Head _moodParty;
    private readonly Head _moodAggressive;
    private bool _disposed;

    public EffnetClassifierHeadService(ILogger<EffnetClassifierHeadService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Input/output tensor names confirmed against the real loaded ONNX models' InputMetadata/
        // OutputMetadata, not the published JSON metadata — the JSON documents the original
        // TensorFlow graph's node names ("model/Placeholder", "model/Sigmoid"/"model/Softmax"),
        // but the actual ONNX export renames them to "embeddings" (input) / "activations" (output)
        // uniformly across all these classifier heads.
        string modelsDir = Path.Combine(AppContext.BaseDirectory, "Tools", "Essentia", "models");
        _jamendoGenre   = new Head(Path.Combine(modelsDir, "mtg_jamendo_genre-discogs-effnet-1.onnx"), JamendoLabels.Length, logger);
        _moodHappy      = new Head(Path.Combine(modelsDir, "mood_happy-discogs-effnet-1.onnx"), 2, logger);
        _moodSad        = new Head(Path.Combine(modelsDir, "mood_sad-discogs-effnet-1.onnx"), 2, logger);
        _moodRelaxed    = new Head(Path.Combine(modelsDir, "mood_relaxed-discogs-effnet-1.onnx"), 2, logger);
        _moodParty      = new Head(Path.Combine(modelsDir, "mood_party-discogs-effnet-1.onnx"), 2, logger);
        _moodAggressive = new Head(Path.Combine(modelsDir, "mood_aggressive-discogs-effnet-1.onnx"), 2, logger);
    }

    /// <summary>True when at least the genre classifier head loaded successfully.</summary>
    public bool IsAvailable => _jamendoGenre.IsAvailable;

    /// <summary>
    /// Classifies a track-level 1280-D embedding into genre + mood predictions. Any head whose
    /// model file is missing contributes a neutral/empty result rather than failing the whole
    /// call — partial coverage degrades gracefully, matching the rest of this pipeline.
    /// </summary>
    public GenreMoodPrediction Classify(float[] embedding)
    {
        ArgumentNullException.ThrowIfNull(embedding);

        var genreScores = _jamendoGenre.Predict(embedding);
        var genres = genreScores is null
            ? Array.Empty<(string, float)>()
            : JamendoLabels.Zip(genreScores, (label, score) => (label, score))
                .OrderByDescending(g => g.score)
                .ToArray();

        return new GenreMoodPrediction(
            JamendoGenres:  genres,
            MoodHappy:      _moodHappy.Predict(embedding)?[0] ?? 0f,
            MoodSad:        _moodSad.Predict(embedding)?[0] ?? 0f,
            MoodRelaxed:    _moodRelaxed.Predict(embedding)?[0] ?? 0f,
            MoodParty:      _moodParty.Predict(embedding)?[0] ?? 0f,
            MoodAggressive: _moodAggressive.Predict(embedding)?[0] ?? 0f);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _jamendoGenre.Dispose();
        _moodHappy.Dispose();
        _moodSad.Dispose();
        _moodRelaxed.Dispose();
        _moodParty.Dispose();
        _moodAggressive.Dispose();
    }

    /// <summary>One small dense-network classifier head: 1280-D embedding in, N-class prediction out.</summary>
    private sealed class Head : IDisposable
    {
        private readonly string _modelPath;
        private readonly int _classCount;
        private readonly ILogger _logger;
        private InferenceSession? _session;
        private bool _loadAttempted;

        public Head(string modelPath, int classCount, ILogger logger)
        {
            _modelPath  = modelPath;
            _classCount = classCount;
            _logger     = logger;
        }

        public bool IsAvailable
        {
            get { EnsureLoaded(); return _session != null; }
        }

        public float[]? Predict(float[] embedding)
        {
            EnsureLoaded();
            if (_session == null) return null;

            // Input is batched ([-1, 1280]), even for a single track-level embedding.
            var tensor = new DenseTensor<float>(embedding, new[] { 1, embedding.Length });
            var inputs = new[] { NamedOnnxValue.CreateFromTensor("embeddings", tensor) };

            using var results = _session.Run(inputs);
            var output = results.First(r => r.Name == "activations").AsTensor<float>();

            var scores = new float[_classCount];
            for (int i = 0; i < _classCount; i++)
                scores[i] = output[0, i];
            return scores;
        }

        private void EnsureLoaded()
        {
            if (_loadAttempted) return;
            _loadAttempted = true;

            if (!File.Exists(_modelPath))
            {
                _logger.LogWarning("[ClassifierHead] Model not found at {Path} — this signal will be unavailable.", _modelPath);
                return;
            }

            try
            {
                var opts = new SessionOptions();
                opts.AppendExecutionProvider_DML();
                _session = new InferenceSession(_modelPath, opts);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ClassifierHead] Failed to load ONNX model from {Path}", _modelPath);
            }
        }

        public void Dispose() => _session?.Dispose();
    }
}
