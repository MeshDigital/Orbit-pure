# Column Configuration & UI Personalization
**Updated**: January 21, 2026

This guide documents the new column configuration system that allows users to customize library and playlist views.

---

## Overview

The `ColumnConfigurationService` provides persistent, user-driven UI customization:
- **Show/Hide Columns**: Toggle visibility for Artist, BPM, Key, Bitrate, Album, Genres, Added date
- **Resize Columns**: Drag column borders to adjust widths
- **Reorder Columns**: Drag column headers to change display order
- **Sorting**: Click headers to sort by any column
- **Persistence**: Settings auto-save to disk with 2-second debounce

---

## Architecture

### ColumnConfigurationService
**Location**: `Services/Library/ColumnConfigurationService.cs`

**Key Methods**:
- `LoadConfigurationAsync()` - Loads from disk or returns defaults
- `SaveConfiguration(config)` - Queues save with Rx debounce
- `GetDefaultConfiguration()` - Returns initial column set

**Settings File**:
- Path: `%APPDATA%/ORBIT/column_config.json`
- Format: JSON array of `ColumnDefinition` objects
- Auto-created on first save

### ColumnDefinition Model
**Location**: `Models/ColumnDefinition.cs`

**Fields**:
- `Id` (string) - Unique identifier (e.g., "Artist", "BPM")
- `Header` (string) - Display name
- `Width` (double?) - Column width in pixels
- `DisplayOrder` (int) - Sort position left-to-right
- `IsVisible` (bool) - Show/hide toggle
- `PropertyPath` (string) - ViewModel property binding (e.g., "BPM", "Artist")
- `CanSort` (bool) - Allow sorting on this column
- `CellTemplateKey` (string?) - Custom cell template (e.g., "VibePill", "StarRating")
- `DataType` (Type?) - Helps with numeric vs string sorting

---

## Default Columns

| Order | ID | Header | Width | Visible | Property | Type |
|-------|---|--------|-------|---------|----------|------|
| 0 | Status | (icon) | 40 | ✓ | StatusSymbol | - |
| 1 | Artist | Artist | 200 | ✓ | Artist | string |
| 2 | Title | Title | 250 | ✓ | Title | string |
| 3 | Duration | Time | 80 | ✓ | DurationFormatted | TimeSpan |
| 4 | BPM | BPM | 70 | ✓ | BPM | double |
| 5 | Key | Key | 60 | ✓ | MusicalKey | string |
| 6 | Bitrate | Bitrate | 80 | ✓ | BitrateFormatted | string |
| 7 | Format | Format | 70 | ✗ | Format | string |
| 8 | Album | Album | 200 | ✓ | Album | string |
| 9 | Genres | Genres | 150 | ✗ | Genres | string |
| 10 | AddedAt | Added | 120 | ✗ | AddedAt | DateTime |

**Hidden by Default**: Format, Genres, Added (save screen space)

---

## Implementation

### ViewModel Integration
The `TrackListViewModel` loads configuration on startup:
```csharp
var config = await _columnConfigService.LoadConfigurationAsync();
Columns = new ObservableCollection<ColumnDefinition>(config);
```

### UI Binding
Columns are bound to the TreeDataGrid or ItemsRepeater in LibraryPage:
```xml
<TreeDataGrid Columns="{Binding Columns}" />
```

### Reactive Save
Changes trigger save with 2-second debounce to avoid excessive I/O:
```csharp
_saveSubject
    .Throttle(TimeSpan.FromSeconds(2))
    .Subscribe(async config => await SaveToFileAsync(config));
```

---

## Examples

### Hide the Bitrate Column
1. Right-click column header → **Hide**
2. Or drag column width to 0
3. Settings saved automatically (after 2s debounce)

### Reorder Columns
1. Drag "Key" column header to position after "Title"
2. Display order updates in real-time
3. New order persists on app restart

### Resize Columns
1. Position cursor on column border
2. Drag left/right to adjust width
3. Width saved on release

---

## JSON Format Example

```json
[
  {
    "Id": "Status",
    "Header": " ",
    "Width": 40.0,
    "DisplayOrder": 0,
    "IsVisible": true,
    "PropertyPath": "StatusSymbol",
    "CanSort": true,
    "CellTemplateKey": null,
    "DataType": null
  },
  {
    "Id": "Artist",
    "Header": "Artist",
    "Width": 200.0,
    "DisplayOrder": 1,
    "IsVisible": true,
    "PropertyPath": "Artist",
    "CanSort": true,
    "CellTemplateKey": null,
    "DataType": "System.String"
  },
  {
    "Id": "BPM",
    "Header": "BPM",
    "Width": 70.0,
    "DisplayOrder": 4,
    "IsVisible": true,
    "PropertyPath": "BPM",
    "CanSort": true,
    "CellTemplateKey": "BpmTemplate",
    "DataType": "System.Double"
  }
]
```

---

## Future Enhancements

### Planned (Post-alpha.9.1)
- **Sortable Columns UI**: UI for drag-to-reorder without LibraryPage changes
- **Save Multiple Profiles**: Support "Analyst View", "DJ View", "Minimal View" presets
- **Column Groups**: Group related columns (e.g., "Audio Analysis" → BPM, Key, Energy)
- **Aggregations**: Show BPM range or average at bottom
- **Custom Cell Templates**: Per-column vibes, star ratings, progress bars

### Phase 24+
- **Database Persistence**: Option to save column config to database instead of JSON
- **Sync Across Devices**: Cloud sync for multi-machine DJ setups

---

## Troubleshooting

### Columns Not Saving
- Check `%APPDATA%/ORBIT/` permissions (must be writable)
- Restart app to force reload from disk
- Check `column_config.json` file exists and is valid JSON

### Columns Reset to Default
- Delete `column_config.json` to restore defaults
- Or manually edit the file to reset visible/width values

### Performance Issues with Many Columns
- Visible columns affect render performance
- Hide unused columns (Format, Genres, AddedAt)
- Reduce number of custom cell templates

---

## Developer Notes

### Adding a New Column
1. Add to `GetDefaultConfiguration()` in ColumnConfigurationService
2. Add corresponding property to ViewModel (e.g., `public double? Energy { get; set; }`)
3. Bind in LibraryPage XAML if using custom template
4. Increment `DisplayOrder` to place in view

### Custom Cell Template
```xml
<TreeDataGrid.Columns>
  <TreeDataGridTextColumn Header="Energy" Width="80" 
    Binding="{Binding Energy}"
    CellTemplate="{StaticResource EnergyVisualizerTemplate}" />
</TreeDataGrid.Columns>
```

### Sorting Numeric vs String
Set `DataType` property:
- `typeof(double)` for BPM (sorts 100 before 20)
- `typeof(string)` for Artist (sorts "Artist" before "Zed")

---

**See Also**:
- [FEATURES.md](../FEATURES.md) - Library management features
- [ARCHITECTURE.md](../ARCHITECTURE.md) - UI architecture
- `TrackListViewModel.cs` - View model implementation
