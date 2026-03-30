---
description: How to use and manage the Global Right Panel (Inspector/Sidebar) system.
---

# Global Right Panel (Inspector) Workflow

This workflow describes how to interact with the global 3-column architecture in ORBIT, specifically the dynamic right panel.

## Architecture Overview
The app uses a `SplitView` in `MainWindow.axaml` managed by the `IRightPanelService`. This allows any page (Search, Library, Inbox) to open a contextual sidebar without manual UI management.

## 1. Opening the Inspector
To open the right panel from a ViewModel, send an `OpenInspectorEvent` via the `ReactiveUI.MessageBus`.

```csharp
// Example from a Track selection handler
var evt = new OpenInspectorEvent(trackViewModel, "TRACK DETAILS", "🎵");
MessageBus.Current.SendMessage(evt);
```

## 2. Registering New Content
The right panel uses Avalonia **DataTemplates** to decide how to render the provided ViewModel. To add a new type of inspector:
1. Create your View (e.g., `MyNewInspectorControl.axaml`).
2. Register the template in `MainWindow.axaml` inside the `SplitView.Pane` content control:

```xml
<DataTemplate DataType="vm:MyNewViewModel">
    <controls:MyNewInspectorControl/>
</DataTemplate>
```

## 3. Responsive Behavior
- The panel uses a `WidthToDisplayModeConverter`.
- **Wide Screens (>1100px):** `Inline` mode (pushes content).
- **Narrow Screens:** `Overlay` mode (floats over content).

## 4. Default/Fallback State
- The `PlayerViewModel` is currently registered as the fallback.
- Closing an inspector will revert to the fallback rather than hiding the panel entirely, if a fallback is set via `_rightPanelService.SetFallback()`.

## 5. Metadata Logic (Downloads)
- Groups in the Download Center use `DownloadGroupViewModel`.
- If `SourcePlaylistName` is null, the UI automatically evaluates the `Album` or `Artist` of the first track for the group title.
