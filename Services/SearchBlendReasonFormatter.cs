using System;
using System.Collections.Generic;

namespace SLSKDONET.Services;

public static class SearchBlendReasonFormatter
{
    public static string? BuildCompactReason(IDictionary<string, object>? metadata)
    {
        if (metadata == null)
            return null;

        var fit = ReadDouble(metadata, "BlendFitScore");
        var reliability = ReadDouble(metadata, "BlendReliability");
        var final = ReadDouble(metadata, "BlendFinalScore");

        if (!fit.HasValue && !reliability.HasValue && !final.HasValue)
            return null;

        var fitLabel = fit switch
        {
            >= 85 => "strong fit",
            >= 70 => "good fit",
            >= 55 => "acceptable fit",
            _ => "weak fit"
        };

        var reliabilityLabel = reliability switch
        {
            >= 0.80 => "trusted peer",
            >= 0.60 => "stable peer",
            >= 0.40 => "neutral peer",
            _ => "unproven peer"
        };

        return final.HasValue
            ? $"{fitLabel} • {reliabilityLabel} • score {final.Value:F0}"
            : $"{fitLabel} • {reliabilityLabel}";
    }

    private static double? ReadDouble(IDictionary<string, object> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var raw) || raw is null)
            return null;

        return raw switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            decimal m => (double)m,
            string s when double.TryParse(s, out var parsed) => parsed,
            _ => null
        };
    }
}