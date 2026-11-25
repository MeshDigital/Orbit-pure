using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using SLSKDONET.Services;
using SLSKDONET.Services.Platform;
using Xunit;

namespace SLSKDONET.Tests.Services.Platform;

/// <summary>#452: cross-platform IPlatformShell seam tests (file reveal/open, notifications, secrets).</summary>
public sealed class PlatformShellTests
{
    [Fact]
    public void WindowsShell_BuildsExplorerSelectAndOpenFolderArgs()
    {
        var launched = new List<ProcessStartInfo>();
        using var shell = new WindowsPlatformShell(null, null, psi => { launched.Add(psi); return true; });

        Assert.Equal("Windows", shell.PlatformName);
        Assert.True(shell.SupportsTaskbarProgress);
        Assert.True(shell.RevealInFolder(@"C:\Music\track.flac"));
        Assert.True(shell.OpenFolder(@"C:\Music"));
        Assert.False(shell.RevealInFolder("   "));
        Assert.False(shell.OpenFolder(""));

        Assert.Equal(2, launched.Count);
        Assert.Equal("explorer.exe", launched[0].FileName);
        Assert.Equal(new[] { @"/select,C:\Music\track.flac" }, launched[0].ArgumentList.ToArray());
        Assert.Equal("explorer.exe", launched[1].FileName);
        Assert.Equal(new[] { @"C:\Music" }, launched[1].ArgumentList.ToArray());
    }

    [Fact]
    public void MacOSShell_BuildsFinderAndOsascriptNotificationArgs()
    {
        var launched = new List<ProcessStartInfo>();
        using var shell = new MacOSPlatformShell(psi => { launched.Add(psi); return true; });

        Assert.Equal("macOS", shell.PlatformName);
        Assert.False(shell.SupportsTaskbarProgress);
        Assert.True(shell.RevealInFolder("/Users/dj/Music/track.flac"));
        Assert.True(shell.OpenFolder("/Users/dj/Music"));
        shell.ShowNotificationIfUnfocused("Message from \"peer\"", @"Line \ 1");

        Assert.Equal(3, launched.Count);
        Assert.Equal("open", launched[0].FileName);
        Assert.Equal(new[] { "-R", "/Users/dj/Music/track.flac" }, launched[0].ArgumentList.ToArray());
        Assert.Equal("open", launched[1].FileName);
        Assert.Equal(new[] { "/Users/dj/Music" }, launched[1].ArgumentList.ToArray());
        Assert.Equal("osascript", launched[2].FileName);
        Assert.Equal("-e", launched[2].ArgumentList[0]);
        Assert.Contains("\\\"peer\\\"", launched[2].ArgumentList[1]);
        Assert.Contains("\\\\", launched[2].ArgumentList[1]);
    }

    [Fact]
    public void LinuxShell_BuildsXdgOpenAndNotifySendArgs()
    {
        var launched = new List<ProcessStartInfo>();
        using var shell = new LinuxPlatformShell(psi => { launched.Add(psi); return true; });

        Assert.Equal("Linux", shell.PlatformName);
        Assert.False(shell.SupportsTaskbarProgress);
        var sampleFile = Path.Combine("music", "album", "track.flac");
        Assert.True(shell.RevealInFolder(sampleFile));
        Assert.True(shell.OpenFolder("/home/dj/music"));
        shell.ShowNotificationIfUnfocused("Error", "Something broke", ToastSeverity.Error);

        Assert.Equal(3, launched.Count);
        Assert.Equal("xdg-open", launched[0].FileName);
        Assert.Equal(new[] { Path.Combine("music", "album") }, launched[0].ArgumentList.ToArray());
        Assert.Equal("xdg-open", launched[1].FileName);
        Assert.Equal(new[] { "/home/dj/music" }, launched[1].ArgumentList.ToArray());
        Assert.Equal("notify-send", launched[2].FileName);
        Assert.Contains("-u", launched[2].ArgumentList);
        Assert.Contains("critical", launched[2].ArgumentList);
    }

    [Fact]
    public void ProtectedDataService_RoundTripsViaCrossPlatformSecretProtector()
    {
        var protector = new InMemorySecretProtector();
        var svc = new ProtectedDataService(logger: null, protector: protector);

        Assert.True(svc.IsSecurityAvailable);
        var cipher = svc.Protect("super-secret-token");
        Assert.NotNull(cipher);
        Assert.NotEqual("super-secret-token", cipher);
        Assert.Equal("super-secret-token", svc.Unprotect(cipher));
        Assert.Null(svc.Protect(null));
        Assert.Null(svc.Unprotect(""));
    }

    private sealed class InMemorySecretProtector : ISecretProtector
    {
        public string Backend => "test-memory";
        public byte[] Protect(byte[] plaintext, string purpose)
            => plaintext.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] ciphertext, string purpose)
            => ciphertext.Select(b => (byte)(b ^ 0x5A)).ToArray();
    }
}
