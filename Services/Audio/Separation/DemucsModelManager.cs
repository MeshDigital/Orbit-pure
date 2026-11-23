using System;
using System.IO;
using System.Security.Cryptography;

namespace SLSKDONET.Services.Audio.Separation;

/// <summary>Whether the Demucs model on disk is the file ORBIT expects (#319).</summary>
public enum DemucsModelIntegrity
{
    /// <summary>No model file.</summary>
    Missing,
    /// <summary>The file's SHA-256 equals <see cref="DemucsModelManager.PinnedSha256"/>.</summary>
    Verified,
    /// <summary>No checksum is pinned yet: the file is used, and its hash is logged so it can be pinned.</summary>
    Unpinned,
    /// <summary>A checksum is pinned and the file differs (corrupt or replaced): it is never loaded.</summary>
    Mismatch,
}

/// <summary>
/// Resolves the Demucs-4s ONNX model file, checks its integrity against a pinned SHA-256 (#319), and provides the
/// model tag (SHA-256 prefix) the stem cache uses for invalidation.
/// <para>
/// There is no official Demucs ONNX download: the previously shown URL
/// (<c>facebookresearch/demucs/releases/download/v4.0.0/demucs-4s.onnx</c>) returns 404 and that repository has no
/// releases. Until the project publishes a model file of its own, <see cref="PinnedSha256"/> is null and a model the
/// user places by hand is accepted as <see cref="DemucsModelIntegrity.Unpinned"/>. Once a file is published, pin its
/// hash here: from then on any other file is refused, exactly like the pinned FFmpeg download.
/// </para>
/// Expected layout: <c>Tools/Essentia/models/demucs-4s.onnx</c> (bundled), else
/// <c>%AppData%/Antigravity/Models/demucs-4s.onnx</c> (placed by the user).
/// </summary>
public sealed class DemucsModelManager
{
    /// <summary>File name for the Demucs v4 ONNX 4-stem model.</summary>
    public const string ModelFileName = "demucs-4s.onnx";

    /// <summary>
    /// SHA-256 (hex) of the one model file ORBIT accepts. Null = nothing published yet, so no pin. When a model is
    /// published (e.g. as an ORBIT GitHub release asset), set this to its hash.
    /// </summary>
    public static readonly string? PinnedSha256 = null;

    private readonly string _resolvedPath;
    private readonly string? _expectedSha256;
    private readonly object _hashGate = new();
    private string? _cachedHash;
    private DateTime _cachedHashStamp;

    /// <param name="expectedSha256">Overrides <see cref="PinnedSha256"/> (tests).</param>
    public DemucsModelManager(string? customModelPath = null, string? expectedSha256 = null)
    {
        _resolvedPath = customModelPath ?? ResolveDefaultPath();
        _expectedSha256 = expectedSha256 ?? PinnedSha256;
    }

    /// <summary>Absolute path to the Demucs-4s ONNX model file.</summary>
    public string ModelPath => _resolvedPath;

    /// <summary>True if the model file exists on disk (it may still fail <see cref="CheckIntegrity"/>).</summary>
    public bool IsAvailable => File.Exists(_resolvedPath);

    /// <summary>True when the model exists and is not a known mismatch: safe to load.</summary>
    public bool IsUsable => CheckIntegrity() is DemucsModelIntegrity.Verified or DemucsModelIntegrity.Unpinned;

    /// <summary>Full SHA-256 of the model file (hex, upper case), cached until the file changes; null when missing.</summary>
    public string? Sha256
    {
        get
        {
            if (!IsAvailable) return null;
            lock (_hashGate)
            {
                var stamp = File.GetLastWriteTimeUtc(_resolvedPath);
                if (_cachedHash is not null && stamp == _cachedHashStamp) return _cachedHash;
                using var stream = File.OpenRead(_resolvedPath);
                _cachedHash = Convert.ToHexString(SHA256.HashData(stream));
                _cachedHashStamp = stamp;
                return _cachedHash;
            }
        }
    }

    /// <summary>Compares the file with the pinned checksum.</summary>
    public DemucsModelIntegrity CheckIntegrity()
    {
        if (Sha256 is not { } actual) return DemucsModelIntegrity.Missing;
        if (string.IsNullOrWhiteSpace(_expectedSha256)) return DemucsModelIntegrity.Unpinned;
        return string.Equals(actual, _expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase)
            ? DemucsModelIntegrity.Verified
            : DemucsModelIntegrity.Mismatch;
    }

    /// <summary>What to tell the user when the model can't be used, or null when it can.</summary>
    public string? UnusableReason() => CheckIntegrity() switch
    {
        DemucsModelIntegrity.Missing =>
            $"The stem separation model isn't installed. Put a Demucs v4 4-stem ONNX export named {ModelFileName} in {GetUserModelDirectory()}.",
        DemucsModelIntegrity.Mismatch =>
            $"The stem separation model at {_resolvedPath} doesn't match the expected file (it may be corrupt or replaced), so it was not loaded. Replace it with the published {ModelFileName}.",
        _ => null,
    };

    /// <summary>
    /// Short stable tag derived from the first 8 hex chars of the file's SHA-256, matching the StemCacheService
    /// model-tag convention. "demucs-4s-missing" when the model isn't there.
    /// </summary>
    public string ModelTag => Sha256 is { } hash ? "demucs-4s-" + hash[..8].ToLowerInvariant() : "demucs-4s-missing";

    /// <summary>The user-writable folder where the model is expected when it isn't bundled.</summary>
    public static string GetUserModelDirectory()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Antigravity", "Models");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string ResolveDefaultPath()
    {
        // 1. Bundled in Tools directory (ships with the application)
        string bundled = Path.Combine(AppContext.BaseDirectory, "Tools", "Essentia", "models", ModelFileName);
        if (File.Exists(bundled)) return bundled;

        // 2. User AppData (placed by the user)
        string userData = Path.Combine(GetUserModelDirectory(), ModelFileName);
        if (File.Exists(userData)) return userData;

        // 3. The preferred location even if not there yet
        return userData;
    }
}
