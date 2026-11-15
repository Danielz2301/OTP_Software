---
name: wpf-industrial-ui-ux
description: Use this skill when designing, styling, and refining industrial dark-theme UI/UX, high-contrast factory wallboards, kiosk displays, responsive layouts, custom ControlTemplates, and accessible operator workflows in OTPSoftware.App.
---

# Industrial UI/UX & Factory Wallboard Design System for WPF

This skill establishes the user experience (UX), visual design hierarchy, styling conventions, and accessibility rules for **OTP Log Monitor & Optical Inspection System** (`OTPSoftware.App`).

---

## 1. Industrial Factory Floor UX Principles

Industrial manufacturing applications run 24/7 on factory floors with noisy environments, shifting ambient lighting, and operators wearing safety gear. Every view must satisfy three core rules:

1. **The 5-Second Glanceability Rule**: An operator or shift supervisor walking past an overhead 55" TV monitor from 5 meters away must immediately perceive overall line health (FPY, total throughput, active NG alarms, and camera link health) within 5 seconds.
2. **Dual-Channel Status Indicators**: Never convey operational status using color alone. Always combine color with unambiguous text labels (`OK`, `NG`, `OFFLINE`) or distinctive iconography to protect color-blind operators and low-light environments.
3. **Ergonomic Low-Glare Dark Palette**: Continuous monitoring under fluorescent factory lights causes eye strain. Use deep slate/navy surfaces (`#0F172A`, `#1E293B`) with high-contrast foreground text rather than stark pure black (`#000000`) or blinding white.

---

## 2. Standard Industrial Color Palette & Semantic Tokens

Define and reuse unified static brushes in `App.xaml` to ensure visual consistency:

```xml
<ResourceDictionary>
    <!-- Background & Surface Layers -->
    <SolidColorBrush x:Key="BgDarkPrimary" Color="#0F172A" />    <!-- Main Window Background -->
    <SolidColorBrush x:Key="BgDarkSurface" Color="#1E293B" />    <!-- Card / Panel Surface -->
    <SolidColorBrush x:Key="BgDarkElevated" Color="#334155" />   <!-- Input / Hover / Border -->
    <SolidColorBrush x:Key="BorderMuted" Color="#475569" />      <!-- Gridlines & Dividers -->

    <!-- Semantic Operational Tokens -->
    <SolidColorBrush x:Key="StatusOkBrush" Color="#10B981" />     <!-- High-yield / Pass Green -->
    <SolidColorBrush x:Key="StatusWarningBrush" Color="#F59E0B" /><!-- Caution / Warning Amber -->
    <SolidColorBrush x:Key="StatusCriticalBrush" Color="#EF4444"/><!-- Defect / Alarm Red -->
    <SolidColorBrush x:Key="StatusInfoBrush" Color="#3B82F6" />   <!-- Telemetry / Station Blue -->

    <!-- Text Typography Hierarchy -->
    <SolidColorBrush x:Key="TextPrimary" Color="#F8FAFC" />      <!-- 100% White/Slate foreground -->
    <SolidColorBrush x:Key="TextSecondary" Color="#94A3B8" />    <!-- 70% Subtitles & labels -->
    <SolidColorBrush x:Key="TextMuted" Color="#64748B" />        <!-- 50% Timestamp & metadata -->
</ResourceDictionary>
```

> [!TIP]
> Always mark brushes with `.Freeze()` or declare them in XAML without animated bindings so they bypass dependency property tracking overhead.

---

## 3. Overhead Kiosk & TV Display Mode (`F11`)

For ceiling-mounted overhead TV displays, the UI must eliminate all desktop chrome and maximize metric card legibility:

### A. Window State Management
```csharp
public void ToggleKioskMode()
{
    if (WindowState == WindowState.Maximized && WindowStyle == WindowStyle.None)
    {
        // Exit Kiosk Mode
        WindowStyle = WindowStyle.SingleBorderWindow;
        WindowState = WindowState.Normal;
        Topmost = false;
    }
    else
    {
        // Enter Kiosk Mode
        WindowStyle = WindowStyle.None;
        WindowState = WindowState.Maximized;
        Topmost = true; // Prevents taskbar popups
    }
}
```

### B. Proportional Scaling & Legibility
- **Viewbox Scaling**: Wrap high-priority KPI counters (Total Units, FPY %, Consecutive NG Count) inside `<Viewbox>` controls so they automatically scale up on 4K / 8K TV panels without hardcoding pixel sizes.
- **Minimum Font Ceiling**: No label on the main Wallboard should be smaller than `13px`; metric digits should scale dynamically between `32px` and `72px`.

---

## 4. Custom Control Styling & Layout Best Practices

### A. Flat Industrial DataGrid Styling
Eliminate default gradient borders, enable row striping, and preserve column header pinning:

```xml
<Style TargetType="DataGridRow">
    <Setter Property="Height" Value="32" />
    <Setter Property="Background" Value="Transparent" />
    <Setter Property="Foreground" Value="{StaticResource TextPrimary}" />
    <Style.Triggers>
        <Trigger Property="ItemsControl.AlternationIndex" Value="1">
            <Setter Property="Background" Value="#172234" />
        </Trigger>
        <Trigger Property="IsSelected" Value="True">
            <Setter Property="Background" Value="#1E3A8A" />
            <Setter Property="Foreground" Value="#FFFFFF" />
        </Trigger>
    </Style.Triggers>
</Style>
```

### B. Pill Badges for Machine & Status Identifiers
Use rounded pill containers (`CornerRadius="12"`) for machine tags (`MMAPI120`, `MMAPI110`) and camera status indicators (`A1`, `A2`, `B1`, `B2`). Keep padding tight (`Padding="8,2"`) to preserve horizontal screen real estate.

### C. Touch-Friendly Targets & Factory Gloves
Buttons, filter checkboxes, and camera selector tabs must have a minimum hit target of **40 × 40 px** (or `MinHeight="36"` with ample padding) to ensure reliable operation on resistive factory touchscreens.

---

## 5. Keyboard Navigation & Operator Shortcuts

Factory technicians prefer quick physical keypresses over mouse precision:

| Shortcut | Action & Scope | Expected Operator Behavior |
| :--- | :--- | :--- |
| **`F11`** | **Toggle Kiosk Mode** | Switches between windowed desktop and fullscreen TV Wallboard. |
| **`ESC`** | **Dismiss / Exit** | Closes `MesDetailWindow` or exits Kiosk Mode. |
| **`Enter` / Double-Click** | **View Detail** | Opens MES parameter breakdown for the currently focused row. |
| **`Ctrl + F`** | **Search / Filter Focus** | Jumps keyboard focus directly into the Serial/Panel filter box. |
| **`Space`** | **Mute / Acknowledge** | Silences the continuous NG streak sound alarm without pausing log polling. |
| **`1` / `2`** | **Camera Channel Toggle** | Toggles between Left (`A1`/`B1`) and Right (`A2`/`B2`) camera viewports in Detail Viewer. |

---

## 6. XAML Performance & Anti-Leak Checklist

- [ ] **No Deep Visual Nesting**: Avoid stacking `Border` inside `Grid` inside `Border` inside `StackPanel`. Flatten layouts to reduce WPF layout computation passes.
- [ ] **Unbind Events on Unload**: Windows and custom controls subscribing to static events or message brokers must unsubscribe in `OnClosed` or use `WeakEventManager` to prevent memory leaks during 24/7 runtimes.
- [ ] **TextTrimming on All Variable Fields**: Every serial number, station name, and spec string must specify `TextTrimming="CharacterEllipsis"` and `ToolTip="{Binding Path=...}"` to prevent text truncation across translations.
- [ ] **Async Loading Indicators**: For heavy camera image loads, display a non-blocking skeleton loader or subtle spinner rather than freezing the UI thread.
