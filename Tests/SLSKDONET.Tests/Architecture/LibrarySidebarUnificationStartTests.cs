using System;
using System.IO;
using Xunit;

namespace SLSKDONET.Tests.Architecture;

public class LibrarySidebarUnificationStartTests
{
    [Fact]
    public void LibraryPage_UsesExtractedPlaylistIntelligencePanelControl()
    {
        var xaml = ReadLibraryPageXaml();

        Assert.Contains("<Grid ColumnDefinitions=\"Auto,*\">", xaml);
        Assert.DoesNotContain("<GridSplitter Grid.Column=\"2\"", xaml);
        Assert.DoesNotContain("<Border Grid.Column=\"3\"", xaml);
        Assert.DoesNotContain("Text=\"Smart Insert Intelligence\"", xaml);
        Assert.DoesNotContain("<Grid IsVisible=\"{Binding IsLibrarySidebarIntelligenceMode}\" RowDefinitions=\"Auto,Auto,Auto,*\">", xaml);
    }

    [Fact]
    public void PlaylistIntelligencePanel_ExistsWithExpectedHeader()
    {
        var xaml = ReadPlaylistIntelligencePanelXaml();

        Assert.Contains("Text=\"Playlist Intelligence\"", xaml);
        Assert.Contains("Text=\"Smart Insert Settings\"", xaml);
        Assert.Contains("Command=\"{Binding SetSmartInsertStrictPresetCommand}\"", xaml);
        Assert.Contains("Text=\"{Binding LibraryIntelligencePlaylistTitle}\"", xaml);
        Assert.Contains("Command=\"{Binding SetLibraryIntelligenceTabCommand}\"", xaml);
    }

    [Fact]
    public void MainWindow_UsesExplicitLibraryInspectorContextTemplates()
    {
        var xaml = ReadMainWindowXaml();

        Assert.Contains("DataTemplate DataType=\"vmCore:LibraryDoubleInspectorViewModel\"", xaml);
        Assert.Contains("DataTemplate DataType=\"vmCore:PlaylistIntelligenceViewModel\"", xaml);
        Assert.Contains("DataTemplate DataType=\"vmCore:PlaylistTrackViewModel\"", xaml);
        Assert.Contains("<controls:DoubleInspectorPanel/>", xaml);
        Assert.Contains("<controls:PlaylistIntelligencePanel/>", xaml);
        Assert.Contains("Inspector content is unavailable for this selection.", xaml);
        Assert.DoesNotContain("TrackInspectorPanel", xaml);
        Assert.DoesNotContain("DataTemplate DataType=\"vmCore:LibraryViewModel\"", xaml);
    }

    [Fact]
    public void LibraryViewModel_DoesNotRetainLegacySidebarModeCompatibilityState()
    {
        var source = ReadLibraryViewModelSource();

        Assert.DoesNotContain("LibrarySidebarMode", source);
        Assert.DoesNotContain("IsLibrarySidebarPlayerMode", source);
        Assert.DoesNotContain("IsLibrarySidebarTrackInspectorMode", source);
        Assert.DoesNotContain("IsLibrarySidebarDoubleInspectorMode", source);
        Assert.DoesNotContain("IsLibrarySidebarIntelligenceMode", source);
        Assert.DoesNotContain("EvaluateSidebarMode(", source);
    }

    [Fact]
    public void LibraryViewModel_DoesNotRetainLegacyDoubleInspectorMirrorProperties()
    {
        var source = ReadLibraryViewModelSource();

        Assert.DoesNotContain("DoubleInspectorTrackA", source);
        Assert.DoesNotContain("DoubleInspectorTrackB", source);
        Assert.DoesNotContain("IsDoubleInspectorPairAnalyzable", source);
        Assert.DoesNotContain("IsDoubleInspectorPairScoreLoading", source);
        Assert.DoesNotContain("HasDoubleInspectorPairContext", source);
        Assert.DoesNotContain("DoubleInspectorHeaderTitle", source);
        Assert.DoesNotContain("DoubleInspectorKeyCompatibilitySummary", source);
        Assert.DoesNotContain("DoubleInspectorBpmDifferenceSummary", source);
        Assert.DoesNotContain("DoubleInspectorEnergyAlignmentSummary", source);
        Assert.DoesNotContain("DoubleInspectorTransitionScore", source);
        Assert.DoesNotContain("DoubleInspectorHarmonicScore", source);
        Assert.DoesNotContain("DoubleInspectorBeatScore", source);
        Assert.DoesNotContain("DoubleInspectorDropScore", source);
        Assert.DoesNotContain("DoubleInspectorReasonTags", source);
        Assert.DoesNotContain("DoubleInspectorTransitionStyleLabel", source);
        Assert.DoesNotContain("DoubleInspectorTransitionStyleReason", source);
    }

    [Fact]
    public void LibraryViewModel_DoesNotRetainLegacyIntelligenceTabMirrorBooleans()
    {
        var source = ReadLibraryViewModelSource();

        Assert.DoesNotContain("IsLibraryIntelligenceSmartInsertActive", source);
        Assert.DoesNotContain("IsLibraryIntelligenceSuggestNextActive", source);
        Assert.DoesNotContain("IsLibraryIntelligenceUpgradeActive", source);
        Assert.DoesNotContain("IsLibraryIntelligenceAutomixActive", source);
    }

    [Fact]
    public void SidebarInspectorLane_DoesNotUseServiceLocatorForSimilarityDependencies()
    {
        var doubleInspectorSource = ReadLibraryDoubleInspectorSource();
        var trackInspectorSource = ReadLibraryTrackInspectorSource();
        var intelligenceSource = ReadPlaylistIntelligenceSource();
        var eventsSource = ReadLibraryEventsSource();

        Assert.DoesNotContain("GetService(typeof(TrackSimilarityService))", doubleInspectorSource);
        Assert.DoesNotContain("GetService(typeof(TransitionStyleClassifier))", doubleInspectorSource);
        Assert.DoesNotContain("GetService(typeof(SimilarityIndex))", trackInspectorSource);
        Assert.DoesNotContain("GetService(typeof(TrackSimilarityService))", intelligenceSource);
        Assert.DoesNotContain("GetService(typeof(TrackSimilarityService))", eventsSource);
    }

    [Fact]
    public void SidebarLane_DoesNotRetainStaleParentForwardingNotifications()
    {
        var librarySource = ReadLibraryViewModelSource();
        var intelligenceSource = ReadPlaylistIntelligenceSource();
        var trackInspectorSource = ReadLibraryTrackInspectorSource();

        Assert.DoesNotContain("OnSuggestNextCandidatesCollectionChanged", librarySource);
        Assert.DoesNotContain("OnPlaylistUpgradeCandidatesCollectionChanged", librarySource);

        Assert.DoesNotContain("_library.OnPropertyChanged(nameof(LibraryViewModel.SmartInsertFromLabel))", intelligenceSource);
        Assert.DoesNotContain("_library.OnPropertyChanged(nameof(LibraryViewModel.IsSuggestNextLoading))", intelligenceSource);
        Assert.DoesNotContain("_library.OnPropertyChanged(nameof(LibraryViewModel.IsPlaylistUpgradeLoading))", intelligenceSource);

        Assert.DoesNotContain("_library.OnPropertyChanged(nameof(LibraryViewModel.TrackExplainabilitySummary))", trackInspectorSource);
        Assert.DoesNotContain("_library.OnPropertyChanged(nameof(LibraryViewModel.HasSimilarTracksPreview))", trackInspectorSource);
    }

    [Fact]
    public void LibraryViewModel_DoesNotRetainSmartInsertShimWrapperMethods()
    {
        var source = ReadLibraryViewModelSource();

        Assert.DoesNotContain("private void SetSmartInsertPairContext", source);
        Assert.DoesNotContain("private void ResetSmartInsertPairContext", source);
        Assert.DoesNotContain("private void SetSmartInsertPreparationHint", source);
        Assert.DoesNotContain("private void ClearSmartInsertPreparationHint", source);
    }

    [Fact]
    public void LibraryTrackInspectorViewModel_DoesNotRetainDeadForwardingNoOps()
    {
        var source = ReadLibraryTrackInspectorSource();
        var librarySource = ReadLibraryViewModelSource();

        Assert.DoesNotContain("OnSimilarTracksPreviewCollectionChanged", source);
        Assert.DoesNotContain("RaiseStateChanged", source);
        Assert.DoesNotContain("IDisposable", source);
        Assert.DoesNotContain("TrackInspector.Dispose();", librarySource);
    }

    [Fact]
    public void LibrarySidebarLane_DoesNotRetainStaleClosureLanguageMarkers()
    {
        var eventsSource = ReadLibraryEventsSource();
        var commandsSource = ReadLibraryCommandsSource();

        Assert.DoesNotContain("Legacy: In-Memory Smart Playlists", eventsSource);
        Assert.DoesNotContain("CS8618 Fix: Initialize with null!", commandsSource);
    }

    [Fact]
    public void LibraryEvents_PublishesExplicitInspectorWrapperContexts()
    {
        var eventsSource = ReadLibraryEventsSource();

        Assert.Contains("OpenInspectorEvent.Create(DoubleInspector, \"Library.TrackSelection.Double\")", eventsSource);
        Assert.Contains("OpenInspectorEvent.Create(Intelligence, \"Library.TrackSelection.EmptyIntelligence\")", eventsSource);
        Assert.Contains("OpenInspectorEvent.Create(single, \"Library.TrackSelection.Single\")", eventsSource);
        Assert.Contains("OpenInspectorEvent.Create(Intelligence, \"Library.ProjectSelection.EmptyIntelligence\")", eventsSource);
        Assert.Contains("ReactiveUI.MessageBus.Current.SendMessage(new CloseInspectorEvent());", eventsSource);
        Assert.DoesNotContain("new OpenInspectorEvent(this, \"DOUBLE INSPECTOR\", \"🔗\")", eventsSource);
        Assert.DoesNotContain("new OpenInspectorEvent(this, \"INTELLIGENCE\", \"🧠\")", eventsSource);
        Assert.DoesNotContain("new OpenInspectorEvent(single, source: \"Library.TrackSelection.Single\")", eventsSource);
    }

    [Fact]
    public void LibraryEvents_SelectionFlow_RoutesThroughChildInspectorOwners()
    {
        var eventsSource = ReadLibraryEventsSource();

        Assert.Contains("_ = DoubleInspector.HandleSelectionChangedAsync(selectedTracks);", eventsSource);
        Assert.Contains("_ = TrackInspector.TryAttachEnhancementsAsync(single);", eventsSource);
        Assert.Contains("TrackInspector.ClearEnhancements();", eventsSource);
        Assert.Contains("_ = Intelligence.RefreshSuggestNextCandidatesAsync();", eventsSource);
        Assert.Contains("_ = Intelligence.RefreshPlaylistUpgradeCandidatesAsync();", eventsSource);
    }

    [Fact]
    public void SimilarTracks_PrimesFromDedicatedInspectorViewModels()
    {
        var source = ReadSimilarTracksSource();

        Assert.Contains("case LibraryDoubleInspectorViewModel", source);
        Assert.Contains("case PlaylistIntelligenceViewModel", source);
        Assert.DoesNotContain("case LibraryDoubleInspectorContext", source);
        Assert.DoesNotContain("case PlaylistIntelligenceInspectorContext", source);
    }

    [Fact]
    public void MainViewModel_HandlesCloseInspectorEvent()
    {
        var source = ReadMainViewModelSource();

        Assert.Contains("ShouldApplyInspectorPayload(evt.ViewModel)", source);
        Assert.Contains("NormalizeInspectorOpenSource(evt.Source)", source);
        Assert.Contains("ShouldApplyInspectorOpenForCurrentPage(source, CurrentPageType)", source);
        Assert.Contains("Listen<SLSKDONET.Events.CloseInspectorEvent>()", source);
        Assert.Contains("_rightPanelService.ClosePanel();", source);
    }

    [Fact]
    public void OpenInspectorEvent_UsesSharedPresentationResolver()
    {
        var source = ReadOpenInspectorEventSource();

        Assert.Contains("ResolvePresentationDefaults", source);
        Assert.Contains("Create(object viewModel, string? source = null)", source);
        Assert.DoesNotContain("string title = \"INSPECTOR\"", source);
        Assert.DoesNotContain("string icon = \"ℹ️\"", source);
    }

    [Fact]
    public void LibraryPage_UsesCardVmProjectListBindings()
    {
        var xaml = ReadLibraryPageXaml();

        // Playlist Folders: the sidebar list is now a nested tree (RootTreeNodes) whose leaves
        // wrap LibraryPlaylistCardViewModel via PlaylistTreeCardNodeViewModel.Card, rather than
        // binding ItemsSource directly to the flat FilteredProjectCards collection.
        Assert.Contains("ItemsSource=\"{Binding Projects.RootTreeNodes}\"", xaml);
        Assert.Contains("SelectedItem=\"{Binding Projects.SelectedTreeNode, Mode=TwoWay}\"", xaml);
        Assert.DoesNotContain("ItemsSource=\"{Binding Projects.FilteredProjects}\"", xaml);
    }

    [Fact]
    public void CompactPlaylistTemplate_UsesCardVmAndMosaicCoverBinding()
    {
        var xaml = ReadCompactPlaylistTemplateXaml();

        Assert.Contains("x:DataType=\"vm:LibraryPlaylistCardViewModel\"", xaml);
        Assert.Contains("<Image Source=\"{Binding CoverBitmap}\" Stretch=\"UniformToFill\"/>", xaml);
        Assert.Contains("CommandParameter=\"{Binding Model}\"", xaml);
        Assert.DoesNotContain("x:DataType=\"models:PlaylistJob\"", xaml);
        Assert.DoesNotContain("DisplayArtUrl, Converter={StaticResource BitmapValueConverter}", xaml);
    }

    [Fact]
    public void DocumentationIndex_TracksSidebarUnificationClosureArtifacts()
    {
        var source = ReadDocumentationIndexSource();

        Assert.Contains("DOCS/memory/library_sidebar_unification_plan.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CLOSURE_HANDOFF_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ASSERTION_ARCHIVE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CLOSURE_SIGNOFF_PACK_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_RETROSPECTIVE_INDEX_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GUARDRAIL_DRIFT_WATCH_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CLOSURE_RECAP_PACK_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_DOCUMENTATION_DRIFT_MONITOR_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CLOSURE_ARCHIVE_CHECKSUM_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_LANE_STABILIZATION_MEMO_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_LONG_TAIL_REGRESSION_WATCHLIST_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_MAINTAINERS_FAQ_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_OWNERSHIP_MAP_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_MAINTENANCE_CADENCE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ARTIFACT_INDEX_CONDENSATION_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_DOCS_NAVIGATION_SIMPLIFICATION_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CROSS_LINK_AUDIT_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_REGRESSION_COMMAND_REFERENCE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_SUPERSESSION_PROTOCOL_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_LONG_TAIL_MAINTENANCE_CHECKLIST_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_MAINTENANCE_MAP_REFINEMENT_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ARCHIVE_GROUPING_STRATEGY_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_MAINTENANCE_RUNBOOK_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GLOSSARY_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_RETENTION_POLICY_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_FOCUSED_GATE_TROUBLESHOOTING_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_DOC_TEMPLATE_STARTER_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ARCHIVE_INDEX_CROSS_REFERENCE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_MEMORY_SUMMARY_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_DOCUMENTATION_CONSOLIDATION_CHECKPOINT_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ARCHIVE_SHAPING_GUIDE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GOVERNANCE_NOTE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_TEMPLATE_RUBRIC_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_INDEX_PRUNING_POLICY_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_REVIEW_CADENCE_MATRIX_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ESCALATION_RUBRIC_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_NAMING_PATTERNS_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_MEMORY_SYNC_CHECKLIST_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ARTIFACT_TAXONOMY_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ARCHIVE_GOVERNANCE_CHECKPOINT_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GOVERNANCE_COMPRESSION_NOTE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_MAINTENANCE_HEURISTICS_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ARTIFACT_GROUPING_HYGIENE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ARCHIVE_NAVIGATION_LABELS_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_QUICKSTART_DESCRIPTOR_CONSISTENCY_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_DEPENDENCY_MAP_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_MAINTENANCE_VALIDATION_RUBRIC_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ARCHIVE_OPERATIONS_RUNBOOK_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_MEMORY_CAPTURE_HEURISTICS_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ARCHIVE_OPERATIONS_CHECKPOINT_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ACTIVE_GROUPING_MATRIX_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_HISTORICAL_SEGMENTATION_GUIDE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_DESCRIPTOR_CLEANUP_NOTE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ARCHIVE_PRUNING_DECISION_TREE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_PLAYBOOK_SHORTHAND_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CONTRACT_UPKEEP_CHECKLIST_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_RELATIONSHIP_MAP_REFINEMENT_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GOVERNANCE_FAQ_ADDENDUM_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_BATCHING_RUBRIC_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_DISCOVERABILITY_AUDIT_CHECKPOINT_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_INDEX_GROUPING_PROTOTYPE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_SUPERSESSION_EXAMPLES_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_VALIDATION_OUTPUT_GUIDE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_MEMORY_HYGIENE_RUBRIC_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CROSS_LINK_MINIMALISM_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ARTIFACT_LIFECYCLE_MAP_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ARCHIVE_ONBOARDING_NOTE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GOVERNANCE_WAVE_CHECKLIST_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_INDEX_ALIGNMENT_CHECKPOINT_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ARCHIVE_STEWARDSHIP_CHECKPOINT_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_QUICKSTART_GROUPING_COMPARISON_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ARCHIVE_ROLE_LEGEND_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_MAINTAINER_HANDOFF_TRIAGE_CHECKLIST_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CONTRACT_ASSERTION_MINIMIZATION_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ARCHIVE_ANNOTATION_STYLE_GUIDE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CLOSURE_MAINTENANCE_ANTI_PATTERNS_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_DOCUMENTATION_WAVE_EXIT_CRITERIA_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ARCHIVE_DISCOVERABILITY_REGRESSION_EXAMPLES_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_LONG_TAIL_ONBOARDING_MATRIX_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CONSOLIDATED_ARCHIVE_STEWARDSHIP_CHECKPOINT_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_QUICKSTART_GROUPING_STRESS_TEST_NOTE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ARTIFACT_ROLE_CROSSWALK_REFINEMENT_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_MAINTAINER_ESCALATION_DECISION_TABLE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CONTRACT_DRIFT_TRIAGE_EXAMPLES_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ANNOTATION_CONSISTENCY_CHECKLIST_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CLOSURE_DOC_REDUNDANCY_FILTER_NOTE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_VALIDATION_REPORTING_SHORTHAND_GUIDE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ONBOARDING_HANDOFF_COMPRESSION_NOTE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ARCHIVE_GOVERNANCE_REVIEW_SCOREBOARD_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_POST_WAVE_CONSOLIDATION_CHECKPOINT_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GROUPING_NAVIGATION_DELTA_NOTE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ROLE_TO_ARTIFACT_COVERAGE_AUDIT_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ESCALATION_BOUNDARY_FAQ_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CONTRACT_REVIEW_SAMPLING_GUIDE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ANNOTATION_DRIFT_EXAMPLES_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ARCHIVE_OVERLAP_PRUNING_CHECKLIST_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_VALIDATION_SHORTHAND_EXAMPLES_PACK_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_MAINTAINER_RESTART_QUICK_REFERENCE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ARCHIVE_GOVERNANCE_SCOREBOARD_RUBRIC_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CONSOLIDATION_WAVE_READINESS_CHECKPOINT_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GROUPING_INDEX_STRESS_CASE_MATRIX_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CROSSWALK_EVIDENCE_TRACE_EXAMPLES_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ESCALATION_EXCEPTION_HANDLING_NOTE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CONTRACT_MAINTENANCE_SAMPLING_CHECKLIST_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ANNOTATION_NORMALIZATION_EXAMPLES_PACK_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_OVERLAP_RETIREMENT_DECISION_TABLE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_VALIDATION_RECAP_COMPRESSION_RUBRIC_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ONBOARDING_RESTART_PITFALLS_NOTE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GOVERNANCE_REVIEW_CADENCE_ADDENDUM_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CONSOLIDATION_WAVE_SIGNOFF_CHECKPOINT_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GROUPING_DRIFT_SCENARIO_MATRIX_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ROLE_EVIDENCE_HANDOFF_MAP_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ESCALATION_TIMEOUT_DECISION_NOTE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CONTRACT_UPKEEP_VERIFICATION_TABLE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ANNOTATION_LINT_SHORTHAND_GUIDE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_OVERLAP_DEPRECATION_CHECKLIST_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_VALIDATION_DELTA_NARRATION_TEMPLATE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_RESTART_CONFIDENCE_CHECKLIST_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GOVERNANCE_REVIEW_DRIFT_ALERT_NOTE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CONSOLIDATION_CONTINUITY_CHECKPOINT_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GROUPING_RESILIENCE_SCORECARD_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ROLE_COVERAGE_EXCEPTION_MAP_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ESCALATION_ROUTING_FALLBACK_NOTE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CONTRACT_INDEX_DRIFT_TRIAGE_MATRIX_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ANNOTATION_CONSISTENCY_QUICK_LINT_CHECKLIST_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_OVERLAP_ARCHIVE_RETIREMENT_PLAYBOOK_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_VALIDATION_EVIDENCE_BREVITY_GUIDE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_MAINTAINER_RELAUNCH_STARTER_PACK_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GOVERNANCE_VARIANCE_REVIEW_CARD_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_REINFORCEMENT_WAVE_READINESS_CHECKPOINT_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GROUPING_ROLLBACK_DECISION_MATRIX_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ROLE_CONTINUITY_EXCEPTION_FAQ_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ESCALATION_HANDOFF_TIMING_TABLE_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CONTRACT_DRIFT_CORRECTION_COOKBOOK_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ANNOTATION_NOISE_REDUCTION_RUBRIC_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_OVERLAP_CONSOLIDATION_AUDIT_MAP_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_VALIDATION_EVIDENCE_ONE_LINE_PACK_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_MAINTAINER_REBOOT_CHECKLIST_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GOVERNANCE_ANOMALY_RESPONSE_CARD_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_RESILIENCE_WAVE_SIGNOFF_CHECKPOINT_2026-05-29.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GROUPING_ROLLBACK_VALIDATION_EXAMPLES_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ROLE_CONTINUITY_EVIDENCE_PACK_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ESCALATION_LATENCY_TROUBLESHOOTING_NOTE_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CONTRACT_CORRECTION_ANTI_PATTERNS_LIST_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ANNOTATION_BREVITY_NORMALIZATION_CHECKLIST_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_OVERLAP_RETIREMENT_VERIFICATION_MATRIX_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_VALIDATION_ONE_LINE_QUALITY_BAR_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_MAINTAINER_RESTART_HANDOFF_MICRO_PACK_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GOVERNANCE_ANOMALY_ESCALATION_MATRIX_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_REINFORCEMENT_CONTINUITY_SIGNOFF_CHECKPOINT_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GROUPING_ROLLBACK_IMPACT_SCORECARD_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ROLE_CONTINUITY_RISK_REGISTER_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ESCALATION_LATENCY_SLA_NOTE_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CONTRACT_CORRECTION_VALIDATION_MATRIX_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ANNOTATION_CLARITY_BENCHMARK_PACK_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_OVERLAP_RETIREMENT_TRACEABILITY_MAP_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_VALIDATION_BREVITY_COMPLIANCE_CHECKLIST_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_MAINTAINER_RESTART_CONFIDENCE_CARD_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GOVERNANCE_ANOMALY_CLOSURE_CHECKLIST_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_REINFORCEMENT_WAVE_HANDOFF_CHECKPOINT_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GROUPING_ROLLBACK_REGRESSION_WATCHLIST_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ROLE_CONTINUITY_ESCALATION_RUBRIC_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ESCALATION_SLA_EXCEPTION_MATRIX_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CONTRACT_CORRECTION_AUDIT_RUNBOOK_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ANNOTATION_BENCHMARK_DRIFT_LOG_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_OVERLAP_TRACEABILITY_QA_CHECKLIST_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_VALIDATION_BREVITY_STYLE_CARD_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_RESTART_CONFIDENCE_VERIFICATION_MAP_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GOVERNANCE_CLOSURE_EVIDENCE_PACK_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CONTINUITY_REINFORCEMENT_SIGNOFF_CHECKPOINT_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GROUPING_ROLLBACK_CLOSURE_AUDIT_GRID_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ROLE_CONTINUITY_INCIDENT_LEDGER_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ESCALATION_SLA_REMEDIATION_PLAYBOOK_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CONTRACT_CORRECTION_ANOMALY_DRILL_CARD_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ANNOTATION_CLARITY_ENFORCEMENT_CHECKLIST_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_OVERLAP_TRACEABILITY_RETIREMENT_RUBRIC_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_VALIDATION_BREVITY_EXCEPTION_REGISTER_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_MAINTAINER_RESTART_READINESS_SCORECARD_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GOVERNANCE_ANOMALY_POSTMORTEM_TEMPLATE_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_REINFORCEMENT_CONTINUITY_TRANSITION_CHECKPOINT_2026-05-30.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GROUPING_ROLLBACK_CLOSURE_CONFIDENCE_MATRIX_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ROLE_CONTINUITY_INCIDENT_RESPONSE_CARD_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ESCALATION_REMEDIATION_VERIFICATION_CHECKLIST_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CONTRACT_ANOMALY_RECOVERY_PLAYBOOK_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ANNOTATION_ENFORCEMENT_DRIFT_TRACKER_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_OVERLAP_RETIREMENT_EVIDENCE_MAP_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_VALIDATION_BREVITY_WAIVER_PROTOCOL_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_RESTART_READINESS_REGRESSION_CHECKLIST_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GOVERNANCE_POSTMORTEM_SYNTHESIS_NOTE_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_REINFORCEMENT_COMPLETION_HANDOFF_CHECKPOINT_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GROUPING_ROLLBACK_CLOSURE_VERIFICATION_LEDGER_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ROLE_CONTINUITY_INCIDENT_ESCALATION_MAP_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ESCALATION_REMEDIATION_EVIDENCE_CHECKLIST_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CONTRACT_ANOMALY_CONTAINMENT_MATRIX_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ANNOTATION_DRIFT_CORRECTION_PLAYBOOK_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_OVERLAP_RETIREMENT_VALIDATION_DASHBOARD_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_VALIDATION_BREVITY_EXEMPTION_CRITERIA_CARD_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_RESTART_REGRESSION_MITIGATION_CHECKLIST_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GOVERNANCE_SYNTHESIS_EVIDENCE_GRID_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_REINFORCEMENT_CLOSURE_TRANSITION_SIGNOFF_CHECKPOINT_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GROUPING_ROLLBACK_CLOSURE_ATTESTATION_CARD_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ROLE_CONTINUITY_ESCALATION_RESPONSE_MATRIX_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ESCALATION_REMEDIATION_AUDIT_TRAIL_NOTE_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CONTRACT_CONTAINMENT_EVIDENCE_CHECKLIST_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ANNOTATION_CORRECTION_CONSISTENCY_RUBRIC_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_OVERLAP_VALIDATION_EXCEPTION_LEDGER_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_VALIDATION_BREVITY_EXEMPTION_REVIEW_CARD_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_RESTART_MITIGATION_SIGNOFF_CHECKLIST_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GOVERNANCE_SYNTHESIS_CLOSURE_MEMO_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_REINFORCEMENT_CLOSURE_HANDOFF_READINESS_CHECKPOINT_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GROUPING_CLOSURE_ATTESTATION_EVIDENCE_GRID_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CONTINUITY_ESCALATION_RESPONSE_AUDIT_MAP_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_REMEDIATION_AUDIT_TRAIL_VERIFICATION_CHECKLIST_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_CONTRACT_CONTAINMENT_EXCEPTION_MATRIX_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_ANNOTATION_CONSISTENCY_DRIFT_MONITOR_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_OVERLAP_EXCEPTION_RESOLUTION_DASHBOARD_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_BREVITY_EXEMPTION_EXPIRY_TRACKER_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_RESTART_SIGNOFF_REGRESSION_MATRIX_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_GOVERNANCE_SYNTHESIS_ACTION_REGISTER_2026-05-31.md", source);
        Assert.Contains("DOCS/LIBRARY_SIDEBAR_UNIFICATION_REINFORCEMENT_HANDOFF_CLOSURE_CHECKPOINT_2026-05-31.md", source);
    }

    private static string ReadLibraryPageXaml()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "Views", "Avalonia", "LibraryPage.axaml");
        Assert.True(File.Exists(filePath), $"Expected library view at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadPlaylistIntelligencePanelXaml()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "Views", "Avalonia", "Controls", "PlaylistIntelligencePanel.axaml");
        Assert.True(File.Exists(filePath), $"Expected playlist intelligence panel at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadMainWindowXaml()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "Views", "Avalonia", "MainWindow.axaml");
        Assert.True(File.Exists(filePath), $"Expected main window view at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadLibraryEventsSource()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "ViewModels", "LibraryViewModel.Events.cs");
        Assert.True(File.Exists(filePath), $"Expected library events source at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadLibraryViewModelSource()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "ViewModels", "LibraryViewModel.cs");
        Assert.True(File.Exists(filePath), $"Expected library view model source at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadLibraryCommandsSource()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "ViewModels", "LibraryViewModel.Commands.cs");
        Assert.True(File.Exists(filePath), $"Expected library commands source at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadLibraryDoubleInspectorSource()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "ViewModels", "LibraryDoubleInspectorViewModel.cs");
        Assert.True(File.Exists(filePath), $"Expected double inspector source at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadLibraryTrackInspectorSource()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "ViewModels", "LibraryTrackInspectorViewModel.cs");
        Assert.True(File.Exists(filePath), $"Expected track inspector source at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadPlaylistIntelligenceSource()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "ViewModels", "PlaylistIntelligenceViewModel.cs");
        Assert.True(File.Exists(filePath), $"Expected playlist intelligence source at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadCompactPlaylistTemplateXaml()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "Views", "Avalonia", "Controls", "CompactPlaylistTemplate.axaml");
        Assert.True(File.Exists(filePath), $"Expected compact playlist template view at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadSimilarTracksSource()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "ViewModels", "SimilarTracksViewModel.cs");
        Assert.True(File.Exists(filePath), $"Expected similar tracks source at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadDocumentationIndexSource()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "DOCUMENTATION_INDEX.md");
        Assert.True(File.Exists(filePath), $"Expected documentation index at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadMainViewModelSource()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "Views", "MainViewModel.cs");
        Assert.True(File.Exists(filePath), $"Expected main view model source at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadOpenInspectorEventSource()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "Events", "OpenInspectorEvent.cs");
        Assert.True(File.Exists(filePath), $"Expected inspector event source at {filePath}");

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
