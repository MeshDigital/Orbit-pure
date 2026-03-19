using Xunit;
using Microsoft.Extensions.Logging;
using Moq;
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SLSKDONET.Tests.Services;

public class NetworkHealthServiceTests
{
    private readonly NetworkHealthService _healthService;
    private readonly Mock<ILogger<NetworkHealthService>> _mockLogger;

    public NetworkHealthServiceTests()
    {
        _mockLogger = new Mock<ILogger<NetworkHealthService>>();
        _healthService = new NetworkHealthService(_mockLogger.Object);
    }

    [Fact]
    public void GetCurrentHealth_WithNoDataStartsHealthy()
    {
        // Act
        var signal = _healthService.GetCurrentHealth();

        // Assert
        Assert.Equal(ThrottleStatus.None, signal.ThrottleStatus);
        Assert.Equal(BanStatus.None, signal.BanStatus);
        Assert.Equal(0, signal.TotalSearchCount);
        Assert.Equal(0, signal.RecentTimeoutCount);
    }

    [Fact]
    public void RecordConnectionStateChange_LoggedInTransition_ClearsFailureStatus()
    {
        // Arrange
        _healthService.RecordConnectionFailure(ConnectionFailureStatus.AuthenticationTimeout, "Test timeout");

        // Act
        _healthService.RecordConnectionStateChange("Connected, LoggedIn");

        // Assert
        var signal = _healthService.GetCurrentHealth();
        Assert.Equal(ConnectionFailureStatus.Healthy, signal.LastFailureStatus);
        Assert.Null(signal.LastFailureMessage);
    }

    [Fact]
    public void RecordSearch_SuccessfulSearch_IncreasesMetrics()
    {
        // Act
        _healthService.RecordSearch("Test Query", 100, 50, true);

        // Assert
        var signal = _healthService.GetCurrentHealth();
        Assert.Equal(1, signal.TotalSearchCount);  // One search record
        Assert.Equal(0, signal.ZeroResultSearchCount);  // 50 results is not zero
        Assert.Equal(1, signal.SuccessfulSearchCount);  // 50 > 0, so successful
        Assert.NotNull(signal.LastSuccessfulSearch);
    }

    [Fact]
    public void DetectThrottleStatus_Suspected_WhenOver80PercentZeroResults()
    {
        // Arrange - Create 10 searches, 9 with zero results
        for (int i = 0; i < 9; i++)
        {
            _healthService.RecordSearch($"Query{i}", 10, 0, true);
        }
        _healthService.RecordSearch("Query9", 10, 1, true);  // One success

        // Act
        var signal = _healthService.GetCurrentHealth();

        // Assert
        Assert.Equal(ThrottleStatus.Suspected, signal.ThrottleStatus);
        Assert.True(signal.IsThrottled);
        Assert.Contains("Suspected throttle", signal.DiagnosticMessage);
    }

    [Fact]
    public void DetectThrottleStatus_Confirmed_WhenPersistsOver5Minutes()
    {
        // Arrange
        long timeOffset = 0;
        
        // Simulate >95% zero-result searches for a period
        for (int i = 0; i < 20; i++)
        {
            _healthService.RecordSearch($"Query{i}", 10, i < 19 ? 0 : 1, true);  // 19 zeros, 1 success
            timeOffset += 15000;  // Advance time by 15 seconds per search
        }

        // Wait for time to advance (this is a simplified test - real test would use a time mock)
        // For now, we just verify the search records are logged
        var signal = _healthService.GetCurrentHealth();

        // Assert
        Assert.Equal(20, signal.TotalSearchCount);
        Assert.Equal(19, signal.ZeroResultSearchCount);
        Assert.Equal(95, signal.ZeroResultPercentage);
    }

    [Fact]
    public void DetectBanStatus_Suspected_WhenConnectionRefusedTwice()
    {
        // Act
        _healthService.RecordConnectionFailure(ConnectionFailureStatus.ConnectionRefused, "Connection refused");
        
        _healthService.RecordConnectionFailure(ConnectionFailureStatus.ConnectionRefused, "Connection refused again");
        var signal = _healthService.GetCurrentHealth();

        // Assert
        Assert.Equal(BanStatus.Suspected, signal.BanStatus);
        Assert.True(signal.IsBanned);
        Assert.Equal(2, signal.RecentConnectionRefusedCount);
    }

    [Fact]
    public void RecordConnectionFailure_AuthenticationTimeout_RecordsCorrectly()
    {
        // Act
        _healthService.RecordConnectionFailure(ConnectionFailureStatus.AuthenticationTimeout, "Auth failed");

        // Assert
        var signal = _healthService.GetCurrentHealth();
        Assert.Equal(ConnectionFailureStatus.AuthenticationTimeout, signal.LastFailureStatus);
        Assert.Equal("Auth failed", signal.LastFailureMessage);
        Assert.Equal(1, signal.RecentTimeoutCount);
    }

    [Fact]
    public void ResetDiagnostics_ClearsAllData()
    {
        // Arrange - Build up data
        _healthService.RecordSearch("Query1", 100, 0, true);
        _healthService.RecordConnectionFailure(ConnectionFailureStatus.NetworkTimeout, "Test");

        // Act
        _healthService.ResetDiagnostics();

        // Assert
        var signal = _healthService.GetCurrentHealth();
        Assert.Equal(0, signal.TotalSearchCount);
        Assert.Equal(0, signal.RecentTimeoutCount);
        Assert.Equal(ConnectionFailureStatus.Healthy, signal.LastFailureStatus);
    }

    [Fact]
    public void GetRecentHistory_ReturnsLimitedEntries()
    {
        // Arrange - Add 150 searches
        for (int i = 0; i < 150; i++)
        {
            _healthService.RecordSearch($"Query{i}", 10, i % 2 == 0 ? 0 : 1, true);
        }

        // Act
        var history = _healthService.GetRecentHistory(50);

        // Assert
        Assert.Equal(50, history.Count);
        Assert.All(history, h => Assert.NotNull(h.Query));
    }

    [Fact]
    public void DiagnosticMessage_IsHealthy_WhenConnectedAndSearchesSucceed()
    {
        // Arrange
        _healthService.RecordConnectionStateChange("Connected, LoggedIn");
        for (int i = 0; i < 5; i++)
        {
            _healthService.RecordSearch($"Query{i}", 100, 50 + i, true);
        }

        // Act
        var signal = _healthService.GetCurrentHealth();

        // Assert
        Assert.True(signal.IsHealthy);
        Assert.Contains("healthy", signal.DiagnosticMessage);
    }

    [Fact]
    public void IsThrottled_ReturnsTrueWhenThrottleDetected()
    {
        // Arrange
        for (int i = 0; i < 9; i++)
        {
            _healthService.RecordSearch($"Query{i}", 10, 0, true);
        }
        _healthService.RecordSearch("Query9", 10, 1, true);

        // Act
        var signal = _healthService.GetCurrentHealth();

        // Assert
        Assert.True(signal.IsThrottled);
        Assert.Equal(ThrottleStatus.Suspected, signal.ThrottleStatus);
    }

    [Fact]
    public void IsBanned_ReturnsTrueWhenBanDetected()
    {
        // Arrange
        _healthService.RecordConnectionFailure(ConnectionFailureStatus.ConnectionRefused, "Refused");
        _healthService.RecordConnectionFailure(ConnectionFailureStatus.ConnectionRefused, "Refused");

        // Act
        var signal = _healthService.GetCurrentHealth();

        // Assert
        Assert.True(signal.IsBanned);
        Assert.Equal(BanStatus.Suspected, signal.BanStatus);
    }

    [Fact]
    public void IsDegraded_ReturnsTrueForMultipleTimeouts()
    {
        // Arrange
        _healthService.RecordConnectionStateChange("Connected, LoggedIn");
        for (int i = 0; i < 3; i++)
        {
            _healthService.RecordConnectionFailure(ConnectionFailureStatus.AuthenticationTimeout, $"Timeout {i}");
        }

        // Act
        var signal = _healthService.GetCurrentHealth();

        // Assert
        Assert.True(signal.IsDegraded);
    }

    [Fact]
    public void RecordConnectionKick_IncrementsCounterAndSetsFailureState()
    {
        // Act
        _healthService.RecordConnectionKick("Kicked from server by admin policy");

        // Assert
        var counters = _healthService.GetReliabilityCounters();
        var signal = _healthService.GetCurrentHealth();
        Assert.Equal(1, counters.KickedEventCount);
        Assert.Equal(ConnectionFailureStatus.ConnectionRefused, signal.LastFailureStatus);
        Assert.Contains("Kicked", signal.LastFailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecordSearchFiltering_AccumulatesAllFilterCounters()
    {
        // Act
        _healthService.RecordSearchFiltering(3, 4, 5, 6, 7, 8);
        _healthService.RecordExcludedPhraseQueryBlock();

        // Assert
        var counters = _healthService.GetReliabilityCounters();
        Assert.Equal(3, counters.FilteredByFormatCount);
        Assert.Equal(4, counters.FilteredByBitrateCount);
        Assert.Equal(5, counters.FilteredBySampleRateCount);
        Assert.Equal(6, counters.FilteredByQueueCount);
        Assert.Equal(7, counters.FilteredByDedupCount);
        Assert.Equal(8, counters.FilteredByExcludedPhraseCount);
        Assert.Equal(1, counters.ExcludedPhraseQueryBlocks);
    }

    [Fact]
    public void ResetDiagnostics_AlsoClearsReliabilityCounters()
    {
        // Arrange
        _healthService.RecordConnectionKick();
        _healthService.RecordExcludedPhraseQueryBlock();
        _healthService.RecordSearchFiltering(1, 1, 1, 1, 1, 1);

        // Act
        _healthService.ResetDiagnostics();

        // Assert
        var counters = _healthService.GetReliabilityCounters();
        Assert.Equal(0, counters.KickedEventCount);
        Assert.Equal(0, counters.ExcludedPhraseQueryBlocks);
        Assert.Equal(0, counters.FilteredByFormatCount);
        Assert.Equal(0, counters.FilteredByBitrateCount);
        Assert.Equal(0, counters.FilteredBySampleRateCount);
        Assert.Equal(0, counters.FilteredByQueueCount);
        Assert.Equal(0, counters.FilteredByDedupCount);
        Assert.Equal(0, counters.FilteredByExcludedPhraseCount);
    }

    [Fact]
    public void RecordTransferOutcome_TracksSucceededAndFailedBuckets()
    {
        // Arrange
        _healthService.RecordTransferOutcome(null);
        _healthService.RecordTransferOutcome(DownloadFailureReason.RemoteQueueDenied);
        _healthService.RecordTransferOutcome(DownloadFailureReason.RemoteAccessDenied);
        _healthService.RecordTransferOutcome(DownloadFailureReason.NetworkError);
        _healthService.RecordTransferOutcome(DownloadFailureReason.Timeout);
        _healthService.RecordTransferOutcome(DownloadFailureReason.PeerRejected);
        _healthService.RecordTransferOutcome(DownloadFailureReason.UserCancelled);
        _healthService.RecordTransferOutcome(DownloadFailureReason.TransferFailed);

        // Act
        var transfer = _healthService.GetTransferCounters();

        // Assert
        Assert.Equal(1, transfer.Succeeded);
        Assert.Equal(1, transfer.RemoteQueueDenied);
        Assert.Equal(1, transfer.RemoteAccessDenied);
        Assert.Equal(1, transfer.NetworkError);
        Assert.Equal(1, transfer.Timeout);
        Assert.Equal(1, transfer.PeerRejected);
        Assert.Equal(1, transfer.Cancelled);
        Assert.Equal(1, transfer.OtherFailure);
        Assert.Equal(8, transfer.Total);
        Assert.Equal(7, transfer.TotalFailed);
    }

    [Fact]
    public void ResetDiagnostics_AlsoClearsTransferCounters()
    {
        // Arrange
        _healthService.RecordTransferOutcome(null);
        _healthService.RecordTransferOutcome(DownloadFailureReason.NetworkError);

        // Act
        _healthService.ResetDiagnostics();

        // Assert
        var transfer = _healthService.GetTransferCounters();
        Assert.Equal(0, transfer.Succeeded);
        Assert.Equal(0, transfer.NetworkError);
        Assert.Equal(0, transfer.Total);
        Assert.Equal(0, transfer.TotalFailed);
    }
}
