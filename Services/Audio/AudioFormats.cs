using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SLSKDONET.Services.Audio;

/// <summary>Container a format's bytes live in (decides tag layout and decoder).</summary>
public enum AudioContainerFamily { Mpeg, Flac, Wave, Aiff, Mp4, Ogg, Asf, Ape, WavPack, Dsd, Other }

/// <summary>Which tag block TagLib reads/writes for the format (Serato cue export picks its frame layout from this).</summary>
public enum AudioTagLayout { None, Id3v2, Xiph, Mp4, Asf, Ape }

/// <summary>What ORBIT does with a format found in a music folder.</summary>
public enum AudioFormatSupport
{
    /// <summary>Imported, played, tagged where possible and analysed.</summary>
    Importable,
    /// <summary>Audio, but not imported yet: counted and reported as "unsupported" by folder imports.</summary>
    Recognised,
    /// <summary>Only meaningful for Soulseek results (e.g. .mp4); ignored in local folders.</summary>
    RemoteOnly,
}

/// <summary>One audio format, keyed by extension.</summary>
/// <param name="Extension">Lower-case, with the dot (".aiff").</param>
/// <param name="DisplayName">Badge/label text ("AIFF").</param>
/// <param name="IsLossless">True for PCM and lossless codecs. M4A is false: it is usually AAC (ALAC is detected per file).</param>
/// <param name="NativeDecode">NAudio's AudioFileReader decodes it itself (exact seeking, no Media Foundation).
/// For AIFF this holds for plain AIFF only; AIFF-C content is checked per file, see <see cref="AudioFormats.CanDecodeNatively"/>.</param>
/// <param name="CanWriteTags">TagLib# reads and writes tags in this container.</param>
public sealed record AudioFormatInfo(
    string Extension,
    string DisplayName,
    bool IsLossless,
    AudioContainerFamily Family,
    AudioTagLayout Tags,
    AudioFormatSupport Support,
    string MimeType,
    string RekordboxKind,
    bool NativeDecode,
    bool CanWriteTags)
{
    /// <summary>Extension without the dot ("aiff"), the form stored in LibraryEntry.Format.</summary>
    public string Token => Extension.TrimStart('.');
    public bool IsImportable => Support == AudioFormatSupport.Importable;
}

/// <summary>
/// The single source of truth for audio file formats: which extensions ORBIT imports, plays,
/// tags, shares and analyses, which are lossless, their MIME type and Rekordbox "Kind".
/// Every "is this an audio file / supported / lossless" check goes through here; the
/// AudioFormatsGuardTests fail when a new hard-coded extension list appears elsewhere.
///
/// Playback: WAV/MP3/AIFF are decoded by NAudio itself; everything else goes through Media
/// Foundation and, when Windows can't decode it, an ffmpeg-decoded copy (<see cref="PlayableAudio"/>).
/// Analysis always decodes with ffmpeg. So "Importable" needs TagLib (or, for AIFF-C,
/// <see cref="AiffHeader"/>) to read the file's properties, and ffmpeg to decode it.
/// </summary>
public static class AudioFormats
{
    private const AudioFormatSupport Imp = AudioFormatSupport.Importable;
    private const AudioFormatSupport Rec = AudioFormatSupport.Recognised;

    public static readonly IReadOnlyList<AudioFormatInfo> All = new AudioFormatInfo[]
    {
        //  ext       name     lossless family                         tags                   support  mime                 rekordbox kind   native  tag-write
        new(".mp3",  "MP3",   false, AudioContainerFamily.Mpeg,    AudioTagLayout.Id3v2, Imp, "audio/mpeg",        "MP3 File",  true,  true),
        new(".flac", "FLAC",  true,  AudioContainerFamily.Flac,    AudioTagLayout.Xiph,  Imp, "audio/flac",        "FLAC File", false, true),
        new(".wav",  "WAV",   true,  AudioContainerFamily.Wave,    AudioTagLayout.Id3v2, Imp, "audio/wav",         "WAV File",  true,  true),
        new(".wave", "WAV",   true,  AudioContainerFamily.Wave,    AudioTagLayout.Id3v2, Imp, "audio/wav",         "WAV File",  true,  true),
        new(".aiff", "AIFF",  true,  AudioContainerFamily.Aiff,    AudioTagLayout.Id3v2, Imp, "audio/aiff",        "AIFF File", true,  true),
        new(".aif",  "AIFF",  true,  AudioContainerFamily.Aiff,    AudioTagLayout.Id3v2, Imp, "audio/aiff",        "AIFF File", true,  true),
        // AIFF-C: TagLib# rejects the AIFC form type and NAudio only reads uncompressed big-endian
        // AIFF, so properties come from AiffHeader and playback from the ffmpeg-decoded copy.
        new(".aifc", "AIFF",  true,  AudioContainerFamily.Aiff,    AudioTagLayout.None,  Imp, "audio/aiff",        "AIFF File", false, false),
        new(".m4a",  "M4A",   false, AudioContainerFamily.Mp4,     AudioTagLayout.Mp4,   Imp, "audio/mp4",         "M4A File",  false, true),
        new(".aac",  "AAC",   false, AudioContainerFamily.Mp4,     AudioTagLayout.Id3v2, Imp, "audio/aac",         "M4A File",  false, true),
        new(".ogg",  "OGG",   false, AudioContainerFamily.Ogg,     AudioTagLayout.Xiph,  Imp, "audio/ogg",         "OGG File",  false, true),
        new(".oga",  "OGG",   false, AudioContainerFamily.Ogg,     AudioTagLayout.Xiph,  Imp, "audio/ogg",         "OGG File",  false, true),
        new(".opus", "OPUS",  false, AudioContainerFamily.Ogg,     AudioTagLayout.Xiph,  Imp, "audio/opus",        "OPUS File", false, true),
        new(".wma",  "WMA",   false, AudioContainerFamily.Asf,     AudioTagLayout.Asf,   Imp, "audio/x-ms-wma",    "WMA File",  false, true),

        // Audio ORBIT recognises (Soulseek ranking, Library Health, folder-import reports) but doesn't import yet.
        new(".alac", "ALAC",  true,  AudioContainerFamily.Mp4,     AudioTagLayout.None,  Rec, "audio/mp4",         "M4A File",  false, false),
        new(".ape",  "APE",   true,  AudioContainerFamily.Ape,     AudioTagLayout.Ape,   Rec, "audio/x-ape",       "APE File",  false, false),
        new(".wv",   "WV",    true,  AudioContainerFamily.WavPack, AudioTagLayout.Ape,   Rec, "audio/x-wavpack",   "WV File",   false, false),
        new(".dsf",  "DSF",   true,  AudioContainerFamily.Dsd,     AudioTagLayout.None,  Rec, "audio/x-dsf",       "DSF File",  false, false),
        new(".dff",  "DFF",   true,  AudioContainerFamily.Dsd,     AudioTagLayout.None,  Rec, "audio/x-dff",       "DFF File",  false, false),
        new(".tta",  "TTA",   true,  AudioContainerFamily.Other,   AudioTagLayout.None,  Rec, "audio/x-tta",       "TTA File",  false, false),
        new(".mka",  "MKA",   false, AudioContainerFamily.Other,   AudioTagLayout.None,  Rec, "audio/x-matroska",  "MKA File",  false, false),
        new(".mpc",  "MPC",   false, AudioContainerFamily.Other,   AudioTagLayout.None,  Rec, "audio/x-musepack",  "MPC File",  false, false),
        new(".m4b",  "M4B",   false, AudioContainerFamily.Mp4,     AudioTagLayout.None,  Rec, "audio/mp4",         "M4A File",  false, false),
        new(".caf",  "CAF",   false, AudioContainerFamily.Other,   AudioTagLayout.None,  Rec, "audio/x-caf",       "CAF File",  false, false),
        new(".mp2",  "MP2",   false, AudioContainerFamily.Mpeg,    AudioTagLayout.None,  Rec, "audio/mpeg",        "MP2 File",  false, false),
        new(".ac3",  "AC3",   false, AudioContainerFamily.Other,   AudioTagLayout.None,  Rec, "audio/ac3",         "AC3 File",  false, false),
        new(".amr",  "AMR",   false, AudioContainerFamily.Other,   AudioTagLayout.None,  Rec, "audio/amr",         "AMR File",  false, false),

        // Soulseek peers share these as audio; a local .mp4 is usually video, so folder scans ignore it.
        new(".mp4",  "MP4",   false, AudioContainerFamily.Mp4,     AudioTagLayout.None,  AudioFormatSupport.RemoteOnly, "audio/mp4", "M4A File", false, false),
    };

    private static readonly Dictionary<string, AudioFormatInfo> ByToken =
        All.ToDictionary(f => f.Token, StringComparer.OrdinalIgnoreCase);

    /// <summary>Extensions (with dot) ORBIT imports from local folders.</summary>
    public static readonly IReadOnlySet<string> ImportableExtensions = Set(All.Where(f => f.IsImportable).Select(f => f.Extension));

    /// <summary>Audio extensions found in music folders that ORBIT reports as "can't be imported yet".</summary>
    public static readonly IReadOnlySet<string> RecognisedOnlyExtensions = Set(All.Where(f => f.Support == AudioFormatSupport.Recognised).Select(f => f.Extension));

    /// <summary>Every local audio extension (importable or recognised): Library Health, sharing, download logs.</summary>
    public static readonly IReadOnlySet<string> LocalAudioExtensions = Set(All.Where(f => f.Support != AudioFormatSupport.RemoteOnly).Select(f => f.Extension));

    /// <summary>Every format token without the dot ("aiff", "mp4"…), Soulseek-only ones included.</summary>
    public static readonly IReadOnlySet<string> AllTokens = Set(All.Select(f => f.Token));

    /// <summary>Lossless tokens without the dot ("flac", "wav", "aif", "aiff"…). Safe for EF <c>Contains</c> (lower-case).</summary>
    public static readonly IReadOnlyList<string> LosslessTokens = All.Where(f => f.IsLossless).Select(f => f.Token).ToArray();

    /// <summary>Lossy tokens without the dot ("mp3", "m4a"…).</summary>
    public static readonly IReadOnlyList<string> LossyTokens = All.Where(f => !f.IsLossless).Select(f => f.Token).ToArray();

    /// <summary>File-picker patterns ("*.mp3", "*.aiff"…) for every importable format.</summary>
    public static IReadOnlyList<string> ImportablePatterns { get; } = All.Where(f => f.IsImportable).Select(f => "*" + f.Extension).ToArray();

    /// <summary>Importable tokens without the dot, for file-dialog filters that take bare extensions.</summary>
    public static IReadOnlyList<string> ImportableTokens { get; } = All.Where(f => f.IsImportable).Select(f => f.Token).ToArray();

    /// <summary>Looks up a format by extension (".aiff"), bare token ("aiff", "AIFF") or file path.</summary>
    public static AudioFormatInfo? Find(string? extensionTokenOrPath)
    {
        if (string.IsNullOrWhiteSpace(extensionTokenOrPath)) return null;
        var value = extensionTokenOrPath.Trim();
        if (ByToken.TryGetValue(value.TrimStart('.'), out var direct)) return direct;
        var ext = Path.GetExtension(value); // .NET Core: never throws on odd characters
        return string.IsNullOrEmpty(ext) ? null : ByToken.GetValueOrDefault(ext.TrimStart('.'));
    }

    /// <summary>True when ORBIT imports files with this path's extension.</summary>
    public static bool IsImportable(string? path) => Find(Path.GetExtension(path ?? string.Empty))?.IsImportable == true;

    /// <summary>True for any local audio file (importable or recognised), by extension.</summary>
    public static bool IsLocalAudioFile(string? path) =>
        Find(Path.GetExtension(path ?? string.Empty)) is { Support: not AudioFormatSupport.RemoteOnly };

    /// <summary>True when the extension or format token ("flac", ".aiff", "AIF") is a lossless format.</summary>
    public static bool IsLossless(string? extensionOrToken) =>
        !string.IsNullOrWhiteSpace(extensionOrToken) && ByToken.TryGetValue(extensionOrToken.Trim().TrimStart('.'), out var f) && f.IsLossless;

    /// <summary>True when the extension or token is a known lossy format.</summary>
    public static bool IsLossy(string? extensionOrToken) =>
        !string.IsNullOrWhiteSpace(extensionOrToken) && ByToken.TryGetValue(extensionOrToken.Trim().TrimStart('.'), out var f) && !f.IsLossless;

    /// <summary>Rekordbox XML "Kind" for a file ("AIFF File", "WAV File"…); unknown extensions get "{EXT} File".</summary>
    public static string RekordboxKind(string? filePath)
    {
        var ext = Path.GetExtension(filePath ?? string.Empty);
        if (string.IsNullOrEmpty(ext)) return "Unknown File Type";
        return Find(ext)?.RekordboxKind ?? $"{ext.TrimStart('.').ToUpperInvariant()} File";
    }

    /// <summary>
    /// MIME type TagLib# resolves for an extension ("taglib/aiff"): it registers every format it
    /// reads under "taglib/&lt;extension&gt;". Use it when the file on disk has a temp extension.
    /// </summary>
    public static string TagLibMimeType(string? extensionOrPath)
    {
        var value = (extensionOrPath ?? string.Empty).Trim();
        var token = Find(value)?.Token ?? Path.GetExtension(value).TrimStart('.');
        return "taglib/" + (string.IsNullOrEmpty(token) ? value.TrimStart('.') : token).ToLowerInvariant();
    }

    /// <summary>MIME type for an extension, or application/octet-stream.</summary>
    public static string MimeType(string? extension) => Find(extension)?.MimeType ?? "application/octet-stream";

    /// <summary>
    /// True when NAudio's AudioFileReader decodes the file itself (and seeks it exactly). AIFF
    /// files are sniffed: an .aif that is really AIFF-C (Logic/GarageBand exports, "sowt") is
    /// rejected by NAudio and must go through the ffmpeg-decoded copy.
    /// </summary>
    public static bool CanDecodeNatively(string path)
    {
        var format = Find(Path.GetExtension(path));
        if (format is null || !format.NativeDecode) return false;
        if (format.Family != AudioContainerFamily.Aiff) return true;
        // Seeks call this repeatedly; the header only changes when the file is replaced.
        return AiffNativeCache.GetOrAdd(path, p => !AiffHeader.TryRead(p, out var header) || header.IsPlainAiff);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> AiffNativeCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlySet<string> Set(IEnumerable<string> values) =>
        new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
}
