using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace SLSKDONET.Tests.Architecture;

/// <summary>
/// Ratchet guards against "placebo" UI: buttons that do nothing, Command bindings that point
/// at members the view model doesn't have, and exceptions swallowed by empty catch blocks.
/// Each ratchet constant is the count when the guard was introduced; it may only go down.
/// </summary>
public class UiWiringGuardTests
{
    /// <summary>
    /// Buttons / RepeatButtons / MenuItems / ToggleButtons in Views/**/*.axaml with no Command,
    /// Click, Flyout, IsChecked binding or code-behind hookup. RATCHET: may only go down.
    /// </summary>
    private const int MaxUnwiredControls = 0;

    /// <summary>
    /// Command="{Binding Foo}" bindings whose resolvable x:DataType has no member producing Foo
    /// (property or [RelayCommand]-generated FooCommand). These are dead buttons.
    /// RATCHET: may only go down.
    /// </summary>
    private const int MaxMissingBoundCommands = 0;

    /// <summary>
    /// Empty (or comment-only) catch blocks in ViewModels/, Services/ and Engine/.
    /// A catch whose body is a comment starting with "// intentional:" is exempt, so document
    /// genuine best-effort cases that way. RATCHET: may only go down.
    /// </summary>
    private const int MaxSilentCatches = 98;

    private static readonly HashSet<string> ClickableElements = new(StringComparer.Ordinal)
        { "Button", "RepeatButton", "MenuItem", "ToggleButton" };

    private static readonly HashSet<string> TemplateScopes = new(StringComparer.Ordinal)
        { "Style", "ControlTheme", "ControlTemplate", "Setter" };

    private static readonly HashSet<string> WiringAttributes = new(StringComparer.Ordinal)
        { "Command", "Click", "Flyout", "PointerPressed", "PointerReleased", "Tapped", "DoubleTapped", "ItemsSource" };

    private const string XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    // ───────────────────────────── Placebo buttons ─────────────────────────────

    [Fact]
    public void UnwiredClickableControls_OnlyGoDown()
    {
        var unwired = FindUnwiredControls();
        var report = string.Join("\n  ", unwired.Take(40));
        Assert.True(unwired.Count <= MaxUnwiredControls,
            $"Unwired clickable controls rose to {unwired.Count} (ratchet max {MaxUnwiredControls}). " +
            $"Give each a Command/Click/Flyout, or remove it. First 40:\n  {report}");
    }

    internal static List<string> FindUnwiredControls()
    {
        var results = new List<string>();
        foreach (var file in RepoGuard.ViewAxamlFiles().OrderBy(f => f, StringComparer.Ordinal))
        {
            var doc = LoadXaml(file);
            if (doc == null) continue;
            var axamlText = File.ReadAllText(file);
            var codeBehindPath = file + ".cs";
            var codeBehind = File.Exists(codeBehindPath) ? File.ReadAllText(codeBehindPath) : string.Empty;

            foreach (var el in doc.Descendants())
            {
                if (!ClickableElements.Contains(el.Name.LocalName)) continue;
                if (el.Ancestors().Any(a => TemplateScopes.Contains(a.Name.LocalName))) continue;
                if (IsWired(el, axamlText, codeBehind)) continue;

                var line = ((IXmlLineInfo)el).LineNumber;
                var label = Attr(el, "Content") ?? Attr(el, "Header") ?? Attr(el, "ToolTip.Tip")
                            ?? Attr(el, "Name") ?? (string?)el.Attribute(XName.Get("Name", XamlNs)) ?? "";
                results.Add($"{RepoGuard.Rel(file)}:{line}  <{el.Name.LocalName}> {Trim(label)}");
            }
        }
        return results;
    }

    private static bool IsWired(XElement el, string axamlText, string codeBehind)
    {
        foreach (var a in el.Attributes())
        {
            var name = a.Name.LocalName;
            if (WiringAttributes.Contains(name)) return true;
            if (name == "IsChecked" && a.Value.Contains("Binding", StringComparison.Ordinal)) return true;
            if (name == "IsHitTestVisible" && a.Value == "False") return true; // purely decorative
        }

        foreach (var child in el.Elements())
        {
            var n = child.Name.LocalName;
            if (n.EndsWith(".Flyout", StringComparison.Ordinal) ||
                n.EndsWith(".ContextFlyout", StringComparison.Ordinal) ||
                n.EndsWith(".ContextMenu", StringComparison.Ordinal) ||
                n == "MenuItem" ||
                n.EndsWith("Interaction.Behaviors", StringComparison.Ordinal))
                return true;
        }

        // Routed Click handled on a container: <StackPanel Button.Click="...">
        if (el.Ancestors().Any(anc => anc.Attributes().Any(a => a.Name.LocalName.EndsWith(".Click", StringComparison.Ordinal))))
            return true;

        // x:Name only counts when something actually uses it for wiring.
        var xName = (string?)el.Attribute(XName.Get("Name", XamlNs)) ?? Attr(el, "Name");
        if (!string.IsNullOrEmpty(xName))
        {
            var n = Regex.Escape(xName);
            if (Regex.IsMatch(codeBehind, $@"\b{n}\s*\??\.\s*(Click|Command|AddHandler|IsCheckedChanged|Checked|Unchecked|PointerPressed|PointerReleased|Tapped)\b"))
                return true;
            foreach (Match m in Regex.Matches(codeBehind, $@"(?:(\w+)\s*=\s*(?:this\.)?FindControl<\w+>\(\s*""{n}""\s*\))|(?:FindControl<\w+>\(\s*""{n}""\s*\)\s*is\s+(?:\w+\s+|\{{\s*\}}\s*)(\w+))"))
            {
                var v = Regex.Escape(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
                if (Regex.IsMatch(codeBehind, $@"\b{v}\s*\??\.\s*(Click|Command|AddHandler|IsCheckedChanged|Checked|Unchecked)\b"))
                    return true;
            }
            // A toggle whose IsChecked drives other elements: IsVisible="{Binding #MyToggle.IsChecked}"
            if (axamlText.Contains($"#{xName}.IsChecked", StringComparison.Ordinal) ||
                Regex.IsMatch(axamlText, $@"ElementName={n}\b[^}}]*IsChecked|IsChecked[^}}]*ElementName={n}\b"))
                return true;
        }

        return false;
    }

    // ───────────────────────────── Bound commands ─────────────────────────────

    private static readonly Regex CommandBindingRegex = new(
        @"^\{\s*(?:Compiled|Reflection)?Binding\s+(?:Path\s*=\s*)?([A-Za-z_]\w*)\s*(?:,|\})", RegexOptions.Compiled);

    [Fact]
    public void BoundCommands_ExistOnTheirDataType_OnlyGoDown()
    {
        var misses = FindMissingBoundCommands();
        var report = string.Join("\n  ", misses);
        Assert.True(misses.Count <= MaxMissingBoundCommands,
            $"Command bindings to members that don't exist on the x:DataType rose to {misses.Count} " +
            $"(ratchet max {MaxMissingBoundCommands}). Each is a dead button:\n  {report}");
    }

    internal static List<string> FindMissingBoundCommands()
    {
        var index = CSharpTypeIndex.Build();
        var misses = new List<string>();

        foreach (var file in RepoGuard.ViewAxamlFiles().OrderBy(f => f, StringComparer.Ordinal))
        {
            var doc = LoadXaml(file);
            if (doc == null) continue;

            foreach (var el in doc.Descendants())
            {
                var cmd = el.Attributes().FirstOrDefault(a => a.Name.LocalName == "Command");
                if (cmd == null) continue;
                var value = cmd.Value.Trim();
                if (value.Contains("ElementName", StringComparison.Ordinal) ||
                    value.Contains("RelativeSource", StringComparison.Ordinal) ||
                    value.Contains("Source=", StringComparison.Ordinal))
                    continue;
                var m = CommandBindingRegex.Match(value);
                if (!m.Success) continue;
                var member = m.Groups[1].Value;

                var dataType = ResolveDataType(el);
                if (dataType == null) continue;
                var (ns, typeName) = dataType.Value;
                var members = index.GetBindableMembers(ns, typeName);
                if (members == null || members.Contains(member)) continue;

                var line = ((IXmlLineInfo)cmd).LineNumber;
                misses.Add($"{RepoGuard.Rel(file)}:{line}  {typeName}.{member}");
            }
        }
        return misses;
    }

    /// <summary>Nearest x:DataType in scope, or null if the DataContext can't be determined statically.</summary>
    private static (string Ns, string Type)? ResolveDataType(XElement el)
    {
        for (var e = el; e != null; e = e.Parent)
        {
            var dt = e.Attribute(XName.Get("DataType", XamlNs));
            if (dt != null) return ParseTypeRef(e, dt.Value);
            if (e != el && TemplateScopes.Contains(e.Name.LocalName)) return null;
            if (e.Name.LocalName is "DataTemplate" or "TreeDataTemplate") return null; // template without x:DataType
            if (e.Attributes().Any(a => a.Name.LocalName == "DataContext")) return null;
        }
        return null;
    }

    private static (string Ns, string Type)? ParseTypeRef(XElement scope, string value)
    {
        value = value.Trim();
        if (value.StartsWith("{", StringComparison.Ordinal)) return null;
        var parts = value.Split(':');
        if (parts.Length != 2) return null;
        var nsUri = scope.GetNamespaceOfPrefix(parts[0])?.NamespaceName;
        if (nsUri == null) return null;
        string clrNs;
        if (nsUri.StartsWith("clr-namespace:", StringComparison.Ordinal)) clrNs = nsUri["clr-namespace:".Length..];
        else if (nsUri.StartsWith("using:", StringComparison.Ordinal)) clrNs = nsUri["using:".Length..];
        else return null;
        var semi = clrNs.IndexOf(';');
        if (semi >= 0) clrNs = clrNs[..semi];
        var typeName = parts[1];
        if (!Regex.IsMatch(typeName, @"^\w+$")) return null;
        return (clrNs.Trim(), typeName);
    }

    // ───────────────────────────── Silent catches ─────────────────────────────

    private static readonly Regex SilentCatchRegex = new(
        @"\bcatch\b[ \t\r\n]*(?:\([^)]*\))?[ \t\r\n]*(?:when[ \t]*\((?:[^()]|\([^()]*\))*\)[ \t\r\n]*)?\{(?<body>(?:[ \t\r\n]|//[^\n]*|/\*(?:(?!\*/).)*\*/)*)\}",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex IntentionalRegex = new(@"^\s*//\s*[Ii]ntentional:", RegexOptions.Compiled);

    [Fact]
    public void SilentCatchBlocks_OnlyGoDown()
    {
        var perFile = FindSilentCatches();
        var total = perFile.Sum(x => x.Count);
        var top = string.Join("\n  ", perFile.Take(20).Select(x => $"{x.Count,4}  {x.File}"));
        Assert.True(total <= MaxSilentCatches,
            $"Empty/comment-only catch blocks rose to {total} (ratchet max {MaxSilentCatches}). " +
            "Log the exception, or mark a genuine best-effort case with '// intentional: <why>'. " +
            $"Top files:\n  {top}");
    }

    internal static List<(string File, int Count)> FindSilentCatches()
    {
        var results = new List<(string File, int Count)>();
        foreach (var dir in new[] { "ViewModels", "Services", "Engine" })
        {
            var full = Path.Combine(RepoGuard.Root, dir);
            if (!Directory.Exists(full)) continue;
            foreach (var file in RepoGuard.EnumerateFiles(full, "*.cs", skipTestData: true))
            {
                var text = File.ReadAllText(file);
                var count = SilentCatchRegex.Matches(text)
                    .Count(m => !IntentionalRegex.IsMatch(m.Groups["body"].Value));
                if (count > 0) results.Add((RepoGuard.Rel(file), count));
            }
        }
        return results.OrderByDescending(x => x.Count).ThenBy(x => x.File, StringComparer.Ordinal).ToList();
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static XDocument? LoadXaml(string file)
    {
        try { return XDocument.Load(file, LoadOptions.SetLineInfo); }
        catch (XmlException) { return null; } // intentional: malformed XAML fails the real build instead
    }

    private static string? Attr(XElement el, string localName) =>
        el.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;

    private static string Trim(string s)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length > 70 ? s[..70] + "…" : s;
    }

    /// <summary>Very small textual index of C# types: simple name → (namespace, source text, base types).</summary>
    private sealed class CSharpTypeIndex
    {
        private readonly Dictionary<string, List<(string Ns, string Text, List<string> Bases)>> _types = new(StringComparer.Ordinal);

        private static readonly Regex NamespaceRegex = new(@"^\s*namespace\s+([\w.]+)", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex TypeDeclRegex = new(
            @"\b(?:class|record)\s+(\w+)(?:\s*<[^>{]*>)?(?:\s*\([^)]*\))?\s*(?::\s*([^{\n]+))?", RegexOptions.Compiled);

        public static CSharpTypeIndex Build()
        {
            var index = new CSharpTypeIndex();
            var root = RepoGuard.Root;
            foreach (var file in RepoGuard.EnumerateFiles(root, "*.cs", skipTestData: true))
            {
                var rel = RepoGuard.Rel(file);
                if (rel.StartsWith("Tests/", StringComparison.Ordinal) ||
                    rel.StartsWith("External/", StringComparison.Ordinal) ||
                    rel.StartsWith("artifacts/", StringComparison.Ordinal) ||
                    rel.StartsWith("Tools/", StringComparison.Ordinal) ||
                    RepoGuard.IsLegacy(file))
                    continue;
                var text = File.ReadAllText(file);
                var ns = NamespaceRegex.Match(text) is { Success: true } nm ? nm.Groups[1].Value : string.Empty;
                foreach (Match m in TypeDeclRegex.Matches(text))
                {
                    var bases = m.Groups[2].Success
                        ? m.Groups[2].Value.Split(',').Select(b => Regex.Replace(b, @"<.*$|\bwhere\b.*$", "").Trim())
                            .Select(b => b.Contains('.') ? b[(b.LastIndexOf('.') + 1)..] : b)
                            .Where(b => Regex.IsMatch(b, @"^\w+$")).ToList()
                        : new List<string>();
                    if (!index._types.TryGetValue(m.Groups[1].Value, out var list))
                        index._types[m.Groups[1].Value] = list = new();
                    list.Add((ns, text, bases));
                }
            }
            return index;
        }

        // Property: public ICommand Foo { get; } / public ReactiveCommand<...> Foo => ...
        // The accessor brace may also sit on the next line (public ICommand? Foo\n    { get => ... }).
        private static readonly Regex PropertyDeclRegex = new(
            @"\b(?:public|internal|protected)\b[^;\n{=()]*?\b(\w+)[ \t]*(?:\r?\n[ \t]*)?(?:\{|=>)", RegexOptions.Compiled);

        // CommunityToolkit [RelayCommand] on DoFoo / DoFooAsync / OnDoFoo → DoFooCommand
        private static readonly Regex RelayCommandRegex = new(
            @"\[RelayCommand[^\]]*\][^(;{]*?\b(\w+)[ \t]*\(", RegexOptions.Compiled);

        private readonly Dictionary<string, HashSet<string>> _memberCache = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _fileMemberCache = new(StringComparer.Ordinal);

        /// <summary>
        /// Bindable member names declared by the type (all partial files) and its known base
        /// classes, or null when the type isn't declared in this repo (framework type: skip).
        /// </summary>
        public HashSet<string>? GetBindableMembers(string ns, string typeName)
        {
            var cacheKey = ns + "." + typeName;
            if (_memberCache.TryGetValue(cacheKey, out var cached)) return cached;
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (!Collect(ns, typeName, set, new HashSet<string>(StringComparer.Ordinal))) return null;
            _memberCache[cacheKey] = set;
            return set;
        }

        private bool Collect(string? ns, string typeName, HashSet<string> into, HashSet<string> visited)
        {
            if (!visited.Add(typeName)) return true;
            if (!_types.TryGetValue(typeName, out var decls)) return false;
            var matching = ns == null ? decls : decls.Where(d => d.Ns == ns).ToList();
            if (matching.Count == 0) matching = decls;
            foreach (var d in matching.DistinctBy(d => d.Text))
            {
                into.UnionWith(MembersOf(d.Text));
                foreach (var b in d.Bases) Collect(null, b, into, visited);
            }
            return true;
        }

        private HashSet<string> MembersOf(string text)
        {
            if (_fileMemberCache.TryGetValue(text, out var cached)) return cached;
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in PropertyDeclRegex.Matches(text)) set.Add(m.Groups[1].Value);
            foreach (Match m in RelayCommandRegex.Matches(text))
            {
                var method = m.Groups[1].Value;
                if (method.EndsWith("Async", StringComparison.Ordinal)) method = method[..^"Async".Length];
                set.Add(method + "Command");
                if (method.Length > 2 && method.StartsWith("On", StringComparison.Ordinal) && char.IsUpper(method[2]))
                    set.Add(method[2..] + "Command");
            }
            _fileMemberCache[text] = set;
            return set;
        }
    }
}
