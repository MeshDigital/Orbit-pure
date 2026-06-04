# Library Intelligence Risk Register

Status: Active
Last Updated: 2026-06-04

## Purpose
Track pending technical risks after LI-010 closure checkpoint and define mitigation direction for the continuation wave.

## Open Risks

- None.

## Closed in LI-119

1. Strict/helper/scorer normalization parity drift for wrapper-delimited MIME metadata
- Outcome: strict policy, helper filtering, and scorer normalization now trim wrapper delimiters (`[]`, `()`, `{}`, `<>`, quotes`) before allowlist evaluation.

2. Adapter-bound and extensionless acceptance drift for wrapper-delimited MIME alias inputs
- Outcome: normalization parity now canonicalizes wrapper-delimited values like `["audio/mpeg"]` consistently across strict/scorer/helper paths.

3. Regression gap for wrapper-delimited MIME canonicalization behavior
- Outcome: targeted regression coverage refreshed in `AutoSearchServiceTests`, `SoulseekSearchHelperTests`, and `MatchScorerTests` for wrapper-delimited adapter-bound and extensionless scenarios.

## Closed in LI-113..LI-115

1. Strict/helper/scorer normalization parity drift for quoted MIME metadata
- Outcome: strict policy, helper filtering, and scorer normalization now trim quote wrappers before allowlist evaluation.

2. Adapter-bound and extensionless acceptance drift for quoted MIME alias inputs
- Outcome: normalization parity now canonicalizes quoted `"audio/mpeg"` style values consistently across strict/scorer/helper paths.

3. Regression gap for quoted MIME canonicalization behavior
- Outcome: targeted regression coverage added in `AutoSearchServiceTests`, `SoulseekSearchHelperTests`, and `MatchScorerTests` for quoted MIME adapter-bound and extensionless scenarios.

## Closed in LI-107..LI-109

1. Strict/helper/scorer normalization parity drift for whitespace-delimited MIME metadata
- Outcome: strict policy, helper filtering, and scorer normalization now trim metadata at first whitespace delimiter before allowlist evaluation.

2. Extensionless candidate acceptance drift for whitespace-delimited MIME alias inputs
- Outcome: normalization parity now canonicalizes whitespace-delimited `audio/mpeg codecs=...` style values consistently across strict/scorer/helper paths.

3. Regression gap for whitespace-delimited MIME canonicalization behavior
- Outcome: targeted regression coverage added in `AutoSearchServiceTests`, `SoulseekSearchHelperTests`, and `MatchScorerTests` for adapter-bound and extensionless whitespace-delimited scenarios.

## Closed in LI-101..LI-103

1. Strict/helper/scorer normalization parity drift for comma-delimited MIME metadata
- Outcome: strict policy, helper filtering, and scorer normalization now trim metadata at first `;` or `,` separator before allowlist evaluation.

2. Extensionless candidate acceptance drift for comma-delimited MIME alias inputs
- Outcome: normalization parity now canonicalizes comma-delimited `audio/mpeg,codecs=...` style values consistently across strict/scorer/helper paths.

3. Regression gap for comma-delimited MIME alias canonicalization behavior
- Outcome: targeted regression coverage added in `SoulseekSearchHelperTests` and `MatchScorerTests` for extensionless comma-delimited MIME alias scenarios.

## Closed in LI-095..LI-097

1. Helper allowlist hygiene drift from malformed caller-supplied format tokens
- Outcome: `SoulseekSearchHelper.FilterCandidates` now filters malformed configured allowlist tokens before allowlist checks.

2. Helper fail-closed behavior when configured allowlist normalizes to empty
- Outcome: helper candidate filtering now falls back to default lossless allowlist semantics when effective configured tokens normalize to empty.

3. Regression gap for helper malformed-allowlist fallback behavior
- Outcome: targeted regression coverage added in `SoulseekSearchHelperTests` for malformed allowlist fallback behavior.

## Closed in LI-090..LI-091

1. Trusted-source reliability drift from case-sensitive repeated-source matching
- Outcome: `MatchScorer.ScoreReliability` now compares repeated-source usernames using case-insensitive matching.

2. Trusted-source reliability drift from whitespace-sensitive repeated-source matching
- Outcome: repeated-source and candidate usernames are now trim-normalized before comparison.

3. Regression gap for trusted-source normalization behavior
- Outcome: targeted regression coverage added in `MatchScorerTests` for trim/case-insensitive repeated-source matching.

## Closed in LI-084..LI-085

1. Adapter-bound allowed-format normalization drift in helper candidate streaming
- Outcome: `SoulseekSearchHelper.SearchCandidatesAsync` now normalizes, canonicalizes, and malformed-token-filters adapter-bound allowed formats before stream execution.

2. Filter-token emission hygiene drift for non-canonical format values
- Outcome: helper filter-token construction now uses strict-style normalization and malformed-token filtering before ext-token emission.

3. Regression gap for adapter-bound normalization parity
- Outcome: targeted regression coverage added in `SoulseekSearchHelperTests` to pin adapter-bound MIME alias canonicalization and malformed token suppression behavior.

## Closed in LI-078..LI-079

1. Scorer allowlist drift from malformed caller-supplied format tokens
- Outcome: `MatchScorer` now filters malformed allowlist tokens during normalization and preserves bounded scoring behavior.

2. Empty effective allowlist after hygiene filtering
- Outcome: scorer format gating now falls back to default lossless allowlist semantics when supplied tokens normalize to empty.

3. Regression gap for malformed scorer allowlist behavior
- Outcome: targeted regression coverage added in `MatchScorerTests` for malformed allowlist fallback behavior.

## Closed in LI-072..LI-073

1. Candidate diagnostics format-description drift for extensionless MIME metadata
- Outcome: `SoulseekSearchHelper.DescribeCandidate` now resolves canonical format labels using normalized extension-first plus normalized candidate-format fallback semantics.

2. Missing fallback semantics for unusable candidate format metadata
- Outcome: diagnostics now emit explicit `unknown` format labels when neither extension nor format metadata can be normalized.

3. Regression gap for strict diagnostic format rendering behavior
- Outcome: targeted regression coverage added in `SoulseekSearchHelperTests` for MIME alias description labels and unknown-format fallback behavior.

## Closed in LI-066..LI-067

1. Prefetch format-policy drift from strict allowlist resolution
- Outcome: `PrefetchVerifier` now resolves allowed formats through `AutoDownloadStrictFilterPolicy` to keep verification behavior aligned with strict search/filter policy and per-track overrides.

2. Extension-only prefetch format checks for extensionless staging paths
- Outcome: prefetch verification now falls back to canonicalized candidate format metadata when staged file extension is unavailable.

3. Regression gap for prefetch strict-policy parity behavior
- Outcome: targeted regression coverage added in `PrefetchVerifierTests` for per-track MIME alias policy handling and extensionless staging fallback behavior.

## Closed in LI-060..LI-061

1. MIME subtype alias canonicalization drift in strict/scorer normalization paths
- Outcome: strict policy, strict candidate filtering, and scorer normalization now canonicalize common MIME subtype aliases (for example `audio/mpeg` => `mp3`) before allowlist evaluation.

2. Metadata-parameter fragment leakage in preferred-format parsing
- Outcome: strict preferred-format resolution now drops malformed metadata fragments (for example `codecs=...`) that can emerge from comma-split MIME parameter values.

3. Regression gap for alias canonicalization and token hygiene behavior
- Outcome: targeted regression coverage added in `SoulseekSearchHelperTests`, `MatchScorerTests`, and `AutoSearchServiceTests` for alias mapping and fragment filtering at strict/scorer/adapter boundaries.

## Closed in LI-054..LI-055

1. Strict-policy MIME-format normalization drift at adapter boundary
- Outcome: `AutoDownloadStrictFilterPolicy` now normalizes MIME-style preferred format values (including metadata suffixes and `x-` prefixes) before adapter-bound strict search execution.

2. Regression gap for normalized strict allowed-format propagation
- Outcome: targeted regression coverage added in `AutoSearchServiceTests` to pin adapter-boundary normalized format behavior.

## Closed in LI-052

1. Strict/scorer MIME-format normalization drift for extension-missing candidates
- Outcome: strict candidate filtering and scorer format gates now normalize MIME-style candidate format metadata before allowlist evaluation, preserving parity when filename extension fallback is unavailable.

2. Regression gap for MIME-style format handling contracts
- Outcome: targeted regression coverage added in `SoulseekSearchHelperTests` and `MatchScorerTests` for MIME-style format normalization.

## Closed in LI-051

1. Strict/scorer allowlist parity drift for malformed non-empty candidate format metadata
- Outcome: strict candidate filtering and scorer format gates now retry allowlist matching with normalized filename extension fallback when candidate format metadata is present but unrecognized.

2. Regression gap for malformed non-empty format metadata fallback behavior
- Outcome: targeted regression coverage added in `SoulseekSearchHelperTests` and `MatchScorerTests`.

## Closed in LI-039..LI-042

1. OnHold fallback-policy parity drift between strict mode and discovery profile gates
- Outcome: strict OnHold MP3-only resolution now honors `EnableMp3Fallback` before forcing MP3 mode.

2. Extension-derived FLAC transcode detection drift in scorer bitrate guard
- Outcome: `MatchScorer` now normalizes format values and uses normalized filename extension fallback before FLAC low-bitrate rejection checks.

3. Regression gap for config-disabled OnHold fallback and extension-derived bitrate guard behavior
- Outcome: targeted regression coverage added in `SoulseekSearchHelperTests` and `MatchScorerTests`.

## Closed in LI-033..LI-036

1. Scorer fallback-policy drift (`AllowMp3Fallback` not honored)
- Outcome: `MatchScorer` now applies a bounded fallback format score for MP3 candidates when fallback is explicitly enabled.

2. Scorer format-normalization drift for dotted/whitespace extensions
- Outcome: `MatchScorer` now normalizes candidate and allowed format values before allowlist evaluation.

3. Regression gap for fallback and normalization contracts
- Outcome: `MatchScorerTests` now pin MP3 fallback uplift behavior and dotted/whitespace extension normalization behavior.

## Closed in LI-027..LI-030

1. Nullable scorer dereference in bitrate/size scoring path
- Outcome: `MatchScorer` now null-safely resolves options-backed file-size minimums, removing nullable dereference risk.

2. Sparse metadata scorer confidence drift
- Outcome: scorer regression contracts now pin conservative sparse-target scoring relative to complete-metadata tracks.

3. Compatibility gate omission for scorer contracts
- Outcome: LI compatibility gate now executes `MatchScorerTests` with strict auto-download + Library surfaces.

## Closed in LI-021..LI-024

1. Sparse metadata defaults masking quality regressions
- Outcome: sparse metadata tracks now apply strict confidence adjustments and a minimum acceptance floor to prevent lowered thresholds from auto-accepting low-signal matches.

2. Test-surface fragmentation across lanes
- Outcome: compatibility gate tasks now run strict auto-download and Library tests together, and baseline execution is captured in lane validation evidence.

## Closed in LI-015..LI-017

1. Strict-query and candidate-filter parity drift
- Outcome: query-time and candidate-time strict filter resolution now share `AutoDownloadStrictFilterPolicy`.

2. Duration-unit mismatch from upstream metadata sources
- Outcome: strict duration filtering now normalizes seconds-scale `CanonicalDuration` values safely before tolerance checks.

## Closed in LI-009

1. Per-track preferred format compatibility
- Outcome: candidate filtering now honors PlaylistTrack.PreferredFormats.

2. Zero/invalid bitrate override compatibility
- Outcome: strict-mode bitrate resolution now falls back safely to configured defaults when override <= 0.

## Validation Baseline

- dotnet build ORBIT-Pure.sln -v minimal: PASS
- dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj --filter "FullyQualifiedName~SoulseekSearchHelperTests": PASS (26/26)
- dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj --filter "FullyQualifiedName~PrefetchVerifierTests": PASS (2/2)
- dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj --filter "FullyQualifiedName~MatchScorerTests": PASS (21/21)
- dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj --filter "FullyQualifiedName~MatchScorerTests|FullyQualifiedName~AutoSearchServiceTests|FullyQualifiedName~SoulseekSearchHelperTests|FullyQualifiedName~DownloadManagerStrictModeGateTests": PASS (73/73)
- dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj --filter "FullyQualifiedName~AutoSearchServiceTests|FullyQualifiedName~SoulseekSearchHelperTests|FullyQualifiedName~DownloadManagerStrictModeGateTests|FullyQualifiedName~MatchScorerTests|FullyQualifiedName~Library": PASS (154/154)
- dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj --filter "FullyQualifiedName~Library": PASS (81/81)
- pwsh -NoProfile -File Tools/validate-memory-governance.ps1: PASS
