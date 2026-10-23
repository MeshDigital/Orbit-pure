# Library metadata enrichment

Fills cover art, album, label, release year, genre/style and ISRC for library tracks
(folder imports and downloads) from several sources, at each source's rate limit.
Code: `Services/Enrichment/`. UI: Analysis page → Enrichment card, Library → Tools →
"Enrich library metadata", Settings → Connection → Metadata sources, inspector "Metadata sources".

## Providers (default order)

| # | Provider | Needs | Gives | Limit (ORBIT's pacing) |
|---|---|---|---|---|
| 1 | File tags (`tags`) | nothing, always on | embedded cover, album, label (publisher), year, genre, track no., ISRC, TBPM/TKEY hints | local, 4 at a time |
| 2 | Deezer (`deezer`) | nothing | cover_xl, album, release date, label + genres (album), ISRC, duration | ≤ 40 req / 5 s and ≥ 110 ms apart (≈ 7.5/s; Deezer documents 50 / 5 s) |
| 3 | MusicBrainz (`musicbrainz`) | nothing | label, catalogue no., release date, MBIDs, genres, Cover Art Archive cover | 1 req/s strictly, `ORBIT/<version> ( github.com/MeshDigital/ORBIT )` User-Agent |
| 4 | Discogs (`discogs`) | personal token | genre + **style**, label, catalogue no., year, cover | ≤ 55 req/min; pauses 60 s when `X-Discogs-Ratelimit-Remaining` ≤ 2 |
| 5 | AcoustID (`acoustid`) | API key + `fpcalc` (Chromaprint, not shipped) | artist/title/MBID for unidentified files | ≤ 3 req/s |
| 6 | Spotify (`spotify`) | Spotify connected | identity, ISRC, album, cover. **No audio features** (blocked for new apps since Nov 2024) | ≥ 400 ms apart; SpotifyEnrichmentService's own 429/403 back-off |
| 7 | Beatport (`beatport`) | **off by default**, experimental | label, genre, release date, cover (scraped public pages) | 1 req / 3 s |

`fpcalc` is looked for next to ORBIT.exe, in `Tools\` there, and on PATH; without it AcoustID
shows "needs fpcalc". Keys are stored DPAPI-encrypted for the Windows user in
`%LocalAppData%\ORBIT\metadata_keys.dat` (`MetadataCredentialStore`), never in config.ini.
Order and on/off live in config.ini `[Enrichment]` (`ProviderOrder`, `EnabledProviders`, `AutoRun`).

## Per track

1. Load the LibraryEntries row and its provenance.
2. Wanted = cover, album, label, year, ISRC, genre; + identity when artist is "Unknown Artist" or
   the title is the file name; + style when a style provider (Discogs) is enabled and ready.
3. Walk providers in order. Skip a provider that can't fill anything still missing, that isn't
   ready (no key/tool/connection, blocked), or whose circuit is open. **Stop as soon as nothing
   wanted is missing.** IDs found on the way (ISRC, MBIDs) are passed to later providers.
   Unidentified files ask AcoustID right after the tags.
4. Text searches are accepted only above a strict score (`EnrichmentMatch`, ≥ 0.85): artist and
   title after normalisation (feat./"Original Mix" dropped, remix/VIP tags kept, so "X (VIP)"
   never matches "X"), duration within 10 s. ISRC lookups need no search.
5. Artwork: embedded bytes are written to the artwork cache as a content-addressed file; remote
   covers are downloaded into the artwork disk cache first (`ArtworkCacheService.PrefetchAsync`)
   and the URL is stored only if that worked.
6. Write the patch to LibraryEntries, PlaylistTracks and Tracks of that hash, record provenance,
   stamp `LastEnrichmentAttempt` (7-day cooldown before the manual action retries a track).

## Merge rules (`MetadataMerger`)

- **Analysis-owned BPM, key and energy are never written.** A web provider's BPM/key is a hint
  and is dropped; only the file's own TBPM fills the `TagBPM` side column when it is empty.
  `MetadataPatch` has no BPM/key/energy fields at all.
- **User edits are never overwritten.** The batch tag editor records `MetadataProvenance` source
  `user` for the fields it changed; those fields are locked for enrichment.
- **Fill-empty only.** A field that already has a value is kept, whoever wrote it (file tags at
  import, an earlier pass, or the user elsewhere). Within one pass the first provider wins.
- One exception: Discogs **styles** may replace a broad genre ("Dance") that an earlier web
  provider wrote (provenance proves it), never one from tags, the user or an import.
- Identity (artist/title) is replaced only for placeholders and only at ≥ 0.85 confidence.
- Answers under 0.6 confidence are ignored.

## Throttling and failure handling

- `ProviderRateLimiter`: reserved time slots (min spacing + jitter, N per window) shared by all
  workers. `ProviderCircuitBreaker`: 429 → pause until Retry-After (or 30 s doubling to 15 min);
  three 5xx/network errors → 60 s pause; three 401/403 → provider switched off for the session
  (card and Settings say so, the end-of-run toast names it).
- A track whose useful provider was paused is **deferred** (`NextAttemptAt`, up to 3 times), not
  completed with holes.
- Every failure is logged (`[Enrichment]`); run errors and blocked providers surface as toasts.
- `MetadataLookupCache` table: 200 bodies kept 60 days, 404/"no data" 14 days, keyed by provider
  + request. Re-runs and albums shared by many tracks cost no requests.

## Queue and triggers (`MetadataEnrichmentOrchestrator`)

- Persistent queue = `EnrichmentTasks` (TrackId = LibraryEntries.UniqueHash, + `Priority`,
  `NextAttemptAt`). Rows left in Processing by a crash are re-queued on start; a leftover queue
  resumes 45 s after startup.
- Triggers: `LibraryImportCompletedEvent` (priority 1), `FileIngestionCompletedEvent` for
  downloads (priority 2), manual "Enrich library" (priority 0, every track missing metadata).
  The automatic ones follow the "Enrich new tracks automatically" switch.
- 4 tracks in flight; 2 while a folder import is adding rows; 1 while audio plays or the
  analysis queue has work (it used to stop for the whole analysis run).
- A run that is active when an import commits a batch (`LibraryEntriesAddedEvent`) queues
  those rows too; the import's completion trigger skips tracks finished in the last 7 days.
- The write re-reads the row in one IMMEDIATE transaction and fills only fields still empty
  at that moment; provenance only for fields really written. Busy/locked writes are retried.
- Also started from the setup wizard's "Metadata and artwork" step (DOCS/FIRST_RUN_SETUP.md);
  progress in the top-bar activity chip.
- Publishes `TracksEnrichedEvent` in batches (≤ 1.5 s or 25 tracks); `VirtualizedTrackCollection`
  refreshes just the materialised rows, so covers appear in place. Progress: `EnrichmentProgressEvent`.

## Schema

Raw SQL in `SchemaMigratorService` patch 38 (never `dotnet ef migrations add`): `EnrichmentTasks`
+`Priority`/`NextAttemptAt`, new `MetadataLookupCache` (PK Provider+CacheKey) and
`MetadataProvenance` (PK TrackUniqueHash+Field). Both are also in the EF model, so
`SchemaReconciler` and `FreshDatabaseSchemaParityTests` cover them.

## Expected duration (≈ 3,300 tracks, default providers, no keys)

File tags: a couple of minutes. Deezer: ~2–3 requests per matched track (ISRC or search + track,
album shared and cached) ≈ 8,000 requests at ~7.5/s ≈ 20 min. MusicBrainz only for what Deezer
left empty (unmatched or label-less tracks; ~1,000 for an underground electronic library) at
2 requests per track and 1/s ≈ 35 min, running alongside Deezer. Total roughly **40–60 min**
of background time, longer while analysis runs. A Discogs token adds one request per track at
1/s (≈ 55 min, in parallel) for style-level genres. A second run is mostly cache hits.

## Tests

`Tests/SLSKDONET.Tests/Services/Enrichment/` (fake HTTP handlers, temp DB): limiter timing,
breaker on 429/403, Deezer quota-in-200, ISRC-first and match rejection, lookup cache, merge
rules, early stop, deferral, queue resume, artwork store, batched events. `LiveProviderSmokeTests`
hit the real APIs and are skipped by default.
