using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class PhraseAlignmentServiceTests
{
    [Fact]
    public async Task AlignPhrasesAsync_KeepsADetectedDrop_WhenTheTemplateSlotIsTooFarAway()
    {
        // A Little Sound - I Just Can't Get Enough: the structural engine finds Drop 1 at
        // 56.0s, while the DnB template's Drop 1 sits at 48 bars = 65.829s. The 9.8s gap is
        // outside the 4.8s tolerance; the label match alone must not move the cue.
        var raw = new List<RawBoundary>
        {
            new() { StartTimeSeconds = 0.0, Label = "Intro", Confidence = 0.9f, EnergyLevel = 0.2f },
            new() { StartTimeSeconds = 21.943, Label = "Build", Confidence = 0.8f, EnergyLevel = 0.5f },
            new() { StartTimeSeconds = 43.886, Label = "Build", Confidence = 0.8f, EnergyLevel = 0.6f },
            new() { StartTimeSeconds = 56.0, Label = "Drop", Confidence = 0.9f, EnergyLevel = 0.9f },
            new() { StartTimeSeconds = 65.829, Label = "Build", Confidence = 0.8f, EnergyLevel = 0.5f },
            new() { StartTimeSeconds = 87.771, Label = "Build", Confidence = 0.7f, EnergyLevel = 0.5f },
            new() { StartTimeSeconds = 122.0, Label = "Drop", Confidence = 0.8f, EnergyLevel = 0.9f },
            new() { StartTimeSeconds = 144.0, Label = "Drop", Confidence = 1.0f, EnergyLevel = 0.9f },
            new() { StartTimeSeconds = 153.6, Label = "Outro", Confidence = 0.7f, EnergyLevel = 0.3f },
        };

        var service = new PhraseAlignmentService(NullLogger<PhraseAlignmentService>.Instance);
        var phrases = await service.AlignPhrasesAsync(
            raw, 175, "DnB", "track-3", cancellationToken: default, trackDuration: 171.771);

        var firstDrop = phrases
            .Where(p => p.Type == PhraseType.Drop)
            .OrderBy(p => p.StartTimeSeconds)
            .First();

        Assert.InRange(firstDrop.StartTimeSeconds, 55.8, 56.2);
        Assert.All(phrases, p => Assert.True(p.EndTimeSeconds <= 171.78,
            $"phrase {p.Label} ends at {p.EndTimeSeconds}s, past the 171.771s file"));
        Assert.DoesNotContain(phrases, p => p.StartTimeSeconds >= 171.771);
    }
}
