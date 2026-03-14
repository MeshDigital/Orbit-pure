using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SLSKDONET.Services.Platform;

namespace SLSKDONET.Services;

/// <summary>
/// Factory for creating platform-specific secure token storage implementations.
/// </summary>
public static class SecureTokenStorageFactory
{
    /// <summary>
    /// Creates the appropriate token storage implementation for the current platform.
    /// </summary>
    /// <param name="serviceProvider">Service provider for dependency injection</param>
    /// <returns>Platform-specific token storage implementation</returns>
    public static ISecureTokenStorage Create(IServiceProvider serviceProvider)
    {
        if (OperatingSystem.IsWindows())
        {
            var logger = serviceProvider.GetRequiredService<ILogger<WindowsTokenStorage>>();
            return new WindowsTokenStorage(logger);
        }
        else if (OperatingSystem.IsMacOS())
        {
            var logger = serviceProvider.GetRequiredService<ILogger<MacOSTokenStorage>>();
            return new MacOSTokenStorage(logger);
        }
        else if (OperatingSystem.IsLinux())
        {
            var logger = serviceProvider.GetRequiredService<ILogger<LinuxTokenStorage>>();
            return new LinuxTokenStorage(logger);
        }
        else
        {
            throw new PlatformNotSupportedException("Secure token storage is not supported on this platform");
        }
    }
}
