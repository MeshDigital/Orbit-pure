using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.ViewModels.Library;

namespace SLSKDONET.Services.Library;

public interface ILibraryTrackListViewStateStore
{
    LibraryTrackListViewState? Load(string key);
    Task SaveAsync(string key, LibraryTrackListViewState state, CancellationToken cancellationToken = default);
}

/// <summary>
/// Stores one small JSON document per Library view beside the library database. Keeping this
/// separate from config.ini avoids rewriting the whole application configuration while scrolling
/// or typing, and the per-playlist temp-file + replace write prevents partial JSON on a crash.
/// </summary>
public sealed class LibraryTrackListViewStateStore : ILibraryTrackListViewStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILogger<LibraryTrackListViewStateStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _rootDirectory;

    public LibraryTrackListViewStateStore(
        ILogger<LibraryTrackListViewStateStore>? logger = null,
        string? rootDirectory = null)
    {
        _logger = logger ?? NullLogger<LibraryTrackListViewStateStore>.Instance;
        _rootDirectory = rootDirectory ?? DefaultRoot();
    }

    public static string DefaultRoot()
    {
        var dbDirectory = Path.GetDirectoryName(OrbitPaths.LibraryDbPath);
        return Path.Combine(
            string.IsNullOrEmpty(dbDirectory) ? AppContext.BaseDirectory : dbDirectory,
            "LibraryViewState");
    }

    public LibraryTrackListViewState? Load(string key)
    {
        try
        {
            var path = PathFor(key);
            if (!File.Exists(path)) return null;
            var state = JsonSerializer.Deserialize<LibraryTrackListViewState>(File.ReadAllText(path), JsonOptions);
            state?.Normalize();
            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            _logger.LogWarning(ex, "Could not restore Library view state {Key}; using defaults", key);
            return null;
        }
    }

    public async Task SaveAsync(
        string key,
        LibraryTrackListViewState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Normalize();
        var path = PathFor(key);
        var json = JsonSerializer.Serialize(state, JsonOptions);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_rootDirectory);
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string PathFor(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A view-state key is required.", nameof(key));
        foreach (var c in key)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                throw new ArgumentException("View-state keys may contain only letters, digits, and dashes.", nameof(key));
        }
        return Path.Combine(_rootDirectory, key + ".json");
    }
}
