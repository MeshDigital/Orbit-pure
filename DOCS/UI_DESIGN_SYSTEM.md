# ORBIT UI Design System

The contract for every view. If a page needs something not listed here, add a token or class to
`Themes/` first. Do not invent it inline.

## Files (load order, all included once from `App.axaml`)

| File | Holds |
|---|---|
| `FluentTheme` (App.axaml) | Base templates, re-accented to mint-teal (`ColorPaletteResources Accent`) |
| `Themes/DesignTokens.axaml` | **All colour values.** Brushes, legacy aliases, Fluent resource overrides, scales |
| `Themes/Icons.axaml` | `icon_*` PathIcon geometries (24x24) |
| `Themes/Controls.axaml` | The class vocabulary below |
| `Themes/GlassConsoleStyles.axaml` | Effects: `glass`, `skeleton`, `pulsing`, `scanning`, `stalled` |
| `src/UI/Tokens/DpiTokens.axaml` | Workstation density tokens (`Ws*`) |

`Themes/AvaloniaTheme.axaml` is only the entry point. **Never `StyleInclude` it from a Window or
UserControl.** A Window-level include outranks App styles and applies every rule twice.

## Tokens

Use brushes (`Brush*`) in views. `Color*` only where a `Color` is required (gradients, shadows).

**Surfaces.** Each level is a few shades lighter than the one below.
| Brush | Hex | Use |
|---|---|---|
| `BrushBg0` | #0F1012 | Shell chrome (top bar, nav), sunken wells (`inset`), input fill |
| `BrushBg1` | #151618 | Page background |
| `BrushBg2` | #1B1C1F | Panels, cards, side panes, dialogs |
| `BrushBg3` | #222327 | Controls: buttons, combo boxes, toasts |
| `BrushBg4` | #2A2C30 | Opaque hover / selected segment |
| `BrushBg5` | #34363B | Pressed, meter track |
| `BrushHover` / `BrushPressed` | white 7% / 12% | Translucent hover/pressed on any surface |
| `BrushSelected` | accent 12% | Selected row / nav item |
| `BrushScrim` / `BrushScrimStrong` | | Modal backdrop / full-screen takeover |

**Borders.** `BrushBorder0` hairline divider · `BrushBorder1` default control/card border ·
`BrushBorder2` hover/emphasis. All three are translucent white, so they work on every surface.

**Text.** `BrushText0` #E8EAED primary · `BrushText1` #A8ADB5 secondary · `BrushText2` #7A808A
muted/section labels · `BrushText3` #5C626B hints, placeholders, empty-state icons ·
`BrushTextDisabled`.

**Accent (the only accent).** `BrushAccent` #4EC9B0 · `BrushAccentHover` · `BrushAccentPressed` ·
`BrushAccentDim` · `BrushAccentSubtle` (tinted bg) · `BrushAccentBorder` (tinted border) ·
`BrushOnAccent` (text on solid accent).

**Semantic (status only, never decoration).** `BrushSuccess` #4CC38A · `BrushWarning` #E5A645 ·
`BrushError` #E5605A · `BrushInfo` #5AA7E8. Each colour also has `*Subtle` (10% bg) and
`*Border` (35%) variants.

**Domain.** `BrushStemVocals/Drums/Bass/Other` only for stem data; `BrushHotCue1`–`8` (+ `BrushOnHotCue`, `BrushOnHotCueLight` for the label) only for CDJ hot-cue pad slots. Not for UI chrome.

**Scales.**
- Spacing (4px grid): `SpaceXxs 2 · SpaceXs 4 · SpaceSm 8 · SpaceMd 12 · SpaceLg 16 · SpaceXl 24 · Space2xl 32 · Space3xl 48`
- Padding presets: `PaddingSm 8,4 · PaddingMd 12,8 · PaddingLg 16,12 · PaddingPanel 16 · PaddingPage 24,16 · PaddingButton 12,6 · PaddingButtonSm 10,4`
- Radius: `RadiusXs 2 · RadiusSm 4` (segments, tags) `· RadiusMd 6` (buttons, inputs, chips) `· RadiusLg 8` (cards, panels, toasts) `· RadiusXl 12` (dialogs) `· RadiusFull`
- Type: `TextSizeDisplay 28 · H1 20 · H2 16 · H3 14 · Body 13 · BodySm 12 · Caption 11 · Mono 12 · Value 18`
- Heights: `ControlHeight 32 · ControlHeightSm 28 · ToolbarHeight 44`
- Icons: `IconSizeXs 12 · Sm 14 · Md 16` (default) `· Lg 20 · Xl 28` (empty state)
- Shadows: `ShadowSm/Md/Lg`. Only floating surfaces (dialogs, toasts, flyouts) get shadows.
- Fonts: `FontUI` (Inter), `FontMono`

**Legacy aliases** (`AccentBrush`, `TextSecondaryBrush`, `DeepBackgroundBrush`, `BorderBrush`,
`RegionBrush`, `Surface*`, `Glass*Brush`, `TextBrush`, `HoverBrush` …) still resolve and map to the
tokens above. **Don't use them in new code.** Each one's §2 replacement is noted in its trailing comment.

## Style classes

### Buttons
Height comes from padding (~32px default, ~27px with `small`). Don't hardcode `Height`.
```xml
<Button Classes="primary"   Content="Download"/>     <!-- solid accent; max ONE per area -->
<Button Classes="secondary" Content="Export"/>       <!-- neutral raised (= un-classed Button) -->
<Button Classes="ghost"     Content="Clear"/>        <!-- no chrome until hover; toolbars -->
<Button Classes="danger"    Content="Delete"/>       <!-- destructive; tinted, solid on hover -->
<Button Classes="primary small" Content="Queue"/>    <!-- small modifier works on all of the above -->
<Button Classes="icon" ToolTip.Tip="Settings"><PathIcon Data="{StaticResource icon_settings}" Width="16" Height="16"/></Button>
<Button Classes="icon small">…</Button>              <!-- 26px square -->
<Button Classes="icon danger">…</Button>             <!-- red glyph, red tint on hover -->
<Button Classes="icon active">…</Button>             <!-- accent glyph = toggled-on state -->
```
Icon buttons **must** have a `ToolTip.Tip`.
Legacy: `icon-button` (40px, player transport), `segbar-item`, `DropDownButton.tool`.
Prefer the new classes.

### Navigation
`Button.nav` (+ `.selected`) for sidebar items: `StackPanel{PathIcon, TextBlock}`.
`Button.group-header` for collapsible sidebar section headers (content: `TextBlock.section`).

### Chips and segmented control
```xml
<ToggleButton Classes="chip" Content="Lossless"/>           <!-- multi-select filter -->
<ToggleButton Classes="chip danger" Content="Failed"/>      <!-- error tint when checked -->
<Border Classes="segmented">                                <!-- single-select -->
  <StackPanel Orientation="Horizontal" Spacing="2">
    <RadioButton Classes="chip" GroupName="view" Content="List" IsChecked="True"/>
    <RadioButton Classes="chip" GroupName="view" Content="Grid"/>
  </StackPanel>
</Border>
```
A checked chip is accent-tinted. Inside `segmented`, the checked segment is neutral raised (`BrushBg4`).

### Surfaces (Border)
| Class | Use |
|---|---|
| `card` | Grouped content inside a page (Bg2, hairline border, RadiusLg, 12,8 padding) |
| `panel` | Docked region: side pane, inspector. No radius. Set the edge `BorderThickness` yourself |
| `panel-header` | 36px title row on a panel: `TextBlock.section` + close `Button.icon.small` |
| `toolbar` | The one action row under a page header (44px, bottom hairline) |
| `inset` | Sunken well: waveforms, logs, drop zones |
| `callout` (+ `info`, `warning`, `error`) | Calm tinted inline message inside a page: a suggestion ("You imported this before…"), a notice, a partial failure. Icon + text + at most two small buttons. Accent tint by default |
| `dialog` | Floating modal (RadiusXl, ShadowLg, 24 padding) |
| `divider` / `divider vertical` | 1px hairline, horizontal / vertical (set Height on vertical) |
| `badge` (+ `neutral`) | **Numeric counters only** (unread, queue). Wrap one TextBlock |
| `fact` (+ `warning`, `error`) | Small mono data badge: BPM, Camelot key, duration, format/bitrate, quality tier, "File missing". Wrap one TextBlock. Not for AI labels |

### Text (TextBlock)
| Class | Spec | Use |
|---|---|---|
| `display` | 28 SemiBold | Hero numbers / splash only |
| `h1` | 20 SemiBold | Page title (one per page) |
| `h2` | 16 SemiBold | Section or dialog title |
| `h3` | 14 SemiBold | Card / group title, empty-state title |
| `subtitle` | 12 Text1 | Line under a page title |
| `body` / `body-sm` | 13 / 12 | Default text / secondary text |
| `caption` | 11 Text2 | Metadata, field labels, helper text |
| `section` | 11 SemiBold, tracking 0.6, Text2 | Section label. **Write the text in caps** (`Text="QUEUE"`) |
| `mono` | FontMono 12 | Paths, hashes, timings, bitrates |
| `value` | 18 SemiBold | Numeric stat in a stat card |
| `muted` `accent` `success` `warning` `error` | colour modifiers | Combine: `Classes="caption warning"` |

### Status and data
```xml
<Ellipse Classes="status-dot success"/>                      <!-- .success .warning .error .info .accent; default grey -->
<ProgressBar Classes="meter" Value="{Binding Energy}" Maximum="1"/>   <!-- 4px bar; .thick = 6px -->
<ProgressBar Classes="meter warning" .../>                   <!-- .neutral .success .warning .error .info -->
```
Use bars, percentages or plain text for AI and analysis output (genre, mood, energy, confidence).
**Don't use coloured pill badges.** `Border.pill` is legacy.

### Numbers
**Decision: every user-facing number is written with the invariant culture**, because the UI copy
is English. A Dutch/Belgian Windows must still show "174.0 BPM", never "174,0 BPM" next to English
text. Numbers sit in the mono/tabular font (`mono`, `num`, `fact`) so columns don't jitter.

| Kind | C# (`Utils/NumFormat`) | XAML (`NumberConverter`) | Output |
|---|---|---|---|
| Count | `CountFormat.N(n)` / `CountFormat.Of(n, "track")` | `CountConverter` | `3,267` · `1 track` |
| BPM | `NumFormat.Bpm(bpm)` (`"—"` when missing) | `ConverterParameter=bpm` | `174.0` |
| BPM delta | `NumFormat.BpmDelta(d)` | `'{}{0:+0.0;-0.0;0.0} BPM'` | `+1.0 BPM` |
| Percent (0–1) | `NumFormat.Percent(f)` / `(f, 1)` | `pct` / `pct1` | `91%` · `91.0%` |
| Percent (0–100) | `NumFormat.PercentValue(v)` / `SignedPercent(v)` | `'{}{0:F0}%'` | `91%` · `+1.6%` |
| Seconds / ms | `NumFormat.Seconds(s)` / `Ms(ms)` | `seconds` | `22.1 s` · `3.2 ms` |
| Clock duration | `NumFormat.Duration(s)` | `duration` | `3:07` · `1:02:45` |
| dB / LUFS | `NumFormat.Db(db, signed: true)` / `Lufs(l)` | `'{}{0:+0.0;-0.0;0.0} dB'` | `+1.5 dB` · `-14.0 LUFS` |
| Frequency | `NumFormat.Khz(hz)` | | `44.1 kHz` |
| Size | `NumFormat.Size(bytes)` (top unit GB with 1 decimal; MB 1 decimal below 100; KB none) | `size` | `8.4 MB` · `2,400.5 GB` |
| Speed | `NumFormat.Speed(bytesPerSec)` | `speed` | `512 KB/s` · `1.2 MB/s` |
| Ratio / zoom | `NumFormat.Ratio(r)` | `ratio` | `1.0x` |
| Anything else | `NumFormat.Ui($"{x:F2} …")` | `'{}{0:F2} …'` | invariant; `P` formats render `91%` |

Rules:
- Never write `ToString("F1")`, `{x:0.0}`, `{x:P0}` or `StringFormat={}{0:F1}` without a culture.
  `NumberFormatGuardTests` fails the build on them in Views/ViewModels/Converters/Models/Utils.
  Integer formats (`F0`, `0`, `D2`) are fine. Logs, file names and serialization are not UI: pin
  them to `CultureInfo.InvariantCulture` (or add them to the guard's allowlist with a reason).
- Percent is written without a space: `91%`, `+1.6%`.
- **Input accepts both styles.** Parse typed numbers with `NumFormat.TryParse` ("174,5" and
  "174.5" both mean 174.5). Every `NumericUpDown` displays invariantly and turns a typed `,` into
  `.` (`Views/Avalonia/NumericInputLocale`). A TwoWay `TextBox` with `NumberConverter` parses
  back the same way.
- Converter parameters in XAML (`'1;0.4'`, widths, thresholds) are parsed with the invariant
  culture, never the OS one.

### Inputs
Un-classed `TextBox` and `ComboBox` are already 32px with RadiusMd, a Bg0 or Bg3 fill and an accent
focus border. `TextBox.search` adds a leading magnifier. `.small` gives 28px.

### Empty state
```xml
<StackPanel Classes="empty-state">
  <PathIcon Data="{StaticResource icon_music}"/>
  <TextBlock Classes="h3" Text="No tracks yet"/>
  <TextBlock Classes="caption" Text="Import a playlist or search Soulseek to get started."/>
  <Button Classes="primary" Content="Import"/>   <!-- optional, max one -->
</StackPanel>
```

### Tabs
`TabControl.tabs-underline` gives compact underline tabs (12px Medium, accent underline). The
context panel uses it.

### Icons
Use `PathIcon` with an `icon_*` key: menu, home, search, inbox, link, library, users, chart,
settings, target, waves, tune, play, pause, skip_previous, skip_next, expand, close, check, add,
download, bell, alert, info, brain, sync, music, chevron_down/right/left/up, more (…), shuffle,
heart, heart_outline, folder, folder_add, playlist_add, delete, edit, columns, filter, export, history,
wand, wrench, stop, open_external, mix, lock, undo, redo, resize, panel_bottom. There are more legacy
`*_regular` keys in App.axaml. Size icons with `IconSize*` (16 by default). The colour inherits from
`Foreground`. If an icon is missing, add a 24x24 single-path geometry to `Icons.axaml`. Don't fall
back to an emoji.

## Layout rules

- **Page shell:** page background is `BrushBg1` (the shell paints it, so pages don't need to).
  Outer padding is `PaddingPage` (24,16).
- **Page header:** the left side holds the title (`h1`) with a `subtitle` under it. The right side
  holds the primary actions: at most one `primary` and the rest `secondary`/`ghost`/`icon`.
  Header height is about 56px.
- **One toolbar row max**, below the header: `Border.toolbar`. Search on the left (`TextBox.search`),
  filter chips next to it, view toggles (`segmented`) on the right. If it doesn't fit, move the
  rarely used items into a "More" `DropDownButton`. Don't add a second row.
- **Filters are chips** (`ToggleButton.chip`). Don't use checkbox rows or coloured buttons for them.
- **Spacing:** only use 4/8/12/16/24/32. Use 8 inside a group, 16 between groups and 24 between
  page sections. Card padding is 12–16.
- **Panels:** a side pane is `Border.panel` with a `panel-header`. Dividers are `BrushBorder0`
  hairlines, never solid #333 lines.
- **Density:** body text 13, metadata 11–12. Nothing below 10px except numeric badges.
- **Lists:** row hover is `BrushHover` and the selected row is `BrushSelected`. Use the theme
  `ListBoxItem` states and don't reinvent them.

## Do / Don't

| Do | Don't |
|---|---|
| Reference `{StaticResource Brush*}` or a class | Write raw hex (`#4EC9B0`, `#252526`, `White`) in Views |
| Use one `primary` button per area | Put several accent-filled buttons side by side |
| Use `PathIcon` + `icon_*` | Use emoji as icons (🔍 📥 ⚙️ 🔔 ▶) in chrome, nav or buttons |
| Use sentence case for labels and buttons ("Add to library") | Shout in ALL CAPS. Caps are only for `TextBlock.section` |
| Use semantic colour only for status | Colour-code categories with rainbow fills, neon glows or `DropShadowEffect` |
| Show meters/bars for scores | Show "vibe" pill badges for AI output |
| Use `RadiusMd` controls and `RadiusLg` cards | Mix 3/5/10/14/20 radii |
| Add a token when a colour is genuinely new | Tint a page's surfaces purple/green to "theme" it |
| Put shadows on floating surfaces only | Give cards box-shadows or glows |
| Override a class locally only for layout (Margin, Width) | Redefine `Button.primary` per page. Fix it in Controls.axaml |

## Pitfalls (Avalonia)

- Fluent paints button states on `/template/ ContentPresenter#PART_ContentPresenter`. Any
  `:pointerover` or `:pressed` Background you set on the control itself has no effect. Copy the
  pattern from `Controls.axaml`.
- Inside `Themes/*.axaml`, use `DynamicResource` for tokens, because they live in a sibling file.
  Views can use either `StaticResource` or `DynamicResource`.
- Never put `((Type)Prop)` casts in binding paths. They fail silently. Use
  `$parent[Window].DataContext.X` instead.
- Missing resource keys fail silently. After editing, check that every `{StaticResource X}` resolves.
- Don't touch window transparency (Mica/AcrylicBlur + FullScreen kills the process).
