using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SLSKDONET.Tests.Architecture;

/// <summary>
/// A Belgian Windows showed "174,0 BPM", "1,0x", "22,1 s", "0,0 %" and "91,0%" next to English
/// copy: "F1", "0.0", "P0" and friends follow the OS culture. User-facing numbers go through
/// Utils/NumFormat (C#) or the NumberConverter (XAML). This guard blocks new culture-dependent
/// numeric formats in UI code. Counts ("N0") have their own guard (CountFormatGuardTests).
///
/// A line is fine when it already pins the culture (NumFormat., InvariantCulture,
/// FormattableString.Invariant, string.Create(...)) or is a log call. Anything else that is
/// genuinely not shown to a person (file names, serialization) belongs in <see cref="Allowlist"/>.
/// </summary>
public class NumberFormatGuardTests
{
    /// <summary>"relative/path.cs" (whole file) or "relative/path.cs:needle" (lines containing needle).</summary>
    private static readonly string[] Allowlist =
    {
        // Empty on purpose: every hit so far was user-facing and got fixed.
    };

    private static readonly string[] ScannedDirs = { "Views", "ViewModels", "Converters", "Models", "Utils" };

    private static readonly Regex XamlStringFormat = new(@"StringFormat=[^""]*?\{0:([^{}]+)\}", RegexOptions.Compiled);
    private static readonly Regex InterpolationHole = new(@"\{[^{}""]*?:([^{}""\s]+)\}", RegexOptions.Compiled);
    private static readonly Regex ToStringFormat = new(@"\.ToString\(""([^""]+)""\)", RegexOptions.Compiled);
    private static readonly Regex StringFormatCall = new(@"string\.Format\(""[^""]*\{\d+:([^{}""]+)\}", RegexOptions.Compiled);
    private static readonly Regex SafeLine = new(
        @"InvariantCulture|NumFormat\.|FormattableString\.Invariant|string\.Create\(|CountFormat\.|" +
        @"Log(Information|Warning|Error|Debug|Trace|Critical)\(|_logger|Logger\.|Debug\.WriteLine|Console\.Write|Trace\.Write",
        RegexOptions.Compiled);

    /// <summary>True for a .NET numeric format whose output depends on the culture's decimal/percent symbols.</summary>
    internal static bool IsCultureSensitive(string spec)
    {
        var s = spec.Trim();
        if (Regex.IsMatch(s, @"^[FfNnPpEe]\d*$"))
            return s.ToUpperInvariant() is not ("F0" or "N0" or "N" or "P" or "E"); // bare N/P: Guid formats; N0: CountFormat
        return Regex.IsMatch(s, @"^[0#.,%+\-;‰ ]+$") && s.IndexOfAny(new[] { '.', ',', '%', ';' }) >= 0;
    }

    [Theory]
    [InlineData("F1", true)]
    [InlineData("0.0", true)]
    [InlineData("P0", true)]
    [InlineData("+0.0;-0.0;0.0", true)]
    [InlineData("0.##", true)]
    [InlineData("F0", false)]
    [InlineData("0", false)]
    [InlineData("N0", false)]
    [InlineData("N", false)]
    [InlineData("HH:mm:ss.ff", false)]
    [InlineData("D2", false)]
    public void Classifier(string spec, bool expected) => Assert.Equal(expected, IsCultureSensitive(spec));

    [Fact]
    public void NoCultureDependentNumberFormats_InUi()
    {
        var root = RepoGuard.Root;
        var offenders = new List<string>();
        foreach (var file in ScannedDirs.Select(d => Path.Combine(root, d)).Where(Directory.Exists)
                     .SelectMany(d => RepoGuard.EnumerateFiles(d, "*.cs", skipTestData: true)
                         .Concat(RepoGuard.EnumerateFiles(d, "*.axaml", skipTestData: true)))
                     .Where(f => !RepoGuard.IsLegacy(f)))
        {
            var rel = RepoGuard.Rel(file);
            if (Allowlist.Contains(rel)) continue;
            var needles = Allowlist.Where(a => a.StartsWith(rel + ":", StringComparison.Ordinal))
                .Select(a => a[(rel.Length + 1)..]).ToArray();
            var lines = File.ReadAllLines(file);
            bool axaml = file.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (needles.Any(n => line.Contains(n, StringComparison.Ordinal))) continue;
                if (axaml)
                {
                    if (XamlStringFormat.Matches(line).Any(m => IsCultureSensitive(m.Groups[1].Value)))
                        offenders.Add($"{rel}:{i + 1}: {line.Trim()}");
                    continue;
                }

                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("///")) continue;
                if (SafeLine.IsMatch(line) || (i > 0 && SafeLine.IsMatch(lines[i - 1]))) continue;

                bool bad =
                    (line.Contains("$\"") && InterpolationHole.Matches(line).Any(m => IsCultureSensitive(m.Groups[1].Value))) ||
                    ToStringFormat.Matches(line).Any(m => IsCultureSensitive(m.Groups[1].Value)) ||
                    StringFormatCall.Matches(line).Any(m => IsCultureSensitive(m.Groups[1].Value));
                if (bad) offenders.Add($"{rel}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Culture-dependent number formats found (they render \"174,0\" on a Dutch/Belgian Windows). Use " +
            "NumFormat.Bpm/Percent/Size/Seconds/Ratio/Signed/... or NumFormat.Ui($\"...\") in C#, and " +
            "Converter={StaticResource NumberConverter}, ConverterParameter=... in XAML. Non-UI uses " +
            "(file names, serialization) go in NumberFormatGuardTests.Allowlist:\n  " + string.Join("\n  ", offenders));
    }
}
