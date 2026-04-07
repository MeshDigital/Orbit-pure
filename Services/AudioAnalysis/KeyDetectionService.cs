using System;
using System.Collections.Generic;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data.Essentia;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Detects musical key and scale from Essentia tonal analysis output,
/// maps results to Open Key and Camelot Wheel notation, and writes them
/// into <see cref="AudioFeaturesEntity"/>.
/// </summary>
public sealed class KeyDetectionService
{
    /// <summary>
    /// Primary algorithm: EDMA profile.
    /// Fallback:          Krumhansl–Schmuckler profile.
    /// The service picks whichever has higher strength.
    /// </summary>
    public void Detect(EssentiaOutput essentiaOutput, AudioFeaturesEntity target)
    {
        ArgumentNullException.ThrowIfNull(essentiaOutput);
        ArgumentNullException.ThrowIfNull(target);

        var tonal = essentiaOutput.Tonal;
        if (tonal == null) return;

        // Pick the profile with higher strength (more confident)
        KeyData? chosen = ChooseBestKey(tonal.KeyEdma, tonal.KeyKrumhansl);
        if (chosen == null) return;

        string key   = chosen.Key?.Trim() ?? string.Empty;
        string scale = chosen.Scale?.Trim().ToLowerInvariant() ?? string.Empty;
        float  confidence = chosen.Strength;

        target.Key           = key;
        target.Scale         = scale;
        target.KeyConfidence = Math.Clamp(confidence, 0f, 1f);
        target.CamelotKey    = ToCamelotKey(key, scale);
    }

    // ──────────────────────────────────── helpers ─────────────────────────

    private static KeyData? ChooseBestKey(KeyData? edma, KeyData? krumhansl)
    {
        if (edma == null) return krumhansl;
        if (krumhansl == null) return edma;
        return edma.Strength >= krumhansl.Strength ? edma : krumhansl;
    }

    /// <summary>
    /// Maps a (key, scale) pair to the Camelot Wheel code used by DJs
    /// (e.g. "C major" → "8B", "A minor" → "8A").
    /// Returns empty string for unmapped combinations.
    /// </summary>
    public static string ToCamelotKey(string key, string scale)
    {
        if (!CamelotTable.TryGetValue((Normalise(key), Normalise(scale)), out var code))
            return string.Empty;
        return code;
    }

    /// <summary>
    /// Maps a (key, scale) pair to Open Key notation (e.g. "C major" → "1d").
    /// </summary>
    public static string ToOpenKey(string key, string scale)
    {
        if (!OpenKeyTable.TryGetValue((Normalise(key), Normalise(scale)), out var code))
            return string.Empty;
        return code;
    }

    // ──────────────────────────────────── lookup tables ───────────────────

    private static string Normalise(string s) => s.Trim().ToLowerInvariant();

    // Camelot Wheel: major keys = B suffix, minor keys = A suffix
    private static readonly Dictionary<(string, string), string> CamelotTable = new()
    {
        { ("c",    "major"), "8B"  }, { ("a",    "minor"), "8A"  },
        { ("g",    "major"), "9B"  }, { ("e",    "minor"), "9A"  },
        { ("d",    "major"), "10B" }, { ("b",    "minor"), "10A" },
        { ("a",    "major"), "11B" }, { ("f#",   "minor"), "11A" },
        { ("e",    "major"), "12B" }, { ("c#",   "minor"), "12A" },
        { ("b",    "major"), "1B"  }, { ("g#",   "minor"), "1A"  },
        { ("f#",   "major"), "2B"  }, { ("d#",   "minor"), "2A"  },
        { ("db",   "major"), "3B"  }, { ("bb",   "minor"), "3A"  },
        { ("ab",   "major"), "4B"  }, { ("f",    "minor"), "4A"  },
        { ("eb",   "major"), "5B"  }, { ("c",    "minor"), "5A"  },
        { ("bb",   "major"), "6B"  }, { ("g",    "minor"), "6A"  },
        { ("f",    "major"), "7B"  }, { ("d",    "minor"), "7A"  },
        // Essentia enharmonic spelling variants
        { ("gb",   "major"), "2B"  }, { ("eb",   "minor"), "2A"  },
        { ("c#",   "major"), "3B"  }, { ("a#",   "minor"), "3A"  },
        { ("g#",   "major"), "4B"  }, { ("e#",   "minor"), "4A"  },
        { ("d#",   "major"), "5B"  }, { ("b#",   "minor"), "5A"  },
        { ("a#",   "major"), "6B"  }, { ("fx",   "minor"), "6A"  },
    };

    private static readonly Dictionary<(string, string), string> OpenKeyTable = new()
    {
        { ("c",    "major"), "1d"  }, { ("a",    "minor"), "1m"  },
        { ("g",    "major"), "2d"  }, { ("e",    "minor"), "2m"  },
        { ("d",    "major"), "3d"  }, { ("b",    "minor"), "3m"  },
        { ("a",    "major"), "4d"  }, { ("f#",   "minor"), "4m"  },
        { ("e",    "major"), "5d"  }, { ("c#",   "minor"), "5m"  },
        { ("b",    "major"), "6d"  }, { ("g#",   "minor"), "6m"  },
        { ("f#",   "major"), "7d"  }, { ("d#",   "minor"), "7m"  },
        { ("db",   "major"), "8d"  }, { ("bb",   "minor"), "8m"  },
        { ("ab",   "major"), "9d"  }, { ("f",    "minor"), "9m"  },
        { ("eb",   "major"), "10d" }, { ("c",    "minor"), "10m" },
        { ("bb",   "major"), "11d" }, { ("g",    "minor"), "11m" },
        { ("f",    "major"), "12d" }, { ("d",    "minor"), "12m" },
    };
}
