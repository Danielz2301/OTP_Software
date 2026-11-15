---
name: dotnet-ci-benchmarking
description: Use this skill when setting up, writing, running, or analyzing BenchmarkDotNet micro-benchmarks, tracking zero-allocation invariants, and integrating automated performance benchmarking into CI/CD pipelines for OTPSoftware.
---

# Continuous Benchmarking & Performance Engineering with BenchmarkDotNet

This skill establishes the methodology, conventions, and CI/CD automation rules for benchmarking performance-critical paths in the **OTP Log Monitor & Optical Inspection System**.

---

## 1. Core Principles & Zero-Allocation Invariants

The `OTPSoftware.Core` library guarantees sub-millisecond telemetry processing, zero LOH fragmentation, and bounded steady-state memory under continuous 24/7 factory operation. 

Every hot path must have automated benchmarks verifying:
- **`Allocated Bytes`**: Must be **0 B** or strictly bounded (e.g. ~192 B per 1,000 noise lines in `LogParserService`).
- **`Gen0 / Gen1 / Gen2 GCs`**: Must be **0.0000** during steady-state processing loops.
- **`Mean Latency`**: Must not regress across releases.

---

## 2. Standard Benchmark Configuration

All benchmarks must use `[MemoryDiagnoser]` and specify explicit Release configurations targeting .NET 10 (`net10.0`):

```csharp
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;

namespace OTPSoftware.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob(RuntimeMoniker.Net100)]
[RankColumn]
[CategoriesColumn]
public class LogParserBenchmarks
{
    private byte[] _sampleLogChunk = null!;
    private string _sampleNoiseLine = null!;

    [GlobalSetup]
    public void Setup()
    {
        _sampleNoiseLine = "2026-11-15 13:40:00.123 [INFO] Worker idle, waiting for sensor trigger...";
        _sampleLogChunk = System.Text.Encoding.UTF8.GetBytes(_sampleNoiseLine);
    }

    [Benchmark(Baseline = true)]
    public bool NaiveStringSplit()
    {
        // Reference baseline (string allocation anti-pattern)
        string[] parts = _sampleNoiseLine.Split(' ');
        return parts.Length > 2 && parts[2] == "[INFO]";
    }

    [Benchmark]
    public bool SpanSimdParse()
    {
        // Target zero-allocation implementation
        ReadOnlySpan<char> span = _sampleNoiseLine.AsSpan();
        return LogParserService.FastCheckLine(span);
    }
}
```

---

## 3. High-Priority Benchmark Targets in OTP Software

### A. Log Tail Reader & UTF-8 Slicing (`LogTailReaderService`)
- **Metric**: Bytes allocated per 1 MB of streaming log data.
- **Verification**: Ensure byte-level newline search (`\n` / `\r\n`) uses `ReadOnlySpan<byte>` without creating temporary `string` instances for unneeded rows.

### B. Reverse MES XML Scanner (`MesDetailService`)
- **Metric**: Latency and heap allocation when scanning backward through 4 MB chunks to find `<OTPDEFECTCODE>`.
- **Verification**: Confirm `ArrayPool<byte>.Shared` buffer renting and returning produces 0 net allocations.

### C. Node-Reusing Cache (`LruMemoryCache<TKey, TValue>`)
- **Metric**: Memory allocations during heavy eviction churn (100,000 insertions into a capacity-24 cache).
- **Verification**: Zero node allocations in steady state via linked-list node pooling.

---

## 4. Running Benchmarks via CLI

### Quick Validation Run (Single Iteration)
```powershell
dotnet run -c Release --project tests/OTPSoftware.Benchmarks/OTPSoftware.Benchmarks.csproj -- --filter *LogParser* --job short
```

### Full Rigorous Benchmark Suite
```powershell
dotnet run -c Release --project tests/OTPSoftware.Benchmarks/OTPSoftware.Benchmarks.csproj -- --artifacts ./BenchmarkResults --exporters json,html,github
```

---

## 5. CI/CD Pipeline Automation (GitHub Actions Example)

Integrate automated benchmark comparisons into `.github/workflows/benchmarks.yml` to catch regressions automatically on pull requests:

```yaml
name: Performance & Zero-Allocation Gate

on:
  pull_request:
    paths:
      - 'src/OTPSoftware.Core/**'
      - 'tests/OTPSoftware.Benchmarks/**'

jobs:
  benchmark:
    runs-on: windows-latest
    steps:
      - name: Checkout Repository
        uses: actions/checkout@v4

      - name: Setup .NET 10 SDK
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Run Micro-Benchmarks
        run: |
          dotnet run -c Release --project tests/OTPSoftware.Benchmarks/OTPSoftware.Benchmarks.csproj -- --filter * --exporters json --artifacts ./BenchmarkArtifacts

      - name: Verify Zero-Allocation Invariants
        shell: pwsh
        run: |
          $json = Get-Content ./BenchmarkArtifacts/results/*-report-full-compressed.json | ConvertFrom-Json
          $allocations = $json.Benchmarks | Where-Object { $_.Statistics.AllocatedMemory -gt 0 -and $_.Method -like "*ZeroAlloc*" }
          if ($allocations) {
            Write-Error "Regression detected! Zero-allocation invariant violated in: $($allocations.Method)"
            exit 1
          }
          Write-Host "Zero-allocation check passed successfully!"
```

---

## 6. Checklist for Benchmark Reviews

- [ ] Does the benchmark class include `[MemoryDiagnoser]`?
- [ ] Is input test data prepared in `[GlobalSetup]` to avoid polluting benchmark timing?
- [ ] Does the benchmark test realistic worst-case chunk sizes (e.g., 4 MB reverse scan, 50,000 noise lines)?
- [ ] Are return values from benchmark methods consumed (returned) to prevent the JIT from dead-code eliminating the work?
- [ ] Has the baseline method been marked with `Baseline = true` for easy ratio comparison?
- [ ] Is memory allocation confirmed to be zero or strictly bounded?
