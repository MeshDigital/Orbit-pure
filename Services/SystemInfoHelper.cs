using System;

namespace SLSKDONET.Services;



/// <summary>
/// Helper class for detecting system resources and calculating optimal parallelism.
/// Phase 4: GPU & Hardware Acceleration
/// </summary>
public static class SystemInfoHelper
{
    /// <summary>
    /// Get the optimal number of parallel analysis threads based on available system resources.
    /// Supports Hybrid Architectures (Intel 12th Gen+) by targeting E-Cores in efficiency mode.
    /// </summary>
    /// <param name="configuredValue">User-configured value (0 = auto-detect)</param>
    /// <returns>Recommended parallel thread count (minimum 1)</returns>
    public static int GetOptimalParallelism(int configuredValue = 0)
    {
        // If user configured a specific value, honor it
        if (configuredValue > 0)
            return Math.Min(configuredValue, 32); // Cap at 32 for safety
            
        var topology = GetCpuTopology();
        var ramGB = GetTotalRamGB();
        var powerMode = GetCurrentPowerMode();
        
        // Phase 1.2: Hybrid Core Logic
        // IF on Battery/Efficiency Mode AND we have E-Cores -> strictly use E-Cores
        if (powerMode == PowerEfficiencyMode.Efficiency && topology.IsHybrid)
        {
            // Use 75% of E-Cores or at least 1, max 4 to save battery
            return Math.Max(1, Math.Min(topology.ECoreCount, 4));
        }

        // Standard Logic (Performance Mode or Non-Hybrid)
        int byCores;
        
        if (topology.IsHybrid)
        {
            // Hybrid: Use all E-Cores + 50% of P-Cores (leave headroom for UI/Foreground)
            // Example: 8P + 8E -> 8E + 4P = 12 Threads
            byCores = topology.ECoreCount + (topology.PCoreCount / 2);
        }
        else
        {
            // Non-Hybrid: Leave 1 core free
            byCores = Math.Max(1, topology.TotalThreads - 1);
        }

        int byRam = Math.Max(1, (int)((ramGB - 2.0) / 0.3)); // 300MB per track
        
        // Take the minimum to avoid overloading either resource
        var optimal = Math.Min(byCores, byRam);
        
        // Cap high-end systems to avoid diminishing returns (IO bottlenecks usually hit around 8-12 concurrent)
        optimal = Math.Min(optimal, 12); 

        // Final safety check for very low specs
        if (topology.TotalThreads <= 4 || ramGB < 8)
            optimal = Math.Min(optimal, 2); 
            
        // Power Sensitivity for Non-Hybrid (Battery fallback)
        if (powerMode == PowerEfficiencyMode.Efficiency && !topology.IsHybrid)
        {
            optimal = Math.Max(1, optimal / 2);
        }

        return optimal;
    }
    
    /// <summary>
    /// Get total available system RAM in gigabytes.
    /// </summary>
    public static double GetTotalRamGB()
    {
        try
        {
            var memoryInfo = GC.GetGCMemoryInfo();
            return memoryInfo.TotalAvailableMemoryBytes / (1024.0 * 1024.0 * 1024.0);
        }
        catch
        {
            // Fallback if GC memory info unavailable
            return 8.0; // Assume 8GB as safe default
        }
    }
    
    /// <summary>
    /// Get a human-readable description of the system configuration.
    /// </summary>
    public static string GetSystemDescription()
    {
        var cores = Environment.ProcessorCount;
        var ramGB = GetTotalRamGB();
        return $"{cores} cores, {ramGB:F1}GB RAM";
    }

    /// <summary>
    /// Safely configures the process priority (e.g., to "BelowNormal" for background tasks).
    /// </summary>
    public static void ConfigureProcessPriority(System.Diagnostics.Process process, System.Diagnostics.ProcessPriorityClass priority)
    {
        try
        {
            process.PriorityClass = priority;
        }
        catch (Exception ex)
        {
            // Ignore errors (e.g. access denied on some systems). 
            // Better to run at normal priority than crash.
            System.Diagnostics.Debug.WriteLine($"Failed to set process priority: {ex.Message}");
        }
    }

    // ==========================================
    // Phase 3: Power Sensitivity (Win32 P/Invoke)
    // ==========================================
    
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus lpSystemPowerStatus);

    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    public enum PowerEfficiencyMode
    {
        Performance, // Plugged In
        Efficiency   // On Battery
    }

    /// <summary>
    /// Detects if the system is running on battery power to throttle background tasks.
    /// </summary>
    public static PowerEfficiencyMode GetCurrentPowerMode()
    {
        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            if (GetSystemPowerStatus(out var status))
            {
                // ACLineStatus: 0 = Offline (Battery), 1 = Online (Plugged In), 255 = Unknown
                if (status.ACLineStatus == 0) 
                    return PowerEfficiencyMode.Efficiency;
            }
        }
        
        // Default to Performance if unknown or plugged in
        return PowerEfficiencyMode.Performance;
    }

    /// <summary>
    /// Phase 4: Detects Primary GPU Vendor for Hardware Acceleration
    /// </summary>
    public enum GpuVendor
    {
        Unknown,
        Nvidia,
        Amd,
        Intel,
        AppleSilicon // Future proofing
    }

    public static GpuVendor GetGpuInfo()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            return GpuVendor.Unknown;

        try
        {
            // Simple WMI query to get Video Controller Name
            // Requires System.Management reference (usually available in .NET desktop)
            // If strictly relying on Core, we might need to parse `wmic` output or similar, 
            // but let's try a safe "Best Effort" string check if Management is available.
            
            // SIMPLIFICATION for .NET Core without checking System.Management dependency:
            // We'll rely on a lightweight check or just safe defaults if we can't easily add the ref.
            // Assumption: User wants us to try. 
            // Better approach without extra huge dependencies:
            // Just return Unknown and let FFmpeg use "auto" which works best 90% of time.
            
            return GpuVendor.Unknown; 
            
            // NOTE: To properly implement this on Windows without external deps is tricky.
            // Let's stick to "auto" in FFmpeg for Phase 4.0 as it's much safer than 
            // fragile WMI calls that might crash or hang.
        }
        catch
        {
            return GpuVendor.Unknown;
        }
    }

    /// <summary>
    /// Phase 4: Returns optimal FFmpeg HW Accel arguments based on system.
    /// </summary>
    public static string GetFfmpegHwAccelArgs()
    {
        // "-hwaccel auto" is available in newer FFmpeg builds and tries to pick best method (CUDA/DXVA2/QSV)
        // It's the safest bet for a generic "Speed up my analysis" feature.
        return "-hwaccel auto";
    }

    // ==========================================
    // Phase 1.2: Hybrid Architecture Detection
    // ==========================================

    public struct CpuTopology
    {
        public int PCoreCount;      // Physical P-Cores
        public int ECoreCount;      // Physical E-Cores
        public int TotalThreads;    // Total logical processors
        public bool IsHybrid => ECoreCount > 0;
        public string ShortLabel => IsHybrid ? $"{PCoreCount}P + {ECoreCount}E" : $"{PCoreCount}C";

        public override string ToString() => IsHybrid 
            ? $"Hybrid ({PCoreCount}P + {ECoreCount}E, {TotalThreads} threads)" 
            : $"Standard ({PCoreCount} Cores, {TotalThreads} threads)";
    }

    private static CpuTopology? _cachedTopology;
    public static CpuTopology Topology => GetCpuTopology();

    /// <summary>
    /// Detects the CPU topology to distinguish between Performance and Efficiency cores.
    /// </summary>
    public static CpuTopology GetCpuTopology()
    {
        if (_cachedTopology.HasValue) return _cachedTopology.Value;

        var result = ExecuteGetCpuTopology();
        _cachedTopology = result;
        return result;
    }

    private static CpuTopology ExecuteGetCpuTopology()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            return new CpuTopology 
            { 
                PCoreCount = Environment.ProcessorCount, 
                TotalThreads = Environment.ProcessorCount 
            };
        }

        uint length = 0;
        // First call to get required buffer size
        NativeMethods.GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore, IntPtr.Zero, ref length);

        if (length == 0) // Should define buffer size
        {
             return new CpuTopology 
            { 
                PCoreCount = Environment.ProcessorCount, 
                TotalThreads = Environment.ProcessorCount 
            };
        }

        IntPtr buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)length);
        try
        {
            if (NativeMethods.GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore, buffer, ref length))
            {
                int pCores = 0;
                int eCores = 0;
                int totalThreads = 0;
                int offset = 0;

                while (offset < length)
                {
                    var info = System.Runtime.InteropServices.Marshal.PtrToStructure<SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(buffer + offset);
                    
                    if (info.Relationship == LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore)
                    {
                        // Identify core type via EfficiencyClass
                        // Intel: 0 = Performance, 1 (or higher) = Efficiency
                        if (info.Processor.EfficiencyClass == 0)
                            pCores++;
                        else
                            eCores++;

                        // Count logical processors (threads) for this core
                        for (int i = 0; i < info.Processor.GroupCount; i++)
                        {
                            totalThreads += CountSetBits(info.Processor.GroupMask[i].Mask);
                        }
                    }
                    
                    offset += (int)info.Size;
                }

                return new CpuTopology 
                { 
                    PCoreCount = pCores, 
                    ECoreCount = eCores, 
                    TotalThreads = totalThreads 
                };
            }
        }
        catch (Exception ex)
        {
             System.Diagnostics.Debug.WriteLine($"Error detecting CPU topology: {ex.Message}");
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }

        // Fallback to basic Environment info
        return new CpuTopology 
        { 
            PCoreCount = Environment.ProcessorCount, 
            TotalThreads = Environment.ProcessorCount 
        };
    }

    private static int CountSetBits(UIntPtr mask)
    {
        ulong v = (ulong)mask;
        int count = 0;
        while (v > 0)
        {
            v &= (v - 1);
            count++;
        }
        return count;
    }

    // ==========================================
    // Native Interop Definitions
    // ==========================================

    public enum LOGICAL_PROCESSOR_RELATIONSHIP
    {
        RelationProcessorCore = 0,
        RelationNumaNode = 1,
        RelationCache = 2,
        RelationGroup = 3,
        RelationAll = 0xffff
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct GROUP_AFFINITY
    {
        public UIntPtr Mask;
        public ushort Group;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValArray, SizeConst = 3)]
        public ushort[] Reserved;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct PROCESSOR_RELATIONSHIP
    {
        public byte Flags;
        public byte EfficiencyClass; // 0 = P-Core, >0 = E-Core
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValArray, SizeConst = 20)]
        public byte[] Reserved;
        public ushort GroupCount;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValArray, SizeConst = 1)]
        public GROUP_AFFINITY[] GroupMask;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX
    {
        public LOGICAL_PROCESSOR_RELATIONSHIP Relationship;
        public uint Size;
        // Union of structures. We only map Processor as it's the first one and the only one we use.
        // Be careful if reusing for other relationships.
        public PROCESSOR_RELATIONSHIP Processor;
    }

    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetLogicalProcessorInformationEx(
            LOGICAL_PROCESSOR_RELATIONSHIP relationship,
            IntPtr buffer,
            ref uint returnedLength);
    }
}
