using System;
using System.Text.RegularExpressions;
using Soulseek;
using SLSKDONET.Models;

namespace SLSKDONET.Services
{
    /// <summary>
    /// Phase 14: Forensic Core - Pre-download file authenticity verification.
    /// </summary>
    public static class MetadataForensicService
    {
        private static readonly Regex VbrRegex = new Regex(@"V\d+|VBR", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex LosslessRegex = new Regex(@"\.(flac|wav|aiff|alac)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SuspiciousExtensions = new Regex(@"\.(wma|ogg|wmv)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly HashSet<int> SuspiciousLossyBitrates = new() { 128, 160, 192, 256, 320 };

        public static bool IsSuspiciousLossless(Track result)
        {
            if (result == null || result.Bitrate <= 0)
                return false;

            var extension = result.Format;
            if (string.IsNullOrWhiteSpace(extension) && !string.IsNullOrWhiteSpace(result.Filename))
            {
                extension = System.IO.Path.GetExtension(result.Filename)?.TrimStart('.');
            }

            return string.Equals(extension, "flac", StringComparison.OrdinalIgnoreCase)
                && SuspiciousLossyBitrates.Contains(result.Bitrate);
        }

        public static string? GetSuspiciousLosslessReason(Track result)
        {
            if (!IsSuspiciousLossless(result))
                return null;

            var quality = string.Join(" / ", new[]
            {
                result.BitDepth.HasValue ? $"{result.BitDepth.Value}-bit" : string.Empty,
                result.SampleRate.HasValue ? $"{result.SampleRate.Value / 1000.0:F1} kHz" : string.Empty
            }.Where(s => !string.IsNullOrWhiteSpace(s)));

            return string.IsNullOrWhiteSpace(quality)
                ? $"Suspicious FLAC: reported lossy bitrate {result.Bitrate} kbps."
                : $"Suspicious FLAC: reported lossy bitrate {result.Bitrate} kbps ({quality}).";
        }

        public static int CalculateTrustScore(Track result)
        {
            int score = 50; 

            if (result.Bitrate > 0)
            {
                if (result.Bitrate >= 320) score += 10;
                else if (result.Bitrate < 128) score -= 20;
            }

            if (string.IsNullOrEmpty(result.Filename)) return score;

            var ext = System.IO.Path.GetExtension(result.Filename)?.ToLower();
            if (LosslessRegex.IsMatch(result.Filename))
            {
                score += 20; 
                if (result.Length.HasValue && result.Size > 0)
                {
                    double minutes = result.Length.Value / 60.0;
                    if (minutes > 0)
                    {
                        double mbPerMin = (result.Size.Value / 1024.0 / 1024.0) / minutes;
                        if (mbPerMin < 2.5) score -= 40; 
                    }
                }
            }
            else if (ext == ".mp3" || ext == ".m4a") score += 5;
            else if (SuspiciousExtensions.IsMatch(result.Filename)) score -= 10;

            if (result.Bitrate > 0 && result.Length.HasValue && result.Length > 0 && result.Size.HasValue)
            {
                if (result.Bitrate >= 320 && (ext == ".mp3"))
                {
                    double expectedBytes = (result.Bitrate * 1000.0 / 8.0) * result.Length.Value;
                    double actualBytes = result.Size.Value;
                    if (actualBytes < (expectedBytes * 0.75)) score -= 50; 
                    else if (actualBytes > (expectedBytes * 1.25)) score -= 10; 
                    else score += 10;
                }
            }

            if (result.UploadSpeed > 0) score += 5; 
            if (result.HasFreeUploadSlot) score += 10; 

            if (IsSuspiciousLossless(result)) score -= 70;

            return Math.Clamp(score, 0, 100);
        }

        public static string GetForensicAssessment(Track result)
        {
            var notes = new System.Collections.Generic.List<string>();
            if (string.IsNullOrEmpty(result.Filename)) return "Unknown";

            var suspiciousReason = GetSuspiciousLosslessReason(result);
            if (!string.IsNullOrWhiteSpace(suspiciousReason)) notes.Add($"🚫 {suspiciousReason}");
            
            var ext = System.IO.Path.GetExtension(result.Filename)?.ToLower();

            if (result.Bitrate >= 320 && result.Length.HasValue && (ext == ".mp3") && result.Size.HasValue)
            {
                double expectedBytes = (result.Bitrate * 1000.0 / 8.0) * result.Length.Value;
                if (result.Size.Value < (expectedBytes * 0.75))
                    notes.Add("⚠️ SIZE MISMATCH: Use caution. File is too small for 320kbps.");
                else if (result.Size.Value > (expectedBytes * 0.90) && result.Size.Value < (expectedBytes * 1.10))
                    notes.Add("✅ VERIFIED: Size matches bitrate perfectly.");
            }

            if (LosslessRegex.IsMatch(result.Filename)) notes.Add("💎 LOSSLESS: High fidelity format.");
            if (result.HasFreeUploadSlot) notes.Add("⚡ INSTANT: Slot available now.");

            if (notes.Count == 0) return "Standard Result";
            return string.Join(" | ", notes);
        }

        public static bool IsGoldenMatch(Track result) => CalculateTrustScore(result) >= 85;
        public static bool IsFake(Track result) => CalculateTrustScore(result) < 40;

        public static SearchTier CalculateTier(Track result)
        {
            if (IsSuspiciousLossless(result)) return SearchTier.Garbage;

            int score = CalculateTrustScore(result);
            if (score >= 85) return SearchTier.Platinum;
            if (score >= 70) return SearchTier.Gold;
            if (score >= 50) return SearchTier.Silver;
            if (score >= 35) return SearchTier.Bronze;
            return SearchTier.Garbage;
        }

        public static string GetTierColor(SearchTier tier) => tier switch
        {
            SearchTier.Platinum => "#E5E4E2",
            SearchTier.Gold => "#FFD700",
            SearchTier.Silver => "#C0C0C0",
            SearchTier.Bronze => "#CD7F32",
            SearchTier.Garbage => "#8B0000",
            _ => "#A0A0A0"
        };

        public static string GetTierDescription(SearchTier tier) => tier switch
        {
            SearchTier.Platinum => "👑 High Fidelity (320kbps+), Trusted User, Complete Metadata.",
            SearchTier.Gold => "💎 Excellent Quality (256kbps+), High Trust Score.",
            SearchTier.Silver => "🥈 Standard Quality (192kbps+), Acceptable match.",
            SearchTier.Bronze => "🥉 Mixed Metadata or lower bitrate.",
            SearchTier.Garbage => "🗑️ Suspect Integrity (Possible upscale, fake or very low quality).",
            _ => "Unknown Quality Analysis."
        };

        public static string GetTierBadge(SearchTier tier) => tier switch
        {
            SearchTier.Platinum => "👑",
            SearchTier.Gold => "💎",
            SearchTier.Silver => "🥈",
            SearchTier.Bronze => "🥉",
            SearchTier.Garbage => "🗑️",
            _ => "❓"
        };

        public static int CalculateTrustScore(PlaylistTrack result)
        {
            if (result.Integrity == Data.IntegrityLevel.Verified) return 100;
            if (result.Integrity == Data.IntegrityLevel.Suspicious) return 20;

            int score = 50;
            int bitrate = result.Bitrate ?? 0;

            if (bitrate >= 320) score += 10;
            else if (bitrate < 128 && bitrate > 0) score -= 20;

            bool isLossless = !string.IsNullOrEmpty(result.Format) && 
                (result.Format.Equals("flac", StringComparison.OrdinalIgnoreCase) || 
                 result.Format.Equals("wav", StringComparison.OrdinalIgnoreCase));

            if (isLossless) score += 20;

            if (!string.IsNullOrEmpty(result.ResolvedFilePath) && System.IO.File.Exists(result.ResolvedFilePath))
            {
                var fileInfo = new System.IO.FileInfo(result.ResolvedFilePath);
                long size = fileInfo.Length;
                double durationSec = (result.CanonicalDuration ?? 0) / 1000.0;

                if (durationSec > 0)
                {
                    double minutes = durationSec / 60.0;
                    if (bitrate >= 320 && !isLossless)
                    {
                        double expectedBytes = (320 * 1000.0 / 8.0) * durationSec;
                        if (size < (expectedBytes * 0.75)) score -= 50;
                    }
                    else if (isLossless)
                    {
                        double mbPerMin = (size / 1024.0 / 1024.0) / minutes;
                        if (mbPerMin < 2.5) score -= 40;
                    }
                }
            }

            return Math.Clamp(score, 0, 100);
        }

        public static bool IsFake(PlaylistTrack result) => CalculateTrustScore(result) < 40;

        public static SearchTier CalculateTier(PlaylistTrack result)
        {
            if (IsFake(result) || (result.Bitrate ?? 0) < 64) return SearchTier.Garbage;
            int score = CalculateTrustScore(result);
            if (score >= 85) return SearchTier.Platinum;
            if (score >= 70) return SearchTier.Gold;
            if (score >= 50) return SearchTier.Silver;
            if (score >= 35) return SearchTier.Bronze;
            return SearchTier.Garbage;
        }
    }
}
