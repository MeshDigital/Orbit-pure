using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.AudioAnalysis;
using Xunit;

namespace SLSKDONET.Tests.Services.AudioAnalysis;

public sealed class CuePointDetectionServiceTests
{
    [Fact]
    public async Task DetectAndPersistAsync_TimeTillDrop_UsesFirstNotStrongestDrop()
    {
        const int duration = 420;
        var energy = Enumerable.Repeat(0.2f, duration).ToArray();

        void SetEnergy(int start, int end, float value)
        {
            for (int i = start; i < end; i++) energy[i] = value;
        }

        SetEnergy(32, 112, 0.65f);
        SetEnergy(128, 208, 0.80f);
        SetEnergy(224, 304, 0.90f);
        SetEnergy(320, duration, 1.00f);

        var features = new AudioFeaturesEntity
        {
            TrackUniqueHash = "first-drop",
            Bpm = 120,
            TrackDuration = duration,
            EnergyCurveJson = JsonSerializer.Serialize(energy),
            TimeTillDropSeconds = 999,
        };
        var service = new CuePointDetectionService(NullLogger<CuePointDetectionService>.Instance);

        await service.DetectAndPersistAsync("first-drop", features, essentiaOutput: null);

        Assert.Equal(320, features.DropTimeSeconds!.Value, precision: 3);
        Assert.Equal(32, features.TimeTillDropSeconds!.Value, precision: 3);
    }

    [Fact]
    public async Task DetectAndPersistAsync_NoUsableDrop_ClearsTimeTillDropToNull()
    {
        var features = new AudioFeaturesEntity
        {
            TrackUniqueHash = "no-drop",
            Bpm = 174,
            TrackDuration = 120,
            EnergyCurveJson = JsonSerializer.Serialize(Enumerable.Repeat(0.4f, 120).ToArray()),
            TimeTillDropSeconds = 32,
        };
        var service = new CuePointDetectionService(NullLogger<CuePointDetectionService>.Instance);

        await service.DetectAndPersistAsync("no-drop", features, essentiaOutput: null);

        Assert.Null(features.TimeTillDropSeconds);
    }
}
