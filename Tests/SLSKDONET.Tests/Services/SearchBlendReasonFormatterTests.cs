using System.Collections.Generic;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class SearchBlendReasonFormatterTests
{
    [Fact]
    public void BuildCompactReason_ShouldReturnStrongTrustedLabel_WhenTelemetryIsHigh()
    {
        var metadata = new Dictionary<string, object>
        {
            ["BlendFitScore"] = 92.0,
            ["BlendReliability"] = 0.91,
            ["BlendFinalScore"] = 95.4
        };

        var reason = SearchBlendReasonFormatter.BuildCompactReason(metadata);

        Assert.NotNull(reason);
        Assert.Contains("strong fit", reason);
        Assert.Contains("trusted peer", reason);
        Assert.Contains("score 95", reason);
    }

    [Fact]
    public void BuildCompactReason_ShouldReturnNull_WhenTelemetryMissing()
    {
        var metadata = new Dictionary<string, object>
        {
            ["Other"] = 123
        };

        var reason = SearchBlendReasonFormatter.BuildCompactReason(metadata);

        Assert.Null(reason);
    }
}
