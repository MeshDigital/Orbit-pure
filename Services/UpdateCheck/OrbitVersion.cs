using System;
using System.Collections.Generic;
using System.Globalization;

namespace SLSKDONET.Services.UpdateCheck;

/// <summary>
/// Minimal SemVer 2.0 version used by the update checker. ORBIT tags are not always clean
/// versions ("v0.2.0", "v0.10.0-alpha", "v0.8.2-beta", sometimes "0.2.0+3b51e77"), so the
/// numeric core and the pre-release identifiers are parsed separately. Build metadata is
/// deliberately ignored for ordering — it identifies a commit, not a newer release.
/// </summary>
internal sealed class OrbitVersion : IComparable<OrbitVersion>, IEquatable<OrbitVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public IReadOnlyList<string> PreRelease { get; }
    public bool IsPreRelease => PreRelease.Count > 0;

    private OrbitVersion(int major, int minor, int patch, IReadOnlyList<string> preRelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
    }

    public static OrbitVersion? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var text = raw.Trim();
        if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V'))
            text = text[1..];

        // Ignore "+<build metadata>".
        var plus = text.IndexOf('+');
        if (plus >= 0)
            text = text[..plus];

        string? preRelease = null;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            preRelease = text[(dash + 1)..];
            text = text[..dash];
        }

        var core = text.Split('.');
        if (core.Length < 2)
            return null;

        if (!TryParsePart(core[0], out var major) ||
            !TryParsePart(core[1], out var minor))
            return null;

        var patch = 0;
        if (core.Length >= 3 && !TryParsePart(core[2], out patch))
            return null;

        if (core.Length > 4)
            return null;

        var identifiers = string.IsNullOrEmpty(preRelease)
            ? Array.Empty<string>()
            : preRelease.Split('.');

        return new OrbitVersion(major, minor, patch, identifiers);
    }

    private static bool TryParsePart(string value, out int result) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);

    public int CompareTo(OrbitVersion? other)
    {
        if (other is null) return 1;

        var core = Major.CompareTo(other.Major);
        if (core != 0) return core;

        core = Minor.CompareTo(other.Minor);
        if (core != 0) return core;

        core = Patch.CompareTo(other.Patch);
        if (core != 0) return core;

        // A release version sorts above any pre-release of the same core.
        if (!IsPreRelease && !other.IsPreRelease) return 0;
        if (!IsPreRelease) return 1;
        if (!other.IsPreRelease) return -1;

        var count = Math.Min(PreRelease.Count, other.PreRelease.Count);
        for (var i = 0; i < count; i++)
        {
            var result = CompareIdentifier(PreRelease[i], other.PreRelease[i]);
            if (result != 0) return result;
        }

        return PreRelease.Count.CompareTo(other.PreRelease.Count);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = long.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
        var rightNumeric = long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);

        if (leftNumeric && rightNumeric)
            return leftNumber.CompareTo(rightNumber);

        // Numeric identifiers sort below alphanumeric ones.
        if (leftNumeric) return -1;
        if (rightNumeric) return 1;

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    public bool Equals(OrbitVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is OrbitVersion other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Major);
        hash.Add(Minor);
        hash.Add(Patch);
        foreach (var identifier in PreRelease)
            hash.Add(identifier, StringComparer.OrdinalIgnoreCase);
        return hash.ToHashCode();
    }

    public override string ToString()
    {
        var core = $"{Major}.{Minor}.{Patch}";
        return IsPreRelease ? $"{core}-{string.Join('.', PreRelease)}" : core;
    }
}
