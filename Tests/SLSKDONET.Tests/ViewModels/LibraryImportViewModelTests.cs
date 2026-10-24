using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>The import sheet's state machine, driven by a fake pipeline (no database).</summary>
public class LibraryImportViewModelTests : IDisposable
{
    private readonly EventBusService _bus = new();
    private readonly ConcurrentQueue<object> _events = new();
    private readonly List<IDisposable> _subs = new();

    public LibraryImportViewModelTests()
    {
        _subs.Add(_bus.GetEvent<ToastRequestedEvent>().Subscribe(e => _events.Enqueue(e)));
        _subs.Add(_bus.GetEvent<TrackAnalysisRequestedEvent>().Subscribe(e => _events.Enqueue(e)));
        _subs.Add(_bus.GetEvent<OpenPlaylistInLibraryEvent>().Subscribe(e => _events.Enqueue(e)));
    }

    public void Dispose()
    {
        foreach (var s in _subs) s.Dispose();
        _bus.Dispose();
    }

    private sealed class FakeRunner
    {
        public readonly TaskCompletionSource<LibraryImportResult> Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IProgress<LibraryImportProgress>? Progress;
        public CancellationToken Token;
        public LibraryImportRequest? Request;
        public int Calls;

        public Task<LibraryImportResult> Run(LibraryImportRequest request, IProgress<LibraryImportProgress> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Request = request;
            Progress = progress;
            Token = ct;
            Started.TrySetResult(true);
            return Finish.Task;
        }
    }

    private LibraryImportViewModel Create(FakeRunner runner) =>
        new(_bus, NullLogger<LibraryImportViewModel>.Instance, null, null, runner.Run, a => a());

    private static LibraryImportProgress P(LibraryImportPhase phase, int found = 0, int processed = 0, int total = 0, int added = 0) =>
        new(phase, found, processed, total, "file.flac", added, 0, 0, 0, 0, 0, 0);

    private static LibraryImportResult Result(int added, bool cancelled = false)
    {
        var r = new LibraryImportResult { FilesFound = added + 1, Added = added, AlreadyInLibrary = 1, WasCancelled = cancelled };
        for (var i = 0; i < added; i++) r.AddedTrackHashes.Add($"hash-{i}");
        r.FolderPaths.Add(@"C:\Music");
        r.Issues.Add(new LibraryImportIssue(LibraryImportIssueKind.Unreadable, @"C:\Music\bad.flac", "Corrupt"));
        r.Unreadable = 1;
        return r;
    }

    [Fact]
    public async Task Run_OpensTheLockingSheet_ShowsPhases_ThenTheSummary()
    {
        var runner = new FakeRunner();
        var vm = Create(runner);

        var run = vm.RunImportAsync(new[] { Guid.NewGuid() }, "Importing Music");
        await runner.Started.Task;

        Assert.True(vm.IsOpen);
        Assert.True(vm.IsRunning);
        Assert.False(vm.IsChipVisible);
        Assert.False(runner.Request!.QueueAnalysis); // analysis is offered on the summary, not automatic
        Assert.Equal(ImportStepState.Active, vm.Steps[0].State);
        Assert.True(vm.IsIndeterminate);

        runner.Progress!.Report(P(LibraryImportPhase.Scanning, found: 120));
        Assert.Equal(120, vm.FilesFound);
        Assert.Equal("120 files", vm.Steps[0].Detail);

        runner.Progress!.Report(P(LibraryImportPhase.Reading, found: 120, processed: 30, total: 120));
        Assert.Equal(ImportStepState.Done, vm.Steps[0].State);
        Assert.Equal(ImportStepState.Active, vm.Steps[1].State);
        Assert.Equal(25, vm.ProgressValue);
        Assert.Equal("Reading tags & checking files", vm.PhaseTitle);
        Assert.False(vm.CloseCommand.CanExecute(null));

        runner.Finish.SetResult(Result(added: 3));
        var result = await run;

        Assert.NotNull(result);
        Assert.True(vm.IsCompleted);
        Assert.False(vm.IsRunning);
        Assert.True(vm.IsOpen);
        Assert.All(vm.Steps, s => Assert.Equal(ImportStepState.Done, s.State));
        Assert.Equal("Import complete", vm.SummaryTitle);
        Assert.Equal("3 tracks added to your library.", vm.SummaryLine);
        Assert.True(vm.IsCleanFinish);
        Assert.True(vm.HasIssues);
        Assert.Equal("Analyse 3 new tracks", vm.AnalyseText);

        vm.ToggleDetailsCommand.Execute(null);
        Assert.True(vm.IsDetailsVisible);

        vm.AnalyseCommand.Execute(null);
        Assert.True(vm.AnalysisQueued);
        Assert.False(vm.AnalyseCommand.CanExecute(null));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_events.OfType<TrackAnalysisRequestedEvent>().Count() < 3 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(new[] { "hash-0", "hash-1", "hash-2" }, _events.OfType<TrackAnalysisRequestedEvent>().Select(e => e.TrackGlobalId).OrderBy(h => h));

        vm.OpenInLibraryCommand.Execute(null);
        Assert.Contains(_events.OfType<OpenPlaylistInLibraryEvent>(), e => e.PlaylistId == Guid.Empty);
        Assert.False(vm.IsOpen);
        Assert.False(vm.IsCompleted);
    }

    [Fact]
    public async Task RunInBackground_SwapsTheSheetForTheChip_AndToastsWhenDone()
    {
        var runner = new FakeRunner();
        var vm = Create(runner);
        var run = vm.RunImportAsync(null);
        await runner.Started.Task;

        vm.RunInBackgroundCommand.Execute(null);
        Assert.False(vm.IsOpen);
        Assert.True(vm.IsChipVisible);
        runner.Progress!.Report(P(LibraryImportPhase.Reading, found: 10, processed: 4, total: 10));
        Assert.Equal("Importing · 4 / 10", vm.ChipText);

        runner.Finish.SetResult(Result(added: 2));
        await run;

        Assert.True(vm.IsChipVisible);
        Assert.Equal("Import done · 2 added", vm.ChipText);
        Assert.Contains(_events.OfType<ToastRequestedEvent>(), t => t.Title == "Import complete");

        vm.ShowSheetCommand.Execute(null);
        Assert.True(vm.IsOpen);
        Assert.False(vm.IsChipVisible);
        vm.CloseCommand.Execute(null);
        Assert.False(vm.IsOpen);
        Assert.False(vm.IsChipVisible);
    }

    [Fact]
    public async Task Cancel_CancelsThePipelineToken_AndTheSummarySaysWhatWasKept()
    {
        var runner = new FakeRunner();
        var vm = Create(runner);
        var run = vm.RunImportAsync(null);
        await runner.Started.Task;

        vm.CancelCommand.Execute(null);
        Assert.True(runner.Token.IsCancellationRequested);
        Assert.True(vm.IsCancelling);
        Assert.False(vm.CancelCommand.CanExecute(null));

        runner.Finish.SetResult(Result(added: 1, cancelled: true));
        await run;

        Assert.Equal("Import cancelled", vm.SummaryTitle);
        Assert.Equal("Stopped early. 1 track added and kept in your library.", vm.SummaryLine);
        Assert.True(vm.IsInterruptedFinish);
    }

    [Fact]
    public async Task StartingASecondImportWhileOneRuns_QueuesIt_AndRunsItAfterwards()
    {
        var first = new FakeRunner();
        var second = new FakeRunner();
        var calls = 0;
        var vm = new LibraryImportViewModel(_bus, NullLogger<LibraryImportViewModel>.Instance, null, null,
            (r, p, ct) => Interlocked.Increment(ref calls) == 1 ? first.Run(r, p, ct) : second.Run(r, p, ct), a => a());

        Assert.True(await vm.StartImportAsync(new[] { Guid.NewGuid() }));
        await first.Started.Task;
        var queuedFolder = Guid.NewGuid();
        Assert.False(await vm.StartImportAsync(new[] { queuedFolder }));
        Assert.Contains(_events.OfType<ToastRequestedEvent>(), t => t.Title == "Import queued");

        first.Finish.SetResult(Result(added: 0));
        await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { queuedFolder }, second.Request!.FolderIds);
        Assert.True(vm.IsRunning);
        second.Finish.SetResult(Result(added: 0));
    }

    [Fact]
    public async Task ARunnerThatThrows_EndsInAnErrorSummary_NotACrash()
    {
        var vm = new LibraryImportViewModel(_bus, NullLogger<LibraryImportViewModel>.Instance, null, null,
            (_, _, _) => throw new InvalidOperationException("disk gone"), a => a());

        var result = await vm.RunImportAsync(null);

        Assert.NotNull(result);
        Assert.Equal("Import stopped", vm.SummaryTitle);
        Assert.Contains("disk gone", vm.SummaryLine);
        Assert.False(vm.IsRunning);
        Assert.True(vm.IsCompleted);
    }
}
