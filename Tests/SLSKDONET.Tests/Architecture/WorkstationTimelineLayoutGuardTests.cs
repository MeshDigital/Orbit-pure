using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SLSKDONET.Tests.Architecture;

public class WorkstationTimelineLayoutGuardTests
{
    [Fact]
    public void TimelineCanvas_AlwaysIncludesDeckRowsAndPersistentScaffold()
    {
        var xaml = ReadWorkstationPageXaml();

        Assert.Contains("<Grid Grid.Row=\"1\" Grid.Column=\"0\">", xaml);
        Assert.Contains("<ItemsControl ItemsSource=\"{Binding Decks}\">", xaml);
        Assert.Contains("IsHitTestVisible=\"False\"", xaml);
        Assert.Contains("Opacity=\"0.72\"", xaml);
    }

    [Fact]
    public void EmptyTimelineOverlay_RemainsGuidanceLayerAndDoesNotReplaceCanvas()
    {
        var xaml = ReadWorkstationPageXaml();

        Assert.Contains("<Border IsVisible=\"{Binding !HasLoadedDecks}\"", xaml);
        Assert.Contains("Text=\"EMPTY TIMELINE\"", xaml);
        Assert.Contains("Text=\"{Binding TimelineEmptyCanvasSummary}\"", xaml);
        Assert.DoesNotContain("<ScrollViewer VerticalScrollBarVisibility=\"Auto\" IsVisible=\"{Binding HasLoadedDecks}\"", xaml);
    }

    [Fact]
    public void TimelineCanvas_IsNotModeOrPanelGatedByVisibilityBindings()
    {
        var xaml = ReadWorkstationPageXaml();

        Assert.DoesNotContain("Grid.Row=\"2\" Grid.Column=\"0\" IsVisible=", xaml);
        Assert.DoesNotContain("IsVisible=\"{Binding HasLoadedDecks}\"", xaml);
    }

    [Fact]
    public void ModeLaneBannerStrip_StaysDeleted_AfterWorkspaceCollapse()
    {
        // The per-mode "lane" banner strip was removed in the 6→2 workspace collapse
        // (its content was either noise or duplicated in the Flow Drawer). Guard against
        // it creeping back in.
        var xaml = ReadWorkstationPageXaml();

        Assert.DoesNotContain("Classes=\"ws-lane\"", xaml);
        Assert.DoesNotContain("Waveform Lane", xaml);
        Assert.DoesNotContain("Automation Lane", xaml);
        Assert.DoesNotContain("Samples Lane", xaml);
    }

    [Fact]
    public void FlowMode_UsesPassiveTimelineOverlayLayer()
    {
        var xaml = ReadWorkstationPageXaml();

        Assert.Contains("<Canvas IsVisible=\"{Binding IsFlowOverlayVisible}\"", xaml);
        Assert.Contains("ItemsSource=\"{Binding FlowTransitions}\"", xaml);
        Assert.Contains("Text=\"{Binding FlowOverlayHint}\"", xaml);
        Assert.Contains("Command=\"{Binding #PageRoot.DataContext.SelectFlowTransitionCommand}\"", xaml);
        Assert.Contains("Text=\"{Binding FlowInspectorTransitionLabel}\"", xaml);
        Assert.Contains("Command=\"{Binding ClearFlowTransitionSelectionCommand}\"", xaml);
        Assert.Contains("PointerPressed=\"OnFlowTransitionHandlePointerPressed\"", xaml);
        Assert.Contains("PointerMoved=\"OnFlowTransitionHandlePointerMoved\"", xaml);
        Assert.Contains("PointerReleased=\"OnFlowTransitionHandlePointerReleased\"", xaml);
        Assert.Contains("PointerPressed=\"OnFlowPhraseMarkerPointerPressed\"", xaml);
        Assert.Contains("PointerMoved=\"OnFlowPhraseMarkerPointerMoved\"", xaml);
        Assert.Contains("PointerReleased=\"OnFlowPhraseMarkerPointerReleased\"", xaml);
        Assert.Contains("ItemsSource=\"{Binding PhraseRegions}\"", xaml);
        Assert.Contains("PointerPressed=\"OnPhraseRegionCreatePointerPressed\"", xaml);
        Assert.Contains("PointerPressed=\"OnPhraseRegionStartHandlePointerPressed\"", xaml);
        Assert.Contains("PointerMoved=\"OnPhraseRegionStartHandlePointerMoved\"", xaml);
        Assert.Contains("PointerReleased=\"OnPhraseRegionStartHandlePointerReleased\"", xaml);
        Assert.Contains("PointerPressed=\"OnPhraseRegionEndHandlePointerPressed\"", xaml);
        Assert.Contains("PointerMoved=\"OnPhraseRegionEndHandlePointerMoved\"", xaml);
        Assert.Contains("PointerReleased=\"OnPhraseRegionEndHandlePointerReleased\"", xaml);
        Assert.Contains("ToolTip.Tip=\"{Binding PhraseMarkerTooltip}\"", xaml);
        Assert.Contains("Opacity=\"{Binding PhraseMarkerOpacity}\"", xaml);
        Assert.Contains("Text=\"{Binding FlowInspectorCombinedScoreLabel}\"", xaml);
        Assert.Contains("Text=\"{Binding FlowInspectorPresetLabel}\"", xaml);
        Assert.Contains("Text=\"{Binding FlowInspectorWarningLabel}\"", xaml);
        Assert.Contains("Text=\"{Binding FlowInspectorScoreRow}\"", xaml);
        Assert.Contains("Text=\"{Binding FlowInspectorAlignmentRow}\"", xaml);
        Assert.Contains("Text=\"{Binding FlowInspectorPhraseRegionSpanLabel}\"", xaml);
        Assert.Contains("Text=\"{Binding FlowInspectorCurveDetailRow}\"", xaml);
        Assert.Contains("Command=\"{Binding ApplyFlowPresetCommand}\"", xaml);
        Assert.Contains("Command=\"{Binding ResetFlowPresetCommand}\"", xaml);
        Assert.Contains("Command=\"{Binding CycleFlowPresetCommand}\"", xaml);
        Assert.Contains("IsVisible=\"{Binding IsPresetApplied}\"", xaml);
        Assert.Contains("ItemsSource=\"{Binding FlowPlaylistTransitions}\"", xaml);
        Assert.Contains("IsVisible=\"{Binding IsFlowPlaylistOverlayVisible}\"", xaml);
        Assert.Contains("Background=\"{Binding CompatibilityColor}\"", xaml);
    }

    [Fact]
    public void Workstation_IsProfessionalPrepOnly_WithInlineReadinessAndNoModalBrowser()
    {
        var xaml = ReadWorkstationPageXaml();

        Assert.DoesNotContain("<views:FlowBuilderView", xaml);
        Assert.DoesNotContain("DataContext=\"{Binding FlowBuilder}\"", xaml);
        Assert.DoesNotContain("Build Set", xaml);
        Assert.DoesNotContain("Build set", xaml);
        Assert.Contains("Text=\"PREP PLAYLIST\"", xaml);
        Assert.Contains("Header=\"Open playlist in Flow Builder (in-app MIX)\"", xaml);
        Assert.Contains("Command=\"{Binding #PageRoot.DataContext.RepairTrackCommand}\"", xaml);
        Assert.Contains("ConverterParameter=Status", xaml);
        Assert.Contains("DoubleTapped=\"OnPrepTrackDoubleTapped\"", xaml);
        Assert.Contains("Data=\"{StaticResource icon_check}\"", xaml);
        Assert.Contains("Data=\"{StaticResource icon_alert}\"", xaml);
        Assert.Contains("x:Name=\"PrepWorkflowStrip\"", xaml);
        Assert.Contains("Text=\"1  ANALYZE\"", xaml);
        Assert.Contains("Text=\"2  PREPARE\"", xaml);
        Assert.Contains("Text=\"3  EXPORT\"", xaml);
        Assert.Contains("Text=\"{Binding AnalysisWorkflowStatus}\"", xaml);
        Assert.Contains("Text=\"{Binding PreparationWorkflowStatus}\"", xaml);
        Assert.Contains("Text=\"{Binding ExportWorkflowStatus}\"", xaml);
        Assert.DoesNotContain("x:Name=\"TrackListOverlay\"", xaml);
        Assert.DoesNotContain("Text=\"Track Pool\"", xaml);
        Assert.DoesNotContain("Load into Workstation", xaml);
    }

    [Fact]
    public void ExportReadiness_DistinguishesFileBlockersFromPrepWarnings()
    {
        var sourceRoot = FindSourceRoot();
        var source = File.ReadAllText(Path.Combine(sourceRoot, "ViewModels", "Workstation", "WorkstationViewModel.cs"));
        var xaml = ReadWorkstationPageXaml();

        Assert.Contains("PlaylistFileIssueCount", source);
        Assert.Contains("PlaylistPrepOnlyIssueCount", source);
        Assert.Contains("need file repair", source);
        Assert.Contains("prep warnings", source);
        Assert.Contains("Rekordbox prep complete", source);
        Assert.Contains("Missing or unreadable files block export", xaml);
        Assert.Contains("incomplete grids and cues are reported as prep warnings", xaml);
    }

    [Fact]
    public void StandaloneFlowBuilder_DeclaresInAppMixScopeAndKeepsAttentionVisible()
    {
        var sourceRoot = FindSourceRoot();
        var xaml = File.ReadAllText(Path.Combine(sourceRoot, "Views", "Avalonia", "FlowBuilderView.axaml"));

        Assert.Contains("Text=\"IN-APP MIX\"", xaml);
        Assert.Contains("IsVisible=\"{Binding NeedsAttention}\"", xaml);
        Assert.Contains("IsVisible=\"{Binding PairNeedsAttention}\"", xaml);
        Assert.Contains("ORBIT playlist playback with MIX enabled", xaml);
    }

    [Fact]
    public void ResponsivePrep_FillsLargeDesktopsWithoutDpiDrivenShrinkage()
    {
        var sourceRoot = FindSourceRoot();
        var code = File.ReadAllText(Path.Combine(sourceRoot, "Views", "Avalonia", "WorkstationPage.axaml.cs"));
        var deckCode = File.ReadAllText(Path.Combine(sourceRoot, "Views", "Avalonia", "Workstation", "WorkstationDeckRow.axaml.cs"));
        var mixerCode = File.ReadAllText(Path.Combine(sourceRoot, "Views", "Avalonia", "Workstation", "MixerCenter.axaml.cs"));

        Assert.Contains("FindControl<SplitView>(\"ShellSplitView\")", code);
        Assert.Contains("grid.VerticalAlignment = VerticalAlignment.Stretch", code);
        Assert.Contains("grid.MaxWidth = double.PositiveInfinity", code);
        Assert.Contains("grid.MaxHeight = double.PositiveInfinity", code);
        Assert.Contains("Classes.Set(\"ws-layout-spacious\", isSpacious)", code);
        Assert.Contains("availableWidth * 0.18", code);
        Assert.Contains("availableHeight * 0.28", code);
        Assert.Contains("availableHeight < 520", code);
        Assert.Contains("OnToggleInspectorClick", code);
        Assert.DoesNotContain("renderScaling >= 1.35", code);
        Assert.DoesNotContain("renderScaling >= 1.35", deckCode);
        Assert.DoesNotContain("RenderScaling", mixerCode);
        Assert.DoesNotContain("FindAncestorOfType<SplitView>", code);
    }

    [Fact]
    public void WorkstationViews_UseNoRawHexColours()
    {
        // The cockpit was moved onto Themes/DesignTokens.axaml (2026-10). Deck, stem and
        // hot-cue colours are data and live there as BrushStem* / BrushHotCue* tokens.
        var sourceRoot = FindSourceRoot();
        var files = new[]
        {
            Path.Combine(sourceRoot, "Views", "Avalonia", "WorkstationPage.axaml"),
            Path.Combine(sourceRoot, "Views", "Avalonia", "Workstation", "WorkstationDeckRow.axaml"),
            Path.Combine(sourceRoot, "Views", "Avalonia", "Workstation", "MixerCenter.axaml"),
            Path.Combine(sourceRoot, "Views", "Avalonia", "Workstation", "ExportDialog.axaml"),
        };

        var offenders = new List<string>();
        foreach (var file in files)
        {
            Assert.True(File.Exists(file), $"Expected workstation view at {file}");
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "=\"#[A-Fa-f0-9]{3,8}\""))
                offenders.Add($"{Path.GetFileName(file)}: {m.Value}");
        }

        Assert.True(offenders.Count == 0,
            "Raw hex colour literals in workstation views (use a Brush* token):\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void WorkstationViews_UseNoEmojiGlyphsAsIcons()
    {
        var sourceRoot = FindSourceRoot();
        var xaml = File.ReadAllText(Path.Combine(sourceRoot, "Views", "Avalonia", "WorkstationPage.axaml"))
                 + File.ReadAllText(Path.Combine(sourceRoot, "Views", "Avalonia", "Workstation", "WorkstationDeckRow.axaml"))
                 + File.ReadAllText(Path.Combine(sourceRoot, "Views", "Avalonia", "Workstation", "MixerCenter.axaml"));

        foreach (var glyph in new[] { "🔒", "⚡", "⏮", "▶", "⟳", "⇌", "◀", "◢", "✕" })
            Assert.DoesNotContain(glyph, xaml);
    }

    private static string ReadWorkstationPageXaml()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "Views", "Avalonia", "WorkstationPage.axaml");
        Assert.True(File.Exists(filePath), $"Expected workstation view at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string FindSourceRoot()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(dir))
        {
            if (File.Exists(Path.Combine(dir, "SLSKDONET.csproj")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        var candidate = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
        if (File.Exists(Path.Combine(candidate, "SLSKDONET.csproj")))
        {
            return candidate;
        }

        return string.Empty;
    }
}
