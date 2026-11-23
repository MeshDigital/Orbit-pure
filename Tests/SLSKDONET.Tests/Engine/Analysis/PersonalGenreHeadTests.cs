using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Configuration;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Engine.Analysis.GenreFusion;
using SLSKDONET.Services;
using SLSKDONET.Services.Genre;
using SLSKDONET.Services.Library.Maintenance;
using SLSKDONET.Tests.Services.Sets;
using Xunit;

namespace SLSKDONET.Tests.Engine.Analysis;

/// <summary>GA11 (#256): the personal genre head is trained, gated on cross-validation, persisted and blended.</summary>
public sealed class PersonalGenreHeadTests : IDisposable
{
    private readonly SetsTestDatabase _db = new("personal-genre");
    private readonly string _dir;

    public PersonalGenreHeadTests()
    {
        _dir = Path.Combine(Path.GetDirectoryName(SLSKDONET.Data.OrbitPaths.LibraryDbPath)!, "personal-head-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort in the isolated temp root */ }
    }

    private static readonly GenreBucket[] Buckets = { GenreBucket.DrumAndBass, GenreBucket.House, GenreBucket.Techno };

    /// <summary>Separable clusters on top of a large shared offset (as mean-pooled embeddings have).</summary>
    private static List<PersonalGenreSample> Clusters(int perClass, int dim, int seed, bool randomLabels = false,
        Func<GenreBucket, Random, IReadOnlyDictionary<GenreBucket, double>?>? baseScores = null)
    {
        var rng = new Random(seed);
        var centres = Buckets.ToDictionary(b => b, _ => Enumerable.Range(0, dim).Select(_ => (float)(rng.NextDouble() * 2 - 1)).ToArray());
        var offset = Enumerable.Range(0, dim).Select(_ => 5f + (float)rng.NextDouble()).ToArray();
        var samples = new List<PersonalGenreSample>();
        var n = 0;
        foreach (var bucket in Buckets)
            for (var i = 0; i < perClass; i++, n++)
            {
                var e = new float[dim];
                for (var d = 0; d < dim; d++) e[d] = offset[d] + centres[bucket][d] * 0.5f + (float)(rng.NextDouble() - 0.5) * 0.6f;
                var label = randomLabels ? Buckets[rng.Next(Buckets.Length)] : bucket;
                samples.Add(new PersonalGenreSample($"h{n}", $"artist{n % 37}", e, label, 0.7f, baseScores?.Invoke(label, rng)));
            }
        return samples;
    }

    /// <summary>A weak base: right ~40 % of the time.</summary>
    private static IReadOnlyDictionary<GenreBucket, double> WeakBase(GenreBucket truth, Random rng)
    {
        var guess = rng.NextDouble() < 0.4 ? truth : Buckets[rng.Next(Buckets.Length)];
        return Buckets.ToDictionary(b => b, b => b == guess ? 0.6 : 0.2);
    }

    [Fact]
    public void Fit_SeparatesSyntheticClusters()
    {
        var train = Clusters(80, 64, seed: 1);
        var test = Clusters(40, 64, seed: 1).Select((s, i) => s with { Hash = "t" + i }).ToList();
        var model = PersonalGenreTrainer.Fit(train, Buckets);

        var correct = test.Count(s => model.Predict(s.Embedding)!.OrderByDescending(kv => kv.Value).First().Key == s.Label);
        Assert.True(correct / (double)test.Count > 0.95, $"accuracy {correct}/{test.Count}");
    }

    [Fact]
    public void Train_EnablesAHeadThatBeatsAWeakBase_AndRoundTripsThroughJson()
    {
        var samples = Clusters(60, 64, seed: 2, baseScores: WeakBase);
        var model = PersonalGenreTrainer.Train(samples, "tag", "fp");

        Assert.True(model.Enabled, model.Reason);
        Assert.Contains(model.BlendWeight, PersonalGenreTrainer.BlendWeights);
        Assert.True(model.BlendMacroF1 >= model.BaseMacroF1 + PersonalGenreTrainer.MinGain);
        Assert.Equal(3, model.Classes.Length);

        var back = PersonalGenreModel.TryParse(model.ToJson())!;
        Assert.Equal(model.Predict(samples[0].Embedding)![GenreBucket.DrumAndBass],
            back.Predict(samples[0].Embedding)![GenreBucket.DrumAndBass], 5);
        Assert.Null(PersonalGenreModel.TryParse("{broken"));
    }

    [Fact]
    public void TooFewLabels_Disable()
    {
        var samples = Clusters(60, 32, seed: 3, baseScores: WeakBase).Where(s => s.Label != GenreBucket.House && s.Label != GenreBucket.Techno)
            .Concat(Clusters(10, 32, seed: 4).Where(s => s.Label == GenreBucket.House)).ToList();
        var model = PersonalGenreTrainer.Train(samples, "tag", "fp");
        Assert.False(model.Enabled);
        Assert.Contains("two genres", model.Reason);
    }

    [Fact]
    public void RandomLabels_AreBlockedByTheCrossValidationGate()
    {
        var samples = Clusters(200, 32, seed: 5, randomLabels: true, baseScores: WeakBase);
        var model = PersonalGenreTrainer.Train(samples, "tag", "fp");
        Assert.False(model.Enabled, model.Reason);
        Assert.Empty(model.Weights);
    }

    [Fact]
    public void Blend_IsSharesPlusHead_AndFlipsFusionsWinner()
    {
        var blended = PersonalGenreTrainer.Blend(
            new Dictionary<GenreBucket, double> { [GenreBucket.Techno] = 1.5, [GenreBucket.House] = 0.5 },
            new Dictionary<GenreBucket, float> { [GenreBucket.House] = 0.9f, [GenreBucket.Techno] = 0.1f }, 0.5f);
        Assert.Equal(0.5 * 0.75 + 0.5 * 0.1, blended[GenreBucket.Techno], 6);
        Assert.Equal(0.5 * 0.25 + 0.5 * 0.9, blended[GenreBucket.House], 6);

        var jamendo = new[] { new GenreLabelScore("techno", 0.6f), new GenreLabelScore("house", 0.3f) };
        var plain = GenreFusionEngine.Fuse(new GenreFusionInput(jamendo, 126))!;
        Assert.Equal(GenreBucket.Techno, plain.Bucket);
        var withHead = GenreFusionEngine.Fuse(new GenreFusionInput(jamendo, 126, Personal: new PersonalHeadInput(
            new Dictionary<GenreBucket, float> { [GenreBucket.House] = 0.95f, [GenreBucket.Techno] = 0.05f }, 0.5f, "m1")))!;
        Assert.Equal(GenreBucket.House, withHead.Bucket);
        Assert.Contains(withHead.Candidates, c => c.Source == "personal-head");
    }

    [Fact]
    public void Writer_UsesOnlyAnEnabledHeadOnAMatchingEmbedding_AndRecordsIt()
    {
        var samples = Clusters(60, 64, seed: 2, baseScores: WeakBase);
        var model = PersonalGenreTrainer.Train(samples, "tag-A", "fp");
        Assert.True(model.Enabled, model.Reason);

        var features = new AudioFeaturesEntity { Bpm = 126, Embedding = samples[0].Embedding, EmbeddingModelTag = "tag-A" };
        Assert.NotNull(GenreFeatureWriter.PersonalInput(model, features));
        Assert.Null(GenreFeatureWriter.PersonalInput(model, new AudioFeaturesEntity { Embedding = samples[0].Embedding, EmbeddingModelTag = "tag-B" }));
        Assert.Null(GenreFeatureWriter.PersonalInput(null, features));

        GenreFeatureWriter.Apply(features, new[] { new GenreLabelScore("techno", 0.5f) }, null, null, null, model);
        var evidence = GenreEvidenceRecord.TryParse(features.GenreEvidenceJson)!;
        Assert.Equal(model.Id, evidence.PersonalHead);
        Assert.NotEmpty(evidence.Personal!);
        Assert.True(SLSKDONET.ViewModels.GenreEvidencePresentation.From(evidence).HasPersonal);
    }

    [Fact]
    public void Refresh_TreatsANewHeadAsStale()
    {
        var row = new GenreRefreshService.StalenessRow
        {
            Hash = "h", FusionVersion = GenreFusionEngine.FusionVersion, HasEvidence = true, PersonalHead = null,
            EmbeddingModelTag = "tag-A", HasEmbedding = true,
        };
        var model = new PersonalGenreModel { Enabled = true, BlendWeight = 0.35f, EmbeddingModelTag = "tag-A", TrainedAtUtc = new DateTime(2026, 11, 20) };
        Assert.Equal(model.Id, GenreRefreshService.ExpectedPersonalHead(row, model));
        Assert.Equal(GenreStaleness.Refuse, GenreRefreshService.Classify(row, null, expectedPersonalHead: model.Id));
        Assert.Equal(GenreStaleness.Fresh, GenreRefreshService.Classify(row, null, expectedPersonalHead: null));
        row.EmbeddingModelTag = "tag-B";
        Assert.Null(GenreRefreshService.ExpectedPersonalHead(row, model)); // that row can't use the head: not stale for it
    }

    [Theory]
    [InlineData("user", 1.0, 1.0f)]
    [InlineData("tags", 1.0, 0.7f)]
    [InlineData("beatport", 0.9, 0.5f)]
    [InlineData("discogs", 0.6, null)]
    [InlineData(null, 1.0, 0.5f)]
    [InlineData("maintenance", 1.0, null)]
    [InlineData("spotify", 1.0, null)]
    [InlineData("deezer", 1.0, null)]
    public void LabelWeights_FollowTheSource(string? source, double confidence, float? expected)
        => Assert.Equal(expected, PersonalGenreHeadService.LabelWeight(source, confidence));

    [Fact]
    public async Task Service_TrainsFromTheLibrary_IgnoresOtherModelsRows_PersistsAndSkipsWhenNothingChanged()
    {
        var samples = Clusters(40, 1280, seed: 7);
        _db.SeedLibrary(samples.Select(s => (s.Hash, s.Artist!, s.Hash)).Append(("other-model", "x", "x")).Append(("from-model", "y", "y")).ToArray());
        await using (var db = _db.Factory.CreateDbContext())
        {
            foreach (var s in samples)
            {
                db.LibraryEntries.Find(s.Hash)!.Genres = GenreTaxonomy.CanonicalDisplay(s.Label, null);
                db.AudioFeatures.Add(new AudioFeaturesEntity
                {
                    TrackUniqueHash = s.Hash, EmbeddingModelTag = "tag-A", Embedding = s.Embedding,
                    GenreDistributionJson = "{\"Techno\":0.5}",
                });
            }
            db.LibraryEntries.Find("other-model")!.Genres = "House";
            db.AudioFeatures.Add(new AudioFeaturesEntity { TrackUniqueHash = "other-model", EmbeddingModelTag = "tag-OLD", Embedding = samples[0].Embedding });
            db.LibraryEntries.Find("from-model")!.Genres = "House";
            db.AudioFeatures.Add(new AudioFeaturesEntity { TrackUniqueHash = "from-model", EmbeddingModelTag = "tag-A", Embedding = samples[0].Embedding });
            db.MetadataProvenance.Add(new MetadataProvenanceEntity { TrackUniqueHash = "from-model", Field = "genre", Source = "maintenance", Confidence = 1 });
            await db.SaveChangesAsync();
        }

        var config = new AppConfig { LearnFromGenreCorrections = true };
        var path = Path.Combine(_dir, "models", "personal-genre-head.json");
        using var bus = new EventBusService();
        using var service = new PersonalGenreHeadService(_db.Factory, config, bus, NullLogger<PersonalGenreHeadService>.Instance,
            modelPath: path, currentEmbeddingTag: () => "tag-A");

        var model = await service.TrainIfChangedAsync();
        Assert.NotNull(model);
        Assert.Equal(samples.Count, model!.LabelCount); // the other model's row and the Maintenance genre are not labels
        Assert.True(model.Enabled, model.Reason);       // a constant "Techno" base loses to the head
        Assert.True(File.Exists(path));
        Assert.Same(model, service.ActiveModel);

        var again = await service.TrainIfChangedAsync();
        Assert.Equal(model.TrainedAtUtc, again!.TrainedAtUtc); // same labels: no retrain

        using var reloaded = new PersonalGenreHeadService(_db.Factory, config, bus, NullLogger<PersonalGenreHeadService>.Instance,
            modelPath: path, currentEmbeddingTag: () => "tag-A");
        Assert.Equal(model.Id, reloaded.Model!.Id);

        config.LearnFromGenreCorrections = false;
        Assert.Null(service.ActiveModel);
        Assert.Null(await service.TrainIfChangedAsync(force: true));
    }

    [Fact]
    public void BaseScores_FoldTheStoredDistributionIntoBuckets()
    {
        var scores = PersonalGenreHeadService.BaseScoresFrom("{\"Drum and Bass\":0.8,\"Hard Techno\":0.2,\"Techno\":0.1,\"Other\":0.05}")!;
        Assert.Equal(0.8, scores[GenreBucket.DrumAndBass]);
        Assert.Equal(0.3, scores[GenreBucket.Techno], 6);
        Assert.Equal(0.05, scores[GenreBucket.Other]);
        Assert.Null(PersonalGenreHeadService.BaseScoresFrom("{}"));
    }
}
