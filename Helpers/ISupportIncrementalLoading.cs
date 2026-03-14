using System.Threading.Tasks;

namespace SLSKDONET.Helpers;

/// <summary>
/// Specifies that a view model supports incremental loading (infinite scrolling).
/// Compatible with UWP paradigms for easier virtualization implementation.
/// </summary>
public interface ISupportIncrementalLoading
{
    /// <summary>
    /// Gets a value indicating whether more items can be loaded.
    /// </summary>
    bool HasMoreItems { get; }

    /// <summary>
    /// Loads more items asynchronously.
    /// </summary>
    /// <param name="count">The number of items to load.</param>
    /// <returns>A wrapper containing the count of items loaded.</returns>
    Task<int> LoadMoreItemsAsync(uint count);
}
