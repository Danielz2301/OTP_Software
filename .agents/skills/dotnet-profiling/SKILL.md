---
name: dotnet-profiling
description: Use this skill when diagnosing, monitoring, and profiling live .NET 10 applications in development or 24/7 production environments using dotnet-counters, dotnet-trace, dotnet-dump, and dotnet-gcdump to investigate memory leaks, CPU spikes, and thread starvation.
---

# Production Runtime Diagnostics & Profiling with Official .NET CLI Tools

This skill establishes standard diagnostic procedures, troubleshooting workflows, and triage playbooks for investigating performance anomalies (memory leaks, thread pool starvation, CPU spikes, and SMB lock contention) in `OTPSoftware.App` while running in development or 24/7 factory production.

---

## 1. The Triage Funnel for 24/7 Production Systems

Never attach heavy, invasive debuggers (like Visual Studio) directly to production factory PCs running real-time inspection. Follow the progressive diagnostic funnel:

```text
[Stage 1: Live Monitoring]  ---> dotnet-counters (0% overhead, continuous health vitals)
                                      |
                                      v (If heap keeps growing)
[Stage 2: Heap Triage]      ---> dotnet-gcdump (Lightweight heap snapshot, no process freeze)
                                      |
                                      v (If root cause involves unmanaged memory or deadlocks)
[Stage 3: Deep Crash/Hang]  ---> dotnet-dump (Full memory snapshot + SOS debugger CLI)
                                      |
                                      v (If UI stutters or CPU spikes)
[Stage 4: Execution Trace]  ---> dotnet-trace (EventPipe CPU sampling & lock contention)
```

---

## 2. Tool 1: Live Monitoring with `dotnet-counters`

Use `dotnet-counters` to monitor runtime health in real-time over command line without stopping the application.

### A. Discover Process ID
```powershell
dotnet-counters ps
```

### B. Launch Live Monitoring
```powershell
dotnet-counters monitor --process-id <PID> --counters System.Runtime
```

### C. Interpreting Vitals for OTP Software:
| Metric | Healthy Operational Threshold | Danger Signal & Root Cause |
| :--- | :--- | :--- |
| **`gc-heap-size`** | Bounded (e.g., 80 MB – 300 MB) | Monotonically increasing across days indicates event listener or collection leaks. |
| **`gen-2-gc-count`** | Rare (only occurs occasionally) | Frequent Gen 2 collections cause UI micro-stutters on Wallboard. |
| **`loh-size`** | Flat & near minimum (< 10 MB) | Increasing LOH size indicates raw bitmap (`photo/Main*.png`) or 4 MB byte buffer bypassing `ArrayPool<byte>`. |
| **`threadpool-queue-length`** | 0 to 5 | Sustained values > 15 indicate thread pool starvation from synchronous SMB blocking I/O (`.Result`, `.Wait()`). |
| **`cpu-usage`** | 1% – 8% steady state | Spikes > 50% during idle indicate polling spin-waits or non-blocking tight loops in `LogTailReaderService`. |

---

## 3. Tool 2: Memory Leaks & Object Retention with `dotnet-gcdump` & `dotnet-dump`

When process memory creeps up over multiple shifts, isolate the retaining reference path.

### A. Capture a Lightweight Snapshot (Zero Impact on Production)
```powershell
# Takes only 1–3 seconds, does not freeze real-time log tailing
dotnet-gcdump collect --process-id <PID> -o ./dumps/otp-heap.gcdump
```
*Analysis:* Open `otp-heap.gcdump` directly in Visual Studio or JetBrains dotMemory to view instance counts and back-reference graphs.

### B. Deep Memory Analysis via `dotnet-dump`
For detailed native memory, LOH fragmentation, or thread locks:
```powershell
# 1. Capture full crash/memory dump
dotnet-dump collect --process-id <PID> --type Full -o ./dumps/otp-full.dmp

# 2. Enter interactive SOS CLI
dotnet-dump analyze ./dumps/otp-full.dmp
```

### C. Essential SOS Debugging Commands:
```text
> dumpheap -stat
# Lists all types by total instance count and size. Look at the bottom 10 lines for the largest memory consumers.

> dumpheap -min 85000
# Lists every single object allocated directly on the Large Object Heap (LOH). Check if large byte[] or BitmapImage buffers are leaked.

> gcroot <OBJECT_ADDRESS>
# Traces the complete GC retention chain from the root (static variable, UI tree, or active thread stack) down to the leaking object.

> clrstack -all
# Prints managed call stacks for all active threads. Use this when the application appears frozen or deadlocked on an SMB network share.
```

---

## 4. Tool 3: CPU Spikes & Lock Contention with `dotnet-trace`

When the Wallboard drops frames or image decoding stutters during high-speed production surges:

### A. Record a 30-Second Execution Trace
```powershell
dotnet-trace collect --process-id <PID> `
  --providers Microsoft-DotNETCore-SampleProfiler,Microsoft-Windows-DotNETRuntime:0x4c14f:4 `
  --duration 00:00:30 `
  -o ./traces/otp-cpu.nettrace
```

### B. Convert for Web Flamegraph Analysis (Speedscope)
```powershell
dotnet-trace convert --format Speedscope ./traces/otp-cpu.nettrace
```
*Analysis:* Drag and drop `otp-cpu.speedscope.json` into [https://www.speedscope.app/](https://www.speedscope.app/) to view the interactive flamegraph, identify hot-path methods, and inspect thread lock waits.

---

## 5. Automated Production Alert Script (Self-Healing & Watchdog)

On unattended factory kiosk PCs, run a watchdog PowerShell task to capture diagnostic artifacts automatically before an OOM occurs:

```powershell
# Watchdog snippet: triggers a gcdump if working set exceeds 1.2 GB
$process = Get-Process -Name "OTPSoftware.App" -ErrorAction SilentlyContinue
if ($process -and $process.WorkingSet64 -gt 1.2GB) {
    $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $dumpPath = "C:\GoArms\diagnostics\leak_$timestamp.gcdump"
    New-Item -ItemType Directory -Force -Path "C:\GoArms\diagnostics"
    & dotnet-gcdump collect --process-id $process.Id -o $dumpPath
    Write-Warning "Working set exceeded 1.2 GB! Automated gcdump generated at $dumpPath"
}
```

---

## 6. Diagnostic Review Checklist

- [ ] Has the issue been reproduced and observed using `dotnet-counters` before capturing heavy dumps?
- [ ] Are all dumps captured with non-invasive tools (`dotnet-gcdump` preferred during active production)?
- [ ] Did `dumpheap -stat` identify leaking types (e.g., `EventHandler`, `BitmapImage`, `UnitRecordModel`)?
- [ ] Did `gcroot` reveal a static event listener, unhooked DispatcherTimer, or undisposed UI binding?
- [ ] Are thread pool queues zero during high-volume log bursts?
