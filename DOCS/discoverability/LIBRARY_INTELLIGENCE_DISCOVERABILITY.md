# Library Intelligence Discoverability

Status: Active
Last Updated: 2026-06-04

## Driver
- agent/workflows/LIBRARY_INTELLIGENCE_DRIVER.md

## Active Slice Queue
- LI-001 through LI-120

## Latest Completed Slice
- LI-001: scanner contract and edge path normalization regression coverage.
- LI-002: section-vector null tolerance hardening with neutral fallback defaults and regression tests.
- LI-003: scoring constants reconciled across TrackSimilarityService and TrackMatchScorer with centralized matching weights.
- LI-004: library health update cadence shifted to adaptive drift-aware recomputation using pending-update markers.
- LI-005: sparse metadata candidate rendering hardened with regression tests for safe fallback displays.
- LI-006: folder registration now consolidates normalized duplicates and retries transient DB lock/busy registration failures.
- LI-007: analysis queue now suppresses duplicate in-flight requests per track hash to keep queue status coherent.
- LI-008: discoverability and recap surfaces synchronized with scanner and queue coherence contracts.
- LI-009: strict auto-download candidate filtering now honors per-track intelligence overrides for preferred formats and bitrate floors.
- LI-010: lane closure checkpoint published with explicit pending risk register.
- LI-011: queue continuation extended through LI-014 to preserve lane execution flow beyond initial closure.
- LI-012: strict-mode compatibility regression contract pinned for preferred-format and bitrate-override behavior.
- LI-013: discoverability/index/recap surfaces synchronized for LI-009 and LI-010 outputs.
- LI-014: continuation checkpoint published with next-slice pointer for LI-015.
- LI-015: strict auto-download query/candidate filter resolution centralized under one shared policy surface.
- LI-016: canonical duration normalization now guards seconds-scale upstream values before strict duration gating.
- LI-017: regression contracts pinned for preferred-format fallback parity, zero-override bitrate parity, and seconds-scale duration normalization.
- LI-018: risk register reconciled after strict filter-parity hardening and duration-unit mitigation rollout.
- LI-019: discoverability/index/recap/memory surfaces synchronized for LI-015 through LI-018 outputs.
- LI-020: continuation checkpoint published with next-slice pointer for LI-021.
- LI-021: sparse-metadata strict acceptance now enforces confidence adjustments and a floor when thresholds are lowered.
- LI-022: sparse-metadata acceptance-floor regression contracts pinned in `AutoSearchServiceTests`.
- LI-023: compatibility gate task added to run strict auto-download and Library surfaces together.
- LI-024: combined compatibility validation baseline executed and captured as lane evidence.
- LI-025: risk register reconciled to close sparse-default masking and test-fragmentation risks.
- LI-026: continuation checkpoint published with next-slice pointer for LI-027.
- LI-027: scorer-hardening continuation wave opened with explicit risk intake for nullable and sparse-metadata scorer contracts.
- LI-028: `MatchScorer` nullable options dereference eliminated in bitrate/size scoring path.
- LI-029: scorer regression contracts now pin sparse-metadata conservatism and null-options score safety behavior.
- LI-030: LI compatibility gate now includes `MatchScorerTests` alongside strict auto-download and Library surfaces.
- LI-031: discoverability/recap/risk/memory surfaces synchronized for LI-027 through LI-030 outputs.
- LI-032: continuation checkpoint published with next-slice pointer for LI-033.
- LI-033: scorer continuation wave re-opened with risk intake focused on fallback-policy and format-normalization drift.
- LI-034: `MatchScorer` now normalizes candidate/allowed extensions consistently and applies optional MP3 fallback scoring when enabled.
- LI-035: `MatchScorerTests` now pin MP3 fallback uplift behavior and dotted/whitespace extension normalization contracts.
- LI-036: scorer-inclusive compatibility baseline refreshed and captured with updated strict+scorer and strict+Library+scorer totals.
- LI-037: discoverability/recap/risk/memory surfaces synchronized for LI-033 through LI-036 outputs.
- LI-038: continuation checkpoint published with next-slice pointer for LI-039.
- LI-039: strict filter policy now gates OnHold MP3-only behavior behind `EnableMp3Fallback` to keep fallback policy parity with discovery profile config.
- LI-040: `MatchScorer` bitrate transcode guard now normalizes extension-derived format values before FLAC low-bitrate rejection checks.
- LI-041: regression contracts now pin config-disabled OnHold fallback behavior and extension-derived FLAC transcode detection.
- LI-042: scorer-inclusive compatibility baseline refreshed and captured with strict+scorer 49/49 and strict+Library+scorer 130/130.
- LI-043: discoverability/recap/risk/memory surfaces synchronized for LI-039 through LI-042 outputs.
- LI-044: continuation checkpoint published with next-slice pointer for LI-045.
- LI-045: strict/scorer continuation risk intake identified remaining format-normalization parity gap in strict candidate filtering.
- LI-046: strict candidate filtering now normalizes candidate format/extension values before allowlist checks.
- LI-047: regression contracts now pin dotted candidate format normalization and whitespace-format extension fallback behavior.
- LI-048: LI compatibility gate contract reconfirmed to cover strict, scorer, and Library surfaces for parity validation.
- LI-049: validation baseline refreshed and captured with strict+scorer 51/51 and strict+Library+scorer 132/132.
- LI-050: continuation checkpoint published with next-slice pointer for LI-051.
- LI-051: strict/scorer parity hardened for malformed non-empty candidate format metadata by retrying allowlist checks with normalized filename extension fallback; validation baseline refreshed to strict+scorer 53/53 and strict+Library+scorer 134/134.
- LI-052: strict/scorer parity hardened for MIME-style candidate format metadata (`audio/x-flac; ...`) by shared normalization before allowlist checks, including extension-missing candidate coverage; validation baseline refreshed to strict+scorer 55/55 and strict+Library+scorer 136/136.
- LI-053: strict-policy continuation risk intake identified adapter-boundary drift risk when MIME-style preferred formats are passed unnormalized into adapter search constraints.
- LI-054: `AutoDownloadStrictFilterPolicy` normalization hardened for MIME-style values, metadata suffixes, and `x-` prefix compatibility.
- LI-055: regression coverage added in `AutoSearchServiceTests` to assert MIME-style per-track preferred formats are normalized before adapter-boundary search execution.
- LI-056: validation baseline refreshed to strict+scorer 56/56 and strict+Library+scorer 137/137; build and memory governance gates passed.
- LI-057: discoverability/recap/risk/memory surfaces synchronized for LI-053 through LI-056 outputs.
- LI-058: continuation checkpoint published with next-slice pointer rolled to LI-059.
- LI-059: strict/scorer continuation risk intake identified MIME subtype alias canonicalization drift (`audio/mpeg` => `mpeg`) and metadata-fragment leakage risk at strict preferred-format normalization boundaries.
- LI-060: strict policy, strict filtering, and scorer normalization hardened with canonical MIME alias mapping (`mpeg`=>`mp3`, `wave`/`vnd.wave`=>`wav`, `mp4`=>`m4a`) plus malformed metadata-fragment token hygiene.
- LI-061: regression coverage added in `SoulseekSearchHelperTests`, `MatchScorerTests`, and `AutoSearchServiceTests` for MIME alias canonicalization parity and metadata-fragment filtering at adapter boundaries.
- LI-062: validation baseline refreshed to strict+scorer 60/60 and strict+Library+scorer 141/141; build and memory governance gates passed.
- LI-063: discoverability/recap/risk/memory surfaces synchronized for LI-059 through LI-062 outputs.
- LI-064: continuation checkpoint published with next-slice pointer rolled to LI-065.
- LI-065: strict verification continuation risk intake identified prefetch format-policy drift from raw config allowlist usage and missing per-track strict policy parity.
- LI-066: `PrefetchVerifier` now resolves allowed formats through `AutoDownloadStrictFilterPolicy`, canonicalizes MIME aliases, and supports candidate-format fallback for extensionless staging paths.
- LI-067: regression coverage added in `PrefetchVerifierTests` to pin prefetch policy parity and extensionless staging fallback behavior.
- LI-068: validation baseline refreshed with focused prefetch tests PASS (2/2), strict+scorer PASS (60/60), strict+Library+scorer PASS (141/141), governance PASS, build PASS.
- LI-069: discoverability/recap/risk/memory surfaces synchronized for LI-065 through LI-068 outputs.
- LI-070: continuation checkpoint published with next-slice pointer rolled to LI-071.
- LI-071: strict diagnostic continuation risk intake identified candidate description drift for extensionless and MIME-alias format metadata.
- LI-072: `SoulseekSearchHelper.DescribeCandidate` now uses normalized extension-first rendering, canonicalized candidate-format fallback, and explicit `unknown` fallback semantics.
- LI-073: regression coverage added in `SoulseekSearchHelperTests` to pin MIME alias description labels and missing-format fallback behavior.
- LI-074: validation baseline refreshed with helper-focused PASS (21/21), strict+scorer PASS (62/62), strict+Library+scorer PASS (143/143), governance PASS, build PASS.
- LI-075: discoverability/recap/risk/memory surfaces synchronized for LI-071 through LI-074 outputs.
- LI-076: continuation checkpoint published with next-slice pointer rolled to LI-077.
- LI-077: scorer continuation risk intake identified malformed caller-supplied allowlist token drift that could suppress valid format scoring.
- LI-078: `MatchScorer` now filters malformed allowlist tokens and falls back to default lossless allowlist when configured tokens normalize to empty.
- LI-079: regression coverage added in `MatchScorerTests` to pin malformed allowlist fallback behavior.
- LI-080: validation baseline refreshed with scorer-focused PASS (17/17), strict+scorer PASS (63/63), strict+Library+scorer PASS (144/144), governance PASS, build PASS.
- LI-081: discoverability/recap/risk/memory surfaces synchronized for LI-077 through LI-080 outputs.
- LI-082: continuation checkpoint published with next-slice pointer rolled to LI-083.
- LI-083: adapter-bound strict-format continuation risk intake identified normalization drift in helper streaming paths.
- LI-084: `SoulseekSearchHelper` now normalizes and canonicalizes adapter-bound `allowedFormats` for candidate streaming and aligns filter-token format hygiene with strict/scorer normalization.
- LI-085: regression coverage added in `SoulseekSearchHelperTests` to pin adapter-bound MIME alias canonicalization, dotted-extension normalization, and malformed token suppression.
- LI-086: validation baseline refreshed with targeted adapter-bound contract PASS (1/1), helper-focused PASS (22/22), strict+scorer PASS (64/64), strict+Library+scorer PASS (145/145), governance PASS, build PASS.
- LI-087: discoverability/recap/risk/memory surfaces synchronized for LI-083 through LI-086 outputs.
- LI-088: continuation checkpoint published with next-slice pointer rolled to LI-089.
- LI-089: scorer continuation risk intake identified trusted-source reliability drift where repeated-source checks were case/whitespace sensitive.
- LI-090: `MatchScorer` trusted-source checks now use trim-safe case-insensitive matching against repeated-source entries.
- LI-091: regression coverage added in `MatchScorerTests` to pin trusted-source normalization behavior.
- LI-092: validation baseline refreshed with scorer-focused PASS (18/18), strict+scorer PASS (65/65), strict+Library+scorer PASS (146/146), governance PASS, build PASS.
- LI-093: discoverability/recap/risk/memory surfaces synchronized for LI-089 through LI-092 outputs.
- LI-094: continuation checkpoint published with next-slice pointer rolled to LI-095.
- LI-095: helper continuation risk intake identified malformed caller allowlist drift in helper candidate filtering.
- LI-096: `SoulseekSearchHelper.FilterCandidates` now filters malformed allowlist tokens and falls back to default lossless allowlist when effective configured tokens normalize to empty.
- LI-097: regression coverage added in `SoulseekSearchHelperTests` to pin malformed configured allowlist fallback behavior.
- LI-098: validation baseline refreshed with helper-focused PASS (23/23), strict+scorer PASS (66/66), strict+Library+scorer PASS (147/147), governance PASS, build PASS.
- LI-099: discoverability/recap/risk/memory surfaces synchronized for LI-095 through LI-098 outputs.
- LI-100: continuation checkpoint published with next-slice pointer rolled to LI-101.
- LI-101: strict/helper/scorer continuation risk intake identified comma-delimited MIME metadata normalization drift for extensionless candidates.
- LI-102: `AutoDownloadStrictFilterPolicy`, `SoulseekSearchHelper`, and `MatchScorer` normalization now trims metadata at first semicolon/comma separator to preserve parity.
- LI-103: regression coverage added in `SoulseekSearchHelperTests` and `MatchScorerTests` for comma-delimited MIME alias canonicalization without extension fallback.
- LI-104: validation baseline refreshed with helper-focused PASS (24/24), scorer-focused PASS (19/19), strict+scorer PASS (68/68), strict+Library+scorer PASS (149/149), governance PASS, build PASS.
- LI-105: discoverability/recap/risk/memory surfaces synchronized for LI-101 through LI-104 outputs.
- LI-106: continuation checkpoint published with next-slice pointer rolled to LI-107.
- LI-107: strict/helper/scorer continuation risk intake identified whitespace-delimited MIME metadata normalization drift for extensionless candidates and preferred-format resolution.
- LI-108: `AutoDownloadStrictFilterPolicy`, `SoulseekSearchHelper`, and `MatchScorer` normalization now trims metadata at first whitespace separator in addition to semicolon/comma handling.
- LI-109: regression coverage added in `AutoSearchServiceTests`, `SoulseekSearchHelperTests`, and `MatchScorerTests` for whitespace-delimited MIME canonicalization behavior.
- LI-110: validation baseline refreshed with helper-focused PASS (25/25), scorer-focused PASS (20/20), strict+scorer PASS (71/71), strict+Library+scorer PASS (152/152), governance PASS, build PASS.
- LI-111: discoverability/recap/risk/memory surfaces synchronized for LI-107 through LI-110 outputs.
- LI-112: continuation checkpoint published with next-slice pointer rolled to LI-113.
- LI-113: strict/helper/scorer continuation risk intake identified quoted MIME metadata normalization drift for adapter-bound preferred formats and extensionless candidates.
- LI-114: `AutoDownloadStrictFilterPolicy`, `SoulseekSearchHelper`, and `MatchScorer` normalization now trims quote wrappers around MIME metadata before and after metadata/subtype extraction.
- LI-115: regression coverage added in `AutoSearchServiceTests`, `SoulseekSearchHelperTests`, and `MatchScorerTests` for quoted MIME canonicalization behavior.
- LI-116: validation baseline refreshed with helper-focused PASS (26/26), scorer-focused PASS (20/20), strict+scorer PASS (72/72), strict+Library+scorer PASS (153/153), governance PASS, build PASS.
- LI-117: discoverability/recap/risk/memory surfaces synchronized for LI-113 through LI-116 outputs.
- LI-118: continuation checkpoint published with next-slice pointer rolled to LI-119.
- LI-119: wrapper-delimited MIME normalization parity hardened in strict policy, helper, and scorer paths (trim wrappers `[]`, `()`, `{}`, `<>`, quotes) with refreshed adapter-bound/helper/scorer regression coverage.
- LI-120: governance synchronization completed and queue-120 closure checkpoint published.

## Next Planned Phase
- LI-001 through LI-120 queue is complete for the current driver revision.
- Next action: extend the LI queue with a new backlog revision before further continuation slices.

## Primary Code Surfaces
- Services related to library scanning, section vectors, and track matching.
- Data entities supporting library health and scoring.
- Tests covering scanner contracts and section vector behavior.

## Scanner Contract Focus
- [Services/LibraryFolderScannerService.cs](../../Services/LibraryFolderScannerService.cs)
- [Tests/SLSKDONET.Tests/Services/LibraryFolderScannerServiceTests.cs](../../Tests/SLSKDONET.Tests/Services/LibraryFolderScannerServiceTests.cs)

## Scanner De-duplication and Retry Focus
- [Services/LibraryFolderScannerService.cs](../../Services/LibraryFolderScannerService.cs)
- [Tests/SLSKDONET.Tests/Services/LibraryFolderScannerServiceTests.cs](../../Tests/SLSKDONET.Tests/Services/LibraryFolderScannerServiceTests.cs)

## Section Vector Contract Focus
- [Services/Similarity/SectionVectorService.cs](../../Services/Similarity/SectionVectorService.cs)
- [Tests/SLSKDONET.Tests/Analysis/SectionVectorServiceTests.cs](../../Tests/SLSKDONET.Tests/Analysis/SectionVectorServiceTests.cs)

## Matching Scoring Contract Focus
- [Configuration/ScoringConstants.cs](../../Configuration/ScoringConstants.cs)
- [Services/Similarity/TrackSimilarityService.cs](../../Services/Similarity/TrackSimilarityService.cs)
- [Services/Similarity/TrackMatchScorer.cs](../../Services/Similarity/TrackMatchScorer.cs)
- [Tests/SLSKDONET.Tests/Analysis/MatchingScoringConstantsTests.cs](../../Tests/SLSKDONET.Tests/Analysis/MatchingScoringConstantsTests.cs)

## Library Health Cadence Contract Focus
- [Services/DashboardService.cs](../../Services/DashboardService.cs)
- [Services/MissionControlService.cs](../../Services/MissionControlService.cs)
- [Services/Repositories/TrackRepository.cs](../../Services/Repositories/TrackRepository.cs)
- [Tests/SLSKDONET.Tests/Services/DashboardServiceLibraryHealthCadenceTests.cs](../../Tests/SLSKDONET.Tests/Services/DashboardServiceLibraryHealthCadenceTests.cs)

## Sparse Metadata Candidate Contract Focus
- [ViewModels/Library/SuggestNextCandidateViewModel.cs](../../ViewModels/Library/SuggestNextCandidateViewModel.cs)
- [ViewModels/Library/PlaylistUpgradeCandidateViewModel.cs](../../ViewModels/Library/PlaylistUpgradeCandidateViewModel.cs)
- [Tests/SLSKDONET.Tests/ViewModels/PlaylistIntelligenceSparseMetadataTests.cs](../../Tests/SLSKDONET.Tests/ViewModels/PlaylistIntelligenceSparseMetadataTests.cs)

## Queue Coherence Contract Focus
- [Services/AnalysisQueueService.cs](../../Services/AnalysisQueueService.cs)
- [Tests/SLSKDONET.Tests/Services/AnalysisQueueTests.cs](../../Tests/SLSKDONET.Tests/Services/AnalysisQueueTests.cs)

## Auto-Download Intelligence Compatibility Focus
- [Services/AutoDownload/AutoSearchService.cs](../../Services/AutoDownload/AutoSearchService.cs)
- [Tests/SLSKDONET.Tests/Services/AutoDownload/AutoSearchServiceTests.cs](../../Tests/SLSKDONET.Tests/Services/AutoDownload/AutoSearchServiceTests.cs)
- [Tests/SLSKDONET.Tests/Services/DownloadManagerStrictModeGateTests.cs](../../Tests/SLSKDONET.Tests/Services/DownloadManagerStrictModeGateTests.cs)

## Risk Register
- [DOCS/recaps/LIBRARY_INTELLIGENCE_RISK_REGISTER.md](../recaps/LIBRARY_INTELLIGENCE_RISK_REGISTER.md)

## Validation Contract
- dotnet build ORBIT-Pure.sln -v minimal
- dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj --filter "FullyQualifiedName~AutoSearchServiceTests|FullyQualifiedName~SoulseekSearchHelperTests|FullyQualifiedName~DownloadManagerStrictModeGateTests"
- dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj --filter "FullyQualifiedName~AutoSearchServiceTests|FullyQualifiedName~SoulseekSearchHelperTests|FullyQualifiedName~DownloadManagerStrictModeGateTests|FullyQualifiedName~MatchScorerTests|FullyQualifiedName~Library"
- dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj --filter "FullyQualifiedName~Library"

## Recap Link
- DOCS/recaps/LIBRARY_INTELLIGENCE_RECAP_PACK.md
