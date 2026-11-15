#Requires -Version 5.1
<#
.SYNOPSIS
    Industrial Release & Single-File Packaging Script for OTP Log Monitor & Optical Inspection System (.NET 10 / WPF / win-x64).

.DESCRIPTION
    Automates the complete release pipeline:
      Step 1: Clean legacy build artifacts (bin, obj, publish, dist)
      Step 2: Run xUnit automated regression test suite (fail-fast on error)
      Step 3: Publish Single-File Self-Contained win-x64 executable with compression
      Step 4: Stage distribution bundle (dist/OTP_Software/) with external config/settings.json & README.txt
      Step 5: Verify startup (--validate-startup) & compress into versioned ZIP archive with SHA-256 hash

.PARAMETER Version
    Semantic version string tagged onto the output ZIP archive (default: "1.0.0").

.PARAMETER SkipTests
    Optional switch to bypass unit test execution during emergency hotfix builds.

.EXAMPLE
    .\build-package.ps1 -Version "1.0.0"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$Version = "1.0.0",

    [Parameter(Mandatory = $false)]
    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ==============================================================================
# 0. CONSOLE LOGGING HELPERS & PATH RESOLUTION
# ==============================================================================
function Write-Step ([string]$StepCode, [string]$Message) {
    Write-Host "`n================================================================================" -ForegroundColor DarkCyan
    Write-Host " [$StepCode] $Message" -ForegroundColor Cyan
    Write-Host "================================================================================" -ForegroundColor DarkCyan
}

function Write-Ok ([string]$Message) {
    Write-Host "  [OK] $Message" -ForegroundColor Green
}

function Write-Info ([string]$Message) {
    Write-Host "  [..] $Message" -ForegroundColor Gray
}

function Write-Warn ([string]$Message) {
    Write-Host "  [!!] $Message" -ForegroundColor Yellow
}

function Write-Fail ([string]$Message) {
    Write-Host "  [ERROR] $Message" -ForegroundColor Red
}

$Stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$RepoRoot  = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Get-Location).Path
}

# Resolve Solution File (supports OTPSoftware.sln or OTP_Software.sln)
$SlnPath = Join-Path $RepoRoot "OTPSoftware.sln"
if (-not (Test-Path $SlnPath)) {
    $AltSln = Join-Path $RepoRoot "OTP_Software.sln"
    if (Test-Path $AltSln) {
        $SlnPath = $AltSln
    } else {
        Write-Fail "Solution file not found at '$SlnPath'."
        exit 1
    }
}

$AppProjPath    = Join-Path $RepoRoot "src\OTPSoftware.App\OTPSoftware.App.csproj"
$TestProjPath   = Join-Path $RepoRoot "tests\OTPSoftware.Tests\OTPSoftware.Tests.csproj"
$ConfigSrcPath  = Join-Path $RepoRoot "config\settings.json"
$PublishTempDir = Join-Path $RepoRoot "publish"
$DistRootDir    = Join-Path $RepoRoot "dist"
$StageDir       = Join-Path $DistRootDir "OTP_Software"
$StageConfigDir = Join-Path $StageDir "config"
$DateStamp      = Get-Date -Format "yyyyMMdd"
$ZipFileName    = "OTPSoftware_v${Version}_${DateStamp}_win-x64.zip"
$ZipFilePath    = Join-Path $DistRootDir $ZipFileName

try {
    Write-Host "`n################################################################################" -ForegroundColor Magenta
    Write-Host "  OTP LOG MONITOR & OPTICAL INSPECTION SYSTEM - RELEASE PACKAGER (v$Version)" -ForegroundColor White
    Write-Host "  Target: .NET 10.0 | C# 14 | WPF | Runtime: win-x64 (Single-File Self-Contained)" -ForegroundColor Gray
    Write-Host "################################################################################" -ForegroundColor Magenta

    # ==============================================================================
    # STEP 1: CLEAN OLD BUILD ARTIFACTS (bin, obj, publish, dist)
    # ==============================================================================
    Write-Step "STEP 1/5" "Cleaning previous build directories (bin, obj, publish, dist)..."

    $DirsToRemove = @(
        $PublishTempDir,
        $DistRootDir,
        (Join-Path $RepoRoot "src\OTPSoftware.Core\bin"),
        (Join-Path $RepoRoot "src\OTPSoftware.Core\obj"),
        (Join-Path $RepoRoot "src\OTPSoftware.App\bin"),
        (Join-Path $RepoRoot "src\OTPSoftware.App\obj"),
        (Join-Path $RepoRoot "tests\OTPSoftware.Tests\bin"),
        (Join-Path $RepoRoot "tests\OTPSoftware.Tests\obj")
    )

    foreach ($Dir in $DirsToRemove) {
        if (Test-Path $Dir) {
            Write-Info "Removing: $Dir"
            Remove-Item -Path $Dir -Recurse -Force -ErrorAction Stop
        }
    }

    & dotnet clean $SlnPath -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet clean failed with exit code $LASTEXITCODE."
    }
    Write-Ok "Workspace cleaned successfully."

    # ==============================================================================
    # STEP 2: RUN AUTOMATED REGRESSION TESTS (xUnit)
    # ==============================================================================
    Write-Step "STEP 2/5" "Running xUnit automated regression test suite..."

    if ($SkipTests) {
        Write-Warn "Unit tests skipped via -SkipTests switch."
    } else {
        & dotnet test $TestProjPath `
            -c Release `
            --nologo `
            --logger "console;verbosity=minimal"

        if ($LASTEXITCODE -ne 0) {
            throw "Automated unit tests FAILED (Exit Code: $LASTEXITCODE). Packaging aborted to prevent defective release!"
        }
        Write-Ok "All xUnit tests passed with 0 regressions."
    }

    # ==============================================================================
    # STEP 3: EXECUTE DOTNET PUBLISH (SINGLE-FILE SELF-CONTAINED WIN-X64)
    # ==============================================================================
    Write-Step "STEP 3/5" "Publishing Single-File Self-Contained executable (win-x64)..."

    & dotnet publish $AppProjPath `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=embedded `
        -p:Version=$Version `
        -o $PublishTempDir

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $PublishedExe = Join-Path $PublishTempDir "OTPSoftware.App.exe"
    if (-not (Test-Path $PublishedExe)) {
        throw "Published executable not found at expected path: '$PublishedExe'."
    }
    Write-Ok "Single-file executable built: $PublishedExe"

    # ==============================================================================
    # STEP 4: ASSEMBLE DISTRIBUTION DIRECTORY (dist/OTP_Software/)
    # ==============================================================================
    Write-Step "STEP 4/5" "Assembling production distribution bundle in 'dist/OTP_Software/'..."

    New-Item -ItemType Directory -Path $StageDir -Force | Out-Null
    New-Item -ItemType Directory -Path $StageConfigDir -Force | Out-Null

    # 4.1 Copy Single-File Executable
    $StagedExePath = Join-Path $StageDir "OTPSoftware.App.exe"
    Copy-Item -Path $PublishedExe -Destination $StagedExePath -Force
    Write-Ok "Staged executable: dist\OTP_Software\OTPSoftware.App.exe"

    # 4.2 Copy External Configuration File (config/settings.json)
    if (-not (Test-Path $ConfigSrcPath)) {
        throw "External configuration file missing at '$ConfigSrcPath'."
    }
    $StagedConfigPath = Join-Path $StageConfigDir "settings.json"
    Copy-Item -Path $ConfigSrcPath -Destination $StagedConfigPath -Force
    Write-Ok "Staged external config: dist\OTP_Software\config\settings.json"

    # 4.3 Generate README.txt (Operations Guide & Changelog)
    $ReadmeTxtPath = Join-Path $StageDir "README.txt"
    $BuildTimestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss zzz"
    $ReadmeContent = @"
================================================================================
 OTP LOG MONITOR & OPTICAL INSPECTION SYSTEM - OPERATIONS & DEPLOYMENT GUIDE
================================================================================
 Version         : v$Version
 Build Date      : $BuildTimestamp
 Target Platform : Windows 10 / Windows 11 (64-bit / win-x64)
 Deployment Mode : Single-File Self-Contained (.NET 10 Runtime Embedded)
================================================================================

1. PACKAGE CONTENTS (CAU TRUC THU MUC PHAN PHOI)
--------------------------------------------------------------------------------
  OTP_Software\
  |-- OTPSoftware.App.exe       : Main application executable (no .NET install required)
  |-- README.txt                : This deployment, configuration & operations guide
  |-- config\
  |   |-- settings.json         : External factory configuration (Edit with Notepad)
  |   `-- user-state.json       : Auto-generated operator UI session preferences
  |-- logs\
  |   `-- gui-error.log         : Auto-generated diagnostic & SMB error log
  `-- exports\
      |-- ShiftReport_*.csv     : Auto-generated shift handover CSV reports
      `-- ShiftChart_*.png      : Auto-generated shift handover chart snapshots

2. SYSTEM REQUIREMENTS & INSTALLATION (CAI DAT TAI MAY TRAM NHA MAY)
--------------------------------------------------------------------------------
  * OS: Windows 10 (1809+) or Windows 11 (x64).
  * Prerequisite Software: NONE. The .NET 10 Desktop Runtime and WPF libraries
    are fully embedded inside OTPSoftware.App.exe.
  * Installation Steps:
    1) Extract the ZIP archive to a local non-system directory, e.g.:
       D:\Apps\OTP_Software\  or  C:\OTP_Software\
    2) Keep the 'config\settings.json' folder alongside 'OTPSoftware.App.exe'.
    3) Ensure the workstation has SMB network read access (TCP Port 445) to
       the configured machine log shares and camera image shares.
    4) Optional: Create a Desktop Shortcut pointing to 'OTPSoftware.App.exe'.
       (The app automatically resolves 'config\settings.json' via its executable
       directory regardless of how it is launched).

3. EDITING MACHINE & CAMERA CONFIGURATION (config\settings.json)
--------------------------------------------------------------------------------
  Open 'config\settings.json' with Notepad to update station IPs or SMB paths
  when relocating a workstation:
  * machines[].name       : Station identifier (e.g., "MMAPI120", "MMAPI110")
  * machines[].logPath    : UNC path to OTP_1.log (e.g., "\\192.168.3.206\c\GoArms\logs\OTP_1.log")
  * machines[].mesLogPath : UNC path to MES_1.log (e.g., "\\192.168.3.206\c\GoArms\logs\MES_1.log")
  * machines[].images[0]  : Left Camera SMB root & station code (e.g., B1 / A1)
  * machines[].images[1]  : Right Camera SMB root & station code (e.g., B2 / A2)
  * wallboard             : NG rate warning/critical % thresholds & shift target
  * autoShiftExport       : Day/Night shift handover CSV/PNG export schedule
  * mesSpecs              : Optical parameter min/max specification overrides

  NOTE: Restart OTPSoftware.App.exe after saving changes to 'config\settings.json'.

4. OPERATOR QUICK KEYS & RUNTIME FEATURES (HUONG DAN VAN HANH NHANH)
--------------------------------------------------------------------------------
  * Language Switcher : Click [ EN | 中文 | VI ] on the top header bar to switch
                        between English, Simplified Chinese, and Vietnamese
                        instantly without restarting the app.
  * Wallboard Mode    : Press [F11] to toggle Fullscreen Kiosk Wallboard Mode
                        (Press [F11] or [ESC] to exit).
  * MES & Defect View : Double-click any unit row (or click 'View Detail') to
                        compare MES Standard Spec vs Measured Value and inspect
                        Left/Right/Side-by-Side camera defect images.
  * Headless Check    : Run 'OTPSoftware.App.exe --validate-startup' in PowerShell
                        or CMD to verify startup health (prints STARTUP_VALIDATION_SUCCESS).

5. CHANGELOG (LICH SU PHIEN BAN)
--------------------------------------------------------------------------------
  [v$Version - $DateStamp]
  * Architecture : Upgraded to .NET 10 / C# 14 Clean Architecture
                   (OTPSoftware.Core + OTPSoftware.App + OTPSoftware.Tests).
  * Performance  : Zero-allocation UTF-8 byte log tailing (FileShare.ReadWrite | Delete)
                   with 64-bit FNV-1a rotation detection and reverse 4 MB MES scanner.
  * Vision Engine: Zero-OOM asynchronous Camera Defect Viewer with DecodePixelWidth
                   downsampling, Frozen BitmapImage handoff, and 10s SMB Circuit Breaker.
  * Wallboard UI : Added Live NG Unit Spotlight widget, Left/Right/Side-by-Side
                   camera inspection, and runtime trilingual support (EN / VI / 中文).
================================================================================
"@
    Set-Content -Path $ReadmeTxtPath -Value $ReadmeContent -Encoding UTF8
    Write-Ok "Generated operator guide: dist\OTP_Software\README.txt"

    # 4.4 Execute Automated Smoke Validation against Staged Single-File Executable
    Write-Info "Running headless smoke check (--validate-startup) on staged executable..."
    $ValidationOutput = & $StagedExePath --validate-startup 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $ValidationOutput -notmatch "STARTUP_VALIDATION_SUCCESS") {
        throw "Staged executable failed '--validate-startup' smoke check (ExitCode=$LASTEXITCODE, Output=$ValidationOutput)."
    }
    # Remove any transient user-state or logs created during headless validation so the ZIP is pristine
    $TransientUserState = Join-Path $StageConfigDir "user-state.json"
    $TransientLogsDir   = Join-Path $StageDir "logs"
    if (Test-Path $TransientUserState) { Remove-Item $TransientUserState -Force }
    if (Test-Path $TransientLogsDir)   { Remove-Item $TransientLogsDir -Recurse -Force }
    Write-Ok "Headless startup validation passed (STARTUP_VALIDATION_SUCCESS)."

    # ==============================================================================
    # STEP 5: COMPRESS DISTRIBUTION PACKAGE INTO VERSIONED ZIP ARCHIVE
    # ==============================================================================
    Write-Step "STEP 5/5" "Compressing distribution bundle into '$ZipFileName'..."

    if (Test-Path $ZipFilePath) {
        Remove-Item -Path $ZipFilePath -Force
    }

    Compress-Archive -Path $StageDir -DestinationPath $ZipFilePath -CompressionLevel Optimal -Force

    if (-not (Test-Path $ZipFilePath)) {
        throw "Failed to create ZIP archive at '$ZipFilePath'."
    }

    # Clean temporary intermediate publish directory
    if (Test-Path $PublishTempDir) {
        Remove-Item -Path $PublishTempDir -Recurse -Force
    }

    $ExeSizeMB = [Math]::Round((Get-Item $StagedExePath).Length / 1MB, 2)
    $ZipSizeMB = [Math]::Round((Get-Item $ZipFilePath).Length / 1MB, 2)
    $ZipHash   = (Get-FileHash -Path $ZipFilePath -Algorithm SHA256).Hash
    $Stopwatch.Stop()

    Write-Host "`n================================================================================" -ForegroundColor Green
    Write-Host " RELEASE PACKAGING SUCCEEDED IN $([Math]::Round($Stopwatch.Elapsed.TotalSeconds, 1))s" -ForegroundColor Green
    Write-Host "================================================================================" -ForegroundColor Green
    Write-Host "  Staged Folder : $StageDir" -ForegroundColor White
    Write-Host "  Executable    : OTPSoftware.App.exe ($ExeSizeMB MB)" -ForegroundColor White
    Write-Host "  External Cfg  : config\settings.json" -ForegroundColor White
    Write-Host "  ZIP Archive   : $ZipFilePath ($ZipSizeMB MB)" -ForegroundColor Cyan
    Write-Host "  SHA-256 Hash  : $ZipHash" -ForegroundColor DarkGray
    Write-Host "================================================================================`n" -ForegroundColor Green
    exit 0
}
catch {
    $Stopwatch.Stop()
    Write-Host "`n================================================================================" -ForegroundColor Red
    Write-Fail "RELEASE PACKAGING FAILED AFTER $([Math]::Round($Stopwatch.Elapsed.TotalSeconds, 1))s"
    Write-Fail $_.Exception.Message
    Write-Host "================================================================================`n" -ForegroundColor Red
    exit 1
}

