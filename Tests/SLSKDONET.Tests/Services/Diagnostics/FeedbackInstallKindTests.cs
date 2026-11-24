using System;
using System.IO;
using System.Reflection;
using SLSKDONET.Services.Diagnostics;
using Xunit;

namespace SLSKDONET.Tests.Services.Diagnostics;

/// <summary>#335: the feedback report says which build and how it was installed.</summary>
public sealed class FeedbackInstallKindTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetDirectoryName(SLSKDONET.Data.OrbitPaths.LibraryDbPath)!, "install-" + Guid.NewGuid().ToString("N"));

    public FeedbackInstallKindTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* intentional: best-effort cleanup of the isolated test folder */ }
    }

    [Fact]
    public void InstallKind_TellsInstallerPortableAndDevBuildsApart()
    {
        Assert.Equal("portable (zip)", FeedbackReportService.InstallKind(_dir));
        File.WriteAllText(Path.Combine(_dir, "unins000.exe"), "");
        Assert.Equal("installer", FeedbackReportService.InstallKind(_dir));
        Assert.Equal("development build", FeedbackReportService.InstallKind(@"C:\src\ORBIT\bin\Debug\net9.0\"));
    }

    [Fact]
    public void TheAppAssembly_CarriesARealVersion()
    {
        var version = typeof(FeedbackReportService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.NotEqual("unknown", version);
        Assert.False(version!.StartsWith("0.0.0", StringComparison.Ordinal));
    }
}
