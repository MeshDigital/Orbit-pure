using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models.Musical;

namespace SLSKDONET.Services.Library;

public sealed class SavedDoublesService : ISavedDoublesService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ILogger<SavedDoublesService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private List<SavedDouble>? _cache;

    public SavedDoublesService(ILogger<SavedDoublesService> logger)
    {
        _logger = logger;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _path = Path.Combine(appData, "ORBIT", "saved_doubles.json");
    }

    public async Task<IReadOnlyList<SavedDouble>> LoadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _cache = await ReadFromDiskAsync().ConfigureAwait(false);
            return _cache.ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IReadOnlyList<SavedDouble> doubles)
    {
        ArgumentNullException.ThrowIfNull(doubles);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _cache = NormalizeAndDedupe(doubles);
            await WriteToDiskAsync(_cache).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddOrUpdateAsync(SavedDouble pair)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _cache ??= await ReadFromDiskAsync().ConfigureAwait(false);
            var normalized = Normalize(pair);

            var existingIndex = _cache.FindIndex(item => IsSamePair(item, normalized));
            if (existingIndex >= 0)
            {
                var existing = _cache[existingIndex];
                _cache[existingIndex] = normalized with
                {
                    CreatedAt = existing.CreatedAt,
                    CachedScore = normalized.CachedScore ?? existing.CachedScore,
                    Label = string.IsNullOrWhiteSpace(normalized.Label) ? existing.Label : normalized.Label
                };
            }
            else
            {
                _cache.Add(normalized);
            }

            _cache = NormalizeAndDedupe(_cache);
            await WriteToDiskAsync(_cache).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(SavedDouble pair)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _cache ??= await ReadFromDiskAsync().ConfigureAwait(false);
            var normalized = Normalize(pair);
            _cache.RemoveAll(item => IsSamePair(item, normalized));
            await WriteToDiskAsync(_cache).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool Exists(string trackAId, string trackBId)
    {
        var (left, right) = Normalize(trackAId, trackBId);
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        _gate.Wait();
        try
        {
            _cache ??= ReadFromDisk();
            return _cache.Any(item =>
                string.Equals(item.TrackAId, left, StringComparison.Ordinal) &&
                string.Equals(item.TrackBId, right, StringComparison.Ordinal));
        }
        finally
        {
            _gate.Release();
        }
    }

    public static (string A, string B) Normalize(string id1, string id2)
        => string.Compare(id1, id2, StringComparison.Ordinal) < 0
            ? (id1, id2)
            : (id2, id1);

    private static SavedDouble Normalize(SavedDouble pair)
    {
        var (left, right) = Normalize(pair.TrackAId, pair.TrackBId);
        return pair with { TrackAId = left, TrackBId = right };
    }

    private static bool IsSamePair(SavedDouble x, SavedDouble y)
        => string.Equals(x.TrackAId, y.TrackAId, StringComparison.Ordinal) &&
           string.Equals(x.TrackBId, y.TrackBId, StringComparison.Ordinal);

    private static List<SavedDouble> NormalizeAndDedupe(IEnumerable<SavedDouble> doubles)
    {
        return doubles
            .Select(Normalize)
            .Where(item => !string.IsNullOrWhiteSpace(item.TrackAId) && !string.IsNullOrWhiteSpace(item.TrackBId))
            .GroupBy(item => (item.TrackAId, item.TrackBId))
            .Select(group => group
                .OrderByDescending(item => item.CreatedAt)
                .First())
            .OrderByDescending(item => item.CreatedAt)
            .ToList();
    }

    private async Task<List<SavedDouble>> ReadFromDiskAsync()
    {
        if (!File.Exists(_path))
            return new List<SavedDouble>();

        try
        {
            var json = await File.ReadAllTextAsync(_path).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<List<SavedDouble>>(json, JsonOptions) ?? new List<SavedDouble>();
            return NormalizeAndDedupe(parsed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed loading saved doubles from {Path}; returning empty set", _path);
            return new List<SavedDouble>();
        }
    }

    private List<SavedDouble> ReadFromDisk()
    {
        if (!File.Exists(_path))
            return new List<SavedDouble>();

        try
        {
            var json = File.ReadAllText(_path);
            var parsed = JsonSerializer.Deserialize<List<SavedDouble>>(json, JsonOptions) ?? new List<SavedDouble>();
            return NormalizeAndDedupe(parsed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed loading saved doubles from {Path}; returning empty set", _path);
            return new List<SavedDouble>();
        }
    }

    private async Task WriteToDiskAsync(IReadOnlyList<SavedDouble> doubles)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(doubles, JsonOptions);
            await FileExtensions.WriteToSafeAsync(_path, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed writing saved doubles to {Path}", _path);
        }
    }
}
