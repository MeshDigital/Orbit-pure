using System;
using System.Collections.Generic;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.Services.Library;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// #216 — the "Scan library integrity" flow must never delete files without an explicit
/// confirmation. These tests prove the scan surfaces fatal issues and only calls remediation
/// after the user accepts the confirmation.
/// </summary>
[Collection(NonParallelCollection.Name)]
public class AnalysisPageIntegrityScanConfirmationTests : IDisposable
{
    private readonly EventBusService _bus = new();
    private readonly Mock<ILibraryCorruptionScanService> _scan;
    private readonly Mock<ICorruptFileRemediationService> _remediation;
    private readonly Mock<IDialogService> _dialog;
    private readonly AnalysisPageViewModel _vm;
    private readonly List<ToastRequestedEvent> _toasts = new();

    public AnalysisPageIntegrityScanConfirmationTests()
    {
        _scan = new Mock<ILibraryCorruptionScanService>();
        _remediation = new Mock<ICorruptFileRemediationService>();
        _dialog = new Mock<IDialogService>();

        var projectionService = new LifecycleProjectionService(new Mock<ILibraryService>().Object);
        _vm = new AnalysisPageViewModel(
            _bus,
            lifecycleProjectionService: projectionService,
            corruptionScanService: _scan.Object,
            remediationService: _remediation.Object,
            dialogService: _dialog.Object);

        _bus.GetEvent<ToastRequestedEvent>().Subscribe(_toasts.Add);
    }

    public void Dispose()
    {
        _bus.Dispose();
        _vm.Dispose();
    }

    private static LibraryCorruptionScanResult ScanResultWith(params CorruptFileScanEntry[] issues) =>
        new(
            Total: issues.Length,
            Clean: 0,
            Warned: 0,
            Fatal: issues.Length,
            Missing: issues.Count(i => i.IsMissing),
            Issues: issues,
            CompletedAt: DateTime.UtcNow);

    private static CorruptFileScanEntry FatalIssue(string path = @"C:\music\bad.flac") =>
        new("h1", path, "Artist", "Title", CorruptionStatus.Fatal, "FFmpeg: decode failure");

    private void SetupScan(CorruptFileScanEntry[] issues)
    {
        _scan.Setup(s => s.ScanLibraryAsync(
                It.IsAny<IProgress<(int, int, string)>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ScanResultWith(issues));
    }

    [Fact]
    public async Task Scan_DeclinedConfirmation_DoesNotRemediate_AndKeepsResultsVisible()
    {
        _dialog.Setup(d => d.ConfirmListAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);
        SetupScan(new[] { FatalIssue() });

        await _vm.ScanLibraryIntegrityCommand.Execute().ToTask();

        _remediation.Verify(r => r.RemediateAsync(
            It.IsAny<IReadOnlyList<CorruptFileScanEntry>>(),
            It.IsAny<IProgress<(int, int, string)>>(),
            It.IsAny<CancellationToken>()), Times.Never);

        // Results stay visible in the integrity panel for manual review.
        Assert.Single(_vm.ScanIssues);
        Assert.Contains(_toasts, t => t.Title == "Files kept");
    }

    [Fact]
    public async Task Scan_AcceptedConfirmation_RemediatesExactlyOnce()
    {
        _dialog.Setup(d => d.ConfirmListAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        SetupScan(new[] { FatalIssue() });

        _remediation.Setup(r => r.RemediateAsync(
                It.IsAny<IReadOnlyList<CorruptFileScanEntry>>(),
                It.IsAny<IProgress<(int, int, string)>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemediationResult(1, 1, 1, 1, 1, Array.Empty<string>()));

        await _vm.ScanLibraryIntegrityCommand.Execute().ToTask();

        _remediation.Verify(r => r.RemediateAsync(
            It.IsAny<IReadOnlyList<CorruptFileScanEntry>>(),
            It.IsAny<IProgress<(int, int, string)>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(_toasts, t => t.Title == "Integrity remediation" && t.Type == SLSKDONET.Views.NotificationType.Success);
    }

    [Fact]
    public async Task Scan_NoDialogService_FailsSafe_AndNeverDeletes()
    {
        // A VM without a dialog service (headless/tests) cannot get a confirmation, so it must not delete.
        var projectionService = new LifecycleProjectionService(new Mock<ILibraryService>().Object);
        var vm = new AnalysisPageViewModel(
            _bus,
            lifecycleProjectionService: projectionService,
            corruptionScanService: _scan.Object,
            remediationService: _remediation.Object,
            dialogService: null);
        SetupScan(new[] { FatalIssue() });

        await vm.ScanLibraryIntegrityCommand.Execute().ToTask();

        _remediation.Verify(r => r.RemediateAsync(
            It.IsAny<IReadOnlyList<CorruptFileScanEntry>>(),
            It.IsAny<IProgress<(int, int, string)>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        Assert.Single(vm.ScanIssues);
        vm.Dispose();
    }
}
