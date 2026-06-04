# Library Sidebar Unification Regression Command Reference (2026-05-29)

Status: Regression command reference
Date: 2026-05-29
Scope: Canonical focused command for validating closure maintenance work.

## Command

`dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj --filter "FullyQualifiedName~ProjectListViewModelSelectionSyncTests|FullyQualifiedName~LibrarySidebarUnificationStartTests|FullyQualifiedName~SidebarViewModelSyncTests|FullyQualifiedName~SidebarAndPanelServiceTests|FullyQualifiedName~LibraryViewModelPlaylistUpgradeCommandTests" -v minimal`

## Usage

1. Run after any sidebar/intelligence closure artifact update that changes discoverability expectations.
2. Run after any route or compatibility-surface code change in the lane.
3. Record the latest result in rolling recap docs when it is part of an execution batch.
