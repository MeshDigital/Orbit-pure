#!/usr/bin/env pwsh
# Creates Workstation Cockpit backlog issues on GitHub.
param(
    [string]$Repo = "MeshDigital/Orbit-pure"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Ensure-Label {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [string]$Color = "1f6feb",
        [string]$Description = ""
    )

    $existing = gh label list --repo $Repo --limit 500 --json name | ConvertFrom-Json
    if ($existing.name -contains $Name) {
        return
    }

    $args = @("label", "create", $Name, "--repo", $Repo, "--color", $Color)
    if (-not [string]::IsNullOrWhiteSpace($Description)) {
        $args += @("--description", $Description)
    }

    gh @args | Out-Null
    Write-Host "  Created missing label: $Name"
}

function Ensure-Labels {
    param([string[]]$Labels)

    foreach ($label in $Labels) {
        switch ($label) {
            "workstation"   { Ensure-Label -Name $label -Color "0e8a16" -Description "Workstation and cockpit surface" }
            "cockpit"       { Ensure-Label -Name $label -Color "5319e7" -Description "Cockpit-first workstation model" }
            "flow"          { Ensure-Label -Name $label -Color "fbca04" -Description "Flow and transition overlays" }
            "timeline"      { Ensure-Label -Name $label -Color "006b75" -Description "Timeline interaction and visuals" }
            "transitions"   { Ensure-Label -Name $label -Color "d93f0b" -Description "Transition shaping and presets" }
            "automation"    { Ensure-Label -Name $label -Color "0052cc" -Description "Automation lane and curve editing" }
            "stems"         { Ensure-Label -Name $label -Color "c5def5" -Description "Stem editing and lane visualization" }
            "state"         { Ensure-Label -Name $label -Color "bfdadc" -Description "Application and VM state architecture" }
            "architecture"  { Ensure-Label -Name $label -Color "a2eeef" -Description "Architecture and design-level work" }
            "analysis"      { Ensure-Label -Name $label -Color "f9d0c4" -Description "Audio analysis and phrase/cue integration" }
            "performance"   { Ensure-Label -Name $label -Color "fef2c0" -Description "Performance and rendering responsiveness" }
            "accessibility" { Ensure-Label -Name $label -Color "7057ff" -Description "Accessibility improvements" }
            "refactor"      { Ensure-Label -Name $label -Color "cfd3d7" -Description "Structural cleanup and reduction" }
            "ui"            { Ensure-Label -Name $label -Color "1d76db" -Description "UI/UX changes" }
            default          { Ensure-Label -Name $label }
        }
    }
}

function New-CockpitIssue {
    param(
        [Parameter(Mandatory)] [string]$Title,
        [Parameter(Mandatory)] [string[]]$Labels,
        [Parameter(Mandatory)] [string]$Why,
        [Parameter(Mandatory)] [string[]]$Acceptance,
        [Parameter(Mandatory)] [string[]]$Plan
    )

    Ensure-Labels -Labels $Labels

    $acceptanceBody = ($Acceptance | ForEach-Object { "- [ ] $_" }) -join "`n"
    $planBody = ($Plan | ForEach-Object -Begin { $i = 1 } -Process { "{0}. {1}" -f $i++, $_ }) -join "`n"

    $body = @"
## Why it matters
$Why

## Acceptance criteria
$acceptanceBody

## Implementation plan
$planBody
"@

    $labelArg = ($Labels -join ",")
    $url = gh issue create --repo $Repo --title $Title --label $labelArg --body $body
    Write-Host "  Created: $url"
}

Write-Host "=== Creating Workstation Cockpit issues on $Repo ===" -ForegroundColor Cyan

New-CockpitIssue -Title "Reduce repeated state surfaces in Workstation shell" -Labels @("workstation", "ui", "cockpit", "refactor") -Why "The current Workstation still repeats the same state across header, lane, inspector, and drawer surfaces. This makes the cockpit feel crowded and weakens the single-surface mental model." -Acceptance @(
    "Header only shows transport, essential tool selection, and minimal session context.",
    "Flow/stems/export/tool summaries are not repeated across regions.",
    "Inspector is the primary detail surface.",
    "Drawer is the primary bulk-workflow surface."
) -Plan @(
    "Audit repeated summary bindings in WorkstationPage.axaml.",
    "Remove duplicate informational surfaces first.",
    "Re-run runtime smoke and cockpit review.",
    "Update cockpit memory and epic."
)

New-CockpitIssue -Title "Finish cockpit-first layout reduction" -Labels @("workstation", "ui", "cockpit", "refactor") -Why "The current shell is closer to a cockpit than before, but it still reads like stacked panels around a timeline rather than one cohesive workstation." -Acceptance @(
    "Timeline region is visually dominant.",
    "Header and drawer feel secondary.",
    "Inspector reads as contextual support, not a competing panel system.",
    "No new workflow dead ends."
) -Plan @(
    "Reduce panel framing that competes with the timeline.",
    "Remove or compress leftover shell sections that behave like mini-pages.",
    "Validate with before/after screenshots."
)

New-CockpitIssue -Title "Make timeline persist as empty canvas" -Labels @("workstation", "ui", "cockpit") -Why "The timeline is supposed to be the center of gravity. If it disappears when no tracks are loaded, the cockpit model breaks." -Acceptance @(
    "Timeline visible with zero tracks.",
    "Empty-state messaging overlays, never replaces, the canvas.",
    "Load/import/analyze actions remain accessible."
) -Plan @(
    "Audit visibility rules for timeline and lanes.",
    "Render empty lane structure with lightweight empty-state affordances.",
    "Validate visually at multiple sizes."
)

New-CockpitIssue -Title "Remove remaining visible deck-first affordances" -Labels @("workstation", "ui", "cockpit", "refactor") -Why "Decks should remain engine primitives, not the dominant interaction model of the workstation shell." -Acceptance @(
    "Deck targeting still possible where needed.",
    "Top-level shell no longer centers A/B/C/D controls.",
    "Inspector and track-row actions own remaining deck-specific operations."
) -Plan @(
    "Audit deck-focused controls in header, inspector, and lanes.",
    "Keep only necessary direct-manipulation controls.",
    "Move prep/detail controls into contextual surfaces."
)

New-CockpitIssue -Title "Deepen Flow integration into timeline canvas" -Labels @("workstation", "ui", "cockpit", "flow") -Why "Flow still behaves partially like an attached subsystem instead of a first-class timeline overlay." -Acceptance @(
    "Flow context visible directly in timeline.",
    "Flow no longer depends on stacked explanatory surfaces.",
    "Drawer is for bulk browsing, not core Flow context."
) -Plan @(
    "Audit what Flow still renders outside timeline.",
    "Move key Flow state into canvas and lane representation.",
    "Retest discoverability of overlays and playlist actions."
)

New-CockpitIssue -Title "Add real automation lane editing" -Labels @("workstation", "ui", "automation", "cockpit") -Why "Automation currently reads more like state summary than editable automation workflow." -Acceptance @(
    "Automation mode exposes editable lane content.",
    "Automation points and curves visible in timeline.",
    "Inspector supports selected automation detail."
) -Plan @(
    "Define automation lane data model.",
    "Render lane content in timeline.",
    "Bind inspector to automation selection."
)

New-CockpitIssue -Title "Add real stems lane editing" -Labels @("workstation", "ui", "stems", "cockpit") -Why "Stems mode currently provides useful controls, but not a full lane-driven editing model comparable to the target cockpit quality." -Acceptance @(
    "Stems have visible lane and state representation in timeline.",
    "Inspector remains primary detailed stems surface.",
    "Lane controls support quick edits without clutter."
) -Plan @(
    "Define lane representation for stems state.",
    "Render lane-level stems visualization.",
    "Keep quick toggles minimal and contextual."
)

New-CockpitIssue -Title "Add transition preset system" -Labels @("workstation", "ui", "transitions", "cockpit") -Why "Transition shaping is core to the DJ.Studio-style workstation target and is currently underrepresented in the UI model." -Acceptance @(
    "Presets like Crossfade, Bass Swap, Full, None, and Custom are available.",
    "Selected preset affects transition planning surfaces.",
    "Inspector exposes preset parameters."
) -Plan @(
    "Define transition preset model.",
    "Add preset selection UI in contextual surfaces.",
    "Bind summaries and guidance to selected preset."
)

New-CockpitIssue -Title "Add phrase-aware snapping" -Labels @("workstation", "ui", "timeline", "analysis") -Why "Phrase awareness is part of the target product bar and critical for credible timeline-first editing." -Acceptance @(
    "Phrase boundaries represented in timeline.",
    "Snapping can target phrase boundaries.",
    "Visual feedback when phrase snapping is active."
) -Plan @(
    "Audit existing phrase and cue analysis data.",
    "Introduce phrase snapping state and visualization.",
    "Validate interaction in Flow and waveform modes."
)

New-CockpitIssue -Title "Implement timeline virtualization and rendering strategy" -Labels @("workstation", "performance", "timeline", "refactor") -Why "The target cockpit density and lane complexity will not scale without a deliberate rendering and virtualization approach." -Acceptance @(
    "Virtualization documented and implemented for large track and lane counts.",
    "Waveform and lane surfaces avoid naive full redraws.",
    "Interaction remains responsive under expected load."
) -Plan @(
    "Define virtualization and rendering architecture.",
    "Choose retained and cached rendering boundaries.",
    "Add targeted performance checks."
)

New-CockpitIssue -Title "Create unified workstation state model" -Labels @("workstation", "architecture", "state", "refactor") -Why "As the cockpit becomes more contextual, state can no longer be spread ad hoc across view bindings without increasing fragility." -Acceptance @(
    "Active tool, focus, zoom, and inspector context are centrally represented.",
    "Cross-surface binding logic is simpler, not more fragmented.",
    "New modes and features plug into the same state model."
) -Plan @(
    "Audit current state dispersion in WorkstationViewModel.",
    "Define normalized workstation state object and layer.",
    "Migrate highest-friction bindings first."
)

New-CockpitIssue -Title "Finish accessibility contrast pass for cockpit surfaces" -Labels @("workstation", "ui", "accessibility") -Why "Several surfaces still risk low readability under dark-theme density, especially supporting text and secondary status rows." -Acceptance @(
    "Secondary text meets acceptable contrast.",
    "Status chips and helper text remain readable at cockpit density.",
    "Manual visual review confirms improvement."
) -Plan @(
    "Audit color pairs across Workstation surfaces.",
    "Adjust tokenized colors where possible.",
    "Re-run screenshot and manual contrast review."
)

Write-Host "=== Done ===" -ForegroundColor Green
