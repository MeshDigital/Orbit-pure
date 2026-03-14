using Avalonia.Media;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Represents a visual "pill" or badge in the Library UI (e.g., Genre, Mood, Energy).
/// </summary>
public record VibePill(string Icon, string Label, IBrush Color);
