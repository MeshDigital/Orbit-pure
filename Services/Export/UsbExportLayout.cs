using System;
using System.Linq;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Export;

/// <summary>How the USB export tree is organised.</summary>
public enum UsbLayoutMode
{
    ByKey,
    ByBpmBracket,
    ByGenre,
    ByPlaylist,
}

/// <summary>
/// LF7 (#349): computes the folder a track lands in for a USB export layout, as real copies (not
/// hardlinks/symlinks — FAT32/exFAT sticks support neither). Folder names are FAT32-safe.
/// </summary>
public static class UsbExportLayout
{
    private static readonly char[] Fat32Invalid = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    public static string GetFolder(Track track, UsbLayoutMode mode, string? playlistName = null)
    {
        return mode switch
        {
            UsbLayoutMode.ByKey => SafeFolder(track.MusicalKey ?? "Unknown"),
            UsbLayoutMode.ByBpmBracket => BpmBracket(track.BPM),
            UsbLayoutMode.ByGenre => SafeFolder(track.Genres ?? "Unknown"),
            UsbLayoutMode.ByPlaylist => SafeFolder(playlistName ?? "Unknown"),
            _ => "Unknown",
        };
    }

    private static string BpmBracket(double? bpm)
    {
        if (bpm is not > 0) return "Unknown";
        var lower = ((int)bpm.Value / 10) * 10;
        return $"{lower}-{lower + 9}";
    }

    /// <summary>Strips FAT32-invalid characters and trailing dots/spaces, caps the length, and falls back to "Unknown".</summary>
    public static string SafeFolder(string name, int maxLength = 120)
    {
        var cleaned = new string(name.Select(c => Fat32Invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(cleaned)) return "Unknown";
        if (cleaned.Length > maxLength)
            cleaned = cleaned[..maxLength].TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(cleaned) ? "Unknown" : cleaned;
    }
}
