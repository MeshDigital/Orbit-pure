using System.Windows.Input;

namespace SLSKDONET.ViewModels.Library;

/// <summary>
/// One entry in the track context menu's "Add to Set" submenu. Carries its own command so the
/// menu item binds to it directly (no parent-DataContext cast in the binding path).
/// </summary>
public sealed record SetMenuEntry(string Name, ICommand Command);
