using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>#341: download naming templates with '/' or '\' must build sub-folders, not flatten to one file.</summary>
public class PathProviderServiceTests
{
    private static Track MakeTrack(string artist, string album, string title, string filename = "track.flac") =>
        new() { Artist = artist, Album = album, Title = title, Filename = filename };

    private static PathProviderService MakeService(string nameFormat, string downloadDir = @"C:\music") =>
        new(new AppConfig { NameFormat = nameFormat, DownloadDirectory = downloadDir },
            new FileNameFormatter(),
            NullLogger<PathProviderService>.Instance);

    [Fact]
    public void GetTrackPath_NestedTemplate_BuildsSubfolders()
    {
        var path = MakeService("{artist}/{album}/{title}").GetTrackPath(MakeTrack("Artist", "Album", "Title"));

        Assert.Equal(Path.Combine(@"C:\music", "Artist", "Album", "Title.flac"), path);
    }

    [Fact]
    public void GetTrackPath_EmptySegment_IsDropped()
    {
        var path = MakeService("{artist}/{album}/{title}").GetTrackPath(MakeTrack("Artist", "", "Title"));

        Assert.Equal(Path.Combine(@"C:\music", "Artist", "Title.flac"), path);
    }

    // #391: when every token evaluates empty AND there is no filename to fall back to, the template
    // must not collapse to a hidden ".ext" file in the download root.
    [Fact]
    public void GetTrackPath_AllTokensEmpty_FallsBackToDefaultStructure()
    {
        var path = MakeService("{artist}/{album}/{title}").GetTrackPath(MakeTrack("", "", "", filename: ""));

        Assert.StartsWith(Path.Combine(@"C:\music", "Unknown", "Unknown", "Unknown"), path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetTrackPath_TraversalSegment_CannotEscapeDownloadDir()
    {
        var path = MakeService("{artist}/../{title}").GetTrackPath(MakeTrack("Artist", "Album", "Title"));

        Assert.True(path.StartsWith(@"C:\music" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("..", path);
    }

    [Fact]
    public void GetTrackPath_FlatDefaultTemplate_IsUnchanged()
    {
        var path = MakeService("{artist|filename} - {title}").GetTrackPath(MakeTrack("Artist", "Album", "Title"));

        Assert.Equal(Path.Combine(@"C:\music", "Artist - Title.flac"), path);
    }
}
