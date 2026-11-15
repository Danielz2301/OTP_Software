---
name: wpf-mvvm-best-practices
description: Use this skill when designing, reviewing, or modifying WPF UI components, XAML bindings, MVVM ViewModels, cross-thread image rendering, DrawingVisual graphics, and localization in OTPSoftware.App.
---

# WPF & MVVM Best Practices for Industrial Inspection UIs

This skill defines the development guidelines and architectural rules for the `OTPSoftware.App` presentation layer.

## 1. Strict MVVM Separation

- **Code-Behind Policy**: View code-behind files (`*.xaml.cs`) must strictly contain UI-only lifecycle code (e.g., event hookups, visual element measuring, direct Canvas rendering dispatch). All business logic, filtering, formatting, and commands must reside in ViewModels.
- **RelayCommand & AsyncRelayCommand**: Use strongly typed commands for button clicks and interactions. Ensure commands disable appropriately via `CanExecute`.
- **ObservableCollection Safety**: When updating collections bound to the UI from background services, either marshal mutations via `App.Current.Dispatcher.InvokeAsync` or use thread-safe dispatch collections.

## 2. Zero-Freeze Industrial Image Pipeline

- **Stream-Direct Downsampling**: Never decode camera images at full resolution unless 1:1 Raw Mode is explicitly toggled. Always set `DecodePixelWidth` to bound memory:
  ```csharp
  var bitmap = new BitmapImage();
  bitmap.BeginInit();
  bitmap.CacheOption = BitmapCacheOption.OnLoad;
  bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
  bitmap.DecodePixelWidth = targetWidth; // e.g. 360 for Spotlight, 1600 for Detail Viewer
  bitmap.StreamSource = fileStream;
  bitmap.EndInit();
  bitmap.Freeze(); // Crucial for cross-thread handoff
  ```
- **Cross-Thread Handoff (`bitmap.Freeze()`)**: Always call `Freeze()` on the background thread where the bitmap was loaded before passing it to the ViewModel or UI thread. Frozen `BitmapSource` instances are immutable and can be safely shared across threads without `Dispatcher` marshaling.
- **Cancellation Safety**: Image loading triggered by rapid user selection changes must support `CancellationToken` to abort ongoing SMB stream reads immediately.

## 3. High-Performance Canvas & Virtualization

- **DrawingVisual for Real-Time Charts**: For high-frequency Wallboard charts (`ChartCanvasRenderer`), do not use heavy UIElement shapes (`Line`, `Rectangle`). Render directly with lightweight `DrawingVisual` and `DrawingContext` to maintain 60 FPS.
- **UI Virtualization**: Large record lists in DataGrids and ListViews must enable UI virtualization:
  ```xml
  VirtualizingPanel.IsVirtualizing="True"
  VirtualizingPanel.VirtualizationMode="Recycling"
  ScrollViewer.CanContentScroll="True"
  ```

## 4. Trilingual Localization (`DynamicResource`)

- **DynamicResource Binding**: All localized text must bind using `DynamicResource` referencing string keys from `Strings.{en,vi,zh}.xaml`:
  ```xml
  <TextBlock Text="{DynamicResource Str_Status_Ok}" />
  ```
- **Text Trimming & ToolTips**: Different languages have varying string lengths (Vietnamese and English often exceed Chinese by 30-50%). Always provide `TextTrimming="CharacterEllipsis"` and full-text `ToolTip` fallbacks to avoid layout breakage.
- **Constant Acronyms**: Standard industrial acronyms (`OTP`, `MES`, `PMNT`, `FPY`, `XYLV`, `OK`, `NG`) remain unchanged across all language dictionaries.
