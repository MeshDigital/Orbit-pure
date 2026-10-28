using System;
using System.IO;

namespace SLSKDONET.Services.Audio;

/// <summary>Tags and stream properties of one audio file.</summary>
public sealed record AudioFileFacts(
    string? Artist,
    string? Title,
    string? Album,
    TimeSpan Duration,
    int BitrateKbps,
    int SampleRate,
    int BitsPerSample,
    double? Bpm,
    string? Key,
    string? CodecDescription);

/// <summary>
/// Reads tags and stream properties for import. TagLib# reads every importable format except
/// AIFF-C: it rejects the AIFC form type ("File form type is not AIFF") and has no MIME mapping
/// for ".aifc", so those files were reported corrupt. They are read with <see cref="AiffHeader"/>,
/// the embedded "ID3 " chunk parsed by TagLib's ID3v2 reader. TagLib exceptions for any other
/// file propagate to the caller, which reports the file as unreadable.
/// </summary>
public static class AudioTagReader
{
    public static AudioFileFacts Read(string filePath)
    {
        bool aiff = AudioFormats.Find(Path.GetExtension(filePath))?.Family == AudioContainerFamily.Aiff;
        if (aiff && AiffHeader.TryRead(filePath, out var header) && header.IsAifc)
            return FromAiffHeader(header);

        try
        {
            using var file = TagLib.File.Create(filePath);
            return new AudioFileFacts(
                file.Tag.FirstPerformer, file.Tag.Title, file.Tag.Album, file.Properties.Duration,
                file.Properties.AudioBitrate, file.Properties.AudioSampleRate, file.Properties.BitsPerSample,
                file.Tag.BeatsPerMinute > 0 ? file.Tag.BeatsPerMinute : null, file.Tag.InitialKey,
                file.Properties.Description);
        }
        catch (TagLib.UnsupportedFormatException) when (aiff && AiffHeader.TryRead(filePath, out var fallback))
        {
            // A plain AIFF named ".aifc": TagLib# has no MIME type for the extension.
            return FromAiffHeader(fallback);
        }
    }

    private static AudioFileFacts FromAiffHeader(AiffHeader header)
    {
        TagLib.Tag? tag = null;
        if (header.Id3Tag is { Length: > 10 } id3)
        {
            try
            {
                tag = new TagLib.Id3v2.Tag(new TagLib.ByteVector(id3));
            }
            catch (Exception ex) when (ex is TagLib.CorruptFileException or ArgumentException or IndexOutOfRangeException or OverflowException)
            {
                Serilog.Log.Debug(ex, "[AudioTagReader] Ignoring an unreadable ID3 chunk in an AIFF-C file");
            }
        }

        return new AudioFileFacts(
            tag?.FirstPerformer, tag?.Title, tag?.Album, header.Duration, header.BitrateKbps,
            (int)Math.Round(header.SampleRate), header.BitsPerSample,
            tag?.BeatsPerMinute > 0 ? tag.BeatsPerMinute : null, tag?.InitialKey,
            header.IsAifc ? $"AIFF-C Audio ({header.CompressionType})" : "AIFF Audio");
    }
}
