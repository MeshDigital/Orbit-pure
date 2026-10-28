using System;
using System.IO;
using SLSKDONET.Data;

namespace SLSKDONET.Configuration;

/// <summary>
/// Command-line flags that pick the user profile ORBIT runs on. Parsed in <c>Program.Main</c>
/// before anything resolves a path.
/// <list type="bullet">
/// <item><c>--profile &lt;dir&gt;</c>: run on that profile folder (config, library, credentials).</item>
/// <item><c>--setup-preview</c>: run on a brand-new temporary profile, so the first-run setup wizard
/// appears without touching the real config.ini or library.db.</item>
/// </list>
/// </summary>
public sealed record StartupArguments(string? ProfileDirectory, bool IsSetupPreview)
{
    public const string ProfileFlag = "--profile";
    public const string SetupPreviewFlag = "--setup-preview";

    /// <param name="tempRoot">Where <c>--setup-preview</c> creates its profile (defaults to %TEMP%).</param>
    public static StartupArguments Parse(string[]? args, string? tempRoot = null)
    {
        string? profile = null;
        var preview = false;
        if (args != null)
        {
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, SetupPreviewFlag, StringComparison.OrdinalIgnoreCase))
                    preview = true;
                else if (string.Equals(arg, ProfileFlag, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    profile = args[++i];
                else if (arg.StartsWith(ProfileFlag + "=", StringComparison.OrdinalIgnoreCase))
                    profile = arg[(ProfileFlag.Length + 1)..];
            }
        }

        if (preview && string.IsNullOrWhiteSpace(profile))
        {
            var root = tempRoot ?? Path.GetTempPath();
            profile = Path.Combine(root, "ORBIT-setup-preview", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        }

        return new StartupArguments(string.IsNullOrWhiteSpace(profile) ? null : Path.GetFullPath(profile.Trim('"')), preview);
    }

    /// <summary>Points <see cref="OrbitPaths"/> at the chosen profile (process-wide).</summary>
    public void Apply()
    {
        if (ProfileDirectory == null) return;
        Directory.CreateDirectory(ProfileDirectory);
        Environment.SetEnvironmentVariable(OrbitPaths.ProfileDirectoryEnvironmentVariable, ProfileDirectory);
    }
}
