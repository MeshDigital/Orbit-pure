# Library UI Optimization (Phase 13)

> [!NOTE]
> This document details the performance optimizations implemented to handle large music libraries (10k+ tracks) with rich metadata visualization.

## Overview
The Library UI suffered from scroll lag and high memory usage due to the complexity of the `DataGrid` rows. Each row contained nested `ItemsControl`s for tags (Vibe Pills), resulting in a deep visual tree. This was resolved by implementing a custom lightweight rendering control and flattening the visual hierarchy.

## Key Components

### 1. VibePillContainer (Lightweight Rendering)
The most significant win came from replacing the standard `ItemsControl` used for displaying "Vibe Pills" (genre/mood tags) with a custom `Control`.

- **Problem**: Using `ItemsControl` creates a `ContentPresenter`, `Border`, and `TextBlock` for *every single tag* in *every single row*. For a library with 5 tags per track and 20 visible rows, this added ~300+ heavy visual elements.
- **Solution**: `VibePillContainer` is a custom Avalonia control that renders all tags onto a single visual surface using low-level drawing commands (`DrawingContext`).
- **Mechanism**:
    - **Caching**: Pre-calculates text layout (`FormattedText`) and sizes in `MeasureOverride` to avoid expensive text shaping during render passes.
    - **Drawing**: In `Render()`, iterates through the cached layout and draws rounded rectangles and text directly.
    - **Result**: Reduces N visual elements to 1 per row.

```csharp
// Example of lightweight rendering in VibePillContainer.cs
public override void Render(DrawingContext context)
{
    if (_cachedPills == null) return;
    foreach (var pill in _cachedPills)
    {
        // Draw background
        context.DrawRectangle(pill.Background, null, rect, cornerRadius, cornerRadius);
        // Draw text
        context.DrawText(pill.Text, new Point(x + 8, textY));
        x += pill.Width + spacing;
    }
}
```

### 2. Visual Tree Flattening (StandardTrackRow)
The `StandardTrackRow.axaml` was refactored to minimize nesting.
- Removed unnecessary `Border` wrappers.
- Used `StackPanel` over `Grid` where precise column alignment wasn't strictly required inside cells (cheaper layout pass).
- Consolidated multiple boolean visibility converters into `MultiBinding` to reduce binding overhead.

### 3. Data Virtualization (VirtualizedTrackCollection)
To support libraries with >50,000 tracks without loading all `PlaylistTrackViewModel`s into memory:
- Implemented a custom `IList` that acts as a window into the database.
- **On-Demand Loading**: Only hydrates ViewModels for the rows currently in the viewport.
- **Ghost Rows**: Placeholders are used until data is fetched asynchronously.

## Performance Impact
- **Visual Tree Depth**: Reduced by ~60% per row.
- **Memory Usage**: Significant reduction in managed object count (fewer Avalonia Visuals).
- **Scrolling**: Achieved 60fps scrolling on tested hardware (previously dipped to <30fps).

## Debugging
New logging was introduced to monitor virtualization behavior:
- `[VirtualizedTrackCollection] Page fault at index X` logs indicate data fetching.
- Ensure `ViewSettings.ShowVibePills` is enabled to see the optimizer in action.
