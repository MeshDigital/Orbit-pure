using System.IO;
using SLSKDONET.Configuration;
using SLSKDONET.Services.Setup;
using Xunit;

namespace SLSKDONET.Tests.Setup;

public class SetupFolderServiceTests
{
    private static SetupFolderService Service(bool writable = true, long free = 500L * 1024 * 1024 * 1024) =>
        new(null, canWrite: _ => writable, freeBytes: _ => free);

    [Fact]
    public void ExistingWritableFolder_IsReady()
    {
        using var sandbox = new SetupSandbox();
        var check = Service().Check(sandbox.Root);
        Assert.Equal(FolderCheckState.Ready, check.State);
        Assert.True(check.IsUsable);
    }

    [Fact]
    public void MissingFolder_WillBeCreated_ButOnlyOnEnsure()
    {
        using var sandbox = new SetupSandbox();
        var target = Path.Combine(sandbox.Root, "Music", "ORBIT");
        var service = Service();

        Assert.Equal(FolderCheckState.WillBeCreated, service.Check(target).State);
        Assert.False(Directory.Exists(target)); // checking never creates

        Assert.True(service.TryEnsure(target, out var error));
        Assert.Null(error);
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void UnwritableFolder_IsInvalid()
    {
        using var sandbox = new SetupSandbox();
        var check = Service(writable: false).Check(sandbox.Root);
        Assert.Equal(FolderCheckState.Invalid, check.State);
        Assert.False(check.IsUsable);
        Assert.Contains("can't write", check.Message);
    }

    [Fact]
    public void RealProbe_TempFolderIsWritable()
    {
        using var sandbox = new SetupSandbox();
        Assert.True(new SetupFolderService().Check(sandbox.Root).IsUsable);
    }

    [Fact]
    public void PathThatIsAFile_IsInvalid_AndCannotBeCreatedUnderneath()
    {
        using var sandbox = new SetupSandbox();
        var file = Path.Combine(sandbox.Root, "not-a-folder.txt");
        File.WriteAllText(file, "x");

        Assert.Equal(FolderCheckState.Invalid, Service().Check(file).State);
        Assert.False(new SetupFolderService().TryEnsure(Path.Combine(file, "child"), out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_IsNotUsable(string? path) => Assert.Equal(FolderCheckState.Empty, Service().Check(path).State);

    [Fact]
    public void RelativePath_IsInvalid() => Assert.Equal(FolderCheckState.Invalid, Service().Check(@"Music\ORBIT").State);

    [Fact]
    public void LowFreeSpace_IsAWarningButUsable()
    {
        using var sandbox = new SetupSandbox();
        var check = Service(free: 512L * 1024 * 1024).Check(sandbox.Root);
        Assert.Equal(FolderCheckState.Warning, check.State);
        Assert.True(check.IsUsable);
        Assert.Contains("512 MB", check.Message);
    }

    [Fact]
    public void StartupArguments_SetupPreview_UsesAFreshTempProfile()
    {
        using var sandbox = new SetupSandbox();
        var args = StartupArguments.Parse(new[] { "--setup-preview" }, sandbox.Root);
        Assert.True(args.IsSetupPreview);
        Assert.NotNull(args.ProfileDirectory);
        Assert.StartsWith(Path.Combine(sandbox.Root, "ORBIT-setup-preview"), args.ProfileDirectory);

        Assert.Equal(Path.GetFullPath(sandbox.Root), StartupArguments.Parse(new[] { "--profile", sandbox.Root }).ProfileDirectory);
        Assert.Equal(Path.GetFullPath(sandbox.Root), StartupArguments.Parse(new[] { "--profile=" + sandbox.Root }).ProfileDirectory);
        Assert.Null(StartupArguments.Parse(new[] { "--something-else" }).ProfileDirectory);
        Assert.Null(StartupArguments.Parse(null).ProfileDirectory);
    }
}
