using System.Diagnostics;
using System.IO;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using Soulseek;
using System.Linq;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Open.Nat;

namespace SLSKDONET.Services;


/// <summary>
/// Soulseek search execution, streaming results, peer search, and track token parsing (#307).
/// </summary>
public partial class SoulseekAdapter
{
    private async Task<int> SearchCoreAsync(
        string query,
        IEnumerable<string>? formatFilter,
        (int? Min, int? Max) bitrateFilter,
        DownloadMode mode,
        Action<IEnumerable<Track>> onTracksFound,
        SearchExecutionProfile? executionProfile,
        Action<SearchLimitExceededException>? onLimitExceeded,
        CancellationToken ct,
        Soulseek.SearchScope? scope = null)
    {
        using var searchLifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var effectiveCt = searchLifetimeCts.Token;

        if (string.IsNullOrWhiteSpace(query))
        {
            _logger.LogDebug("Search skipped because query was empty.");
            return 0;
        }

        // Correctness-first: serialize outbound searches until per-query callback isolation exists.
        var maxOutboundSearches = 1;
        while (true)
        {
            effectiveCt.ThrowIfCancellationRequested();
            var currentInFlight = Volatile.Read(ref _outboundSearchInFlight);
            if (currentInFlight < maxOutboundSearches
                && Interlocked.CompareExchange(ref _outboundSearchInFlight, currentInFlight + 1, currentInFlight) == currentInFlight)
            {
                break;
            }

            await Task.Delay(25, effectiveCt);
        }

        var client = await WaitForReadyClientAsync(effectiveCt);
        if (client == null)
        {
            _logger.LogInformation("Search skipped for query {SearchQuery} because Soulseek client is not ready.", query);
            return 0;
        }

        var directories = new ConcurrentDictionary<string, List<Soulseek.File>>();
        var resultCount = 0;
        var totalFilesReceived = 0;
        var filteredByFormat = 0;
        var filteredByBitrate = 0;
        var filteredBySampleRate = 0;
        var filteredByQueue = 0;
        var filteredByDedup = 0;
        var pendingCallbacks = 0;
        var searchDispatchCompleted = 0;
        // Protocol-level telemetry: raw peer response count (before any per-file filtering) and
        // the terminal search state, both sourced from the library's own SearchOptions callbacks
        // rather than inferred from our own counters — tells us whether "0 results" means "0 peers
        // replied" vs "peers replied but every file got filtered" vs "the search was cancelled/errored".
        var peersResponded = 0;
        var finalSearchState = Soulseek.SearchStates.None;
        var callbackDrainTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hardCapTriggered = 0;
        var formatSet = formatFilter?.Select(f => f.ToLowerInvariant()).ToHashSet() ?? new HashSet<string>();
        var hardResultCap = Math.Max(1000, _config.SearchHardResultCap);
        var hardFileCap = Math.Max(0, _config.SearchHardFileCap);
        var excludedPhraseSet = new ReadOnlyCollection<string>(_excludedPhrases.Keys.ToList());
        // Beta 2026: Result fingerprinting — deduplicate by (FileName + FileSize + Duration) within one search.
        // Reduces noise by up to 70% on popular tracks shared by many peers.
        var seenThisSearch = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var minBitrateStr = bitrateFilter.Min?.ToString() ?? "0";
            var maxBitrateStr = (bitrateFilter.Max == null || bitrateFilter.Max == 0) ? "∞" : bitrateFilter.Max.ToString()!;

            // Golden Rule: Rate Limiting (configurable global delay, default 200ms)
            while (true)
            {
                var extraDelay = Math.Max(0, executionProfile?.AdditionalThrottleDelayMs ?? 0);
                var tokenCapacity = Math.Max(1, executionProfile?.TokenBucketCapacity ?? _config.SearchTokenBucketCapacity);
                var tokenRefillMs = Math.Max(500, executionProfile?.TokenRefillIntervalMs ?? _config.SearchTokenBucketRefillMs);
                var waitMs = 0;

                await _rateLimitLock.WaitAsync(effectiveCt);
                try
                {
                    RefillSearchTokens(tokenCapacity, tokenRefillMs);
                    if (_searchBucketTokens >= 1d)
                    {
                        _searchBucketTokens -= 1d;
                        break;
                    }

                    waitMs = MillisecondsUntilNextToken(tokenRefillMs) + extraDelay;
                    _logger.LogDebug(
                        "Search token bucket empty. Waiting {WaitMs}ms before dispatch (capacity={Capacity}, refill={RefillMs}ms).",
                        waitMs,
                        tokenCapacity,
                        tokenRefillMs);
                }

                finally
                {
                    _rateLimitLock.Release();
                }

                await Task.Delay(waitMs, effectiveCt);
            }

            _logger.LogInformation("Search started for query {SearchQuery} with mode {SearchMode}, format filter {FormatFilter}, bitrate range {MinBitrate}-{MaxBitrate}",
                query, mode, formatFilter == null ? "NONE" : string.Join(", ", formatFilter), minBitrateStr, maxBitrateStr);

            // NEW Phase 12.2: Proactive Network Safety - Prevent sending banned phrases
            var lowerQuery = query.ToLowerInvariant();
            if (_excludedPhrases.Count > 0)
            {
                 foreach (var phrase in _excludedPhrases.Keys)
                 {
                     if (lowerQuery.Contains(phrase))
                     {
                         _logger.LogWarning("🚨 [NETWORK SAFETY] Aborting search to prevent soft-ban: Query '{Query}' contains banned phrase '{Phrase}'", query, phrase);
                         _healthService.RecordExcludedPhraseQueryBlock();
                         return 0;
                     }
                 }
            }
            
            var searchQuery = Soulseek.SearchQuery.FromText(query);
            var responseLimit = executionProfile?.EffectiveResponseLimit ?? Math.Max(20, _config.SearchResponseLimit);
            var fileLimit = executionProfile?.EffectiveFileLimit ?? Math.Max(20, _config.SearchFileLimit);
            if (executionProfile != null)
            {
                _eventBus.Publish(new SearchPressureStatusEvent(
                    executionProfile.PressureLevel.ToString(),
                    Math.Max(20, responseLimit),
                    Math.Max(20, fileLimit),
                    Math.Max(1, executionProfile.EffectiveVariationCap),
                    Math.Max(0, executionProfile.AdditionalThrottleDelayMs)));
            }
            var maxPeerQueueLength = Math.Max(0, _config.MaxPeerQueueLength);
            var options = new SearchOptions(
                searchTimeout: Math.Max(5000, _config.SearchTimeout),
                responseLimit: Math.Max(20, responseLimit),
                filterResponses: true,
                minimumResponseFileCount: 1,
                maximumPeerQueueLength: maxPeerQueueLength,
                fileLimit: Math.Max(20, fileLimit),
                removeSingleCharacterSearchTerms: true,
                // Reject a whole response up front when its queue is already past our ceiling —
                // every file in it would fail the same per-file queue check in fileFilter below,
                // so this just avoids iterating them one by one.
                responseFilter: response =>
                    maxPeerQueueLength <= 0 || response.QueueLength <= maxPeerQueueLength,
                fileFilter: file =>
                {
                    var decision = SearchFilterPolicy.EvaluateFile(
                        file,
                        formatSet,
                        bitrateFilter,
                        _config.PreferredMaxSampleRate,
                        excludedPhraseSet);
                    return decision.IsAccepted;
                },
                responseReceived: _ => Interlocked.Increment(ref peersResponded),
                stateChanged: args => finalSearchState = args.Search.State
            );

            // The SearchAsync method in the library (or wrapper) seems to handle the waiting internally 
            // based on the stack trace showing SearchToCallbackAsync waiting.
            // So we just await the search initialization/execution.
            void TrySignalCallbackDrain()
            {
                if (Volatile.Read(ref searchDispatchCompleted) == 1 && Volatile.Read(ref pendingCallbacks) == 0)
                {
                    callbackDrainTcs.TrySetResult();
                }
            }

            await TrackNetworkCallAsync("Search", query, () => client.SearchAsync(
                searchQuery,
                (response) =>
                {
                    Interlocked.Increment(ref pendingCallbacks);

                    _logger.LogDebug("Received response from {User} with {Count} files", response.Username, response.Files.Count());

                    try
                    {
                        var foundTracksInResponse = new List<Track>();

                        // Process each search response
                        foreach (var file in response.Files)
                        {
                            if (mode == DownloadMode.Album)
                            {
                                var directoryName = Path.GetDirectoryName(file.Filename);
                                if (!string.IsNullOrEmpty(directoryName))
                                {
                                    var key = $"{response.Username}@{directoryName}";
                                    directories.AddOrUpdate(key, 
                                        _ => new List<Soulseek.File> { file }, 
                                        (_, list) => { list.Add(file); return list; });
                                }
                            }
                            else // Normal mode
                            {
                                var currentFileCount = Interlocked.Increment(ref totalFilesReceived);
                                if (hardFileCap > 0 && currentFileCount > hardFileCap)
                                {
                                    if (Interlocked.CompareExchange(ref hardCapTriggered, 1, 0) == 0)
                                    {
                                        var reason = $"Hard file cap reached ({hardFileCap}) for query '{query}'";
                                        _logger.LogWarning("{Reason}", reason);
                                        _eventBus.Publish(new SearchHardCapTriggeredEvent(query, hardResultCap, hardFileCap, reason));
                                        onLimitExceeded?.Invoke(new SearchLimitExceededException(reason, hardResultCap, hardFileCap));
                                        searchLifetimeCts.Cancel();
                                    }

                                    return;
                                }

                                var extension = Path.GetExtension(file.Filename)?.TrimStart('.').ToLowerInvariant();
                                var fileDecision = SearchFilterPolicy.EvaluateFile(
                                    file,
                                    formatSet,
                                    bitrateFilter,
                                    _config.PreferredMaxSampleRate,
                                    excludedPhraseSet,
                                    Math.Max(0, _config.MaxPeerQueueLength),
                                    response.QueueLength);

                                if (!fileDecision.IsAccepted)
                                {
                                    switch (fileDecision.Reason)
                                    {
                                        case SearchRejectionReason.Format:
                                            var formatRejects = Interlocked.Increment(ref filteredByFormat);
                                            if (formatRejects <= 3)
                                            {
                                                _logger.LogInformation("[FILTER] Rejected by format: {File} (extension: {Ext}, allowed: {Formats})", file.Filename, extension, string.Join(", ", formatSet));
                                            }
                                            break;
                                        case SearchRejectionReason.Bitrate:
                                            Interlocked.Increment(ref filteredByBitrate);
                                            break;
                                        case SearchRejectionReason.SampleRate:
                                            Interlocked.Increment(ref filteredBySampleRate);
                                            break;
                                        case SearchRejectionReason.Queue:
                                            Interlocked.Increment(ref filteredByQueue);
                                            break;
                                    }
                                    continue;
                                }

                                var lengthAttr = file.Attributes?.FirstOrDefault(a => a.Type == Soulseek.FileAttributeType.Length);
                                var rawDurationSeconds = lengthAttr?.Value ?? 0;

                                // Beta 2026: Fingerprint dedup with peer-awareness.
                                // Keep duplicates only when they come from a better queue peer.
                                var fpKey = _resultFingerprinter.Create(file.Filename, file.Size, rawDurationSeconds);
                                var isDedupReplacement = false;
                                if (seenThisSearch.TryGetValue(fpKey, out var existingQueue))
                                {
                                    if (response.QueueLength < existingQueue)
                                    {
                                        seenThisSearch[fpKey] = response.QueueLength;
                                        isDedupReplacement = true;
                                    }
                                    else
                                    {
                                        Interlocked.Increment(ref filteredByDedup);
                                        continue;
                                    }
                                }
                                else
                                {
                                    seenThisSearch.TryAdd(fpKey, response.QueueLength);
                                }

                                // Memory Optimization: Only allocate Track object for files that survive the filters
                                // Use the helper method to parse metadata correctly
                                var track = ParseTrackFromFile(file, response);
                                track.Metadata ??= new Dictionary<string, object>();
                                track.Metadata["IsDedup"] = isDedupReplacement;

                                var acceptedCount = Interlocked.Increment(ref resultCount);
                                if (acceptedCount > hardResultCap)
                                {
                                    if (Interlocked.CompareExchange(ref hardCapTriggered, 1, 0) == 0)
                                    {
                                        var reason = $"Hard result cap reached ({hardResultCap}) for query '{query}'";
                                        _logger.LogWarning("{Reason}", reason);
                                        _eventBus.Publish(new SearchHardCapTriggeredEvent(query, hardResultCap, hardFileCap, reason));
                                        onLimitExceeded?.Invoke(new SearchLimitExceededException(reason, hardResultCap, hardFileCap));
                                        searchLifetimeCts.Cancel();
                                    }

                                    return;
                                }

                                if (acceptedCount <= 3) // Log first 3 matches
                                {
                                    _logger.LogInformation("[ACCEPT] Track passed filters: {Artist} - {Title} ({Bitrate} kbps, {Ext})", track.Artist, track.Title, track.Bitrate, extension);
                                }

                                foundTracksInResponse.Add(track);
                            }
                        }
                        
                        if (foundTracksInResponse.Any())
                        {
                            PublishTracksInBatches(onTracksFound, foundTracksInResponse, 50);
                        }
                    }
                    finally
                    {
                        if (Interlocked.Decrement(ref pendingCallbacks) == 0)
                        {
                            TrySignalCallbackDrain();
                        }
                    }
                },
                scope: scope ?? Soulseek.SearchScope.Network,
                options: options,
                cancellationToken: effectiveCt
            ));

            Volatile.Write(ref searchDispatchCompleted, 1);
            TrySignalCallbackDrain();

            if (Volatile.Read(ref pendingCallbacks) > 0)
            {
                using var callbackDrainTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(effectiveCt);
                callbackDrainTimeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    await callbackDrainTcs.Task.WaitAsync(callbackDrainTimeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "Search callback drain timed out for query {SearchQuery}; returning with {Pending} callback(s) still pending.",
                        query,
                        Volatile.Read(ref pendingCallbacks));
                }
            }

            var finalResultCount = Volatile.Read(ref resultCount);
            var finalTotalFilesReceived = Volatile.Read(ref totalFilesReceived);
            var finalFilteredByFormat = Volatile.Read(ref filteredByFormat);
            var finalFilteredByBitrate = Volatile.Read(ref filteredByBitrate);
            var finalFilteredBySampleRate = Volatile.Read(ref filteredBySampleRate);
            var finalFilteredByQueue = Volatile.Read(ref filteredByQueue);
            var finalFilteredByDedup = Volatile.Read(ref filteredByDedup);

            if (mode == DownloadMode.Album)
            {
                _logger.LogInformation("Found {Count} potential album directories.", directories.Count);
                // TODO: In a future step, we would rank these directories and create album download jobs.
                // For now, we will just log them.
                finalResultCount = directories.Count;
            }
            else
            {
                finalResultCount = Math.Min(finalResultCount, hardResultCap);
                if (hardFileCap > 0)
                {
                    finalTotalFilesReceived = Math.Min(finalTotalFilesReceived, hardFileCap);
                }
            }

            _logger.LogInformation(
                "Search completed ({State}): {ResultCount} results from {TotalFiles} files, {PeersResponded} peers responded " +
                "(filtered: {FormatFiltered} format, {BitrateFiltered} bitrate, {SampleRateFiltered} sample-rate, " +
                "{QueueFiltered} queue, {DedupFiltered} dedup)",
                finalSearchState, finalResultCount, finalTotalFilesReceived, Volatile.Read(ref peersResponded),
                finalFilteredByFormat, finalFilteredByBitrate,
                finalFilteredBySampleRate, finalFilteredByQueue, finalFilteredByDedup);

            _healthService.RecordSearchFiltering(
                finalFilteredByFormat,
                finalFilteredByBitrate,
                finalFilteredBySampleRate,
                finalFilteredByQueue,
                finalFilteredByDedup,
                0);

            // Record search results for health diagnostics
            _healthService.RecordSearch(query, finalTotalFilesReceived, finalResultCount, true);
            
            return finalResultCount;
        }
        catch (OperationCanceledException)
        {
             _logger.LogInformation("Search cancelled for query {SearchQuery}", query);
             return Volatile.Read(ref resultCount); // Return whatever we found before cancellation
        }
        catch (Exception ex)
        {
             // Check if we are shutting down or disconnected
             var state = _client?.State;
             if (ct.IsCancellationRequested ||
                 state.HasValue &&
                 (state.Value.HasFlag(SoulseekClientStates.Disconnected) || state.Value.HasFlag(SoulseekClientStates.Disconnecting)))
             {
                 _logger.LogWarning("Search aborted for query {SearchQuery} due to connection shutdown: {Message}", query, ex.Message);
                 _healthService.RecordSearchFiltering(
                     filteredByFormat,
                     filteredByBitrate,
                     filteredBySampleRate,
                     filteredByQueue,
                     filteredByDedup,
                     0);
                 _healthService.RecordSearch(query, totalFilesReceived, resultCount, false, "Connection shutdown");
                 return resultCount; 
             }
             
             _logger.LogError(ex, "Search failed for query {SearchQuery} with mode {SearchMode}", query, mode);
             _healthService.RecordSearchFiltering(
                 filteredByFormat,
                 filteredByBitrate,
                 filteredBySampleRate,
                 filteredByQueue,
                 filteredByDedup,
                 0);
             _healthService.RecordSearch(query, totalFilesReceived, resultCount, false, ex.Message);
             // Re-throw if it's not a shutdown scenario? 
             // Actually, returning 0 or partial results is safer than crashing the flow if the search fails.
             // But let's stick to previous logic: throw if it's a real error.
             throw; 
        }
        finally
        {
            Interlocked.Decrement(ref _outboundSearchInFlight);
        }
    }

    private async Task<SoulseekClient?> WaitForReadyClientAsync(CancellationToken ct)
    {
        int initWait = 0;
        const int maxInitWait = 10;
        while (_client == null && initWait < maxInitWait)
        {
            _logger.LogDebug("Waiting for Soulseek client initialization (attempt {Attempt}/{Max})", initWait + 1, maxInitWait);
            await Task.Delay(200, ct);
            initWait++;
        }

        var client = _client;
        if (client == null)
            return null;

        if (client.State.HasFlag(SoulseekClientStates.Disconnecting) || client.State.HasFlag(SoulseekClientStates.Disconnected))
            return null;

        int waitRetries = 0;
        const int retryDelayMs = 500;
        var maxWaitRetries = Math.Max(20, GetEffectiveConnectTimeout(_config.ConnectTimeout) / retryDelayMs);
        var waitStartUtc = DateTime.UtcNow;
        var nextProgressLogAtSeconds = 2 + Random.Shared.Next(0, 2);

        while (!client.State.HasFlag(SoulseekClientStates.LoggedIn) && waitRetries < maxWaitRetries)
        {
            await Task.Delay(retryDelayMs, ct);
            waitRetries++;

            var elapsedSeconds = (int)(DateTime.UtcNow - waitStartUtc).TotalSeconds;
            if (elapsedSeconds >= nextProgressLogAtSeconds)
            {
                _logger.LogDebug(
                    "Waiting for Soulseek login... (State: {State}, Elapsed: {Elapsed}s, Attempt {Attempt}/{Max})",
                    client.State,
                    elapsedSeconds,
                    waitRetries,
                    maxWaitRetries);
                nextProgressLogAtSeconds += 2 + Random.Shared.Next(0, 2);
            }

            client = _client;
            if (client == null)
                return null;
            if (client.State.HasFlag(SoulseekClientStates.Disconnecting) || client.State.HasFlag(SoulseekClientStates.Disconnected))
                return null;
        }

        if (!client.State.HasFlag(SoulseekClientStates.LoggedIn))
        {
            _logger.LogInformation("Soulseek not logged in yet after readiness wait (State: {State})", client.State);
            return null;
        }

        return client;
    }

    public async IAsyncEnumerable<Track> StreamResultsAsync(
        string query,
        IEnumerable<string>? formatFilter,
        (int? Min, int? Max) bitrateFilter,
        DownloadMode mode,
        SearchExecutionProfile? executionProfile = null,
        [EnumeratorCancellation] CancellationToken ct = default,
        SearchScopeKind scopeKind = SearchScopeKind.Network)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<Track>();
        var searchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Exception? streamFailure = null;
        SearchLimitExceededException? capException = null;

        // Run the existing search logic in a background task
        // We use the existing SearchAsync but redirect its "onTracksFound" callback to write to the channel
        var searchTask = Task.Run(async () =>
        {
            try
            {
                await SearchCoreAsync(query, formatFilter, bitrateFilter, mode, (tracks) =>
                {
                    foreach (var track in tracks)
                    {
                        channel.Writer.TryWrite(track);
                    }
                }, executionProfile,
                onLimitExceeded: ex =>
                {
                    capException = ex;
                    searchCts.Cancel();
                },
                searchCts.Token,
                scope: scopeKind == SearchScopeKind.Wishlist ? Soulseek.SearchScope.Wishlist : null);
            }
            catch (OperationCanceledException) when (capException != null)
            {
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation, ignore
            }
            catch (Exception ex)
            {
                streamFailure = ex;
                if (ct.IsCancellationRequested || !(_client?.State.HasFlag(SoulseekClientStates.LoggedIn) ?? false))
                {
                    _logger.LogWarning("Background stream search stopped: {Message}", ex.Message);
                }
                else
                {
                    _logger.LogWarning(ex, "Error in background streaming search for {Query}", query);
                }
            }
            finally
            {
                channel.Writer.Complete(streamFailure ?? capException);
            }
        }, ct); // Use outer CT for Task scheduling.

        // Yield results from the channel
        while (await channel.Reader.WaitToReadAsync(ct))
        {
            while (channel.Reader.TryRead(out var track))
            {
                yield return track;
            }
        }

        await searchTask;

        if (streamFailure != null)
            throw streamFailure;

        if (capException != null)
            throw capException;
    }

    public async Task<List<Track>> SearchUserForTrackAsync(
        string username,
        string query,
        IEnumerable<string>? formatFilter,
        (int? Min, int? Max) bitrateFilter,
        int timeoutMs,
        CancellationToken ct = default)
    {
        var results = new List<Track>();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(query))
        {
            return results;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(500, timeoutMs)));

        try
        {
            await SearchCoreAsync(
                query,
                formatFilter,
                bitrateFilter,
                DownloadMode.Normal,
                onTracksFound: tracks => results.AddRange(tracks),
                executionProfile: null,
                onLimitExceeded: null,
                timeoutCts.Token,
                scope: Soulseek.SearchScope.User(new[] { username }));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Targeted timeout — normal when the known-good peer no longer has the file or is slow.
        }

        return results;
    }

    private Track ParseTrackFromFile(Soulseek.File file, Soulseek.SearchResponse response)
    {
        // Extract bitrate and length from file attributes
        var bitrateAttr = file.Attributes?.FirstOrDefault(a => a.Type == FileAttributeType.BitRate);
        var bitrate = bitrateAttr?.Value ?? 0;
        var lengthAttr = file.Attributes?.FirstOrDefault(a => a.Type == FileAttributeType.Length);
        var length = lengthAttr?.Value ?? 0;

        // If bitrate is not reported by peer, infer it from File Size and Length
        if (bitrate <= 0 && length > 0 && file.Size > 0)
        {
            // Inferred bitrate: (Size in bytes * 8 bits/byte) / (Length in seconds * 1000)
            bitrate = (int)((file.Size * 8) / (length * 1000));
        }
        
        var sampleRateAttr = file.Attributes?.FirstOrDefault(a => a.Type == FileAttributeType.SampleRate);
        var sampleRate = sampleRateAttr?.Value;
        
        var bitDepthAttr = file.Attributes?.FirstOrDefault(a => a.Type == FileAttributeType.BitDepth);
        var bitDepth = bitDepthAttr?.Value;

        var pathSegments = file.Filename
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Trim())
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToList();

        var rawFilename = Path.GetFileNameWithoutExtension(file.Filename);
        var cleanFilename = CleanTrackToken(rawFilename);

        string artist = "Unknown Artist";
        string title = cleanFilename;
        string album = string.Empty;

        // Path-first intelligence: treat directory chain as primary metadata source.
        if (pathSegments.Count >= 2)
        {
            var parentAlbum = CleanTrackToken(pathSegments[^2]);
            if (IsLikelyMetadataSegment(parentAlbum))
            {
                album = parentAlbum;
            }
        }

        if (pathSegments.Count >= 3)
        {
            var parentArtist = CleanTrackToken(pathSegments[^3]);
            if (IsLikelyMetadataSegment(parentArtist))
            {
                artist = parentArtist;
            }
        }

        // Safe filename fallback: only split when explicit artist-title delimiter exists.
        var filenameParts = Regex.Split(cleanFilename, @"\s[-–—]\s", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (filenameParts.Length >= 2)
        {
            var filenameArtist = CleanTrackToken(filenameParts[0]);
            var filenameTitle = CleanTrackToken(string.Join(" - ", filenameParts.Skip(1)));

            if (!string.IsNullOrWhiteSpace(filenameTitle))
            {
                // If path artist is unavailable or generic, trust filename artist.
                if (artist == "Unknown Artist" || !IsLikelyMetadataSegment(artist))
                {
                    artist = string.IsNullOrWhiteSpace(filenameArtist) ? artist : filenameArtist;
                }

                title = filenameTitle;
            }
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            title = rawFilename;
        }

        if (string.IsNullOrWhiteSpace(album) && pathSegments.Count >= 2)
        {
            album = CleanTrackToken(pathSegments[^2]);
        }

        var track = new Track
        {
            Artist = artist,
            Title = title,
            Album = album,
            PathSegments = pathSegments, // Phase 1.1: Context for the Brain
            Filename = file.Filename,
            Directory = Path.GetDirectoryName(file.Filename),
            Username = response.Username,
            Format = Path.GetExtension(file.Filename)?.TrimStart('.').ToLowerInvariant(),
            Bitrate = bitrate,
            SampleRate = sampleRate,
            BitDepth = bitDepth,
            Size = file.Size,
            Length = length,
            SoulseekFile = file,
            
            HasFreeUploadSlot = response.HasFreeUploadSlot,
            QueueLength = response.QueueLength,
            UploadSpeed = response.UploadSpeed
        };

        return track;
    }

    private static string CleanTrackToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var cleaned = Regex.Replace(value, @"^\d{1,3}[\s\-_.]+", string.Empty, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        cleaned = Regex.Replace(cleaned, @"\[[^\]]*\]|\([^\)]*\)", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return cleaned.Trim();
    }

    private static bool IsLikelyMetadataSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
            return false;

        var normalized = segment.Trim();
        if (NonMetadataPathTokens.Contains(normalized))
            return false;

        return normalized.Length >= 2;
    }

    // Delegates to ShareIndexService — the single source of truth for share-folder resolution
    // (Library Sources + the legacy SharedFolderPath/DownloadDirectory extras), so the count
    // announced to the server always matches what the serving pipeline actually indexes.
    private string[] ResolveShareFolders() => _shareIndex.ResolveShareFolders();

}
