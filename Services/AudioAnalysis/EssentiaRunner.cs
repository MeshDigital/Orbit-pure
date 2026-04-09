using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data.Essentia;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Wrapper around the <c>essentia_streaming_extractor_music</c> CLI binary.
/// Runs the extractor on a decoded WAV file and parses the JSON output into
/// <see cref="EssentiaOutput"/>. Returns <see langword="null"/> gracefully when
/// the binary is not installed.
/// </summary>
public sealed class EssentiaRunner
{
    private readonly string? _binaryPath;
    private readonly ILogger<EssentiaRunner> _logger;

    public EssentiaRunner(ILogger<EssentiaRunner> logger)
    {
        _logger     = logger;
        _binaryPath = ResolveEssentiaBinary();

        if (_binaryPath is null)
            _logger.LogInformation("[EssentiaRunner] Binary not found — BPM/key extraction will be skipped.");
        else
            _logger.LogDebug("[EssentiaRunner] Using Essentia binary at {Path}", _binaryPath);
    }

    /// <summary>
    /// Returns <see langword="true"/> when the Essentia streaming extractor
    /// binary was located; <see langword="false"/> means it is not installed.
    /// </summary>
    public bool IsAvailable => _binaryPath is not null;

    /// <summary>
    /// Runs <c>essentia_streaming_extractor_music</c> on <paramref name="wavPath"/>
    /// and returns the parsed output. Returns <see langword="null"/> when the binary
    /// is not found or the extraction fails.
    /// </summary>
    public async Task<EssentiaOutput?> RunAsync(
        string wavPath,
        CancellationToken ct = default)
    {
        if (_binaryPath is null)
            return null;

        if (!File.Exists(wavPath))
        {
            _logger.LogWarning("[EssentiaRunner] Input WAV not found: {Path}", wavPath);
            return null;
        }

        string jsonOut = Path.Combine(Path.GetTempPath(),
            $"orbit_essentia_{Guid.NewGuid():N}.json");

        try
        {
            string args = BuildArgs(wavPath, jsonOut);
            _logger.LogDebug("[EssentiaRunner] Running extractor for {Wav}", wavPath);

            var (exitCode, stderr) = await RunProcessAsync(_binaryPath, args, ct)
                .ConfigureAwait(false);

            if (exitCode != 0)
            {
                _logger.LogWarning(
                    "[EssentiaRunner] Extractor exited {Code} for {Wav}: {Err}",
                    exitCode, wavPath, stderr.Trim());
                return null;
            }

            if (!File.Exists(jsonOut))
            {
                _logger.LogWarning("[EssentiaRunner] No JSON output produced for {Wav}", wavPath);
                return null;
            }

            return await ParseJsonAsync(jsonOut, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("[EssentiaRunner] Cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[EssentiaRunner] Failed for {Wav}", wavPath);
            return null;
        }
        finally
        {
            TryDelete(jsonOut);
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static string BuildArgs(string inputWav, string outputJson)
    {
        // Standard streaming extractor invocation: <binary> <input> <output> [profile]
        var sb = new StringBuilder();
        sb.Append($"\"{inputWav}\" ");
        sb.Append($"\"{outputJson}\"");
        return sb.ToString();
    }

    private static async Task<EssentiaOutput?> ParseJsonAsync(
        string jsonPath, CancellationToken ct)
    {
        using var stream = new FileStream(
            jsonPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65536, useAsync: true);

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return await JsonSerializer.DeserializeAsync<EssentiaOutput>(stream, options, ct)
            .ConfigureAwait(false);
    }

    private static async Task<(int ExitCode, string Stderr)> RunProcessAsync(
        string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardError  = true,
            RedirectStandardOutput = true,   // consume stdout to prevent pipe filling
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        using var process = new Process { StartInfo = psi };
        var stderrBuilder = new StringBuilder();

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) stderrBuilder.AppendLine(e.Data);
        };

        process.Start();
        process.BeginErrorReadLine();
        // Drain stdout to avoid deadlocks when the binary writes to it
        _ = process.StandardOutput.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            throw;
        }

        return (process.ExitCode, stderrBuilder.ToString());
    }

    /// <summary>
    /// Searches for the Essentia streaming extractor binary in known locations.
    /// </summary>
    private static string? ResolveEssentiaBinary()
    {
        // Candidate binary names (Windows has .exe; Linux/macOS do not)
        string[] names = OperatingSystem.IsWindows()
            ? ["essentia_streaming_extractor_music.exe", "streaming_extractor_music.exe"]
            : ["essentia_streaming_extractor_music", "streaming_extractor_music"];

        // 1. Bundled alongside the executable
        foreach (string name in names)
        {
            string bundled = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(bundled)) return bundled;
        }

        // 2. Bundled in Tools/essentia/ sub-directory
        foreach (string name in names)
        {
            string tools = Path.Combine(AppContext.BaseDirectory, "Tools", "essentia", name);
            if (File.Exists(tools)) return tools;
        }

        // 3. System PATH
        foreach (string name in names)
        {
            if (IsOnPath(name)) return name;
        }

        return null;
    }

    private static bool IsOnPath(string binaryName)
    {
        try
        {
            var psi = new ProcessStartInfo(binaryName, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(2000);
            return p?.ExitCode == 0 || p?.ExitCode == 1; // both acceptable (some print to stderr)
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
    }
}
