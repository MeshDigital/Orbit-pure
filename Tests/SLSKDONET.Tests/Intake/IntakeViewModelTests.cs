using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.Services.Intake;
using SLSKDONET.ViewModels.Intake;
using Xunit;

namespace SLSKDONET.Tests.Intake;

public sealed class IntakeViewModelTests
{
    private readonly DbContextOptions<AppDbContext> _options =
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"intake-vm-{Guid.NewGuid():N}").Options;

    private IntakeViewModel Create()
    {
        var factory = new Factory(_options);
        var scan = new IntakeScanService(factory, Mock.Of<IIntakeFileInspector>(), NullLogger<IntakeScanService>.Instance);
        var apply = new IntakeApplyService(factory, Mock.Of<IIntakeLibraryWriter>(), NullLogger<IntakeApplyService>.Instance);
        return new IntakeViewModel(scan, apply, factory, Mock.Of<IDialogService>(), NullLogger<IntakeViewModel>.Instance);
    }

    private void Seed(IntakeSessionStatus status, params (string Name, IntakeLane Lane, IntakeAction Action, IntakeItemStatus Status)[] items)
    {
        using var db = new AppDbContext(_options);
        var session = new IntakeSessionEntity { SourceRoot = @"E:\", Label = "SSD", Status = status };
        db.IntakeSessions.Add(session);
        foreach (var (name, lane, action, st) in items)
            db.IntakeItems.Add(new IntakeItemEntity
            {
                SessionId = session.Id, SourcePath = @"E:\" + name, RelativePath = name, Extension = Path.GetExtension(name),
                Lane = lane, Action = action, Status = st, Artist = "A", Title = name,
            });
        db.SaveChanges();
    }

    [Fact]
    public async Task NoSession_ShowsTheStartState()
    {
        var vm = Create();
        await vm.LoadAsync();
        Assert.True(vm.IsStart);
        Assert.False(vm.IsReviewing);
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task OpenSession_IsReopened_WithLaneCountsAndAFilter()
    {
        Seed(IntakeSessionStatus.Ready,
            ("a.flac", IntakeLane.Good, IntakeAction.Keep, IntakeItemStatus.Scanned),
            ("b.flac", IntakeLane.Good, IntakeAction.Keep, IntakeItemStatus.Scanned),
            ("c.mp3", IntakeLane.Suspicious, IntakeAction.Upgrade, IntakeItemStatus.Scanned),
            ("d.mp3", IntakeLane.Reject, IntakeAction.Skip, IntakeItemStatus.Failed));
        var vm = Create();
        await vm.LoadAsync();

        Assert.True(vm.IsReviewing);
        Assert.Equal("Intake · SSD", vm.SessionTitle);
        Assert.Equal(4, vm.Rows.Count);
        Assert.Equal(2, vm.LaneFilters.Single(f => f.Lane == IntakeLane.Good).Count);
        Assert.Equal(1, vm.LaneFilters.Single(f => f.IsFailedFilter).Count);
        Assert.Equal(0, vm.LaneFilters.Single(f => f.Lane == IntakeLane.Reject).Count); // failed rows aren't double-counted
        Assert.Equal("2 to add · 1 to search for", vm.DecisionsSummary);
        Assert.Equal("Apply 3 decisions", vm.ApplyText);

        vm.LaneFilters.Single(f => f.Lane == IntakeLane.Suspicious).SelectCommand!.Execute(null);
        Assert.Equal(new[] { "c.mp3" }, vm.Rows.Select(r => r.PathText));
        vm.LaneFilters.Single(f => f.Lane == IntakeLane.BetterCopy).SelectCommand!.Execute(null);
        Assert.True(vm.IsFilterEmpty);
    }

    [Fact]
    public void OpenSession_LoadsItsRows_UnderAQueuedUiContext()
    {
        // Regression: the session used to be set through a posted callback, so on the real UI
        // thread the rows were reloaded before the session existed and the review showed 0 files.
        Seed(IntakeSessionStatus.Ready, ("a.flac", IntakeLane.Good, IntakeAction.Keep, IntakeItemStatus.Scanned),
            ("b.flac", IntakeLane.Reject, IntakeAction.Skip, IntakeItemStatus.Scanned));
        var context = new QueueContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var vm = Create();
            var load = vm.LoadAsync();
            context.RunUntil(() => load.IsCompleted);
            load.GetAwaiter().GetResult();
            Assert.Equal(2, vm.Rows.Count);
            Assert.Equal(1, vm.LaneFilters.Single(f => f.Lane == IntakeLane.Reject).Count);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public async Task SetAllShown_ChangesEveryFileInTheLane()
    {
        Seed(IntakeSessionStatus.Ready,
            ("a.mp3", IntakeLane.Suspicious, IntakeAction.Upgrade, IntakeItemStatus.Scanned),
            ("b.mp3", IntakeLane.Suspicious, IntakeAction.Upgrade, IntakeItemStatus.Scanned),
            ("c.flac", IntakeLane.Good, IntakeAction.Keep, IntakeItemStatus.Scanned));
        var vm = Create();
        await vm.LoadAsync();
        vm.LaneFilters.Single(f => f.Lane == IntakeLane.Suspicious).SelectCommand!.Execute(null);
        Assert.Equal("Set all 2 shown to", vm.SetShownText);

        await vm.SetShownSkipCommand.ExecuteAsync(null);

        await using var db = new AppDbContext(_options);
        var actions = await db.IntakeItems.ToDictionaryAsync(i => i.RelativePath, i => i.Action);
        Assert.Equal(IntakeAction.Skip, actions["a.mp3"]);
        Assert.Equal(IntakeAction.Skip, actions["b.mp3"]);
        Assert.Equal(IntakeAction.Keep, actions["c.flac"]);
        Assert.All(vm.Rows, r => Assert.Equal(IntakeAction.Skip, r.SelectedAction!.Action));
    }

    [Fact]
    public async Task ChangingARowsAction_IsSaved()
    {
        Seed(IntakeSessionStatus.Ready, ("a.flac", IntakeLane.Good, IntakeAction.Keep, IntakeItemStatus.Scanned));
        var vm = Create();
        await vm.LoadAsync();

        var row = vm.Rows.Single();
        Assert.DoesNotContain(row.ActionOptions, o => o.Action == IntakeAction.Replace); // no library match
        row.SelectedAction = row.ActionOptions.Single(o => o.Action == IntakeAction.Skip);
        await Task.Delay(50);

        await using var db = new AppDbContext(_options);
        Assert.Equal(IntakeAction.Skip, (await db.IntakeItems.SingleAsync()).Action);
        Assert.Equal("1 to leave", vm.DecisionsSummary);
    }

    [Fact]
    public async Task UnfinishedScan_OffersResume()
    {
        Seed(IntakeSessionStatus.Scanning, ("a.flac", IntakeLane.Pending, IntakeAction.None, IntakeItemStatus.Pending));
        var vm = Create();
        await vm.LoadAsync();
        Assert.True(vm.HasUnfinishedScan);
        Assert.True(vm.ResumeScanCommand.CanExecute(null));
        Assert.False(vm.IsReviewing);
    }

    private sealed class QueueContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback, object?)> _queue = new();
        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
        public void RunUntil(Func<bool> done)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!done() && DateTime.UtcNow < until)
                if (_queue.TryTake(out var work, 20)) work.Item1(work.Item2);
        }
    }

    private sealed class Factory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public Factory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }
}
