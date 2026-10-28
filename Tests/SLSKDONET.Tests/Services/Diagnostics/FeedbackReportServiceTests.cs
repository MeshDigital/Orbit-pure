using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Configuration;
using SLSKDONET.Services.Diagnostics;
using SLSKDONET.ViewModels.Diagnostics;
using Xunit;

namespace SLSKDONET.Tests.Services.Diagnostics;

public sealed class FeedbackReportServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 25, 14, 30, 0);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"orbit-feedback-{Guid.NewGuid():N}");
    private readonly string _logs;
    private readonly string _out;

    public FeedbackReportServiceTests()
    {
        _logs = Directory.CreateDirectory(Path.Combine(_root, "logs")).FullName;
        _out = Path.Combine(_root, "feedback");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* intentional: temp cleanup is best-effort */ }
    }

    private FeedbackReportService CreateService() => new(NullLogger<FeedbackReportService>.Instance, new AppConfig())
    {
        LogDirectory = _logs,
        OutputDirectory = _out,
        Now = () => Now,
    };

    private static string Line(DateTime at, string message) => $"[{at:yyyy-MM-dd HH:mm:ss.fff} INF] Test: {message}";

    [Fact]
    public void ReadRecentLines_KeepsOnlyTheWindow_WithContinuationLines()
    {
        var path = Path.Combine(_logs, "run_20261025_140000_000.txt");
        File.WriteAllLines(path, new[]
        {
            Line(Now.AddMinutes(-30), "too old"),
            "   at Old.StackFrame()",
            Line(Now.AddMinutes(-5), "recent error"),
            "   at Recent.StackFrame()",
            Line(Now.AddMinutes(-1), "latest"),
        });

        var lines = FeedbackReportService.ReadRecentLines(path, Now - FeedbackReportService.LogWindow);

        Assert.Equal(3, lines.Count);
        Assert.Contains("recent error", lines[0]);
        Assert.Equal("   at Recent.StackFrame()", lines[1]);
        Assert.Contains("latest", lines[2]);
    }

    [Theory]
    [InlineData("Login with password=hunter2 ok", "hunter2")]
    [InlineData("GET http://127.0.0.1:5000/callback?code=AQDsecret123&state=x", "AQDsecret123")]
    [InlineData("Header Authorization: Bearer abc.def-ghi", "abc.def-ghi")]
    [InlineData("{\"refresh_token\":\"tok-999\"}", "tok-999")]
    public void Redact_RemovesSecrets(string line, string secret)
    {
        var redacted = FeedbackReportService.Redact(line);

        Assert.DoesNotContain(secret, redacted);
        Assert.Contains("***", redacted);
    }

    [Fact]
    public void Redact_KeepsUsefulDiagnostics_AndHidesUserFolder()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var redacted = FeedbackReportService.Redact($"Spotify returned status code: 403 for {profile}\\Music\\a.flac");

        Assert.Contains("status code: 403", redacted);
        Assert.Contains("%USERPROFILE%\\Music\\a.flac", redacted);
        Assert.DoesNotContain(profile, redacted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateReport_ZipsLogScreenshotNoteAndSystemInfo_WhileLogIsOpenForWriting()
    {
        var previousRun = Path.Combine(_logs, "run_20261025_140500_000.txt");
        File.WriteAllLines(previousRun, new[] { Line(Now.AddMinutes(-10), "crashed here password=secret1") });

        // Serilog holds the current run's file open for writing.
        var currentRun = Path.Combine(_logs, "run_20261025_142000_000.txt");
        await using (var writer = new FileStream(currentRun, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            var bytes = Encoding.UTF8.GetBytes(Line(Now.AddMinutes(-2), "Spotify refused access") + Environment.NewLine);
            writer.Write(bytes);
            writer.Flush();

            var report = await CreateService().CreateReportAsync(new FeedbackReportRequest("Import kept failing", new byte[] { 1, 2, 3 }));

            Assert.True(File.Exists(report.ZipPath));
            Assert.True(report.IncludesScreenshot);
            using var zip = ZipFile.OpenRead(report.ZipPath);
            var names = zip.Entries.Select(e => e.FullName).ToHashSet();
            Assert.Equal(new HashSet<string> { "system.txt", "log-last-15-min.txt", "note.txt", "screenshot.png" }, names);

            var log = Read(zip, "log-last-15-min.txt");
            Assert.Contains("crashed here", log);
            Assert.Contains("Spotify refused access", log);
            Assert.DoesNotContain("secret1", log);
            Assert.Equal("Import kept failing", Read(zip, "note.txt"));
            Assert.Contains("Spotify Client ID", Read(zip, "system.txt"));
        }
    }

    [Fact]
    public async Task CreateReport_WithoutScreenshotOrNote_LeavesThemOut()
    {
        var report = await CreateService().CreateReportAsync(new FeedbackReportRequest("  ", null));

        Assert.False(report.IncludesScreenshot);
        using var zip = ZipFile.OpenRead(report.ZipPath);
        Assert.Equal(new[] { "log-last-15-min.txt", "system.txt" }, zip.Entries.Select(e => e.FullName).OrderBy(n => n));
    }

    [Fact]
    public async Task Dialog_Email_CopiesZipAndOpensMailApp()
    {
        var opened = new List<string>();
        var vm = new FeedbackDialogViewModel(CreateService(), NullLogger.Instance, screenshotPng: null)
        {
            Note = "Spotify connect loop",
            CopyFileToClipboard = _ => Task.FromResult(true),
            OpenUri = uri => { opened.Add(uri); return true; },
            RevealInFolder = _ => throw new InvalidOperationException("should not need the folder"),
        };

        await vm.CreateReportAsync();
        Assert.True(vm.IsReportReady);

        await vm.ShareViaEmailAsync();

        var mailto = Assert.Single(opened);
        Assert.StartsWith("mailto:?subject=", mailto);
        Assert.Contains(Uri.EscapeDataString("Spotify connect loop"), mailto);
        Assert.Contains("clipboard", vm.StatusText);
        Assert.False(vm.StatusIsError);
    }

    [Fact]
    public async Task Dialog_WhatsApp_FallsBackToWebAndFolder_WhenNothingWorks()
    {
        var opened = new List<string>();
        var revealed = new List<string>();
        var vm = new FeedbackDialogViewModel(CreateService(), NullLogger.Instance, screenshotPng: null)
        {
            CopyFileToClipboard = _ => Task.FromResult(false),
            OpenUri = uri => { opened.Add(uri); return uri.StartsWith("https://", StringComparison.Ordinal); },
            RevealInFolder = revealed.Add,
        };
        await vm.CreateReportAsync();

        await vm.ShareViaWhatsAppAsync();

        Assert.Equal(2, opened.Count);
        Assert.StartsWith("whatsapp://send", opened[0]);
        Assert.StartsWith("https://web.whatsapp.com/send", opened[1]);
        // Clipboard failed, so the user is pointed at the file instead of told to paste.
        Assert.Equal(vm.Report!.ZipPath, Assert.Single(revealed));
        Assert.Contains("Drag the zip", vm.StatusText);
    }

    private static string Read(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }
}
