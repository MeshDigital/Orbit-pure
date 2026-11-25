using System;
using Microsoft.ML.OnnxRuntime;

namespace SLSKDONET.Services.Platform;

/// <summary>
/// Centralizes ONNX Runtime execution-provider selection per OS/RID (#454):
/// uses DirectML on Windows builds compiled with <c>ORBIT_ONNX_DIRECTML</c>,
/// and cleanly falls back to CPU on macOS (<c>osx-x64</c>, <c>osx-arm64</c>), Linux (<c>linux-x64</c>),
/// or Windows machines where DirectML is unavailable.
/// </summary>
public static class OnnxExecutionProviderHelper
{
    /// <summary>True when this build was compiled with the Windows DirectML ONNX Runtime package.</summary>
    public static bool IsDirectMlCompiled =>
#if ORBIT_ONNX_DIRECTML
        true;
#else
        false;
#endif

    /// <summary>
    /// Attempts to append the best available hardware execution provider to <paramref name="options"/>.
    /// Returns true if a hardware EP was attached (with <paramref name="providerName"/> set to e.g. "DirectML"),
    /// or false when CPU execution should be used (with <paramref name="error"/> populated if a hardware EP threw).
    /// </summary>
    public static bool TryEnableHardwareProvider(
        SessionOptions options,
        int dmlDeviceId,
        out string providerName,
        out Exception? error)
    {
        error = null;
#if ORBIT_ONNX_DIRECTML
        if (OperatingSystem.IsWindows())
        {
            try
            {
                options.AppendExecutionProvider_DML(dmlDeviceId);
                providerName = "DirectML";
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }
#endif
        providerName = "CPU";
        return false;
    }
}
