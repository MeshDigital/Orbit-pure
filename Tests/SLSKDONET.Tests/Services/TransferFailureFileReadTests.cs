using Xunit;
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// #334: a "File read error" (the Soulseek library's stale-resume-offset signal) must be classified
/// as a reset-and-retry, not as a queue rejection that blacklists an innocent peer.
/// </summary>
public class TransferFailureFileReadTests
{
    [Theory]
    [InlineData("File read error")]
    [InlineData("file read error")]
    [InlineData("peer file read error")]
    public void ClassifyTransferFailure_FileReadError_ReturnsResetPartAndRetry(string message)
    {
        var result = DownloadManager.ClassifyTransferFailure(new Exception(message));

        Assert.True(result.ResetPartAndRetry);
        Assert.Equal(DownloadFailureReason.TransferFailed, result.RetryFailureReason);
        Assert.False(result.AllowHedgeFailover);
    }

    [Fact]
    public void ClassifyTransferFailure_TransferRejectedExceptionWithFileReadError_IsNotQueueDenied()
    {
        // Soulseek wraps a stale resume offset as TransferRejectedException("File read error").
        var result = DownloadManager.ClassifyTransferFailure(new TestTransferRejectedException("File read error"));

        Assert.True(result.ResetPartAndRetry);
        Assert.NotEqual(DownloadFailureReason.RemoteQueueDenied, result.RetryFailureReason);
    }

    [Fact]
    public void ClassifyTransferFailure_TransferRejectedExceptionWithoutReadError_StillQueueDenied()
    {
        // A plain rejection (no read-error message) keeps the existing queue-denied mapping.
        var result = DownloadManager.ClassifyTransferFailure(new TestTransferRejectedException("peer queue rejected transfer"));

        Assert.False(result.ResetPartAndRetry);
        Assert.Equal(DownloadFailureReason.RemoteQueueDenied, result.RetryFailureReason);
    }

    [Fact]
    public void ClassifyTransferFailure_TransferRejectedWrappedInAggregate_StillQueueDenied()
    {
        // #385: async paths wrap the rejection in an AggregateException; the classifier must unwrap it.
        // The message deliberately omits "queue" so only the type-name unwrap can classify this.
        var result = DownloadManager.ClassifyTransferFailure(new AggregateException(new TestTransferRejectedException("peer rejected transfer")));

        Assert.Equal(DownloadFailureReason.RemoteQueueDenied, result.RetryFailureReason);
    }
}

/// <summary>Stand-in for Soulseek.TransferRejectedException (the classifier matches on type-name string).</summary>
internal sealed class TestTransferRejectedException(string message) : Exception(message);
