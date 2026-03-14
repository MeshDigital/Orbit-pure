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

namespace SLSKDONET.Services;

/// <summary>
/// Real Soulseek.NET adapter for network interactions.
/// </summary>
public class SoulseekAdapter : ISoulseekAdapter, IDisposable
{
    private readonly ILogger<SoulseekAdapter> _logger;
    private readonly AppConfig _config;
    private readonly IEventBus _eventBus;
    private static readonly SemaphoreSlim _connectLock = new(1, 1);
    public bool IsConnected => _client?.State.HasFlag(SoulseekClientStates.Connected) == true && 
                              !_client.State.HasFlag(SoulseekClientStates.Disconnecting);
    
    public bool IsLoggedIn => _client?.State.HasFlag(SoulseekClientStates.LoggedIn) == true;
    
    public event EventHandler<DownloadProgressEventArgs>? DownloadProgressChanged;
    public event EventHandler<DownloadCompletedEventArgs>? DownloadCompleted;

    // Rate Limiting
    private static readonly SemaphoreSlim _rateLimitLock = new(1, 1);
    private DateTime _lastSearchTime = DateTime.MinValue;

    private readonly ConcurrentDictionary<string, byte> _excludedPhrases = new();

    public SoulseekAdapter(ILogger<SoulseekAdapter> logger, AppConfig config, IEventBus eventBus)
    {
        _logger = logger;
        _config = config;
        _eventBus = eventBus;
    }

    private SoulseekClient? _client;

    public async Task ConnectAsync(string? password = null, CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct);
        try
        {
            if (IsConnected && IsLoggedIn) 
            {
                _logger.LogInformation("Already connected and logged in as {Username}.", _config.Username);
                return;
            }

            if (_client != null)
            {
                try { _client.Disconnect(); _client.Dispose(); } catch { }
            }

            _client = new SoulseekClient(minorVersion: _config.SoulseekMinorVersion);
            
            // Subscribe to state changes BEFORE connecting to catch early login states
            _client.StateChanged += (sender, args) =>
            {
                _logger.LogInformation("Soulseek state change: {State} (was {PreviousState})", 
                    args.State, args.PreviousState);
                
                _eventBus.Publish(new SoulseekStateChangedEvent(
                    args.State.ToString(), 
                    args.State.HasFlag(SoulseekClientStates.Connected) && !args.State.HasFlag(SoulseekClientStates.Disconnecting)));
            };

            // Phase 5/10: Adhere to new global exclusions from Soulseek Server
            _client.ExcludedSearchPhrasesReceived += (sender, phrases) =>
            {
                int added = 0;
                foreach (var phrase in phrases)
                {
                    if (_excludedPhrases.TryAdd(phrase.ToLowerInvariant(), 0))
                        added++;
                }
                if (added > 0)
                {
                    _logger.LogInformation("Added {Added} new excluded search phrases. Total known exclusions: {Total}", added, _excludedPhrases.Count);
                }
            };

            _logger.LogInformation("Connecting to Soulseek as {Username} on {Server}:{Port}...", 
                _config.Username, _config.SoulseekServer, _config.SoulseekPort);
            
            await _client.ConnectAsync(
                _config.SoulseekServer ?? "server.slsknet.org", 
                _config.SoulseekPort == 0 ? 2242 : _config.SoulseekPort, 
                _config.Username, 
                password, 
                ct);
            
            _logger.LogInformation("Successfully connected to Soulseek as {Username}", _config.Username);
            _eventBus.Publish(new SoulseekConnectionStatusEvent("connected", _config.Username ?? "Unknown"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Soulseek: {Message}", ex.Message);
            throw;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        if (_client != null)
        {
            _client.Disconnect();
            await Task.CompletedTask;
            _logger.LogInformation("Disconnected from Soulseek");
        }
    }

    public void Disconnect()
    {
        if (_client != null)
        {
            _client.Disconnect();
            _logger.LogInformation("Disconnected from Soulseek");
        }
    }

    public async Task<int> SearchAsync(
        string query,
        IEnumerable<string>? formatFilter,
        (int? Min, int? Max) bitrateFilter,
        DownloadMode mode, // Add DownloadMode parameter
        Action<IEnumerable<Track>> onTracksFound,
        CancellationToken ct = default)
    {
        // Wait briefly for the client to be created if ConnectAsync is still initializing
        int initWait = 0;
        const int maxInitWait = 10; // ~2s total
        while (_client == null && initWait < maxInitWait)
        {
            _logger.LogInformation("Waiting for Soulseek client initialization (retry {Attempt}/{Max})...", initWait + 1, maxInitWait);
            await Task.Delay(200, ct);
            initWait++;
        }

        if (_client == null)
        {
            throw new InvalidOperationException("Soulseek client not initialized yet. ConnectAsync may not have completed.");
        }

        // Wait for Soulseek to be fully logged in before searching
        // Fixes: "The server connection must be connected and logged in" error on startup
        int waitRetries = 0;
        const int maxWaitRetries = 20; // Increased to 10s (20 * 500ms)
        const int retryDelayMs = 500;
        
        // Wait until we are Connected AND LoggedIn (and not LoggingIn)
        while ((!_client.State.HasFlag(SoulseekClientStates.LoggedIn)) && waitRetries < maxWaitRetries)
        {
            _logger.LogInformation("Waiting for Soulseek login... (State: {State}, Attempt {Attempt}/{Max})", _client.State, waitRetries + 1, maxWaitRetries);
            await Task.Delay(retryDelayMs, ct);
            waitRetries++;
        }

        if (!_client.State.HasFlag(SoulseekClientStates.LoggedIn))
        {
            _logger.LogError("Soulseek failed to login within {Seconds} seconds. State: {State}", maxWaitRetries * retryDelayMs / 1000.0, _client.State);
            throw new InvalidOperationException($"Soulseek failed to login in time. State: {_client.State}. Cannot perform search.");
        }

        var directories = new ConcurrentDictionary<string, List<Soulseek.File>>();
        var resultCount = 0;
        var totalFilesReceived = 0;
        var filteredByFormat = 0;
        var filteredByBitrate = 0;
        var formatSet = formatFilter?.Select(f => f.ToLowerInvariant()).ToHashSet();

        try
        {
            var minBitrateStr = bitrateFilter.Min?.ToString() ?? "0";
            var maxBitrateStr = bitrateFilter.Max?.ToString() ?? "unlimited";
            
            // Golden Rule: Rate Limiting (500ms global delay)
            await _rateLimitLock.WaitAsync(ct);
            try
            {
                var timeSinceLast = DateTime.UtcNow - _lastSearchTime;
                if (timeSinceLast.TotalMilliseconds < 550) // 550ms just to be safe
                {
                    var delay = 550 - (int)timeSinceLast.TotalMilliseconds;
                    _logger.LogDebug("Rate Limiting: Delaying search by {Ms}ms", delay);
                    await Task.Delay(delay, ct);
                }
                _lastSearchTime = DateTime.UtcNow;
            }
            finally
            {
                _rateLimitLock.Release();
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
                         return 0;
                     }
                 }
            }
            
            var searchQuery = Soulseek.SearchQuery.FromText(query);
            var options = new SearchOptions(
                searchTimeout: 30000, // 30 seconds
                responseLimit: 1000,
                fileLimit: 10000
            );

            // The SearchAsync method in the library (or wrapper) seems to handle the waiting internally 
            // based on the stack trace showing SearchToCallbackAsync waiting.
            // So we just await the search initialization/execution.
            var searchResult = await _client!.SearchAsync(
                searchQuery,
                (response) =>
                {
                    _logger.LogDebug("Received response from {User} with {Count} files", response.Username, response.Files.Count());
                    
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
                            totalFilesReceived++;
                            
                            // Apply format filter
                            var extension = Path.GetExtension(file.Filename)?.TrimStart('.').ToLowerInvariant();
                            if (formatSet != null && formatSet.Any() && !formatSet.Contains(extension ?? ""))
                            {
                                filteredByFormat++;
                                if (filteredByFormat <= 3) // Log first 3 filtered items
                                {
                                    _logger.LogInformation("[FILTER] Rejected by format: {File} (extension: {Ext}, allowed: {Formats})", file.Filename, extension, string.Join(", ", formatSet));
                                }
                                continue;
                            }

                            // Soulseek Network Adherence: Filter out excluded phrases
                            if (_excludedPhrases.Count > 0)
                            {
                                bool isExcluded = false;
                                var lowerPath = file.Filename.ToLowerInvariant();
                                foreach (var phrase in _excludedPhrases.Keys)
                                {
                                    if (lowerPath.Contains(phrase))
                                    {
                                        isExcluded = true;
                                        break;
                                    }
                                }
                                if (isExcluded)
                                {
                                    continue;
                                }
                            }

                            // NEW Phase 12.3: Extract Bitrate quickly to avoid allocating full Track object if it fails filters
                            var bitrateAttr = file.Attributes?.FirstOrDefault(a => a.Type == Soulseek.FileAttributeType.BitRate);
                            var rawBitrate = bitrateAttr?.Value ?? 0;
                            
                            // Apply bitrate filter BEFORE object allocation
                            if (bitrateFilter.Min.HasValue && rawBitrate < bitrateFilter.Min.Value)
                            {
                                filteredByBitrate++;
                                _logger.LogTrace("Filtered by bitrate (too low): {File} ({Bitrate} < {Min})", file.Filename, rawBitrate, bitrateFilter.Min.Value);
                                continue;
                            }
                            if (bitrateFilter.Max.HasValue && rawBitrate > bitrateFilter.Max.Value && bitrateFilter.Max.Value > 0)
                            {
                                filteredByBitrate++;
                                _logger.LogTrace("Filtered by bitrate (too high): {File} ({Bitrate} > {Max})", file.Filename, rawBitrate, bitrateFilter.Max.Value);
                                continue;
                            }

                            // Memory Optimization: Only allocate Track object for files that survive the filters
                            // Use the helper method to parse metadata correctly
                            var track = ParseTrackFromFile(file, response);

                            if (resultCount <= 3) // Log first 3 matches
                            {
                                _logger.LogInformation("[ACCEPT] Track passed filters: {Artist} - {Title} ({Bitrate} kbps, {Ext})", track.Artist, track.Title, track.Bitrate, extension);
                            }
                            foundTracksInResponse.Add(track);
                            resultCount++;
                        }
                    }

                    if (foundTracksInResponse.Any())
                    {
                        onTracksFound(foundTracksInResponse);
                    }
                },
                options: options,
                cancellationToken: ct
            );

            if (mode == DownloadMode.Album)
            {
                _logger.LogInformation("Found {Count} potential album directories.", directories.Count);
                // TODO: In a future step, we would rank these directories and create album download jobs.
                // For now, we will just log them.
                resultCount = directories.Count;
            }

            _logger.LogInformation("Search completed: {ResultCount} results from {TotalFiles} files (filtered: {FormatFiltered} by format, {BitrateFiltered} by bitrate)",
                resultCount, totalFilesReceived, filteredByFormat, filteredByBitrate);
            
            return resultCount;
        }
        catch (OperationCanceledException)
        {
             _logger.LogInformation("Search cancelled for query {SearchQuery}", query);
             return resultCount; // Return whatever we found before cancellation
        }
        catch (Exception ex)
        {
             // Check if we are shutting down or disconnected
             if (ct.IsCancellationRequested || _client?.State == SoulseekClientStates.Disconnected || _client?.State == SoulseekClientStates.Disconnecting)
             {
                 _logger.LogWarning("Search aborted for query {SearchQuery} due to connection shutdown: {Message}", query, ex.Message);
                 return resultCount; 
             }
             
             _logger.LogError(ex, "Search failed for query {SearchQuery} with mode {SearchMode}", query, mode);
             // Re-throw if it's not a shutdown scenario? 
             // Actually, returning 0 or partial results is safer than crashing the flow if the search fails.
             // But let's stick to previous logic: throw if it's a real error.
             throw; 
        }
    }

    public async IAsyncEnumerable<Track> StreamResultsAsync(
        string query,
        IEnumerable<string>? formatFilter,
        (int? Min, int? Max) bitrateFilter,
        DownloadMode mode,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<Track>();
        var searchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Run the existing search logic in a background task
        // We use the existing SearchAsync but redirect its "onTracksFound" callback to write to the channel
        var searchTask = Task.Run(async () =>
        {
            try
            {
                await SearchAsync(query, formatFilter, bitrateFilter, mode, (tracks) =>
                {
                    foreach (var track in tracks)
                    {
                        channel.Writer.TryWrite(track);
                    }
                }, searchCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation, ignore
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested || _client?.State != SoulseekClientStates.LoggedIn)
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
                channel.Writer.Complete();
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

        // Await the task to ensure we catch any exceptions or ensure clean exit
        // (Though we swallowed exceptions above to ensure channel closes, checking here is good hygiene)
        // await searchTask; 
    }

    /// <summary>
    /// Progressive search strategy: Tries multiple search queries with increasing leniency.
    /// 1. Strict: "Artist - Title" (exact match expected)
    /// 2. Relaxed: "Artist Title" (keyword-based)
    /// 3. Album: Album-based search (fallback)
    /// Returns results from the first successful strategy.
    /// </summary>
    public async Task<int> ProgressiveSearchAsync(
        string artist,
        string title,
        string? album,
        IEnumerable<string>? formatFilter,
        (int? Min, int? Max) bitrateFilter,
        Action<IEnumerable<Track>> onTracksFound,
        CancellationToken ct = default)
    {
        if (_client == null)
        {
            throw new InvalidOperationException("Not connected to Soulseek");
        }

        var maxAttempts = _config.MaxSearchAttempts;
        _logger.LogInformation("Starting progressive search: {Artist} - {Title} (album: {Album})", artist, title, album ?? "unknown");

        // Strategy 1: Strict search "Artist - Title"
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (ct.IsCancellationRequested)
                return 0;

            try
            {
                var strictQuery = $"{artist} - {title}";
                _logger.LogInformation("Attempt {Attempt}/{Max}: Strict search: {Query}", attempt, maxAttempts, strictQuery);
                
                var resultCount = await SearchAsync(
                    strictQuery,
                    formatFilter,
                    bitrateFilter,
                    DownloadMode.Normal,
                    onTracksFound,
                    ct);
                
                if (resultCount > 0)
                {
                    _logger.LogInformation("Progressive search succeeded with strict query after {Attempt} attempt(s)", attempt);
                    return resultCount;
                }

                if (attempt < maxAttempts)
                    await Task.Delay(500, ct); // Brief delay before retry
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Strict search attempt {Attempt} failed", attempt);
            }
        }

        // Strategy 2: Relaxed search "Artist Title"
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (ct.IsCancellationRequested)
                return 0;

            try
            {
                var relaxedQuery = $"{artist} {title}";
                _logger.LogInformation("Attempt {Attempt}/{Max}: Relaxed search: {Query}", attempt, maxAttempts, relaxedQuery);
                
                var resultCount = await SearchAsync(
                    relaxedQuery,
                    formatFilter,
                    bitrateFilter,
                    DownloadMode.Normal,
                    onTracksFound,
                    ct);
                
                if (resultCount > 0)
                {
                    _logger.LogInformation("Progressive search succeeded with relaxed query after {Attempt} attempt(s)", attempt);
                    return resultCount;
                }

                if (attempt < maxAttempts)
                    await Task.Delay(500, ct); // Brief delay before retry
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Relaxed search attempt {Attempt} failed", attempt);
            }
        }

        // Strategy 3: Album search (fallback)
        if (!string.IsNullOrEmpty(album))
        {
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (ct.IsCancellationRequested)
                    return 0;

                try
                {
                    _logger.LogInformation("Attempt {Attempt}/{Max}: Album search: {Query}", attempt, maxAttempts, album);
                    
                    var resultCount = await SearchAsync(
                        album,
                        formatFilter,
                        bitrateFilter,
                        DownloadMode.Album,
                        onTracksFound,
                        ct);
                    
                    if (resultCount > 0)
                    {
                        _logger.LogInformation("Progressive search succeeded with album search after {Attempt} attempt(s)", attempt);
                        return resultCount;
                    }

                    if (attempt < maxAttempts)
                        await Task.Delay(500, ct); // Brief delay before retry
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Album search attempt {Attempt} failed", attempt);
                }
            }
        }

        _logger.LogWarning("Progressive search exhausted all strategies for {Artist} - {Title}", artist, title);
        return 0;
    }

    private Track ParseTrackFromFile(Soulseek.File file, Soulseek.SearchResponse response)
    {
        // Extract bitrate and length from file attributes
        var bitrateAttr = file.Attributes?.FirstOrDefault(a => a.Type == FileAttributeType.BitRate);
        var bitrate = bitrateAttr?.Value ?? 0;
        var lengthAttr = file.Attributes?.FirstOrDefault(a => a.Type == FileAttributeType.Length);
        var length = lengthAttr?.Value ?? 0;

        // Path-Aware Extraction: Split full path into segments for context scoring
        var pathSegments = file.Filename.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        // Parse filename for raw metadata
        var rawFilename = Path.GetFileNameWithoutExtension(file.Filename);
        
        // Basic cleanup of leading numbers (e.g. "01 - ", "01.")
        var cleanFilename = System.Text.RegularExpressions.Regex.Replace(rawFilename, @"^\d+[\s\-_\.]+", "").Trim();

        // Simplified splitting: many Soulseek files follow "Artist - Title" or "Artist-Title"
        var parts = cleanFilename.Split(new[] { " - ", " -", "- ", "-" }, 2, StringSplitOptions.RemoveEmptyEntries);

        string artist = "Unknown Artist";
        string title = cleanFilename;
        string album = "";

        if (parts.Length >= 2)
        {
            artist = parts[0].Trim();
            title = parts[1].Trim();
        }
        else if (pathSegments.Count >= 2)
        {
            // Folder structure fallback: .../Artist/Album/Track.mp3
            // The Scoring Engine will look deep into pathSegments, 
            // but we provide a "best guess" here for the UI.
            album = pathSegments[^2];
            if (pathSegments.Count >= 3) artist = pathSegments[^3];
        }

        return new Track
        {
            Artist = artist,
            Title = title,
            Album = album,
            PathSegments = pathSegments, // Phase 1.1: Context for the Brain
            Filename = file.Filename,
            Directory = Path.GetDirectoryName(file.Filename),
            Username = response.Username,
            Bitrate = bitrate,
            Size = file.Size,
            Length = length,
            SoulseekFile = file,
            
            HasFreeUploadSlot = response.HasFreeUploadSlot,
            QueueLength = response.QueueLength,
            UploadSpeed = response.UploadSpeed
        };
    }

    public async Task<bool> DownloadAsync(
        string username,
        string filename,
        string outputPath,
        long? size = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        long startOffset = 0)  // Phase 2.5: Add resume support
    {
        if (this._client == null)
        {
            throw new InvalidOperationException("Not connected to Soulseek");
        }

        try
        {
            this._logger.LogInformation("Downloading {Filename} from {Username} to {OutputPath} (offset: {Offset})", 
                filename, username, outputPath, startOffset);
            
            // Check if already cancelled
            ct.ThrowIfCancellationRequested();

            var directory = Path.GetDirectoryName(outputPath);
            if (directory != null)
                System.IO.Directory.CreateDirectory(directory);

            // Track state for timeout logic
            DateTime lastActivity = DateTime.UtcNow;
            long lastBytes = startOffset;  // Start from existing bytes
            bool isQueued = false;

            var downloadOptions = new TransferOptions(
                stateChanged: (args) =>
                {
                    // Update queued status
                    if (args.Transfer.State.HasFlag(TransferStates.Queued))
                    {
                        isQueued = true;
                    }
                    else if (args.Transfer.State.HasFlag(TransferStates.InProgress))
                    {
                        isQueued = false;
                        
                        // Check for progress activity
                        if (args.Transfer.BytesTransferred > lastBytes)
                        {
                            lastBytes = args.Transfer.BytesTransferred;
                            lastActivity = DateTime.UtcNow;
                        }

                        if (size.HasValue && size.Value > 0)
                        {
                            double percentage = (double)args.Transfer.BytesTransferred / size.Value;
                            progress?.Report(percentage);
                            
                            DownloadProgressChanged?.Invoke(this, new DownloadProgressEventArgs(
                                filename, username, percentage, args.Transfer.BytesTransferred, size.Value));
                        }
                    }
                });

            // Phase 2.5: Use Append mode if resuming, Create if starting fresh
            var fileMode = startOffset > 0 ? FileMode.Append : FileMode.Create;
            using var fileStream = new FileStream(outputPath, fileMode, FileAccess.Write, FileShare.None, 8192, useAsync: true);
            
            // We wrap the Soulseek DownloadAsync in our own task to enforce our custom timeout logic
            // The underlying client has some timeout logic, but we want granular control over "Stalled vs Queued"
            var downloadTask = this._client.DownloadAsync(
                username,
                filename,
                () => Task.FromResult((Stream)fileStream),
                size,
                startOffset: startOffset,  // Pass the offset to Soulseek client
                options: downloadOptions,
                cancellationToken: ct);

            // Monitoring Loop
            while (!downloadTask.IsCompleted)
            {
                // Check if we should time out
                // Modified: Only timeout if NOT queued and no activity for 60s
                if (!isQueued && (DateTime.UtcNow - lastActivity).TotalSeconds > 60)
                {
                    // STALLED: Not queued, but no bytes moved for 60s
                    throw new TimeoutException("Transfer stalled for 60 seconds (0 bytes received).");
                }
                
                // If we are queued, we WAIT INDEFINITELY (or until user cancels)
                // This is the key fix: Don't timeout if we are just waiting in line.

                await Task.WhenAny(downloadTask, Task.Delay(1000, ct));
            }

            await downloadTask; // Propagate exceptions/completion

            this._logger.LogInformation("Download completed: {Filename}", filename);
            progress?.Report(1.0);
            _eventBus.Publish(new TransferFinishedEvent(filename, username));
            
            DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs(filename, username, true));
            
            return true;
        }
        catch (OperationCanceledException)
        {
            this._logger.LogWarning("Download cancelled: {Filename}", filename);
            _eventBus.Publish(new TransferCancelledEvent(filename, username));
            
            DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs(filename, username, false, "Cancelled"));
            
            throw; 
        }
        catch (TimeoutException ex)
        {
            this._logger.LogWarning("Download timeout: {Filename} from {Username} - {Message}", filename, username, ex.Message);
            _eventBus.Publish(new TransferFailedEvent(filename, username, "Connection timeout"));
            
            DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs(filename, username, false, "Timeout"));
            
            return false;
        }
        catch (IOException ex)
        {
            this._logger.LogError(ex, "I/O error during download: {Filename} from {Username}", filename, username);
            _eventBus.Publish(new TransferFailedEvent(filename, username, "I/O error: " + ex.Message));
            
            DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs(filename, username, false, "I/O Error: " + ex.Message));
            
            return false;
        }
        catch (Exception ex) when (ex.Message.Contains("refused") || ex.Message.Contains("aborted") || ex.Message.Contains("Unable to read"))
        {
            this._logger.LogWarning("Network error during download: {Filename} from {Username} - {Message}", filename, username, ex.Message);
            _eventBus.Publish(new TransferFailedEvent(filename, username, "Connection failed"));
            
            DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs(filename, username, false, "Connection Failed"));
            
            return false;
        }
        catch (Soulseek.TransferRejectedException ex)
        {
             // RETHROW: "Too many files" or "Banned" 
             // This allows DownloadManager to catch it and trigger Exponential Backoff / Retry
             DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs(filename, username, false, "Rejected: " + ex.Message));
             throw; 
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Download failed: {Message}", ex.Message);
            _eventBus.Publish(new TransferFailedEvent(filename, username, ex.Message));
            
            DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs(filename, username, false, ex.Message));
            
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            _client?.Disconnect();
            _client?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing SoulseekClient");
        }
    }
}
