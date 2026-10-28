using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SLSKDONET.Services.Platform;

/// <summary>Heavy features that are switched off on machines that can't run them properly.</summary>
public enum HardwareFeature
{
    /// <summary>BPM, key, waveform, loudness and cue analysis of tracks.</summary>
    TrackAnalysis,
    /// <summary>ONNX models: genre, mood, similarity embeddings and CUE-DETR cue confidence.</summary>
    AiTagging,
    /// <summary>Demucs stem separation (Edit Lab, Workstation stem mixer).</summary>
    StemSeparation,
}

public sealed record GpuInfo(string Name, long DedicatedMemoryBytes);

/// <summary>Whether a feature runs here, and in plain words why not (or what limits it).</summary>
public sealed record FeatureVerdict(HardwareFeature Feature, bool IsAllowed, string? Reason, string? Advice = null)
{
    public string DisplayName => Feature switch
    {
        HardwareFeature.TrackAnalysis => "Track analysis (BPM, key, cues)",
        HardwareFeature.AiTagging => "AI genre, mood and similarity",
        HardwareFeature.StemSeparation => "Stem separation",
        _ => Feature.ToString(),
    };
}

/// <summary>
/// What this machine is, and which heavy features it is allowed to run. ORBIT's analyzer and AI
/// models ship for 64-bit Windows only (the ONNX runtime package is DirectML, the Essentia
/// analyzer is a Windows binary), and stem separation / AI tagging need enough memory not to
/// thrash the PC. Below the minimum the feature is switched off (a hard block, not a warning),
/// and the setup wizard lists what was switched off and why.
/// </summary>
public sealed class HardwareProfile
{
    public const long GB = 1024L * 1024 * 1024;

    // Minimums. Memory is compared with a little slack: an "8 GB" PC reports ~7.8 GB usable.
    public const long AnalysisMinRam = 4 * GB;
    public const int AnalysisMinCores = 2;
    public const long AiMinRam = 8 * GB;
    public const int AiMinCores = 4;
    public const long StemsMinRamWithGpu = 8 * GB;
    public const long StemsMinRamCpuOnly = 16 * GB;
    public const long StemsMinGpuMemory = 4 * GB;
    private const double MemorySlack = 0.9;

    public required string OsName { get; init; }
    public required bool IsWindows { get; init; }
    public required Architecture Architecture { get; init; }
    public required int LogicalCores { get; init; }
    public required long TotalMemoryBytes { get; init; }
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = Array.Empty<GpuInfo>();

    public GpuInfo? BestGpu => Gpus.OrderByDescending(g => g.DedicatedMemoryBytes).FirstOrDefault();

    /// <summary>The platform ORBIT's native analyzer and AI runtime are built for.</summary>
    public bool IsSupportedPlatform => IsWindows && Architecture == Architecture.X64;

    public IReadOnlyList<FeatureVerdict> Verdicts => _verdicts ??= Enum.GetValues<HardwareFeature>().Select(Evaluate).ToList();
    private IReadOnlyList<FeatureVerdict>? _verdicts;

    public bool Allows(HardwareFeature feature) => Verdicts.First(v => v.Feature == feature).IsAllowed;

    public FeatureVerdict VerdictFor(HardwareFeature feature) => Verdicts.First(v => v.Feature == feature);

    public IEnumerable<FeatureVerdict> Blocked => Verdicts.Where(v => !v.IsAllowed);

    public string Summary
    {
        get
        {
            var gpu = BestGpu is { } g
                ? $"{g.Name} ({FormatGb(g.DedicatedMemoryBytes)} video memory)"
                : "no dedicated graphics card found";
            return string.Create(CultureInfo.InvariantCulture,
                $"{OsName} {Architecture.ToString().ToLowerInvariant()}, {LogicalCores} CPU threads, {FormatGb(TotalMemoryBytes)} memory, {gpu}");
        }
    }

    private FeatureVerdict Evaluate(HardwareFeature feature)
    {
        if (!IsSupportedPlatform)
        {
            return new FeatureVerdict(feature, false,
                $"ORBIT's analyzer and AI models are built for 64-bit Windows; this is {OsName} on {Architecture.ToString().ToLowerInvariant()}.");
        }

        return feature switch
        {
            HardwareFeature.TrackAnalysis =>
                !HasRam(AnalysisMinRam) ? Block(feature, $"needs {FormatGb(AnalysisMinRam)} memory; this PC has {FormatGb(TotalMemoryBytes)}")
                : LogicalCores < AnalysisMinCores ? Block(feature, $"needs {AnalysisMinCores} CPU threads; this PC has {LogicalCores}")
                : Allow(feature),

            HardwareFeature.AiTagging =>
                !HasRam(AiMinRam) ? Block(feature, $"needs {FormatGb(AiMinRam)} memory; this PC has {FormatGb(TotalMemoryBytes)}")
                : LogicalCores < AiMinCores ? Block(feature, $"needs {AiMinCores} CPU threads; this PC has {LogicalCores}")
                : Allow(feature),

            HardwareFeature.StemSeparation => EvaluateStems(),

            _ => Allow(feature),
        };
    }

    private FeatureVerdict EvaluateStems()
    {
        var gpuOk = BestGpu is { } g && g.DedicatedMemoryBytes >= StemsMinGpuMemory * MemorySlack;
        if (gpuOk && HasRam(StemsMinRamWithGpu)) return Allow(HardwareFeature.StemSeparation);
        if (HasRam(StemsMinRamCpuOnly))
            return Allow(HardwareFeature.StemSeparation) with
            {
                Advice = "No graphics card with 4 GB video memory, so separating runs on the CPU and takes a few minutes per track."
            };

        return Block(HardwareFeature.StemSeparation,
            $"needs {FormatGb(StemsMinRamCpuOnly)} memory, or {FormatGb(StemsMinRamWithGpu)} with a graphics card that has {FormatGb(StemsMinGpuMemory)} video memory; this PC has {FormatGb(TotalMemoryBytes)}"
            + (BestGpu is { } best ? $" and {FormatGb(best.DedicatedMemoryBytes)} video memory" : " and no dedicated graphics card"));
    }

    private bool HasRam(long minimum) => TotalMemoryBytes >= minimum * MemorySlack;

    private static FeatureVerdict Allow(HardwareFeature f) => new(f, true, null);

    private static FeatureVerdict Block(HardwareFeature f, string reason) => new(f, false, char.ToUpperInvariant(reason[0]) + reason[1..] + ".");

    public static string FormatGb(long bytes)
    {
        var gb = bytes / (double)GB;
        return gb >= 10 || Math.Abs(gb - Math.Round(gb)) < 0.05
            ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(gb):0} GB")
            : string.Create(CultureInfo.InvariantCulture, $"{gb:0.0} GB");
    }
}

/// <summary>
/// Detects the machine once per process. Engines consult <see cref="Current"/> before loading a
/// model, so a blocked feature reports itself unavailable through the paths that already handle
/// "model missing".
/// </summary>
public static class HardwareCapabilities
{
    private static readonly Lazy<HardwareProfile> Detected = new(Detect);

    /// <summary>Tests replace the detected machine with a fixed profile.</summary>
    internal static HardwareProfile? Override { get; set; }

    public static HardwareProfile Current => Override ?? Detected.Value;

    public static bool Allows(HardwareFeature feature) => Current.Allows(feature);

    private static HardwareProfile Detect()
    {
        var os = OperatingSystem.IsWindows() ? "Windows"
            : OperatingSystem.IsMacOS() ? "macOS"
            : OperatingSystem.IsLinux() ? "Linux"
            : RuntimeInformation.OSDescription;

        return new HardwareProfile
        {
            OsName = os,
            IsWindows = OperatingSystem.IsWindows(),
            Architecture = RuntimeInformation.OSArchitecture,
            LogicalCores = Environment.ProcessorCount,
            // Physical memory as the runtime sees it (also honours container limits).
            TotalMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            Gpus = OperatingSystem.IsWindows() ? DetectWindowsGpus() : Array.Empty<GpuInfo>(),
        };
    }

    /// <summary>
    /// Display adapters from the registry. qwMemorySize is the real dedicated VRAM (WMI's
    /// AdapterRAM is a 32-bit field that caps at 4 GB). Virtual/basic display drivers are skipped.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IReadOnlyList<GpuInfo> DetectWindowsGpus()
    {
        var result = new List<GpuInfo>();
        try
        {
            using var display = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (display == null) return result;

            foreach (var name in display.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                using var adapter = display.OpenSubKey(name);
                var desc = adapter?.GetValue("DriverDesc") as string;
                if (string.IsNullOrWhiteSpace(desc) || desc.Contains("Basic", StringComparison.OrdinalIgnoreCase)
                    || desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || desc.Contains("Remote", StringComparison.OrdinalIgnoreCase))
                    continue;

                long memory = adapter!.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long q => q,
                    byte[] { Length: 8 } b => BitConverter.ToInt64(b),
                    int i => (uint)i,
                    byte[] { Length: 4 } b => BitConverter.ToUInt32(b),
                    _ => adapter.GetValue("HardwareInformation.MemorySize") switch
                    {
                        int i => (uint)i,
                        byte[] { Length: 4 } b => BitConverter.ToUInt32(b),
                        _ => 0L,
                    },
                };
                if (result.All(g => g.Name != desc)) result.Add(new GpuInfo(desc, memory));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            Serilog.Log.Warning(ex, "Hardware check: couldn't read display adapters from the registry");
        }
        return result;
    }
}
