namespace SLSKDONET.ViewModels;

/// <summary>
/// A row a virtualised list returns before its data has loaded ("Loading…"). Grids must never
/// select, drag or act on one; <see cref="SLSKDONET.Views.Avalonia.Controls.VirtualGrid"/> checks this.
/// </summary>
public interface IPlaceholderRow
{
    bool IsPlaceholder { get; }
}
