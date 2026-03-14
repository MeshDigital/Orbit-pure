using System.Threading.Tasks;

namespace SLSKDONET.Services;

/// <summary>
/// Interface for secure, cross-platform token storage.
/// Implementations should use platform-specific secure storage mechanisms.
/// </summary>
public interface ISecureTokenStorage
{
    /// <summary>
    /// Saves a refresh token securely.
    /// </summary>
    /// <param name="refreshToken">The refresh token to store</param>
    Task SaveRefreshTokenAsync(string refreshToken);

    /// <summary>
    /// Loads the stored refresh token.
    /// </summary>
    /// <returns>The refresh token, or null if not found</returns>
    Task<string?> LoadRefreshTokenAsync();

    /// <summary>
    /// Deletes the stored refresh token.
    /// </summary>
    Task DeleteRefreshTokenAsync();
}
