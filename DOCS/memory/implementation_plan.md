# Workstation Cockpit Redesign & UI/UX Overhaul (Master Plan)

> Status: Historical coordination index (workstation overhaul and strict-download hardening are completed in this stream; sidebar unification remains parked)
>
> Last reviewed: 2026-05-26
>
> See also: [workstation_overhaul_completed_work.md](workstation_overhaul_completed_work.md), [download_filtering_phase2_completion_report.md](download_filtering_phase2_completion_report.md), [library_sidebar_unification_plan.md](library_sidebar_unification_plan.md)

This document establishes the master design decisions, planning files registry, and technical roadmap to overhaul the ORBIT Workstation environment into a high-density, professional-grade timeline cockpit. The goal is to maximize vertical screen space, eliminate redundant controls, and focus user interaction through a single Contextual Inspector, matching the workflows of **DJ.Studio Pro** and **Mixed In Key**.

---

## 📋 Resolved Design Decisions

Based on user review and optimization goals, the following key design choices are finalized:

1. **Crossfader Position**: 
   > [!NOTE]
   > The horizontal crossfader is relocated to the **Flow Inspector** (contextual transition-first approach). This keeps the main timeline layout completely clean, allocating maximum screen space to the waveforms.
2. **Key Display Format**: 
   > [!NOTE]
   > Key notation in the condensed left track details panel of the deck rows will utilize **Camelot Notation** (e.g. `8A` / `9B`). This facilitates rapid harmonic mixing during linear playlist construction.
3. **Hot Cue Pad Layout**: 
   > [!NOTE]
   > The 8 CDJ-style hot cue pads in the Track Inspector are arranged in a touch-friendly **2x4 grid** (resembling physical DJ hardware controllers).

---

## 🗂️ Registry of Planning Files

The following memory and planning documents have been created to organize development across different areas of the application:

| Document Path | Purpose / Scope | State / Context |
| :--- | :--- | :--- |
| [workstation_redesign_overhaul.md](workstation_redesign_overhaul.md) | Central blueprint for the workstation visual clutter audit, global header consolidation, and track inspector layout. | Historical blueprint; implementation delivered and summarized in workstation completion walkthrough. |
| [library_sidebar_unification_plan.md](library_sidebar_unification_plan.md) | Architectural proposal to unify double sidebars, simplify workspace layout, and route panels via the Sliding Right Panel. | Parked epic (explicitly not active execution). |
| [library_waveform_automix_plan.md](library_waveform_automix_plan.md) | Technical plan for waveform blob unpacking, background analysis progress updates, and playlist optimizer integration. | Historical execution blueprint; keep for rationale and future deltas. |
| [download_filtering_strict_mode_hardening.md](download_filtering_strict_mode_hardening.md) | Technical roadmap to introduce fuzzy fallback toggles, duration tolerance gates, and MatchScorer quality validation rules. | Historical rationale; superseded by completion report and landed slices. |
| [download_filtering_implementation_plan.md](download_filtering_implementation_plan.md) | Specific file-level proposed modifications to Settings Page and Download Manager to support strict search filtering. | Historical phased backlog snapshot; Phase 1/2 complete, Phase 3 parked. |

---

## 🛠️ Proposed Changes

### Workstation UI Components

---

#### [MODIFY] [WorkstationPage.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Views/Avalonia/WorkstationPage.axaml)
- **Mixer Center Removal**: Delete `<wsViews:MixerCenter Grid.Row="3" Grid.Column="0"/>` to reclaim vertical timeline space.
- **Grid Layout Restructuring**:
  - In `CockpitGrid`, the timeline row container (`Grid.Row="1"`) contains a nested grid.
  - Modify this nested grid's RowDefinitions from `RowDefinitions="Auto,Auto,*,Auto"` to `RowDefinitions="Auto,Auto,*"` to eliminate the 4th row (previously occupied by `MixerCenter`).
  - Ensure the Right-Hand Inspector (`Classes="ws-inspector"`) keeps its `Grid.RowSpan="3"`, which now spans the entire vertical height of the timeline viewport.
- **Crossfader Integration**:
  - Embed the Crossfader slider and deck letter indicators (`A` and `B`) inside the `IsFlowMode` section of the Right-Hand Inspector:
    ```xml
    <Separator Background="#252526" Height="1" Margin="0,8"/>
    <StackPanel Spacing="4">
        <TextBlock Text="CROSSFADER (A ── B)" FontSize="9" FontWeight="Black" Foreground="#666"/>
        <Grid ColumnDefinitions="Auto,*,Auto" ColumnSpacing="8" VerticalAlignment="Center">
            <TextBlock Grid.Column="0" Text="A" FontSize="11" FontWeight="Bold" Foreground="#7FC7FF" VerticalAlignment="Center"/>
            <Slider Grid.Column="1" Minimum="0" Maximum="1"
                    Value="{Binding CrossfaderPosition, Mode=TwoWay}"
                    Height="24"
                    VerticalAlignment="Center"
                    ToolTip.Tip="Crossfader: ← Deck A  ·  Deck B →"/>
            <TextBlock Grid.Column="2" Text="B" FontSize="11" FontWeight="Bold" Foreground="#95D36A" VerticalAlignment="Center"/>
        </Grid>
    </StackPanel>
    ```

---

#### [DELETE] [MixerCenter.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Views/Avalonia/Workstation/MixerCenter.axaml)
- Delete the file entirely. All duplicate transport/quantize/metronome controls have already been relocated to the global header, and the crossfader is relocated to the Flow Inspector.

---

#### [DELETE] [MixerCenter.axaml.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Views/Avalonia/Workstation/MixerCenter.axaml.cs)
- Delete the corresponding code-behind file.

---

#### [MODIFY] [WorkstationTimelineLayoutGuardTests.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Tests/SLSKDONET.Tests/Architecture/WorkstationTimelineLayoutGuardTests.cs)
- Add a new helper `ReadWorkstationDeckRowXaml()` to read the deck row XAML content.
- Add `WorkstationDeckRow_IsUltraThinAndStripped` to assert that `WorkstationDeckRow.axaml` enforces the `Height="64"` height constraint, uses `ColumnDefinitions="200,*"`, and does not contain `Slider`, `Expander`, or buttons of classes `hotcue`/`stem-toggle`/`phrase-chip` in its main layout body.
- Add `WorkstationPage_TrackInspectorContainsSelectedDeckBindings` to assert that the right-hand inspector contains correct bindings pointing to `FocusedDeck` properties.
- Add `WorkstationPage_DoesNotReferenceMixerCenter` to assert that `MixerCenter` is no longer referenced anywhere in `WorkstationPage.axaml`.

---

## 🧪 Verification Plan

### Automated Tests
Execute the layout guard tests to ensure layout constraints are strictly adhered to:
```powershell
# Run the Workstation timeline layout guard tests
dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj --filter "FullyQualifiedName~WorkstationTimelineLayoutGuardTests"
```

### Manual Verification
1. **Vertical Space Utilization**: Launch the application and verify that 5–6 deck rows can fit on the timeline canvas simultaneously without forcing vertical scrolling.
2. **Contextual Flow Inspector**: Switch to **Flow Mode** in the header. Verify that the Right-Hand Inspector displays the new Crossfader slider.
3. **Crossfader Control**: Slide the crossfader in the Flow Inspector and verify that the sin/cos volume levels adjust between Deck A and Deck B.
4. **Mixer Center Removal**: Verify that the bottom section of the timeline is clean, and that the timeline and Flow Drawer expand to utilize all available vertical space.
