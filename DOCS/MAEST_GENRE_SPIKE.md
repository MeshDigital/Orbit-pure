# MAEST genre model spike (GA12, #257) — decision

**Decision: Reject for now (Won't do in this epic).** Re-open only on the conditions at the end.
Decision doc only: no production code, no model files added. Facts below were read at the source on
2026-11-23: [essentia.upf.edu/models.html](https://essentia.upf.edu/models.html), the model JSON
[`discogs-maest-30s-pw-519l-2.json`](https://essentia.upf.edu/models/feature-extractors/maest/discogs-maest-30s-pw-519l-2.json)
and [`models/LICENSE`](https://essentia.upf.edu/models/LICENSE).

## 1. Files and versions

| Model | Classes | Context | Notes |
|---|---|---|---|
| `discogs-maest-30s-pw-519l-2` | 519 styles | 30 s | Trained on "Discogs23 (unreleased)", 4M tracks; released 2025-01-22; TensorFlow 2.17 |
| `discogs-maest-30s-pw-2`, `-30s-pw-ts-2`, `-20s-pw-2`, `-10s-pw-2`, `-10s-fs-2`, `-10s-dw-2`, `-5s-pw-2` | 400 styles | 5–30 s | The same 400 classes ORBIT already gets from Discogs-EffNet |
| `genre_discogs519-discogs-maest-30s-pw-519l-1` | 519 | — | Separate classification head over MAEST embeddings (version `-1`, fed by the `-2` extractor in the site's own sample) |

- **Format.** Only `.pb` (frozen TensorFlow) files are linked. The JSON lists `"model_types": ["frozen_model", "onnx"]`
  and the page says ONNX versions exist, but **no MAEST `.onnx` URL is published**. ORBIT runs ONNX Runtime only, so
  adopting MAEST would start with converting a 12-layer transformer ourselves (tf2onnx) and proving parity — the
  same risk class as the #270 EffNet input bug, which silently halved genre accuracy.
- **Size.** Not stated on the site. Estimate: a ViT-base-sized transformer (768-D, 12 layers, ~86 M parameters) is
  ~350 MB in float32, against 18 MB for the Discogs-EffNet model ORBIT ships today: too big to bundle, so on-demand download (§7).

## 2. Input contract

- 16 kHz mono; input tensor `melspectrogram`, shape `[1, 1876, 96]` (1,876 frames × 96 bands ≈ 30 s).
- **Normalisation constants: not published** in the JSON or on the page. MAEST is known to normalise differently
  from EffNet; we would have to recover the constants from the training code and verify them against Essentia's
  `TensorflowPredictMAEST` output — exactly the kind of silent mismatch that cost us #270.
- Outputs: `PartitionedCall/Identity_13` = 519 sigmoids; `Identity` = logits; `Identity_1…12` = per-layer token
  embeddings `[1, tokens, 768]` (JSON tags `Identity_7` as "the" embedding; the site's 519-head sample uses
  `Identity_12` — another inconsistency to resolve by measurement).
- Windows (per #257 Tips): up to 3 energy-ranked, drop-centred 30 s windows (#252 weighting), never the first 30 s.

## 3. What the 519 classes add

Checked against the JSON's `classes` list:

| Style | In MAEST-519 | In Discogs-400 (shipped) |
|---|---|---|
| Drum n Bass, Jungle, Halftime, Dubstep, Grime, Techno, Hard Techno, House, Deep House, Tech House, UK Garage | yes | yes |
| **Liquid Funk, Neurofunk, Riddim, Bass House, Afro House** | **no** | no |
| New club-adjacent: Baltimore Club, Footwork, Ghettotech, Glitch Hop, UK Funky, Hard Beat, Neo Trance, Doomcore, J-Core, Witch House | yes | no |

The subgenres DJs in this library actually care about (Liquid / Neuro / Jump-Up, Riddim, Bass House) are **still
absent**. The new classes are all "Other"-bucket confusers (footwork, UK funky, ghettotech); they would sharpen
`Other`, not the four club buckets. Subgenre keeps coming from metadata (GA8) and now the personal head (GA11).

## 4. Latency and memory

**Not measured** — there is no ONNX file to load, and converting it is the first follow-up below, not a spike
step. Estimate only: a ViT-base pass over a 30 s window is typically around a second on a desktop CPU, so 3 windows
would add several seconds per track on the `AiTagging` floor machine, plus roughly 1 GB of RAM while the session
is loaded. That alone requires lazy loading and an opt-in setting (§7).

## 5. Licence

The models page says all MTG models are **CC BY-NC-SA 4.0**; the `LICENSE` file in the same directory is headed and
written as **CC BY-NC-ND 4.0** ("applies to all model files hosted in this directory"). Either way MAEST is under the
**same terms as the Discogs-EffNet models ORBIT already ships**, with a proprietary licence available on request
(mtg-info@upf.edu). MAEST adds no new licensing problem, but it does not remove the existing one: if ORBIT is ever
sold, *both* need the proprietary licence. (The NC-SA vs NC-ND discrepancy should be clarified with the MTG before
any commercial release, independently of this decision.)

## 6. Accuracy

The #257 bar: adopt only if, after #250/#251/#252 (all merged), #249 still shows confident-wrong errors that EffNet
plus rhythm can't fix. Where that stands now (`GenreBenchmark --audio`, library backup, real audio):

`GenreBenchmark --audio --limit 500 --personal`, 2026-11-23: 2,344 labelled tracks in the library backup (500-track
round-robin sample: Other 147, DnB 146, House 146, Techno 38, Dubstep 23), real audio decoded read-only, current
EffNet preprocessing (#270), mean 1.0 s/track for decode + EffNet + heads + rhythm.

| Configuration | Tracks | Accuracy | Club macro-F1 | Confident-wrong (≥ 0.70) |
|---|---|---|---|---|
| EffNet fusion (Discogs-400 + Jamendo), no gate | 500 | 65.0 % | 0.519 | 0 % — calibration caps confidence at 0.69 |
| + rhythm & tempo gate (shipped, GA6) | 500 | 64.8 % | 0.522 | 0 % |
| Hold-out 20 %: shipped fusion | 102 | 67.6 % | 0.444 | 0 % |
| Hold-out 20 %: + personal head (GA11, forced at 0.35) | 102 | **75.5 %** | **0.547** | 0 % |
| MAEST alone / MAEST + EffNet | — | not measured (no ONNX, §1–2) | — | — |

So about a third of audio-only genre calls are still wrong. "Confident-wrong" is 0 % only because the GA5/GA6
calibration no longer lets any label claim ≥ 0.70: it is honest uncertainty, not accuracy.

The residual audio error is still large (the calibrated top bin is under 70 % right), so the bar is met *in
principle*. But the cheapest wins against it are already in place and don't need a 300 MB+ model:

- **GA8 (metadata first):** on the library backup, 2,737 of 4,281 tracks (64 %) have a clean file-tag or
  unknown-origin genre that now decides cue behaviour, so audio errors no longer reach their cues.
- **GA11 (personal head):** +7.8 pp accuracy and +0.10 club macro-F1 on the hold-out with no new model files
  (its cross-validation gate kept it off on this 400-track training sample: +1.6 pts, under the 2-pt bar; with the
  full library's ~2,700 labels it decides for itself).

MAEST alone vs EffNet fusion vs both was **not measured** (no ONNX file, §1–2). The published claim is only that
the 519l model is "expected to show slightly better performance" than 400-class MAEST, with validation mAP "N/A" in
its own JSON.

## 7. Distribution (if ever adopted)

Same pattern as FFmpeg (`Services/Platform/FfmpegInstallerService.cs`) and Demucs (`DemucsModelManager`): opt-in
setting (default off), on-demand download into LocalAppData with a **pinned SHA-256**, `HardwareCapabilities`
gating (`AiTagging` plus a RAM floor), lazy session creation only when enabled, and one inference lock per session
(never concurrent `Run()` on DirectML — the extractor's `_inferenceLock` pattern).

## Recommendation

**Reject for now.** Reasons, in order:

1. No published ONNX export and unpublished normalisation: adopting means converting and validating a 12-layer
   transformer ourselves, the highest-risk step for the smallest proven gain.
2. The 519 classes don't contain the subgenres this library needs; the new ones only sharpen the "Other" bucket.
3. Cost: hundreds of MB to download, ~1 GB RAM, seconds per track on the floor machine, for every user who opts in.
4. The residual error is being attacked more cheaply by metadata-first resolution (GA8) and the personal head
   (GA11), both shipped.

**Re-open when all hold:** (a) the MTG publishes a MAEST `.onnx` with its preprocessing constants (or a parity
harness against Essentia's own `TensorflowPredictMAEST` is built in `Tools/`), (b) a `GenreBenchmark --audio`
run on tracks **without** a usable library genre (the ones GA8 can't help) still shows more than 25 % wrong after
the personal head, and (c) the MTG confirms the licence terms (§5) for how ORBIT is distributed.

Follow-up issues if re-opened: (1) `Tools/` parity harness + ONNX conversion of `discogs-maest-30s-pw-519l-2`;
(2) `GenreBenchmark --maest` (MAEST alone / EffNet / both, on the `--audio --limit 300` subset, CPU and DirectML
timings); (3) opt-in download + `MaestGenreExtractor` behind `HardwareCapabilities`; (4) fusion source + taxonomy
entries for the 119 new classes.
