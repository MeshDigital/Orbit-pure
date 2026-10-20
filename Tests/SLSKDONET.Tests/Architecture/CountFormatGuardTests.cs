using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SLSKDONET.Converters;
using SLSKDONET.Utils;
using Xunit;

namespace SLSKDONET.Tests.Architecture;

/// <summary>
/// The import sheet said "Analyse 3.267 new tracks" on a Dutch Windows while the rest of the UI
/// said "2,400": "N0" follows the OS culture. Counts go through CountFormat / CountConverter.
/// </summary>
public class CountFormatGuardTests
{
    private static readonly Regex InterpolatedN0 = new(@"\{[^{}""]+:N0\}", RegexOptions.Compiled);
    private static readonly Regex BindingN0 = new(@"StringFormat='[^']*\{0:N0\}", RegexOptions.Compiled);

    [Fact]
    public void Counts_AreInvariant_OnADutchMachine()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
            Assert.Equal("3,267", CountFormat.N(3267));
            Assert.Equal("3,267 tracks", CountFormat.Of(3267, "track"));
            Assert.Equal("1 track", CountFormat.Of(1, "track"));
            var converter = new CountConverter();
            Assert.Equal("3,267", converter.Convert(3267, typeof(string), null, CultureInfo.CurrentCulture));
            Assert.Equal("3,267 files found", converter.Convert(3267, typeof(string), "{0} files found", CultureInfo.CurrentCulture));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void NoCultureDependentCountFormats_InUi()
    {
        var root = RepoGuard.Root;
        var offenders = new[] { "ViewModels", "Services", "Views" }
            .Select(d => Path.Combine(root, d)).Where(Directory.Exists)
            .SelectMany(d => RepoGuard.EnumerateFiles(d, "*.cs", skipTestData: true)
                .Concat(RepoGuard.EnumerateFiles(d, "*.axaml", skipTestData: true)))
            .Where(f => !RepoGuard.IsLegacy(f))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, line, i)))
            .Where(x => InterpolatedN0.IsMatch(x.line) || BindingN0.IsMatch(x.line))
            .Select(x => $"{RepoGuard.Rel(x.f)}:{x.i + 1}: {x.line.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "Culture-dependent count formats (\"{x:N0}\" / StringFormat {0:N0}) found; use CountFormat.N(x) " +
            "or Converter={StaticResource CountConverter}:\n  " + string.Join("\n  ", offenders));
    }
}
