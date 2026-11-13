# Genre accuracy plan: DnB / Dubstep / Techno / House (2026-11-12)

**Status:** planned. Epic: #245. Child issues GA1–GA13 are listed under [Work items](#work-items).
**Verified against:** `master` @ `6b8a708`. If line numbers have drifted, find the code by symbol name.

## Goal

When ORBIT labels a track **Drum & Bass, Dubstep/140, Techno or House**, the label should be right.
When it is not sure, it should say so. A wrong confident label is worse than "Other / unsure",
because ORBIT uses genre to pick the BPM bracket, phrase preset, drop signal weights and cue strategy
(`GenreFamilyClassifier` → `IGenreFamilyAnalysisStrategy`). A DnB track filed as Techno gets the wrong
cues.

The architecture has four layers, applied in this order:

1. **Metadata first.** Trusted tags (user, Beatport, Discogs, clean file tags) beat audio guesses.
2. **Audio model.** The Discogs-EffNet 400-style output, which ORBIT already computes, actually feeds the vote.
3. **Rhythm & tempo gate.** Deterministic evidence (4-on-the-floor kick, backbeat snare or half-time
   snare, octave-aware BPM) corrects the classic confusions between scenes.
4. **Personal head.** A small classifier trained on the user's own corrected genres, blended in only
   when it measurably helps.

Everything is measured on a ground-truth benchmark (GA3) **before and after** each change.

---

## Where we are today (verified)

The genre pipeline lives in `Services/AudioAnalysis/AudioAnalysisService.cs`:

| Step | Code | What actually happens |
|---|---|---|
| ONNX embedding | `DiscogsEffnetEmbeddingExtractor.RunInference` | Whole track → non-overlapping 128-frame mel patches (2.048 s each, `EffnetMelSpectrogramExtractor.ExtractPatches`) → **one** batched DirectML run → `embeddings` [n,1280] + `activations` [n,400] → **plain mean over all patches** (`MeanAcrossPatches`). |
| Discogs-400 styles | `AudioAnalysisService.cs:352-362` | The 400 style scores and `TopStyle` are **persisted** (`StyleScoresBlob`, `TopStyle`, #213) and shown in the Track Inspector with "Use as genre". They are **never fed into genre fusion.** |
| Jamendo genre head | `EffnetClassifierHeadService` (87 classes) | The **only live audio genre vote** (`"onnx-jamendo"`, weight 0.85). It is coarse: `drumnbass`, `dubstep`, `techno`, `house`, `deephouse`, `minimal`, `breakbeat`… |
| Essentia high-level | `InferAndApplyGenreAsync` "1) Explicit model heads" (`:740-780`) | **Dead.** `Data/Essentia/profile.yaml` has `svm_models: []`, and the bundled binary returns an empty `highlevel` section. The Musicnn/Jamendo/Discogs400 `ExtensionData` readers never fire. |
| Style-centroid prior | `InferGenreFromStyleCentroidsAsync` (`:1013`) | **Dead.** It reads `DeepTextureEmbedding ?? VectorEmbedding` (`:1017`), not the EffNet `Embedding` that analysis fills, and `style_definitions` rows are only written by `StyleLabPersistenceVerifier`. |
| BPM heuristic | `InferGenreHeuristic` (`:963`) | Only used when there are no candidates at all. |
| Guardrails | `ApplyBpmGuardrails` (`:1060`) | Small ± nudges for DnB/House/Techno by BPM range. No Dubstep rule, and it is not octave-aware. |
| Family | `GenreFamilyClassifier.Classify(winner, bpm, conf, ModelInferred)` (`:865`) | Has its own tempo override: a model label like Techno at 160–190 BPM becomes Breakbeat. |
| Cue/phrase consumers | `AnalysisPipelineResultBuilder.cs:30-36`, `PhraseAlignmentService.ResolvePreset`, `CueGenerationService:538/702`, `DropCountdownCues:102` | Read **only** `DetectedSubGenre`/`ElectronicSubgenre`. **The user's or Beatport's genre tag (`LibraryEntry.Genres`) is never used for analysis.** `GenreSource.UserTag` and `GenreSource.LocalFileTag` exist but are never produced anywhere in production code. |
| Backfill | `StyleBackfillService` | Queues Mood-only runs for tracks **missing** `TopStyle`. It cannot see **stale** scores from older pooling or model versions. |
| Personal classifier | `Tests/.../PersonalClassifierTests.cs`, `StyleClassifierTests.cs` | Both are `<Compile Remove>`d (`SLSKDONET.Tests.csproj:37,40`). The services they test no longer exist. |
| Metadata providers | `Services/Enrichment/*` | `BeatportProvider` is **experimental and off by default** (it scrapes, at 1 request per 3 s). `DiscogsProvider` needs a personal token. Spotify genres are **artist-level** and Deezer genres are **album-level**. MusicBrainz genres are folksonomy votes (top 3). |

Models ship in the repo through **Git LFS** (`*.onnx`, `*.pb`) and are copied by `<Content Include="Tools\**\*.*">`.
The Discogs-400 label file is tracked through a `!` exception in `.gitignore`.

## Verdicts on the external proposal

The proposal that prompted this plan was checked claim by claim:

| Claim | Verdict | Action |
|---|---|---|
| Metadata first (Beatport > Discogs > tags), audio only when missing or generic | **Agree, but not wired at all today.** Analysis never reads `LibraryEntry.Genres`. Beatport is off by default, so most users have file tags, MusicBrainz, Deezer and Spotify. | GA8 |
| "EssentiaRunner runs legacy SVMs and overwrites genre" | **Wrong.** Essentia high-level output is empty, and every genre comes from in-process ONNX. Nothing to stop. Only dead code to delete. | GA2 cleanup |
| "ONNX pipeline isn't prioritised or lacks weights" | **Wrong.** The weights ship and ONNX is the only live source. The real gap is that only the coarse Jamendo head votes. | GA5 |
| Download `genre_discogs400-discogs-effnet-1.onnx` (~2 MB head) | **Not needed.** `discogs-effnet-bsdynamic-1.onnx` already outputs the 400 activations (`activations`, `StyleClassCount = 400`). | none |
| Auto-download models on startup | **Not needed for EffNet**, which ships with the app. A large optional model (MAEST) would need a pinned-SHA download like the FFmpeg installer. | GA12 |
| MAEST-30s-519 for higher accuracy | **Plausible, unverified.** It is large and slow on CPU, its licence needs checking, and its input pipeline is different. | GA12 spike, only if GA3 shows a need |
| "Never classify from the first 30 s" | **Already true.** The whole track is mean-pooled. The real problem is **dilution**: 32-bar DJ intros and outros, often kick-only or beatless, weigh as much as the drops. | GA7 energy-weighted pooling |
| Weight Drop 1 / 60 % / 20 % windows using `TrackPhraseEntity`/`SectionMarkers` | **Wrong order.** Genre is inferred in Step 5b, **before** cue/phrase detection (Step 6). Use an energy weighting that is available at Step 5b. | GA7 |
| Style mapping table (Liquid Funk, Neurofunk, Jump Up, Brostep, Riddim, Deep Dubstep, Acid Techno, Afro House, Bass House) | **Partly invented.** None of those classes exist in Discogs-400. The real classes are listed below. | GA1 |
| Put the rhythm gate in `AnalysisPipelineResultBuilder` | **Wrong place.** That class rebuilds cue inputs from persisted rows. The gate belongs in genre fusion. | GA6 |
| Use `FourOnTheFloorAnalysisStrategy` / `DrumPatternFingerprintEngine` / `BassMusicAnalysisStrategy` as rhythm detectors | **They are not detectors.** The strategies run *after* classification, and `DrumPatternFingerprintEngine` is a slow HPSS breakdown/drop-switch finder. A real 4x4 / backbeat / half-time detector has to be built. | GA4 |
| "Low vocal / soulful arousal" for Techno vs House | **Signals unavailable.** `InstrumentalProbability`/`Arousal`/`Valence` come from the dead Essentia high-level section. Available instead: the ONNX mood heads (aggressive, party, relaxed…). | GA6 (optional voice head) |
| Personal head trained on corrections, ~20–30 per genre, pure C# | **Agree.** `Microsoft.ML` 3.0.1 and `MathNet.Numerics` are already referenced. It replaces the dead centroid prior. | GA11 |
| Backfill with `StyleBackfillService`, verify with `StyleClassifierTests` | **Partly.** Backfill exists but cannot detect stale rows. `StyleClassifierTests` is excluded and unrelated. Verify with the GA3 benchmark instead. | GA10, GA3 |

---

## Target design

```
                    ┌────────────── Layer 1: metadata (GA8) ─────────────┐
LibraryEntry.Genres ─┤ provenance: user > tags(non-junk) > beatport >     │
 + provenance        │ discogs(non-VA) > musicbrainz > deezer/spotify     │
                    └────────────────────────┬───────────────────────────┘
                                             │  TrackGenreResolver (pure)
ONNX EffNet (1 run) ─► per-patch activations │  → ResolvedGenre {Bucket, Subgenre,
   energy-weighted pool (GA7)                │     Confidence, Source, Evidence}
   ├─ Discogs-400 → GenreTaxonomy buckets ───┤
   │  (noisy-OR, GA1+GA5)                    │
   └─ Jamendo-87 → buckets ──────────────────┤
Personal head (GA11) ────────────────────────┤
                                             ▼
                GenreFusionEngine (pure, GA2) + RhythmTempoGate (GA6)
                   ▲ RhythmSignature (GA4): kick4x4, backbeat, halftime, octave-folded BPM
```

### Buckets

`DrumAndBass`, `Dubstep` (the "140" scene), `Techno`, `House`, `Other`.
**`Other` is mandatory.** Without it, every pop, hip-hop or hardstyle track gets forced into the nearest
club bucket. Buckets map to the existing `GenreFamily` enum (DnB → `Breakbeat`, Dubstep → `Dubstep`,
Techno/House → `FourOnTheFloor` + subgenre, Other → `Unknown`). **Do not add `GenreFamily` members**:
every consumer treats `Unknown` as "generic behaviour", which is the safety net.

### Discogs-400 → bucket mapping (real class names)

All of these are verified to exist in `genre_discogs400-discogs-effnet-1.json`.

| Bucket | Discogs-400 classes | Notes |
|---|---|---|
| **DrumAndBass** | `Electronic---Drum n Bass`, `Electronic---Jungle`, `Electronic---Halftime` | Halftime is a DnB-scene style on a 170–175 grid. Liquid, neuro and jump-up don't exist as classes, so subgenre comes from metadata. |
| **Dubstep** | `Electronic---Dubstep`, `Electronic---Grime`, `Hip Hop---Grime` | Grime is at 140, as in Beatport's "140 / Deep Dubstep / Grime". Riddim and brostep don't exist as classes. |
| **Techno** | `Electronic---Techno`, `Electronic---Hard Techno`, `Electronic---Minimal Techno`, `Electronic---Deep Techno`, `Electronic---Dub Techno`, `Electronic---Schranz`, `Electronic---Acid` (0.5), `Electronic---Industrial` (0.5), `Electronic---EBM` (0.3), `Electronic---Minimal` (0.5) | The fractional weights are ambiguous classes: Industrial is also a rock style, Minimal is also minimal house. |
| **House** | `Electronic---House`, `Electronic---Tech House`, `Electronic---Deep House`, `Electronic---Progressive House`, `Electronic---Acid House`, `Electronic---Garage House`, `Electronic---Ghetto House`, `Electronic---Tribal House`, `Electronic---Hip-House`, `Electronic---Italo House`, `Electronic---Euro House`, `Electronic---Electro House` (0.6), `Electronic---Tribal` (0.5), `Electronic---Nu-Disco` (0.4) | Tech House stays `House` (its subgenre maps to `FourOnTheFloorSubgenre.TechHouseTechno`). |
| **Other** (must be explicit) | `Hardcore`, `Gabber`, `Hardstyle`, `Happy Hardcore`, `Speedcore`, `Jumpstyle`, `Hard Trance`, `Trance`, `Psy-Trance`, `Goa Trance`, `Progressive Trance`, `Tech Trance`, `Breaks`, `Breakbeat`, `Progressive Breaks`, `Big Beat`, `Electro`, `UK Garage`, `Speed Garage`, `Bassline`, `Juke`, `Footwork`-like, `Hip Hop---Bass Music`, `Trip Hop`, `Downtempo`, `Ambient`… | These are the **confusers**. Hardcore and gabber run at 160–200 BPM with a 4x4 kick, so a tempo-only rule calls them DnB. UKG and bassline are 130–138 2-step, Breaks is 125–135. |

### Rhythm & tempo gate (GA6)

Fold BPM into the two candidate octaves first (e.g. 87↔174, 70↔140). `features.Bpm` can still be
half- or double-time at Step 5b, because `BeatGridFitter` runs at Step 5c and the strategies' `RefineBpm` runs after genre.

| Evidence | Boost | Penalise |
|---|---|---|
| tempo ∈ 160–185 (either octave) **and** backbeat or break snare (period 2 beats) **and not** 4x4 kick | DnB ×1.6 | House, Techno, Dubstep ×0.3 |
| tempo ∈ 160–185 **and** strong 4x4 kick | Other (hardcore) ×1.4 | DnB ×0.4 |
| tempo ∈ 135–150 **and** half-time snare (one per bar, period 4 beats) **and not** 4x4 | Dubstep ×1.6 | DnB, House ×0.3 |
| tempo ∈ 135–150 **and** strong 4x4 | Techno ×1.2 (hard techno) / Other (trance) | Dubstep ×0.4 |
| tempo ∈ 118–135 **and** strong 4x4 | House and Techno unchanged (let the model decide) | DnB, Dubstep ×0.3 |
| tempo ∈ 125–140 **and** 2-step, no 4x4 | Other (UKG/bassline/breaks) ×1.2 | House ×0.6 |
| Halftime DnB (174 grid, snare once per bar) | keep DnB when tempo is octave 170–175 **and** the model has DnB/Halftime mass | do not push to Dubstep |

**Hard rules.** A gate factor never takes a bucket below **0.15×** its raw score, so nothing is zeroed.
A bucket whose raw model score is **≥ 0.85**, and that leads the runner-up by ≥ 0.25, is never penalised.
Every applied factor is written into the evidence record. The gate **replaces** `ApplyBpmGuardrails`
and `InferGenreHeuristic`; it does not stack on top of them.

---

## Work items

Each item has a self-contained GitHub issue. The issue is the source of truth for status.

| ID | Issue | Title | Depends on | Wave | Effort |
|---|---|---|---|---|---|
| GA1 | #246 | `GenreTaxonomy`: one canonical mapping (Discogs-400, Jamendo, Beatport, Discogs, tag text → bucket + subgenre, plus a junk list) | none | 0 | M |
| GA2 | #247 | Extract `GenreFusionEngine` from `AudioAnalysisService` (no behaviour change) and delete dead Essentia genre paths | none | 0 | M |
| GA4 | #248 | `RhythmSignatureAnalyzer`: 4x4 kick, backbeat vs half-time snare, octave-folded tempo | none | 0 | L |
| GA3 | #249 | Genre ground-truth benchmark (`Tests/GenreBenchmark`): baseline before any tuning | GA1, GA2 | 1 | M |
| GA5 | #250 | Wire Discogs-400 style scores into fusion (built but never wired) | GA1, GA2 (GA3 to tune) | 1 | M |
| GA6 | #251 | Rhythm & tempo gate in fusion, replacing `ApplyBpmGuardrails`/heuristic | GA4, GA5 | 2 | M |
| GA7 | #252 | Energy-weighted pooling of per-patch style activations (embedding unchanged) | GA5 | 2 | M |
| GA8 | #253 | Metadata-first `TrackGenreResolver`, wired into cue/phrase consumers, plus `GenreEvidenceJson` | GA1, GA6 | 2 | L |
| GA9 | #254 | Track Inspector "Genre evidence" bars and a Maintenance "Genre conflict" rule | GA8 | 3 | M |
| GA10 | #255 | Backfill: detect stale style/fusion versions, plus re-fuse without audio | GA6, GA7 | 3 | M |
| GA11 | #256 | Personal genre head from user corrections, replacing the dead centroid prior | GA3, GA8 | 3 | L |
| GA12 | #257 | Spike: MAEST-30s-519. Decision doc only | GA3 numbers | 4 | M |
| GA13 | #258 | Release: bump `CurrentSetupVersion` and add What's New for this epic | all user-facing items | 4 | S |

**File-conflict notes for parallel agents**
- GA2, GA5, GA6 and GA7 all touch genre fusion. After GA2, fusion lives in its own file. Merge GA2 **first**, then GA5, then GA6 and GA7.
- GA7 and GA10 both touch `DiscogsEffnetEmbeddingExtractor` / `StyleModelTag`. Merge GA7 first.
- GA8 and GA9 both touch `AnalysisPipelineResultBuilder` callers and the Track Inspector. Run them in order.
- **No child bumps `CurrentSetupVersion` itself.** GA13 does it once.

---

## Tips & tricks (read before touching genre code)

### Environment
- **Git LFS:** run `git lfs pull` in a fresh clone or worktree. Without it, `*.onnx` files are ~130-byte pointer files.
  `InferenceSession` fails to load, logs "Embeddings disabled", and every genre test against real models
  silently does nothing. Check the size: `discogs-effnet-bsdynamic-1.onnx` must be ~18 MB.
- **Never** run analysis or benchmarks against the live `%AppData%/ORBIT/library.db`. Unit tests use
  `ORBIT_DB_PATH` + `TestDatabaseSetup`. Benchmarks open a **copy** made with SQLite online backup, as `Tests/CueBenchmark/README.md` does.
- Unit tests must not depend on the real ONNX models. Feed synthetic score vectors and embeddings into
  the pure classes (`GenreTaxonomy`, `GenreFusionEngine`, `RhythmTempoGate`, `TrackGenreResolver`).
  Real-model behaviour is measured by the benchmark, not asserted in CI.

### ONNX / DirectML
- `DiscogsEffnetEmbeddingExtractor` and `EffnetClassifierHeadService` are **DI singletons with a
  `SemaphoreSlim` around `Run()`**. Concurrent `Run()` on one DirectML session crashed the process
  natively, and no managed `catch` can stop that. Never create an `InferenceSession` per track, and never
  call `Run` outside the lock. If you add a head (for example voice/instrumental), copy the `Head` pattern
  (CPU, 1 thread, lazy load, lock).
- Respect `HardwareCapabilities.Allows(HardwareFeature.AiTagging)`. Below the minimum, the models stay
  unloaded and fusion must still work (metadata + rhythm only).
- The real ONNX tensor names differ from the published JSON: the input is `melspectrogram` and the outputs are
  `embeddings`/`activations`. Check names against `session.InputMetadata`/`OutputMetadata`, never the JSON.
- **Discogs-400 outputs are independent sigmoids (multi-label), not a softmax.** Don't softmax them.
  Don't sum them and read the result as a probability. For a bucket, use **noisy-OR**
  (`1 − Π(1 − w·pᵢ)`) or the max member. Real top scores are often 0.2–0.5, so thresholds like "< 0.65 = low
  confidence" mean nothing until they are calibrated on GA3.
- Per-patch activations are already computed. `RunInference` throws them away in `MeanAcrossPatches`.
  Reweighting them (GA7) costs **zero extra inference**.

### Don't break similarity search
- `features.Embedding` + `EmbeddingModelTag` feed `SimilarityIndex` / `TrackSimilarityService`. **Keep the
  persisted embedding a plain mean.** If you change it, old and new vectors are no longer comparable and
  "similar tracks" quietly gets worse. Weighted pooling only changes the style/genre path, and that
  path's own `StyleModelTag` gets a suffix (for example `|pool=energy1`).

### Tempo & rhythm
- `features.Bpm` at Step 5b is the raw tracker value. It can be integer-quantised and octave-wrong (memory:
  178 stored for a 174 track, half/double-time). **Always evaluate both octaves**, and never let the gate's
  verdict depend on one octave being right.
- There is a circular dependency: genre picks the BPM octave (`IGenreFamilyAnalysisStrategy.RefineBpm`), and BPM picks the genre (the gate).
  Break it by giving the gate octave-folded tempo plus rhythm evidence, never the refined BPM.
- Downbeat phase is often wrong (`DownbeatOffsetSeconds` is a best-of-4 guess). Use **phase-invariant**
  rhythm features: autocorrelation of band onset envelopes at lags of 1, 2 and 4 beats. Don't use "snare on
  beat 3" counted from a downbeat you can't trust.
- Measure rhythm on **drop-energy regions only** (top ~40 % RMS bars). Intros are kick-only or beatless,
  and breakdowns have no drums.
- **Don't reuse `DrumPatternFingerprintEngine.IsolatePercussiveSignal` on full tracks.** It median-filters
  every bin with fresh `List<double>` allocations, which is far too slow. Use 3 band-limited onset envelopes (kick ~40–120 Hz,
  snare body ~150–400 Hz + crack 1.5–5 kHz, hats > 6 kHz) from one STFT pass. Budget: < 150 ms per track.

### Metadata
- **Never overwrite a user's genre.** Provenance (`MetadataProvenanceEntity`, sources `user`/`tags`/
  `maintenance`) decides what is trusted. Disagreements become a *review proposal* (GA9), not an edit.
- **Junk tags** count as missing: `Electronic`, `Electronica`, `Dance`, `Dance & DJ`, `Club`, `EDM`,
  `Electronic Dance Music`, `Other`, `Unknown`, `Misc`, `Genre`, `Various`, empty, and bare ID3v1 numeric
  codes such as `(52)` = Electronic. Treat `Bass` and `Bass Music` as ambiguous, not as Dubstep.
- Spotify genres belong to the **artist** and Deezer genres to the **album**. They are a weak vote only, never authoritative.
- Discogs styles are **release-level**. On a VA compilation or a release with more than 3 styles, they are a weak vote.
- Beatport genres are **per-track** and the most reliable source. Map their real names:
  "Drum & Bass", "140 / Deep Dubstep / Grime", "Dubstep", "Techno (Peak Time / Driving)",
  "Techno (Raw / Deep / Hypnotic)", "Hard Techno", "Tech House", "House", "Deep House",
  "Melodic House & Techno", "Progressive House", "Afro House", "Bass House", "Minimal / Deep Tech",
  "UK Garage / Bassline", "Breaks / Breakbeat / UK Bass", "Trance (Main Floor)", "Hard Dance / Hardcore".
  (Melodic House & Techno → Techno/Melodic Techno; Bass House → House; UKG/Breaks/Trance/Hard Dance → Other.)
- `ApplyUseAsGenreAsync` writes a genre the **model** proposed. Treat it as a "confirmed suggestion", never as
  independent ground truth: exclude it from benchmark labels and give it a low weight in personal-head training.

### Label strings are an API
- Downstream code substring-matches genre text: `GenreFamilyClassifier` ("dnb", "drum and bass",
  "techn", "hous"…), `PhraseAlignmentService.ResolvePreset` ("melodic"), `DropCountdownCues`, the Dashboard
  Genre Galaxy, smart playlists and style playlists. Keep the canonical display strings **stable**:
  `Drum and Bass`, `Dubstep`, `Techno`, `Melodic Techno`, `Tech House`, `House`, `Trance`. Grep every
  consumer before renaming one. Add new subgenres as *extra* text (e.g. `Hard Techno`), which still contains "techn".

### Process
- **Measure first.** The previous AI review of Cue Forge (memory: *Cue Forge Gemini Review*) made many
  wrong claims. Every multiplier and weight in this plan is a starting point. Tune it on GA3 and record
  the before/after in the PR.
- The key metric is the **confident-wrong rate** (label confidence ≥ 0.7 and wrong), not accuracy. A DJ forgives
  "unsure" but not a confident lie.
- When something "should work but doesn't", check whether it was built and never wired. This plan exists
  because the best signal (Discogs-400) was built and never wired.
- Schema: no `dotnet ef migrations add`. Use raw SQL in `SchemaMigratorService`, add the EF property, and keep
  `FreshDatabaseSchemaParityTests` green.
- UI: show AI-derived genre as **bars and percentages, not coloured pills**. Use the mono font for numbers, design empty,
  loading and error states, and use only tokens from `Themes/DesignTokens.axaml`.
