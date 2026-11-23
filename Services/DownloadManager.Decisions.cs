using System;
using SLSKDONET.Views;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json; // Phase 2A: Checkpoint serialization
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Serilog.Context;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services.InputParsers;
using SLSKDONET.Services.AutoDownload;
using SLSKDONET.Utils;
using SLSKDONET.Services.Models;
using SLSKDONET.Services.Downloads;
using SLSKDONET.Data.Essentia;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Services.Repositories; // [NEW] Namespace
using SLSKDONET.Services.IO; // Added explicit using
using SLSKDONET.Events;

namespace SLSKDONET.Services;

/// <summary>Self-contained static download decisions moved out of the 226 KB DownloadManager (#307).</summary>
public partial class DownloadManager
{
    internal sealed record TransferFailureDisposition(
        DownloadFailureReason RetryFailureReason,
        bool AllowHedgeFailover,
        /// <summary>
        /// Fixed retry delay.  Null means use exponential seconds backoff (1s → 2s → 4s → 8s …).
        /// </summary>
        TimeSpan? Delay,
        string OperatorMessage);

    internal static async Task<DownloadDiscoveryService.DiscoveryResult> ResolveDiscoveryWithStrictGateAsync(
        bool strictModeEnabled,
        bool allowFuzzyFallback,
        Func<Task<(Track? BestMatch, AutoSearchDiagnostics Diagnostics)>> strictSearch,
        Func<Task<DownloadDiscoveryService.DiscoveryResult>> legacyDiscovery,
        Action<AutoSearchDiagnostics>? onStrictMatch = null,
        Action<AutoSearchDiagnostics, bool>? onStrictMiss = null)
    {
        if (!strictModeEnabled)
        {
            return await legacyDiscovery();
        }

        var strictResult = await strictSearch();
        if (strictResult.BestMatch != null)
        {
            onStrictMatch?.Invoke(strictResult.Diagnostics);
            return new DownloadDiscoveryService.DiscoveryResult(strictResult.BestMatch, null, null);
        }

        onStrictMiss?.Invoke(strictResult.Diagnostics, allowFuzzyFallback);

        if (!allowFuzzyFallback)
        {
            return new DownloadDiscoveryService.DiscoveryResult(null, null, null);
        }

        return await legacyDiscovery();
    }

    internal static TransferFailureDisposition ClassifyTransferFailure(Exception ex)
    {
        var message = ex.Message ?? string.Empty;

        if (message.Contains("banned", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("not authorized", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("access denied", StringComparison.OrdinalIgnoreCase))
        {
            return new TransferFailureDisposition(
                DownloadFailureReason.RemoteAccessDenied,
                AllowHedgeFailover: false,
                Delay: null,
                OperatorMessage: "peer denied transfer access");
        }

        if (ex.GetType().Name.Contains("TransferRejectedException", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Too many files", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("queue", StringComparison.OrdinalIgnoreCase))
        {
            return new TransferFailureDisposition(
                DownloadFailureReason.RemoteQueueDenied,
                AllowHedgeFailover: true,
                Delay: TimeSpan.FromSeconds(30),  // was 2 min — hedge failover fires first anyway; 30s is enough before re-try
                OperatorMessage: "peer queue rejected transfer");
        }

        if (message.Contains("refused", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("aborted", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Unable to read", StringComparison.OrdinalIgnoreCase) ||
            ex is IOException)
        {
            // Transient TCP failure → seconds-scale back-off (Epic 4.1)
            return new TransferFailureDisposition(
                DownloadFailureReason.NetworkError,
                AllowHedgeFailover: true,
                Delay: null,   // null → exponential seconds backoff in retry logic
                OperatorMessage: "network transfer error");
        }

        if (ex is TimeoutException)
        {
            // Transient timeout → seconds-scale back-off (Epic 4.1)
            return new TransferFailureDisposition(
                DownloadFailureReason.Timeout,
                AllowHedgeFailover: true,
                Delay: null,   // null → exponential seconds backoff in retry logic
                OperatorMessage: "transfer timeout");
        }

        return new TransferFailureDisposition(
            DownloadFailureReason.PeerRejected,
            AllowHedgeFailover: false,
            Delay: null,
            OperatorMessage: string.IsNullOrWhiteSpace(message) ? "peer rejected transfer" : message);
    }

    internal static string? ResolveDiscoveryReason(string? sourceProvenance, string? matchReason, string? scoreBreakdown)
    {
        var preferredDiscoveryReason = !string.IsNullOrWhiteSpace(matchReason)
            ? matchReason
            : scoreBreakdown;

        if (string.Equals(sourceProvenance, "ShieldSanitized", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(preferredDiscoveryReason)
                ? "🛡 Shield sanitized search"
                : $"🛡 Shield sanitized · {preferredDiscoveryReason}";
        }

        return string.IsNullOrWhiteSpace(preferredDiscoveryReason)
            ? null
            : preferredDiscoveryReason;
    }
}
