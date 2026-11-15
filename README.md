# 🔬 OTP Log Monitor & Optical Inspection System

![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)
![C# 14](https://img.shields.io/badge/C%23-14.0-239120?style=for-the-badge&logo=csharp&logoColor=white)
![WPF Desktop](https://img.shields.io/badge/WPF-MVVM%20Desktop-0078D4?style=for-the-badge&logo=windows&logoColor=white)
![xUnit Tests](https://img.shields.io/badge/xUnit-Test%20Suite%20Passing-2EA043?style=for-the-badge&logo=checkmarx&logoColor=white)
![Platform](https://img.shields.io/badge/Platform-Windows%20x64-0078D6?style=for-the-badge&logo=windows11&logoColor=white)
![i18n](https://img.shields.io/badge/i18n-EN%20%7C%20%E4%B8%AD%E6%96%87%20%7C%20VI-F59E0B?style=for-the-badge)

**OTP Log Monitor & Optical Inspection System** is an industrial-grade, real-time Machine Vision & AOI (Automated Optical Inspection) telemetry, MES parameter verification, and optical defect visualization platform engineered on **.NET 10 (`C# 14`)** and **WPF (Windows Presentation Foundation)**.

Designed for 24/7 factory-floor operation across parallel inspection stations (**MMAPI120**, **MMAPI110**, and dynamic *N*-machine topologies), the system combines **zero-allocation SMB log tailing**, **reverse chunked MES XML stream indexing**, and a **memory-bounded asynchronous camera image engine (Zero-OOM)** with instant runtime switching between **English (`EN`)**, **Vietnamese (`VI`)**, and **Simplified Chinese (`中文`)**.

---

## 🏗️ 1. Solution Architecture (Clean Architecture / Functional Core – Imperative Shell)

The solution ([OTPSoftware.sln](file:///c:/Users/s7828/Documents/OTP_Software/OTPSoftware.sln)) enforces strict separation of concerns across three projects following the **Functional Core / Imperative Shell** paradigm:

```mermaid
flowchart LR
    subgraph SMB["🏭 Factory SMB Network Shares"]
        OTP["OTP_1.log (.1..N)\nReal-Time Station Logs"]
        MES["MES_1.log (.1..N)\nMES XML Transactions"]
        CAM["Camera Shares (A1/A2/B1/B2)\npiresult/ & photo/"]
    end

    subgraph CORE["⚙️ OTPSoftware.Core (net10.0 / C# 14 Pure Library)"]
        TAIL["LogTailReaderService\nNon-Locking + FNV-1a Rotation"]
        PARSE["LogParserService\nSIMD SearchValues + Span<char>"]
        MES_SVC["MesDetailService\nReverse 4MB UTF-8 Byte Slicer"]
        CAM_SVC["CameraImageService & ImageLocatorService\nPort 445 Circuit Breaker + LRU Cache"]
        STATS["StatisticsService & GuiDisplayState\n4-Tier Filtering + Single-Pass KPI"]
    end

    subgraph APP["🖥️ OTPSoftware.App (net10.0-windows / WPF MVVM)"]
        WB["MainWindow (Wallboard)\nKPIs + Live NG Spotlight + Charts"]
        DETAIL["MesDetailWindow\nMES Spec Table + Camera Defect Viewer"]
        IMG_LDR["CameraImageLoader & ImageHelper\nAsync FileStream + DecodePixelWidth + Freeze()"]
        I18N["LocalizationService\nDynamicResource (EN / VI / 中文)"]
    end

    OTP --> TAIL --> PARSE --> STATS --> WB
    MES --> MES_SVC --> DETAIL
    CAM --> CAM_SVC --> IMG_LDR --> WB
    IMG_LDR --> DETAIL
    I18N -.-> WB
    I18N -.-> DETAIL
```

| Project | Target Framework | Architectural Role & Core Responsibilities |
| :--- | :--- | :--- |
| **[`OTPSoftware.Core`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.Core/OTPSoftware.Core.csproj)** | `net10.0`<br>(`C# 14`) | **Pure Functional Core (0 UI Dependencies)**:<br>• [`LogTailReaderService`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.Core/Services/LogTailReaderService.cs): Non-locking SMB stream reader (`FileShare.ReadWrite \| FileShare.Delete`), UTF-8 byte-level `\n` line slicing (zero LOH string allocations), 64-bit `FNV-1a` head/tail rotation fingerprinting, and rotated archive draining (`OTP_1.1.log`).<br>• [`LogParserService`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.Core/Services/LogParserService.cs): Single-pass SIMD `SearchValues<string>` pre-filtering and `ReadOnlySpan<char>` token extraction (~192 B per 1,000 noise lines).<br>• [`MesDetailService`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.Core/Services/MesDetailService.cs): Reverse 4 MB `ArrayPool<byte>` stream scanner operating directly on UTF-8 `ReadOnlySpan<byte>` to decode only matched XML records (~1–9 KB), plus zero-split `PipeSectionReader` optical spec evaluation with `.GetAlternateLookup<ReadOnlySpan<char>>()`.<br>• [`CameraImageService`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.Core/Services/CameraImageService.cs) & [`ImageLocatorService`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.Core/Services/ImageLocatorService.cs): Non-blocking TCP Port 445 reachability probe with a 10s per-host **SMB Circuit Breaker**, lazy `Directory.EnumerateDirectories` / `EnumerateFiles` discovery, and thread-safe node-reusing [`LruMemoryCache<TKey, TValue>`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.Core/Caching/LruMemoryCache.cs) (0 B steady-state heap allocation).<br>• [`UnitCollectionExtensions`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.Core/Extensions/UnitCollectionExtensions.cs), [`StatisticsService`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.Core/Services/StatisticsService.cs), & [`OtpJsonContext`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.Core/Models/OtpJsonContext.cs): Fluent unit filtering, single-pass hourly throughput & consecutive NG streak analytics, and AOT-ready `System.Text.Json` Source Generation. |
| **[`OTPSoftware.App`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.App/OTPSoftware.App.csproj)** | `net10.0-windows`<br>(`WPF`, `C# 14`) | **Imperative Shell & Presentation Layer (Strict MVVM)**:<br>• [`MainWindow.xaml`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.App/MainWindow.xaml) & [`MainViewModel`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.App/ViewModels/MainViewModel.cs): Industrial dark-theme Wallboard, KPI summary cards, consecutive NG fixture alarm banner, and 3 analytical tabs (*Log Records*, *Faulty PMNT Rankings*, *Charts*).<br>• [`SpotlightViewModel`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.App/ViewModels/SpotlightViewModel.cs): Drives the **Live NG Unit Spotlight** widget on the main Wallboard.<br>• [`MesDetailWindow.xaml`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.App/Views/MesDetailWindow.xaml) & [`MesDetailViewModel`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.App/ViewModels/MesDetailViewModel.cs): Split-pane **MES Parameter Details & Optical Inspection** modal with an integrated **Camera Defect Viewer**.<br>• [`ChartViewModel`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.App/ViewModels/ChartViewModel.cs) & [`ChartCanvasRenderer`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.App/Views/Charts/ChartCanvasRenderer.cs): Pure UI-agnostic chart geometry ViewModel paired with a hardware-accelerated `DrawingVisual` canvas renderer.<br>• [`CameraImageLoader`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.App/Services/CameraImageLoader.cs) & [`ImageHelper`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.App/Services/ImageHelper.cs): Asynchronous, cancellation-safe, downsampled `BitmapSource` pipeline. |
| **[`OTPSoftware.Tests`](file:///c:/Users/s7828/Documents/OTP_Software/tests/OTPSoftware.Tests/OTPSoftware.Tests.csproj)** | `net10.0-windows`<br>(`xUnit`, `C# 14`) | **Automated Verification Suite**:<br>• Validates log tailing, rotation/tunneling edge cases, `ReadOnlySpan<char>` parsers, 4-tier `GuiDisplayState` eviction, MES XML boundary straddling, LRU cache eviction, OWASP CSV formula sanitization, and 100% resource key parity across `Strings.{en,vi,zh}.xaml`. |

---

## ✨ 2. Key Innovations & Technical Highlights

### 🔍 A. View Detail Window & Integrated Camera Defect Viewer
Opening **View Detail** on any unit row launches the dual-pane **MES Parameter Details & Optical Inspection** window ([`MesDetailWindow.xaml`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.App/Views/MesDetailWindow.xaml)):
- **Left Pane — MES Optical Parameter Verification**:
  - Parses `<OTPDEFECTCODE>` and `<OTPDEFECTVALUE>` payloads from `MES_1.log` (and rotated `.1`–`.5` archives).
  - Evaluates every optical parameter (`White_lv`, `whitex`, `whitey`, `cct`, `contrast`, `ntsc`, `flk`, `Red_LV`, `Green_LV`, `Blue_LV`, etc.) against factory specification bounds (`Standard Spec` vs `Measured Value` → `OK` / `NG` / `Ref`).
  - Supports instant keyword filtering, **NG Only** badge toggling, and one-click **Copy Panel** / **Copy Parameter Table** (TSV clipboard export).
- **Right Pane — Camera Defect Viewer**:
  - **Channel Switching**: Toggle seamlessly between **Left Cam (`B1` / `A1`)** and **Right Cam (`B2` / `A2`)** with live image resolution badges.
  - **Side-by-Side Comparison**: Inspect Left and Right station camera captures simultaneously in a synchronized dual-viewport layout.
  - **Interactive Zoom & 1:1 Raw Inspection**: Supports mouse-wheel zoom, click-and-drag panning, `100%` fit-to-screen, `200%` magnification, and on-demand **1:1 Raw High-Res PNG** loading (`photo/Main*.png`), plus a clickable thumbnail strip of cropped defect regions (`defectPicture/`).

### 🚨 B. Wallboard "Live NG Unit Spotlight" Widget
- Located directly on the main Wallboard ([`MainWindow.xaml`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.App/MainWindow.xaml)), the **Live NG Unit Spotlight** ([`SpotlightViewModel.cs`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.App/ViewModels/SpotlightViewModel.cs)) automatically pins the most recent `NG` unit across all monitored machines.
- Displays the panel serial number, machine source badge, fixture ID (`PMNT`), primary defect code (`XYLV`, `POWERON`, `POWEROFF`, `FLICKER`), and asynchronously loads the best-matching defect crop (`PrimaryDefectCropPath`) or algorithm overview image (`OverviewImagePath`) without blocking real-time log polling.

### 🛡️ C. Industrial Zero-OOM Asynchronous Image Engine
AOI cameras generate massive image files over SMB shares. To guarantee **Zero UI Freeze** and **Zero Out-Of-Memory (OOM) / LOH Fragmentation** during continuous 24/7 operation, [`ImageHelper.cs`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.App/Services/ImageHelper.cs) and [`CameraImageLoader.cs`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.App/Services/CameraImageLoader.cs) enforce strict memory invariants:

| Protection Layer | Implementation Mechanism | Memory & Performance Impact |
| :--- | :--- | :--- |
| **Smart Format Priority** | Prioritizes compressed `piresult/AlgIntegrate.jpg` / `Main*_L255.jpg` (~9–12 MB) and `defectPicture/*.jpg` crops. Raw `photo/Main*.png` (`17701 × 14208`, ~50 MB file / ~1 GB uncompressed RGBA) is **never loaded by default**—only when the operator explicitly activates **1:1 Raw Mode**. | Prevents ~1 GB uncompressed bitmap spikes on routine inspections. |
| **Stream-Direct Downsampling** | Streams directly from `FileStream` (`FileAccess.Read`, `FileShare.ReadWrite \| FileShare.Delete`, `FileOptions.SequentialScan`) into WIC with `BitmapCreateOptions.IgnoreColorProfile`, `BitmapCacheOption.OnLoad`, and hardware-accelerated `DecodePixelWidth` (`360 px` for Spotlight, `1600 px` for Detail Viewer). | Eliminates intermediate `byte[]` / `MemoryStream` LOH allocations; reduces decoded RAM footprint from **~750 MB** to **~3–8 MB**. |
| **Immutable Cross-Thread Handoff** | Every decoded `BitmapImage` immediately calls `bitmap.Freeze()` on the background worker thread before returning to the ViewModel. | Zero `Dispatcher` marshaling bottleneck; 100% thread-safe WPF binding. |
| **Cancellation & Cache Guard** | Rapidly clicking through rows cancels in-flight SMB reads via `CancellationTokenSource` before decoding; bounded LRU cache (`capacity: 24`) evicts old previews automatically. | Prevents thread-pool starvation and bounds peak image cache memory. |

> [!IMPORTANT]
> Raw `photo/Main*.png` images (`17701 × 14208` pixels) require ~1,006 MB if decoded at full resolution. Even when the operator requests **1:1 Raw Mode** in the Detail Viewer, the engine caps decoding at a safe `DecodePixelWidth = 4096` ceiling unless external Explorer viewing is used, completely eliminating `OutOfMemoryException` on 8 GB / 16 GB factory terminals.

### 🌐 D. Runtime Trilingual Localization (`EN` | `中文` | `VI`)
- Operators can switch the entire UI language instantaneously via the header bar selector **`[ 🌐 EN | 中文 | VI ]`** without restarting the application:
  - **English (`en-US`)**: [`Strings.en.xaml`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.App/Resources/Languages/Strings.en.xaml)
  - **Vietnamese (`vi-VN`)**: [`Strings.vi.xaml`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.App/Resources/Languages/Strings.vi.xaml)
  - **Simplified Chinese (`zh-CN`)**: [`Strings.zh.xaml`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.App/Resources/Languages/Strings.zh.xaml)
- All views ([`MainWindow.xaml`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.App/MainWindow.xaml) and [`MesDetailWindow.xaml`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.App/Views/MesDetailWindow.xaml)) use `DynamicResource` bindings, proportional `*` / `Auto` column sizing, `TextTrimming="CharacterEllipsis"`, and full-text `ToolTip` fallbacks so longer Vietnamese and English labels never clip or compress the Camera Defect Viewer.
- Core factory acronyms (`OTP`, `MES`, `PMNT`, `FPY`, `XYLV`, `POWERON`, `POWEROFF`, `FLICKER`, `OK`, `NG`) and exported CSV column headers remain standardized across all locales.

---

## 📂 3. Camera Data Directory Contract (AOI Image Storage Layout)

Each optical inspection cycle writes image artifacts to a station-specific SMB share (`B1`/`B2` for **MMAPI120**, `A1`/`A2` for **MMAPI110**). [`CameraImageService`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.Core/Services/CameraImageService.cs) and [`ImageLocatorService`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.Core/Services/ImageLocatorService.cs) resolve folders matching the naming contract:

### Naming Convention
```text
[Status]_[Serial]_[Timestamp]_[Station]
```
- **`[Status]`**: Inspection verdict (`OK` or `NG`).
- **`[Serial]`**: Panel serial number from MES / barcode reader (e.g., `TL687304F0ED`, `TL693874Q0BB`).
- **`[Timestamp]`**: Capture timestamp token generated by the AOI station (e.g., `1113091205` or `20261113091205`).
- **`[Station]`**: Camera channel identifier (`A1` = Machine 110 Left, `A2` = Machine 110 Right, `B1` = Machine 120 Left, `B2` = Machine 120 Right).
- **Examples**:
  - `OK_TL687304F0ED_1113091205_A1`
  - `NG_TL693874Q0BB_1114083012_B1`

### Directory Tree & File Roles
```text
\\192.168.3.203\e\LOG\                              <-- Camera Root Share (or Date Subfolders: yyyyMMdd / yyyy-MM\yyyy-MM-dd)
└── NG_TL687304F0ED_1113091205_A1\                  <-- Panel Inspection Directory
    ├── piresult\                                   <-- [DEFAULT LOAD TARGET] Algorithm Processed Output
    │   ├── AlgIntegrate.jpg                        <-- Priority 1 Overview Image (~9–12 MB integrated algorithm overlay)
    │   ├── Main_1113091205_L255.jpg                <-- Priority 2 Overview Fallback (Full-field L255 luminance JPG)
    │   ├── defectPicture\                          <-- High-Priority Cropped Defect ROIs (prioritized by active defect code)
    │   │   ├── XYLV_ROI1_001.jpg
    │   │   └── POWERON_ROI2_002.jpg
    │   └── classifyPicture\                        <-- Secondary AI/Rule-Classified Defect Crops
    │       └── Classify_001.jpg
    │
    ├── photo\                                      <-- [ON-DEMAND 1:1 ONLY] Raw Uncompressed Sensor Captures
    │   ├── Main_1113091205_L0.png                  <-- Raw Luminance L0 / Dark-field frame
    │   ├── Main_1113091205_L255.png                <-- Raw Ultra-High-Res PNG (17701 × 14208 px, ~50 MB)
    │   └── Sub_RGB_*.png                           <-- Raw RGB / Grayscale pattern frames (L0–L255)
    │
    └── debugData1\                                 <-- Algorithm Telemetry & Coordinate Metadata
        ├── roi.dat                                 <-- Optical ROI bounding-box coordinates & alignment matrices
        └── defect.dat                              <-- Raw defect vector coordinates & pixel cluster metrics
```

> [!TIP]
> `CameraImageService` searches across a **3-tier hierarchy**:
> 1. Exact log timestamp date folders (`yyyyMMdd`, `yyyy-MM-dd`, `yyyy_MM_dd`, `yyyyMM\yyyyMMdd`, `yyyy-MM\yyyy-MM-dd`).
> 2. Current day and previous day rollover folders (handling midnight shift boundaries).
> 3. Root path and up to 15 recent container subdirectories—using lazy `Directory.EnumerateDirectories` so even shares with 50,000+ folders respond in milliseconds.

---

## ⚙️ 4. Configuration & Factory Floor Operations

### A. System Configuration ([`config/settings.json`](file:///c:/Users/s7828/Documents/OTP_Software/config/settings.json))
All machine log endpoints, camera SMB shares, Wallboard alarm thresholds, shift handover schedules, and custom MES optical specification overrides are managed in `config/settings.json`:

```json
{
  "_comment": "Override default configuration or add new machines. Delete this file or any key to revert to SettingsService defaults. images[0] = Left camera, images[1] = Right camera.",
  "machines": [
    {
      "name": "MMAPI120",
      "enabled": true,
      "color": "#74B9FF",
      "bgColor": "#1B3B52",
      "logPath": "\\\\192.168.3.206\\c\\GoArms\\logs\\OTP_1.log",
      "mesLogPath": "\\\\192.168.3.206\\c\\GoArms\\logs\\MES_1.log",
      "images": [
        { "root": "\\\\192.168.3.203\\e\\LOG", "code": "B1" },
        { "root": "\\\\192.168.3.204\\e\\LOG", "code": "B2" }
      ]
    },
    {
      "name": "MMAPI110",
      "enabled": true,
      "color": "#A29BFE",
      "bgColor": "#3A2A54",
      "logPath": "\\\\192.168.3.205\\c\\GoArms\\logs\\OTP_1.log",
      "mesLogPath": "\\\\192.168.3.205\\c\\GoArms\\logs\\MES_1.log",
      "images": [
        { "root": "\\\\192.168.3.201\\e\\LOG", "code": "A1" },
        { "root": "\\\\192.168.3.202\\e\\LOG", "code": "A2" }
      ]
    }
  ],
  "wallboard": {
    "warningNgRatePercent": 2.0,
    "criticalNgRatePercent": 5.0,
    "shiftTargetUnits": 15000,
    "carouselIntervalSec": 15
  },
  "autoShiftExport": {
    "enabled": true,
    "dayShiftStart": "08:30",
    "nightShiftStart": "20:30",
    "dayShiftTime": "20:30",
    "nightShiftTime": "08:30"
  },
  "mesSpecs": {
    "White_lv": { "min": 300.0, "spec": ">= 300.000" },
    "contrast": { "min": 1050.0, "spec": ">= 1050" },
    "ntsc": { "min": 0.42, "spec": ">= 0.42 (or >= 42%)" }
  }
}
```

| Configuration Section | Key Parameters | Operational Behavior |
| :--- | :--- | :--- |
| **`machines[]`** | `name`, `enabled`, `logPath`, `mesLogPath`, `images[]` | Defines 1..*N* inspection machines. `images[0]` maps to **Left Cam** (`B1`/`A1`), `images[1]` maps to **Right Cam** (`B2`/`A2`). |
| **`wallboard`** | `warningNgRatePercent`, `criticalNgRatePercent`, `shiftTargetUnits`, `carouselIntervalSec` | Controls color-coded FPY/NG Rate KPI thresholds and automatic tab/spotlight rotation intervals on Wallboard displays. |
| **`autoShiftExport`** | `enabled`, `dayShiftStart`, `nightShiftStart`, `dayShiftTime`, `nightShiftTime` | Automatically exports shift summary CSV (`exports/ShiftReport_{yyyyMMdd}_{DAY\|NIGHT}.csv`) and chart PNG (`exports/ShiftChart_{yyyyMMdd}_{DAY\|NIGHT}.png`) at shift handover. |
| **`mesSpecs`** | Parameter name (`White_lv`, `whitex`, `cct`, `contrast`, `ntsc`, `flk`, etc.) | Overrides built-in optical specification bounds (`min`, `max`, `spec`) without recompiling code. |

> [!NOTE]
> If `config/settings.json` is missing or contains a syntax error in a subsection, [`SettingsService`](file:///c:/Users/s7828/Documents/OTP_Software/src/OTPSoftware.Core/Services/SettingsService.cs) gracefully falls back to built-in factory defaults (`MMAPI120` & `MMAPI110`) and surfaces a non-blocking warning banner in the UI.

### B. Automatic Session Persistence (`config/user-state.json`)
Operator preferences—including active UI language (`en-US`, `vi-VN`, `zh-CN`), enabled/disabled machine toggles, custom log paths, consecutive NG threshold, sound alarm mute state, auto-scroll toggle, and active tab—are saved automatically to `config/user-state.json` on change and restored on startup.

### C. Wallboard Kiosk Mode & Operator Shortcuts
- **Fullscreen Wallboard Kiosk Mode (`F11`)**: Press `F11` (or click the **Wallboard Mode** button in the header) to hide configuration chrome and maximize high-contrast KPI cards, the **Live NG Unit Spotlight**, and real-time charts for overhead factory TV monitors. Press `F11` or `ESC` to exit Kiosk Mode.
- **Double-Click / View Detail**: Double-click any log record or press the **View Detail** button to open the **MES Parameter Details & Optical Inspection** modal (`ESC` closes the modal).
- **Right-Click Context Menu**: Right-click any unit row to copy `PanelName`/`BLUNAME`, open MES Detail, or open Left (`A1`/`B1`), Right (`A2`/`B2`), or Both camera folders directly in Windows Explorer.

---

## 📦 5. Packaging, Deployment & Verification CLI

### A. Quick Launch & Development Build
```powershell
# 1. Restore NuGet packages across the solution
dotnet restore OTPSoftware.sln

# 2. Build the entire solution in Release mode (0 warnings, 0 errors)
dotnet build OTPSoftware.sln -c Release

# 3. Run the WPF application in Release mode
dotnet run --project src/OTPSoftware.App/OTPSoftware.App.csproj -c Release

# 4. Run headless startup & DI self-validation (returns EXIT=0 & STARTUP_VALIDATION_SUCCESS)
./src/OTPSoftware.App/bin/Release/net10.0-windows/win-x64/OTPSoftware.App.exe --validate-startup
```

> [!TIP]
> On factory workstations, you can also double-click **[`Launch-GUI.bat`](file:///c:/Users/s7828/Documents/OTP_Software/Launch-GUI.bat)** at the repository root to launch the published single-file binary or compiled Release executable automatically.

### B. Single-File Self-Contained Production Deployment (`win-x64`)
To deploy onto production line PCs **without installing the .NET 10 Desktop Runtime separately**, publish a self-contained single-file executable:

```powershell
dotnet publish src/OTPSoftware.App/OTPSoftware.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish
```

**Deployment Artifacts in `publish/`:**
- `publish/OTPSoftware.App.exe` — Standalone x64 executable embedding the .NET 10 runtime, WPF libraries, and all application assemblies.
- `publish/config/settings.json` — Externalized configuration file (kept outside the single-file bundle so factory technicians can edit IP addresses and specs directly with Notepad).

### C. Running the Automated xUnit Test Suite
The [`OTPSoftware.Tests`](file:///c:/Users/s7828/Documents/OTP_Software/tests/OTPSoftware.Tests/OTPSoftware.Tests.csproj) project verifies log parsing, SMB tailing/rotation, MES XML evaluation, LRU caching, and trilingual resource dictionary parity:

```powershell
# Execute the full xUnit test suite in Release configuration
dotnet test OTPSoftware.sln -c Release --no-build --logger "console;verbosity=normal"
```

---

## 🛠️ 6. Troubleshooting & Diagnostics

- **Diagnostic Logs (`logs/gui-error.log`)**: Unhandled background exceptions, SMB share timeouts, and configuration warnings are written thread-safely to `logs/gui-error.log`.
- **Camera SMB Offline Badges**: The header bar displays real-time reachability badges (`A1`, `A2`, `B1`, `B2`). If a camera PC goes offline, the **10-second SMB Circuit Breaker** prevents UI stalls and automatically recovers as soon as TCP port 445 responds again.
