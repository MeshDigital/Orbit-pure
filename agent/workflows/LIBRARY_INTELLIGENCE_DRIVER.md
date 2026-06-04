# Library Intelligence Driver

Status: Active
Owner: Autonomous Agent
Last Updated: 2026-06-04

## Audit Snapshot
- Queue artifact coverage verified: LI-001 through LI-120 queue files are present under `.agent/queues/`.
- Current execution pointer: LI-113.
- No missing LI queue artifacts detected during driver audit.

## Scope
Library intelligence hardening for scan alignment, section vectors, matching quality, and resilient metadata inference.

## Slice Queue (120 Items)
1. LI-001: Validate scanner service contract and edge-path normalization.
2. LI-002: Harden section vector service defaults and null tolerance.
3. LI-003: Reconcile scoring constants usage across matching pipelines.
4. LI-004: Improve library health entity update cadence and drift detection.
5. LI-005: Add tests for sparse metadata tracks in intelligence path.
6. LI-006: Tighten folder scanner de-duplication and retry semantics.
7. LI-007: Ensure queue processing keeps intelligence state coherent.
8. LI-008: Capture discoverability updates for intelligence contracts.
9. LI-009: Validate auto-download compatibility with intelligence enrichments.
10. LI-010: Produce lane closure checkpoint with pending risk register.
11. LI-011: Extend lane queue for post-closure continuation.
12. LI-012: Pin strict-mode compatibility contract for per-track overrides.
13. LI-013: Synchronize discoverability/recap for LI-009 and LI-010 outputs.
14. LI-014: Publish continuation checkpoint and roll next-slice pointer.
15. LI-015: Centralize strict auto-download filter policy resolution.
16. LI-016: Add duration-unit normalization guard for strict duration filtering.
17. LI-017: Pin parity and duration normalization contracts in regression tests.
18. LI-018: Reconcile risk register after strict-filter parity hardening.
19. LI-019: Synchronize discoverability/recap/memory for LI-015..LI-018 outputs.
20. LI-020: Publish continuation checkpoint and roll next-slice pointer.
21. LI-021: Enforce sparse-metadata acceptance floor against lowered strict thresholds.
22. LI-022: Pin sparse-metadata acceptance contracts in `AutoSearchServiceTests`.
23. LI-023: Add compatibility gate task combining strict and Library contract surfaces.
24. LI-024: Execute compatibility validation baseline and capture pass evidence.
25. LI-025: Reconcile risk register and recap/discoverability surfaces for closed risks.
26. LI-026: Publish continuation checkpoint and roll next-slice pointer.
27. LI-027: Re-open risk intake and map scorer hardening targets.
28. LI-028: Eliminate nullable scorer dereference risk in bitrate/size path.
29. LI-029: Pin sparse-metadata scorer contracts and null-options safety behavior.
30. LI-030: Extend LI compatibility gate to include scorer contract coverage.
31. LI-031: Synchronize discoverability/recap/risk/memory surfaces for LI-027..LI-030.
32. LI-032: Publish continuation checkpoint and roll next-slice pointer.
33. LI-033: Re-open scorer risk intake for fallback-policy and format-normalization drift.
34. LI-034: Harden `MatchScorer` format normalization and optional MP3 fallback handling.
35. LI-035: Pin scorer fallback and extension-normalization contracts in regression tests.
36. LI-036: Execute scorer-inclusive compatibility baseline and capture updated pass evidence.
37. LI-037: Synchronize discoverability/recap/risk/memory surfaces for LI-033..LI-036.
38. LI-038: Publish continuation checkpoint and roll next-slice pointer.
39. LI-039: Enforce strict-policy parity so OnHold MP3 mode is gated by config fallback policy.
40. LI-040: Harden `MatchScorer` bitrate transcode guard to use normalized extension-derived formats.
41. LI-041: Pin fallback-parity and extension-derived bitrate guard contracts in regression tests.
42. LI-042: Execute scorer-inclusive compatibility baseline and capture updated pass evidence.
43. LI-043: Synchronize discoverability/recap/risk/memory surfaces for LI-039..LI-042 outputs.
44. LI-044: Publish continuation checkpoint and roll next-slice pointer.
45. LI-045: Re-open strict/scorer parity risk intake for post-LI-044 continuation.
46. LI-046: Harden strict scoring-policy parity surfaces identified by LI-045 risk intake.
47. LI-047: Pin strict/scorer parity contracts in regression tests.
48. LI-048: Reconcile compatibility gate coverage for newly pinned parity contracts.
49. LI-049: Execute validation baseline refresh and capture updated evidence.
50. LI-050: Synchronize governance artifacts and publish continuation checkpoint.
51. LI-051: Harden strict/scorer parity for malformed candidate format metadata via extension fallback and refresh compatibility baseline.
52. LI-052: Harden strict/scorer parity for MIME-style candidate format metadata normalization and refresh compatibility baseline.
53. LI-053: Re-open strict-policy normalization risk intake for adapter-boundary format drift.
54. LI-054: Harden strict filter policy normalization for MIME-style formats and metadata suffixes.
55. LI-055: Pin adapter-boundary regression contracts for MIME-style per-track preferred formats.
56. LI-056: Execute validation baseline refresh and capture updated evidence.
57. LI-057: Synchronize governance artifacts for LI-053..LI-056 outputs.
58. LI-058: Publish continuation checkpoint and roll next-slice pointer.
59. LI-059: Re-open strict/scorer normalization risk intake for MIME subtype alias canonicalization and metadata-fragment drift.
60. LI-060: Harden strict/scorer/policy normalization for MIME alias canonicalization and malformed metadata-fragment token hygiene.
61. LI-061: Pin strict/scorer/adapter-boundary regression contracts for MIME alias canonicalization and metadata-fragment filtering.
62. LI-062: Execute validation baseline refresh and capture updated evidence.
63. LI-063: Synchronize governance artifacts for LI-059..LI-062 outputs.
64. LI-064: Publish continuation checkpoint and roll next-slice pointer.
65. LI-065: Re-open strict verification risk intake for prefetch format-policy drift.
66. LI-066: Harden prefetch verification format gate for strict policy parity.
67. LI-067: Pin prefetch verification regression contracts for policy parity and extensionless staging fallback.
68. LI-068: Execute validation baseline refresh and capture updated evidence.
69. LI-069: Synchronize governance artifacts for LI-065..LI-068 outputs.
70. LI-070: Publish continuation checkpoint and roll next-slice pointer.
71. LI-071: Re-open strict diagnostic risk intake for candidate format-description drift.
72. LI-072: Harden candidate diagnostics format rendering for strict normalization parity.
73. LI-073: Pin strict diagnostic regression contracts for MIME alias and unknown-format fallback behavior.
74. LI-074: Execute validation baseline refresh and capture updated evidence.
75. LI-075: Synchronize governance artifacts for LI-071..LI-074 outputs.
76. LI-076: Publish continuation checkpoint and roll next-slice pointer.
77. LI-077: Re-open scorer allowlist risk intake for malformed configured extension tokens.
78. LI-078: Harden scorer allowlist normalization with malformed-token hygiene and safe default fallback.
79. LI-079: Pin scorer regression contracts for malformed allowlist fallback behavior.
80. LI-080: Execute validation baseline refresh and capture updated evidence.
81. LI-081: Synchronize governance artifacts for LI-077..LI-080 outputs.
82. LI-082: Publish continuation checkpoint and roll next-slice pointer.
83. LI-083: Re-open adapter-bound strict-format normalization risk intake for streaming-path parity.
84. LI-084: Harden `SoulseekSearchHelper` adapter-bound allowed-format normalization and filter-token hygiene parity.
85. LI-085: Pin helper regression contracts for adapter-bound MIME alias canonicalization and malformed token filtering.
86. LI-086: Execute validation baseline refresh and capture updated evidence.
87. LI-087: Synchronize governance artifacts for LI-083..LI-086 outputs.
88. LI-088: Publish continuation checkpoint and roll next-slice pointer.
89. LI-089: Re-open scorer reliability risk intake for trusted-source matching drift.
90. LI-090: Harden `MatchScorer` repeated-source matching with case-insensitive and trim-safe normalization.
91. LI-091: Pin scorer regression contracts for trusted-source normalization behavior.
92. LI-092: Execute validation baseline refresh and capture updated evidence.
93. LI-093: Synchronize governance artifacts for LI-089..LI-092 outputs.
94. LI-094: Publish continuation checkpoint and roll next-slice pointer.
95. LI-095: Re-open helper strict/scorer parity risk intake for malformed caller allowlist drift in candidate filtering.
96. LI-096: Harden `SoulseekSearchHelper.FilterCandidates` malformed-token allowlist hygiene and default lossless fallback semantics.
97. LI-097: Pin helper regression contract for malformed allowlist fallback behavior.
98. LI-098: Execute helper/strict/scorer/library validation baseline refresh and capture evidence.
99. LI-099: Synchronize governance artifacts for LI-095..LI-098 outputs.
100. LI-100: Publish continuation checkpoint and roll next-slice pointer.
101. LI-101: Re-open strict/helper/scorer normalization parity risk intake for comma-delimited MIME metadata drift.
102. LI-102: Harden strict policy, helper, and scorer normalization to trim metadata at first semicolon/comma separator.
103. LI-103: Pin helper and scorer regression contracts for extensionless comma-delimited MIME metadata alias canonicalization.
104. LI-104: Execute validation baseline refresh and capture helper/scorer/strict compatibility evidence.
105. LI-105: Synchronize governance artifacts for LI-101..LI-104 outputs.
106. LI-106: Publish continuation checkpoint and roll next-slice pointer.
107. LI-107: Re-open strict/helper/scorer normalization parity risk intake for whitespace-delimited MIME metadata drift.
108. LI-108: Harden strict policy, helper, and scorer normalization to trim whitespace-delimited metadata fragments.
109. LI-109: Pin adapter-bound strict-policy, helper, and scorer regression contracts for whitespace-delimited MIME canonicalization.
110. LI-110: Execute validation baseline refresh and capture helper/scorer/strict compatibility evidence.
111. LI-111: Synchronize governance artifacts for LI-107..LI-110 outputs.
112. LI-112: Publish continuation checkpoint and roll next-slice pointer.
113. LI-113: Re-open strict/helper/scorer normalization parity risk intake for quoted MIME metadata drift.
114. LI-114: Harden strict policy, helper, and scorer normalization for quoted MIME token canonicalization.
115. LI-115: Pin strict adapter-bound, helper, and scorer regression contracts for quoted MIME canonicalization parity.
116. LI-116: Execute validation baseline refresh and capture helper/scorer/strict compatibility evidence.
117. LI-117: Synchronize governance artifacts for LI-113..LI-116 outputs.
118. LI-118: Publish continuation checkpoint and roll next-slice pointer.
119. LI-119: Harden strict/helper/scorer normalization parity for wrapper-delimited MIME metadata and refresh validation baseline.
120. LI-120: Synchronize governance artifacts and publish queue-120 closure checkpoint.

## Execution Loop
1. Create slice artifact in .agent/queues/ using the slice template.
2. Integrate discoverability updates into DOCUMENTATION_INDEX.md and lane discoverability docs.
3. Implement code changes needed by the slice.
4. Run lane gates and capture pass/fail output.
5. Generate lane recap pack delta.
6. Append memory deltas in .agent/memory and repository memory notes.
7. Mark slice status and continue to next item.

## Gate Commands
- dotnet build ORBIT-Pure.sln -v minimal
- dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj --filter "FullyQualifiedName~MatchScorerTests|FullyQualifiedName~AutoSearchServiceTests|FullyQualifiedName~SoulseekSearchHelperTests|FullyQualifiedName~DownloadManagerStrictModeGateTests"
- dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj --filter "FullyQualifiedName~AutoSearchServiceTests|FullyQualifiedName~SoulseekSearchHelperTests|FullyQualifiedName~DownloadManagerStrictModeGateTests|FullyQualifiedName~MatchScorerTests|FullyQualifiedName~Library"
- dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj --filter "FullyQualifiedName~Library"

## Discoverability Targets
- DOCUMENTATION_INDEX.md
- DOCS/discoverability/LIBRARY_INTELLIGENCE_DISCOVERABILITY.md
- DOCS/recaps/LIBRARY_INTELLIGENCE_RECAP_PACK.md

## Memory Targets
- .agent/memory/LIBRARY_INTELLIGENCE_MEMORY.md
- /memories/repo/library-intelligence-slice2-progress.md
- /memories/repo/library-scan-alignment.md
- DOCS/memory/library_waveform_automix_plan.md

## Recap Targets
- agent/recaps/LIBRARY_INTELLIGENCE_RECAP_2026-05-31.md
- DOCS/recaps/LIBRARY_INTELLIGENCE_RECAP_PACK.md
