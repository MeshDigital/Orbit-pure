using System.IO;
using Xunit;
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// Unit tests for DownloadManager.ClassifyTransferFailure — the 5-branch transfer error taxonomy.
/// </summary>
public class TransferFailureClassificationTests
{
    // ────────────────────────────────────────────────────────────────
    // RemoteAccessDenied
    // ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("You are banned from this peer")]
    [InlineData("Not authorized to download")]
    [InlineData("access denied by remote host")]
    public void ClassifyTransferFailure_AccessDeniedMessages_ReturnsRemoteAccessDenied(string message)
    {
        var ex = new Exception(message);
        var result = DownloadManager.ClassifyTransferFailure(ex);

        Assert.Equal(DownloadFailureReason.RemoteAccessDenied, result.RetryFailureReason);
    }

    [Theory]
    [InlineData("You are banned from this peer")]
    [InlineData("Not authorized to download")]
    [InlineData("access denied by remote host")]
    public void ClassifyTransferFailure_AccessDenied_NoHedgeAndNoDelay(string message)
    {
        var ex = new Exception(message);
        var result = DownloadManager.ClassifyTransferFailure(ex);

        Assert.False(result.AllowHedgeFailover);
        Assert.Null(result.DelayMinutes);
    }

    [Fact]
    public void ClassifyTransferFailure_AccessDenied_ShouldNotAutoRetry()
    {
        var ex = new Exception("banned");
        var result = DownloadManager.ClassifyTransferFailure(ex);

        Assert.False(result.RetryFailureReason.ShouldAutoRetry());
    }

    // ────────────────────────────────────────────────────────────────
    // RemoteQueueDenied
    // ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Too many files in queue")]
    [InlineData("Remote peer queue is full")]
    [InlineData("join the queue and wait")]
    public void ClassifyTransferFailure_QueueDeniedMessages_ReturnsRemoteQueueDenied(string message)
    {
        var ex = new Exception(message);
        var result = DownloadManager.ClassifyTransferFailure(ex);

        Assert.Equal(DownloadFailureReason.RemoteQueueDenied, result.RetryFailureReason);
    }

    [Fact]
    public void ClassifyTransferFailure_TransferRejectedException_ReturnsRemoteQueueDenied()
    {
        // The classifier matches on type name string so a subclass named TransferRejectedException works.
        var ex = new TransferRejectedException("peer rejected transfer");
        var result = DownloadManager.ClassifyTransferFailure(ex);

        Assert.Equal(DownloadFailureReason.RemoteQueueDenied, result.RetryFailureReason);
    }

    [Fact]
    public void ClassifyTransferFailure_QueueDenied_AllowsHedgeWithTwoMinuteDelay()
    {
        var ex = new Exception("queue rejected");
        var result = DownloadManager.ClassifyTransferFailure(ex);

        Assert.True(result.AllowHedgeFailover);
        Assert.Equal(2, result.DelayMinutes);
    }

    // ────────────────────────────────────────────────────────────────
    // NetworkError
    // ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Connection refused by remote peer")]
    [InlineData("Transfer aborted unexpectedly")]
    [InlineData("Unable to read data from transport connection")]
    public void ClassifyTransferFailure_NetworkErrorMessages_ReturnsNetworkError(string message)
    {
        var ex = new Exception(message);
        var result = DownloadManager.ClassifyTransferFailure(ex);

        Assert.Equal(DownloadFailureReason.NetworkError, result.RetryFailureReason);
    }

    [Fact]
    public void ClassifyTransferFailure_IOException_ReturnsNetworkError()
    {
        var ex = new IOException("pipe broken");
        var result = DownloadManager.ClassifyTransferFailure(ex);

        Assert.Equal(DownloadFailureReason.NetworkError, result.RetryFailureReason);
    }

    [Fact]
    public void ClassifyTransferFailure_NetworkError_AllowsHedgeWithTwoMinuteDelay()
    {
        var ex = new IOException("pipe broken");
        var result = DownloadManager.ClassifyTransferFailure(ex);

        Assert.True(result.AllowHedgeFailover);
        Assert.Equal(2, result.DelayMinutes);
    }

    // ────────────────────────────────────────────────────────────────
    // Timeout
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void ClassifyTransferFailure_TimeoutException_ReturnsTimeout()
    {
        var ex = new TimeoutException("transfer stalled");
        var result = DownloadManager.ClassifyTransferFailure(ex);

        Assert.Equal(DownloadFailureReason.Timeout, result.RetryFailureReason);
    }

    [Fact]
    public void ClassifyTransferFailure_Timeout_AllowsHedgeWithOneMinuteDelay()
    {
        var ex = new TimeoutException();
        var result = DownloadManager.ClassifyTransferFailure(ex);

        Assert.True(result.AllowHedgeFailover);
        Assert.Equal(1, result.DelayMinutes);
    }

    // ────────────────────────────────────────────────────────────────
    // PeerRejected (catch-all)
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void ClassifyTransferFailure_UnknownException_ReturnsPeerRejected()
    {
        var ex = new InvalidOperationException("some unknown condition");
        var result = DownloadManager.ClassifyTransferFailure(ex);

        Assert.Equal(DownloadFailureReason.PeerRejected, result.RetryFailureReason);
    }

    [Fact]
    public void ClassifyTransferFailure_PeerRejected_NoHedgeAndNoDelay()
    {
        var ex = new InvalidOperationException("some unknown condition");
        var result = DownloadManager.ClassifyTransferFailure(ex);

        Assert.False(result.AllowHedgeFailover);
        Assert.Null(result.DelayMinutes);
    }

    [Fact]
    public void ClassifyTransferFailure_PeerRejected_OperatorMessageIsExceptionMessage()
    {
        const string msg = "some unique error text";
        var ex = new InvalidOperationException(msg);
        var result = DownloadManager.ClassifyTransferFailure(ex);

        Assert.Contains(msg, result.OperatorMessage);
    }

    [Fact]
    public void ClassifyTransferFailure_PeerRejected_EmptyMessage_UsesDefaultOperatorMessage()
    {
        var ex = new Exception(string.Empty);
        var result = DownloadManager.ClassifyTransferFailure(ex);

        Assert.Equal(DownloadFailureReason.PeerRejected, result.RetryFailureReason);
        Assert.False(string.IsNullOrWhiteSpace(result.OperatorMessage));
    }

    // ────────────────────────────────────────────────────────────────
    // ShouldAutoRetry cross-check
    // ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DownloadFailureReason.RemoteQueueDenied,  true)]
    [InlineData(DownloadFailureReason.NetworkError,       true)]
    [InlineData(DownloadFailureReason.Timeout,            true)]
    [InlineData(DownloadFailureReason.RemoteAccessDenied, false)]
    [InlineData(DownloadFailureReason.PeerRejected,       true)]
    public void ShouldAutoRetry_ReflectsRetryPolicy(DownloadFailureReason reason, bool expected)
    {
        Assert.Equal(expected, reason.ShouldAutoRetry());
    }

    // ────────────────────────────────────────────────────────────────
    // Priority: access-denied beats queue (banned comes first in switch)
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void ClassifyTransferFailure_BannedMessageBeatsQueueMessage_ReturnsAccessDenied()
    {
        // Message contains both "banned" and "queue" — the access-denied branch is checked first.
        var ex = new Exception("banned from queue");
        var result = DownloadManager.ClassifyTransferFailure(ex);

        Assert.Equal(DownloadFailureReason.RemoteAccessDenied, result.RetryFailureReason);
    }
}

/// <summary>
/// Stand-in for Soulseek.NET's TransferRejectedException.
/// The classifier matches on type-name string, so this triggers the same branch.
/// </summary>
internal sealed class TransferRejectedException(string message) : Exception(message);
