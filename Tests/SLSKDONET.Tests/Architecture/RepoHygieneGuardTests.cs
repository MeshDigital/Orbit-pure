using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace SLSKDONET.Tests.Architecture;

/// <summary>
/// Repo hygiene guards: no hack scripts / backup files in source dirs, no WPF .xaml,
/// a ratchet on raw hex colour literals in views, and no references to undefined
/// XAML resource keys.
/// </summary>
public class RepoHygieneGuardTests
{
    private static readonly string[] ProtectedDirs =
        { "Services", "ViewModels", "Views", "Models", "Data", "Database", "Engine", "Tests" };

    private static readonly string[] ForbiddenExtensions =
        { ".bak", ".orig", ".py", ".ps1", ".bat", ".tmp", ".code-workspace" };

    /// <summary>
    /// Current number of raw hex colour attribute values (e.g. Foreground="#FF00AA") in
    /// Views/**/*.axaml. RATCHET: this number may only go down. When you replace literals
    /// with theme resources, lower it to the new count. Never raise it — use a brush/colour
    /// resource from Themes/ instead of adding a new literal.
    /// </summary>
    private const int MaxRawHexColors = 3106;

    /// <summary>
    /// Resource keys that are legitimately referenced but not defined in this repo because
    /// they come from Avalonia's Fluent theme (or Avalonia's built-in control themes).
    /// Only genuine framework keys belong here; a typo or missing key must be fixed in XAML.
    /// </summary>
    private static readonly HashSet<string> FrameworkResourceKeys = new(StringComparer.Ordinal)
    {
        // Fluent theme system colour (FluentTheme's Default/Dark theme dictionaries).
        "SystemChromeLowColor",
    };

    [Fact]
    public void ProtectedSourceDirs_ContainNoBackupOrScriptFiles()
    {
        var root = RepoGuard.Root;
        var offenders = new List<string>();
        foreach (var dir in ProtectedDirs)
        {
            var full = Path.Combine(root, dir);
            if (!Directory.Exists(full)) continue;
            foreach (var file in RepoGuard.EnumerateFiles(full, "*", skipTestData: true))
            {
                var name = Path.GetFileName(file);
                if (ForbiddenExtensions.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                    offenders.Add(RepoGuard.Rel(file));
            }
        }

        Assert.True(offenders.Count == 0,
            "Backup files / one-off scripts do not belong in source directories " +
            "(put tooling under Tools/ or delete it):\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void NoWpfXamlFiles_OnlyAxaml()
    {
        // No WPF .xaml files are allowed anywhere (Avalonia uses .axaml). Nothing is
        // allowlisted: Themes/ModernDarkTheme.xaml and Services/InputDialog.xaml were dead
        // WPF leftovers and have been removed.
        var offenders = RepoGuard.EnumerateFiles(RepoGuard.Root, "*.xaml", skipTestData: false)
            .Select(RepoGuard.Rel)
            .ToList();

        Assert.True(offenders.Count == 0, "WPF .xaml files found (use .axaml):\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void CueForgePageTypo_DoesNotComeBack()
    {
        Assert.False(File.Exists(Path.Combine(RepoGuard.Root, "Views", "Avalonia", "CueForgePagee.axaml")),
            "Views/Avalonia/CueForgePagee.axaml was renamed to CueForgePage.axaml; do not reintroduce the typo.");
        Assert.True(File.Exists(Path.Combine(RepoGuard.Root, "Views", "Avalonia", "CueForgePage.axaml")));
    }

    private static readonly Regex HexAttributeRegex = new("=\"#[0-9A-Fa-f]{3,8}\"", RegexOptions.Compiled);

    [Fact]
    public void RawHexColorLiterals_InViews_OnlyGoDown()
    {
        var perFile = RepoGuard.ViewAxamlFiles()
            .Select(f => (File: RepoGuard.Rel(f), Count: HexAttributeRegex.Matches(File.ReadAllText(f)).Count))
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count)
            .ToList();
        var total = perFile.Sum(x => x.Count);

        var top = string.Join("\n  ", perFile.Take(15).Select(x => $"{x.Count,5}  {x.File}"));
        Assert.True(total <= MaxRawHexColors,
            $"Raw hex colour literals in Views/**/*.axaml rose to {total} (ratchet max {MaxRawHexColors}). " +
            $"Use a theme brush/colour resource instead. Top files:\n  {top}");
    }

    private static readonly Regex ResourceRefRegex =
        new(@"\{(?:StaticResource|DynamicResource)\s+(?:ResourceKey=)?([A-Za-z_][\w.]*)\s*\}", RegexOptions.Compiled);

    private static readonly Regex KeyDefRegex = new("x:Key=\"([^\"]+)\"", RegexOptions.Compiled);

    [Fact]
    public void ReferencedResourceKeys_AreDefined()
    {
        var root = RepoGuard.Root;
        var allAxaml = RepoGuard.EnumerateFiles(root, "*.axaml", skipTestData: false)
            .Where(f => !RepoGuard.IsLegacy(f))
            .ToList();

        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in allAxaml)
            foreach (Match m in KeyDefRegex.Matches(File.ReadAllText(f)))
                defined.Add(m.Groups[1].Value);

        var themesDir = Path.Combine(root, "Themes");
        var scanned = RepoGuard.ViewAxamlFiles()
            .Concat(Directory.Exists(themesDir) ? RepoGuard.EnumerateFiles(themesDir, "*.axaml", false) : Enumerable.Empty<string>());

        var missing = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var f in scanned)
        {
            var text = File.ReadAllText(f);
            foreach (Match m in ResourceRefRegex.Matches(text))
            {
                var key = m.Groups[1].Value;
                if (defined.Contains(key) || IsFrameworkKey(key)) continue;
                var line = text.Take(m.Index).Count(c => c == '\n') + 1;
                if (!missing.TryGetValue(key, out var list)) missing[key] = list = new List<string>();
                list.Add($"{RepoGuard.Rel(f)}:{line}");
            }
        }

        var sb = new StringBuilder();
        foreach (var (key, uses) in missing)
            sb.Append("\n  ").Append(key).Append("  <- ").Append(string.Join(", ", uses.Take(5)));
        Assert.True(missing.Count == 0,
            "XAML references resource keys that are defined nowhere (fix the key, define it in Themes/, " +
            "or — only for genuine Fluent built-ins — allowlist it):" + sb);
    }

    private static bool IsFrameworkKey(string key) => FrameworkResourceKeys.Contains(key);
}

/// <summary>Shared repo-walking helpers for the architecture guard tests.</summary>
internal static class RepoGuard
{
    private static readonly Lazy<string> _root = new(FindSourceRoot);
    public static string Root => _root.Value;

    public static string Rel(string path) =>
        Path.GetRelativePath(Root, path).Replace('\\', '/');

    public static bool IsLegacy(string path) =>
        Rel(path).StartsWith("Views/Avalonia/Legacy/", StringComparison.OrdinalIgnoreCase);

    /// <summary>All compiled Views/**/*.axaml (Views/Avalonia/Legacy is excluded from the build).</summary>
    public static IEnumerable<string> ViewAxamlFiles() =>
        EnumerateFiles(Path.Combine(Root, "Views"), "*.axaml", skipTestData: false).Where(f => !IsLegacy(f));

    /// <summary>Recursive file walk that skips bin/obj, dot-directories and (optionally) TestData.</summary>
    public static IEnumerable<string> EnumerateFiles(string dir, string pattern, bool skipTestData)
    {
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var f in Directory.EnumerateFiles(current, pattern))
                yield return f;
            foreach (var sub in Directory.EnumerateDirectories(current))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') ||
                    name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                    (skipTestData && name.Equals("TestData", StringComparison.OrdinalIgnoreCase)))
                    continue;
                stack.Push(sub);
            }
        }
    }

    private static string FindSourceRoot()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(dir))
        {
            if (File.Exists(Path.Combine(dir, "SLSKDONET.csproj")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Could not locate SLSKDONET.csproj above " + AppDomain.CurrentDomain.BaseDirectory);
    }
}
