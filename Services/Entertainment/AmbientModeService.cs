using Microsoft.Extensions.Logging;
using SLSKDONET.Models.Entertainment;

namespace SLSKDONET.Services.Entertainment;

/// <summary>
/// Implements ORBIT's Ambient Mode — monitors idle time and playback state,
/// auto-activating a slow, meditative visual mode when appropriate.
/// </summary>
public sealed class AmbientModeService : IAmbientModeService, IDisposable
{
    private readonly ILogger<AmbientModeService> _logger;
    private AmbientModeConfig _config = new();
    private bool _isActive;
    private bool _isPlaying;
    private DateTime _lastActivity = DateTime.UtcNow;
    private DateTime _pausedSince = DateTime.MaxValue;
    private readonly System.Timers.Timer _pollTimer;
    private bool _disposed;

    public bool IsActive => _isActive;
    public AmbientModeConfig Config => _config;

    public event EventHandler<bool>? ActiveChanged;

    public AmbientModeService(ILogger<AmbientModeService> logger)
    {
        _logger = logger;
        _pollTimer = new System.Timers.Timer(10_000); // Check every 10 s
        _pollTimer.Elapsed += OnPollTimerElapsed;
        _pollTimer.AutoReset = true;
        _pollTimer.Start();
    }

    public void Activate()
    {
        if (_isActive) return;
        _isActive = true;
        _logger.LogInformation("Ambient Mode activated.");
        ActiveChanged?.Invoke(this, true);
    }

    public void Deactivate()
    {
        if (!_isActive) return;
        _isActive = false;
        _logger.LogInformation("Ambient Mode deactivated.");
        ActiveChanged?.Invoke(this, false);
    }

    public void Toggle()
    {
        if (_isActive) Deactivate(); else Activate();
    }

    public void NotifyUserActivity()
    {
        _lastActivity = DateTime.UtcNow;
        // Any user activity exits Ambient Mode
        if (_isActive) Deactivate();
    }

    public void NotifyPlaybackState(bool isPlaying)
    {
        if (isPlaying == _isPlaying) return;
        _isPlaying = isPlaying;

        if (isPlaying)
        {
            // Reset pause timer when playback resumes
            _pausedSince = DateTime.MaxValue;
            // Playing resumes, but don't auto-deactivate — the user chose to play
        }
        else
        {
            // Record when pause started
            _pausedSince = DateTime.UtcNow;
        }
    }

    public void UpdateConfig(AmbientModeConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    private void OnPollTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (_isActive) return; // Already active, nothing to do

        var now = DateTime.UtcNow;

        // Check idle timeout
        if ((now - _lastActivity).TotalSeconds >= _config.IdleTimeoutSeconds)
        {
            _logger.LogDebug("Ambient Mode triggered by idle timeout.");
            Activate();
            return;
        }

        // Check paused timeout
        if (!_isPlaying && _pausedSince != DateTime.MaxValue
            && (now - _pausedSince).TotalSeconds >= _config.PausedTimeoutSeconds)
        {
            _logger.LogDebug("Ambient Mode triggered by pause timeout.");
            Activate();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Stop();
        _pollTimer.Dispose();
    }
}
