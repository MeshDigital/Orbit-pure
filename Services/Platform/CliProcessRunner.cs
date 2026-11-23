using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;

namespace SLSKDONET.Services.Platform;

/// <summary>Outcome of one command-line tool run.</summary>
/// <param name="Started">False when the tool doesn't exist or couldn't start (e.g. <c>secret-tool</c> not installed).</param>
/// <param name="TimedOut">True when the run was killed after its timeout.</param>
public sealed record CliResult(bool Started, int ExitCode, string StdOut, string StdErr, bool TimedOut = false)
{
    public bool Succeeded => Started && !TimedOut && ExitCode == 0;
    public static CliResult NotStarted(string why) => new(false, -1, string.Empty, why);
}

/// <summary>
/// Runs a platform tool (macOS <c>security</c>, Linux <c>secret-tool</c>) safely: arguments go through
/// <see cref="ProcessStartInfo.ArgumentList"/> (no hand-built quoting), secrets only ever travel on <b>stdin</b> (never
/// in argv, which every local user can read with <c>ps</c>), and every run has a timeout after which the process
/// tree is killed (#313/#314).
/// </summary>
public interface ICliProcessRunner
{
    CliResult Run(string fileName, IReadOnlyList<string> arguments, string? standardInput, TimeSpan timeout);
}

public sealed class CliProcessRunner : ICliProcessRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    public CliResult Run(string fileName, IReadOnlyList<string> arguments, string? standardInput, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start()) return CliResult.NotStarted($"{fileName} did not start");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return CliResult.NotStarted($"{fileName} is not available: {ex.Message}");
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (standardInput is not null)
        {
            process.StandardInput.Write(standardInput);
            process.StandardInput.Close();
        }

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* intentional: it exited between the timeout and the kill */ }
            return new CliResult(true, -1, string.Empty, $"{fileName} timed out after {timeout.TotalSeconds:0} s", TimedOut: true);
        }

        process.WaitForExit(); // flush the redirected streams
        return new CliResult(true, process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }
}
