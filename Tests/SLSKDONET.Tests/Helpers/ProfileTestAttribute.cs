using System;
using System.Linq;
using Xunit;

namespace SLSKDONET.Tests.Helpers;

/// <summary>
/// Skips profile-specific tests unless the requested feature profile is enabled locally.
/// Set ORBIT_FEATURE_PROFILE to a semicolon-separated list of active profiles.
/// </summary>
public sealed class ProfileTestAttribute : FactAttribute
{
    public ProfileTestAttribute(string profileName)
    {
        var active = Environment.GetEnvironmentVariable("ORBIT_FEATURE_PROFILE") ?? string.Empty;
        var enabled = active.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(profileName, StringComparer.OrdinalIgnoreCase);

        if (!enabled)
            Skip = $"Profile '{profileName}' is not enabled. Set ORBIT_FEATURE_PROFILE to run this test.";
    }
}