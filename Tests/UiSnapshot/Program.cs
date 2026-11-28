using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Intake;
using SLSKDONET.Services.Vault;
using SLSKDONET.ViewModels.Intake;
using SLSKDONET.ViewModels.Vault;
using SLSKDONET.Views.Avalonia;

// Everything happens under a temp folder: never the user's library, config or Vault.
var output = Path.GetFullPath(args.Length > 0 ? args[0] : "ui-snapshots");
Directory.CreateDirectory(output);
var temp = Path.Combine(Path.GetTempPath(), $"orbit-ui-snapshot-{Guid.NewGuid():N}");
Directory.CreateDirectory(temp);
Environment.SetEnvironmentVariable("ORBIT_DB_PATH", Path.Combine(temp, "library.db"));

AppBuilder.Configure<SLSKDONET.App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

int width = 1440, height = 900;
if (Environment.GetEnvironmentVariable("SNAP_WIDTH") is { } w) width = int.Parse(w);

try
{
    Intake(output, temp, width, height);
    Vault(output, temp, width, height);
    Console.WriteLine($"Snapshots written to {output}");
}
finally
{
    SqliteConnection.ClearAllPools();
    try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
}

static void Intake(string output, string temp, int width, int height)
{
    // Start state.
    var (vm, options) = IntakeVm();
    Snap(new IntakePage(vm), vm.LoadAsync(), Path.Combine(output, "intake-1-start.png"), width, height);

    // Review state with a realistic mix of lanes.
    (vm, options) = IntakeVm();
    SeedReview(options);
    Snap(new IntakePage(vm), vm.LoadAsync(), Path.Combine(output, "intake-2-review.png"), width, height);

    // Same, filtered to Suspicious.
    (vm, options) = IntakeVm();
    SeedReview(options);
    var page = new IntakePage(vm);
    Snap(page, vm.LoadAsync(), null, width, height, after: () =>
    {
        vm.LaneFilters.Single(f => f.Lane == IntakeLane.Suspicious).SelectCommand!.Execute(null);
    }, path2: Path.Combine(output, "intake-3-suspicious.png"));

    // A check that was paused.
    (vm, options) = IntakeVm();
    using (var db = new AppDbContext(options))
    {
        var s = new IntakeSessionEntity { SourceRoot = @"E:\", Label = "SAMSUNG T7 (E:)", Status = IntakeSessionStatus.Scanning };
        db.IntakeSessions.Add(s);
        for (int i = 0; i < 40; i++)
            db.IntakeItems.Add(new IntakeItemEntity { SessionId = s.Id, SourcePath = $@"E:\Music\{i}.flac", RelativePath = $"Music/{i}.flac", Extension = ".flac", Status = i < 15 ? IntakeItemStatus.Scanned : IntakeItemStatus.Pending, Lane = i < 15 ? IntakeLane.Good : IntakeLane.Pending });
        db.SaveChanges();
    }
    Snap(new IntakePage(vm), vm.LoadAsync(), Path.Combine(output, "intake-4-paused.png"), width, height);
}

static (IntakeViewModel, DbContextOptions<AppDbContext>) IntakeVm()
{
    var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"snap-{Guid.NewGuid():N}").Options;
    var factory = new Factory(options);
    var scan = new IntakeScanService(factory, new NoInspector(), NullLogger<IntakeScanService>.Instance);
    var apply = new IntakeApplyService(factory, new NoWriter(), NullLogger<IntakeApplyService>.Instance);
    var vm = new IntakeViewModel(scan, apply, factory, Null<IDialogService>(), NullLogger<IntakeViewModel>.Instance);
    return (vm, options);
}

static void SeedReview(DbContextOptions<AppDbContext> options)
{
    using var db = new AppDbContext(options);
    var s = new IntakeSessionEntity { SourceRoot = @"E:\", Label = "SAMSUNG T7 (E:)", Status = IntakeSessionStatus.Ready, JunkCount = 312 };
    db.IntakeSessions.Add(s);
    var rows = new (string Rel, string? Artist, string? Title, string Ext, int Kbps, int Sec, IntakeVerdict V, IntakeMatchKind M)[]
    {
        ("DnB/Netsky - Rio.flac", "Netsky", "Rio", ".flac", 912, 271, IntakeClassifier.Classify(F(".flac", 912, 271), null), IntakeMatchKind.None),
        ("DnB/Sub Focus - Solar System (Extended Mix).flac", "Sub Focus", "Solar System (Extended Mix)", ".flac", 1004, 364, IntakeClassifier.Classify(F(".flac", 1004, 364), new IntakeLibraryMatch("x", "D:\\m.mp3", "mp3", 192, IntakeMatchKind.Metadata)), IntakeMatchKind.Metadata),
        ("House/Fisher - Losing It.mp3", "Fisher", "Losing It", ".mp3", 320, 391, IntakeClassifier.Classify(F(".mp3", 320, 391), new IntakeLibraryMatch("y", "D:\\l.flac", "flac", 900, IntakeMatchKind.Metadata)), IntakeMatchKind.Metadata),
        ("House/Fisher - Losing It (copy).mp3", "Fisher", "Losing It", ".mp3", 320, 391, new IntakeVerdict(IntakeLane.Duplicate, IntakeAction.Skip, "Another copy on this drive is used instead: House/Fisher - Losing It.mp3"), IntakeMatchKind.None),
        ("Techno/Amelie Lens - Hypnotized.flac", "Amelie Lens", "Hypnotized", ".flac", 870, 402, IntakeClassifier.Classify(F(".flac", 870, 402, AudioAuthenticityVerdict.TranscodedMediumBitrate, 16000), null), IntakeMatchKind.None),
        ("Old rips/track03.mp3", null, null, ".mp3", 128, 233, IntakeClassifier.Classify(new IntakeFacts(".mp3", true, null, null, 233, 128, 0, null, 0, null), null), IntakeMatchKind.None),
        ("Old rips/Basement Jaxx - Where's Your Head At.mp3", "Basement Jaxx", "Where's Your Head At", ".mp3", 192, 286, IntakeClassifier.Classify(F(".mp3", 192, 286, null), null), IntakeMatchKind.None),
        ("Promos/Preview - Unknown ID.mp3", "Unknown", "ID", ".mp3", 320, 30, IntakeClassifier.Classify(F(".mp3", 320, 30), null), IntakeMatchKind.None),
        ("Broken/Pendulum - Watercolour.flac", "Pendulum", "Watercolour", ".flac", 980, 305, IntakeClassifier.Classify(F(".flac", 980, 305, decodeErrors: 37), null), IntakeMatchKind.None),
        ("DnB/Wilkinson - Afterglow.wav", "Wilkinson", "Afterglow", ".wav", 1411, 248, IntakeClassifier.Classify(F(".wav", 1411, 248), null), IntakeMatchKind.None),
        ("DnB/Chase & Status - Blind Faith.aiff", "Chase & Status", "Blind Faith", ".aiff", 1411, 298, IntakeClassifier.Classify(F(".aiff", 1411, 298), null), IntakeMatchKind.None),
        ("Edits/Camo & Krooked - Ember (VIP).flac", "Camo & Krooked", "Ember (VIP)", ".flac", 905, 296, IntakeClassifier.Classify(F(".flac", 905, 296), null), IntakeMatchKind.None),
    };
    foreach (var r in rows)
        db.IntakeItems.Add(new IntakeItemEntity
        {
            SessionId = s.Id, SourcePath = @"E:\" + r.Rel.Replace('/', '\\'), RelativePath = r.Rel, Extension = r.Ext,
            Artist = r.Artist, Title = r.Title, BitrateKbps = r.Kbps, DurationSeconds = r.Sec, Status = IntakeItemStatus.Scanned,
            Lane = r.V.Lane, Action = r.V.Action, Reason = r.V.Reason, MatchKind = r.M, MatchedTrackHash = r.M == IntakeMatchKind.None ? null : "lib",
        });
    db.IntakeItems.Add(new IntakeItemEntity
    {
        SessionId = s.Id, SourcePath = @"E:\Locked\x.flac", RelativePath = "Locked/x.flac", Extension = ".flac",
        Status = IntakeItemStatus.Failed, Lane = IntakeLane.Pending, Error = "Access to the path is denied.",
    });
    db.SaveChanges();
}

static IntakeFacts F(string ext, int kbps, int sec, AudioAuthenticityVerdict? spectral = AudioAuthenticityVerdict.GenuineLossless, double? cutoff = 22000, int decodeErrors = 0) =>
    new(ext, true, "A", "T", sec, kbps, decodeErrors, spectral, 0.9, cutoff);

static void Vault(string output, string temp, int width, int height)
{
    var dbPath = Path.Combine(temp, "library.db");
    var music = Path.Combine(temp, "Music");
    Directory.CreateDirectory(music);
    using (var c = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
    {
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE LibraryEntries (UniqueHash TEXT); CREATE TABLE Projects (Id TEXT); CREATE TABLE CuePoints (Id TEXT);"
            + string.Concat(Enumerable.Range(0, 120).Select(i => $"INSERT INTO LibraryEntries VALUES ('t{i}');"))
            + string.Concat(Enumerable.Range(0, 900).Select(i => $"INSERT INTO CuePoints VALUES ('c{i}');"))
            + "INSERT INTO Projects VALUES ('p1'),('p2'),('p3');";
        cmd.ExecuteNonQuery();
    }
    var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"snap-vault-{Guid.NewGuid():N}").Options;
    using (var db = new AppDbContext(options))
    {
        db.LibraryFolders.Add(new LibraryFolderEntity { FolderPath = music });
        for (int i = 0; i < 6; i++)
        {
            var p = Path.Combine(music, "Artist", $"Track {i}.flac");
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, new byte[4096 + i]);
            db.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = $"h{i}", FilePath = p, AvailabilityState = TrackAvailabilityState.Ready });
        }
        db.SaveChanges();
    }
    var factory = new Factory(options);

    // No Vault yet.
    var local = new VaultLocalState(NullLogger<VaultLocalState>.Instance);
    var vault = new VaultService(factory, local, NullLogger<VaultService>.Instance);
    var vm = new VaultViewModel(vault, local, Null<IDialogService>(), NullLogger<VaultViewModel>.Instance);
    Snap(new VaultPage(vm), vm.LoadAsync(), Path.Combine(output, "vault-1-start.png"), width, height);

    // After a backup.
    var vaultRoot = VaultService.ResolveVaultRoot(Path.Combine(temp, "BackupDrive"));
    vault.BackupAsync(vaultRoot).GetAwaiter().GetResult();
    File.Delete(Path.Combine(music, "Artist", "Track 2.flac"));
    vm = new VaultViewModel(vault, local, Null<IDialogService>(), NullLogger<VaultViewModel>.Instance);
    Snap(new VaultPage(vm), vm.LoadAsync(), Path.Combine(output, "vault-2-ready.png"), width, height);

    // Restore preview open.
    vm = new VaultViewModel(vault, local, Null<IDialogService>(), NullLogger<VaultViewModel>.Instance);
    var page = new VaultPage(vm);
    Snap(page, vm.LoadAsync(), null, width, height, after: () => Wait(vm.PlanRestoreCommand.ExecuteAsync(null)),
        path2: Path.Combine(output, "vault-3-restore-preview.png"));
}

static void Snap(Control content, Task load, string? path, int width, int height, Action? after = null, string? path2 = null)
{
    var window = new Window { Width = width, Height = height, Content = content };
    window.Show();
    Wait(load);
    Pump();
    if (path != null) Save(window, path);
    if (after != null)
    {
        after();
        Pump();
        if (path2 != null) Save(window, path2);
    }
    window.Close();
}

static void Save(Window window, string path)
{
    var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered.");
    frame.Save(path);
    Console.WriteLine(path);
}

static void Pump()
{
    for (int i = 0; i < 20; i++)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(15);
    }
}

static void Wait(Task t)
{
    var until = DateTime.UtcNow.AddSeconds(20);
    while (!t.IsCompleted && DateTime.UtcNow < until)
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(10);
    }
    t.GetAwaiter().GetResult();
}

static T Null<T>() where T : class => DispatchProxy.Create<T, NullProxy>();

public class NullProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var rt = method?.ReturnType;
        if (rt == null || rt == typeof(void)) return null;
        if (rt == typeof(Task)) return Task.CompletedTask;
        if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = rt.GetGenericArguments()[0];
            var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, new[] { value });
        }
        return rt.IsValueType ? Activator.CreateInstance(rt) : null;
    }
}

sealed class Factory : IDbContextFactory<AppDbContext>
{
    private readonly DbContextOptions<AppDbContext> _options;
    public Factory(DbContextOptions<AppDbContext> options) => _options = options;
    public AppDbContext CreateDbContext() => new(_options);
}

sealed class NoInspector : IIntakeFileInspector
{
    public Task<IntakeInspection> InspectAsync(string path, bool deep, CancellationToken ct) => throw new NotSupportedException();
}

sealed class NoWriter : IIntakeLibraryWriter
{
    public string LibraryRoot => Path.GetTempPath();
    public string TargetPathFor(IntakeItemEntity item) => throw new NotSupportedException();
    public Task<int> ImportFilesAsync(IReadOnlyList<string> paths, CancellationToken ct) => throw new NotSupportedException();
    public Task<string> ReplaceAsync(IntakeItemEntity item, string newFile, CancellationToken ct) => throw new NotSupportedException();
    public Task<int> QueueUpgradesAsync(string title, IReadOnlyList<IntakeItemEntity> items, CancellationToken ct) => throw new NotSupportedException();
}
