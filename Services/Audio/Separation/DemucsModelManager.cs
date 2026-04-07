using System;
using System.IO;
using System.Security.Cryptography;

namespace SLSKDONET.Services.Audio.Separation;

/// <summary>
/// Resolves and validates the Demucs-4s ONNX model file path, and provides
/// a stable model tag (SHA-256 prefix) for the stem cache invalidation system.
///
/// Expected model layout:
///   Tools/Essentia/models/demucs-4s.onnx   (primary)
///   [AppData]/Antigravity/Models/demucs-4s.onnx  (user-downloaded)
/// </summary>
public sealed class DemucsModelManager
{
    /// <summary>File name for the Demucs v4 ONNX 4-stem model.</summary>
    public const string ModelFileName = "demucs-4s.onnx";

    /// <summary>
    /// Public download URL for the Demucs-4s ONNX export.
    /// MIT-licensed model by Alexandre Défossez et al.
    /// </summary>
    public const string ModelDownloadUrl =
        "https://github.com/facebookresearch/demucs/releases/download/v4.0.0/demucs-4s.onnx";

    private readonly string _resolvedPath;
    private string? _cachedTag;

    public DemucsModelManager(string? customModelPath = null)
    {
        _resolvedPath = customModelPath ?? ResolveDefaultPath();
    }

    /// <summary>Absolute path to the Demucs-4s ONNX model file.</summary>
    public string ModelPath => _resolvedPath;

    /// <summary>True if the model file exists on disk.</summary>
    public bool IsAvailable => File.Exists(_resolvedPath);

    /// <summary>
    /// Short stable tag derived from the first 8 hex chars of the file's SHA-256.
    /// Matches the StemCacheService model-tag convention.
    /// Returns "demucs-4s-missing" when the model is not yet downloaded.
    /// </summary>
    public string ModelTag
    {
        get
        {
            if (!IsAvailable) return "demucs-4s-missing";
            if (_cachedTag != null) return _cachedTag;

            using var sha = SHA256.Create();
            using var stream = File.OpenRead(_resolvedPath);
            byte[] hash = sha.ComputeHash(stream);
            _cachedTag = "demucs-4s-" + Convert.ToHexString(hash)[..8].ToLowerInvariant();
            return _cachedTag;
        }
    }

    /// <summary>
    /// Returns the user-writable path where the model should be stored
    /// when downloaded automatically.
    /// </summary>
    public static string GetUserModelDirectory()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Antigravity", "Models");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ──────────────────────────────────── helpers ─────────────────────────

    private static string ResolveDefaultPath()
    {
        // 1. Bundled in Tools directory (ships with the application)
        string bundled = Path.Combine(
            AppContext.BaseDirectory, "Tools", "Essentia", "models", ModelFileName);
        if (File.Exists(bundled)) return bundled;

        // 2. User AppData (e.g. downloaded on first run)
        string userData = Path.Combine(GetUserModelDirectory(), ModelFileName);
        if (File.Exists(userData)) return userData;

        // 3. Return the preferred write location even if not yet downloaded
        return userData;
    }
}
