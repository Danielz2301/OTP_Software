---
name: dotnet-resilience-and-fault-tolerance
description: Use this skill when designing, implementing, or reviewing resilience patterns, SMB network fault handling, circuit breakers, retry with exponential backoff and jitter, timeouts, and graceful degradation in OTPSoftware.
---

# Resilience & Fault-Tolerance Engineering for Industrial SMB Networks

This skill defines the architectural patterns, failure-handling policies, and defensive programming standards for managing remote network shares (`\\192.168.3.x`), camera storage PCs, and transient factory LAN disconnections in `OTPSoftware`.

---

## 1. Industrial Network Realities & Core Invariants

Factory LANs are harsh operational environments. Camera PCs reboot during maintenance shifts, network cables vibrate loose, and remote inspection software holds transient file locks.

Every remote network interaction must adhere to these invariants:
1. **Zero-UI-Freeze Guarantee**: A network failure, SMB disconnection, or 30-second Windows UNC path hang must **never** block the WPF UI thread (`Dispatcher`) or stall log tailing on adjacent healthy stations.
2. **Fail-Fast via Circuit Breakers**: Do not wait for Windows OS-level 30-second network timeouts on offline machines. Detect failures early and fail fast.
3. **Graceful Degradation**: When a camera share goes offline, the system must continue tailing station logs, record NG verdicts, and surface visual offline badges (`A1`, `A2`, `B1`, `B2`) without throwing unhandled exceptions.

---

## 2. Non-Blocking SMB Circuit Breaker Pattern

Before attempting expensive filesystem probes (`Directory.Exists`, `EnumerateFiles`, `FileStream.Open`) across UNC paths (`\\192.168.3.203\e\LOG`), probe network port reachability:

```csharp
public sealed class SmbCircuitBreaker
{
    private readonly TimeSpan _openDuration = TimeSpan.FromSeconds(10);
    private long _lastFailureTicks;
    private int _circuitState; // 0 = Closed (Healthy), 1 = Open (Failing Fast)

    public bool IsOpen => Volatile.Read(ref _circuitState) == 1 &&
                          DateTime.UtcNow.Ticks - Volatile.Read(ref _lastFailureTicks) < _openDuration.Ticks;

    public async ValueTask<bool> CheckReachabilityAsync(string host, int port = 445, int timeoutMs = 150)
    {
        if (IsOpen) return false; // Fail fast without network call

        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            
            // Success: Reset circuit to Closed
            Volatile.Write(ref _circuitState, 0);
            return true;
        }
        catch
        {
            // Trip circuit to Open
            Volatile.Write(ref _lastFailureTicks, DateTime.UtcNow.Ticks);
            Volatile.Write(ref _circuitState, 1);
            return false;
        }
    }
}
```

---

## 3. Retry with Exponential Backoff + Jitter

When opening streaming logs (`OTP_1.log`, `MES_1.log`), the inspection software on the machine may hold an exclusive lock for a few milliseconds during log rotation. 

Use **Decorrelated Jitter** rather than static intervals to prevent the "thundering herd" problem:

```csharp
public static async Task<T> ExecuteWithJitterRetryAsync<T>(
    Func<CancellationToken, Task<T>> action,
    int maxRetries = 3,
    int baseDelayMs = 50,
    CancellationToken ct = default)
{
    for (int attempt = 1; attempt <= maxRetries; attempt++)
    {
        try
        {
            return await action(ct).ConfigureAwait(false);
        }
        catch (IOException ex) when (IsTransientSharingViolation(ex) && attempt < maxRetries)
        {
            // Jitter calculation: baseDelay * 2^(attempt - 1) + random jitter
            int maxJitter = baseDelayMs * (1 << (attempt - 1));
            int delay = Random.Shared.Next(baseDelayMs, maxJitter);
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    return await action(ct).ConfigureAwait(false);
}

private static bool IsTransientSharingViolation(IOException ex)
{
    // Win32 ERROR_SHARING_VIOLATION (0x80070020) or ERROR_LOCK_VIOLATION (0x80070021)
    int hr = ex.HResult;
    return hr == unchecked((int)0x80070020) || hr == unchecked((int)0x80070021);
}
```

---

## 4. Timeout Budgets & Cancellation Discipline

Windows SMB calls can hang indefinitely if a network switch drops TCP packets silently. Every I/O request must be bound to a strict timeout budget using linked tokens:

```csharp
public async Task<Stream?> SafeOpenSmbStreamAsync(string uncPath, CancellationToken callerToken)
{
    // Enforce a hard 3-second budget for remote SMB metadata & stream acquisition
    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken, timeoutCts.Token);

    try
    {
        return await Task.Run(() => new FileStream(
            uncPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan),
            linkedCts.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
    {
        // Timed out on SMB network handshake
        return null;
    }
    catch (Exception ex)
    {
        ErrorLogger.LogWarning($"SMB Read failed for {uncPath}: {ex.Message}");
        return null;
    }
}
```

---

## 5. Graceful Degradation & Fallback Hierarchy

When hardware or network components fail, the system must degrade progressively:

| Failure Scenario | Fallback Behavior & Mitigation |
| :--- | :--- |
| **Camera Share Offline (`A1`/`B1`)** | Trip Circuit Breaker $\rightarrow$ Render amber/gray offline badge on Wallboard $\rightarrow$ Continue tailing OTP logs $\rightarrow$ Retry probe every 10s. |
| **High-Res Image Missing (`photo/Main*.png`)** | Fallback to algorithmic overview (`piresult/AlgIntegrate.jpg`) $\rightarrow$ Fallback to cropped defect (`defectPicture/*.jpg`) $\rightarrow$ Render "Image Unavailable" placeholder. |
| **`config/settings.json` Corrupted** | Log non-blocking warning $\rightarrow$ Automatically fall back to built-in immutable machine configurations (`MMAPI120` & `MMAPI110`) $\rightarrow$ Display warning banner in GUI. |
| **MES XML Boundary Truncation** | Scan backwards across chunk boundary $\rightarrow$ If `<OTPDEFECTCODE>` is partially written by machine, skip partial token and re-sync on next polling cycle. |

---

## 6. Resilience Review Checklist

- [ ] Does every remote SMB access check reachability or use a circuit breaker?
- [ ] Are transient file-sharing violations (`0x80070020`) retried with jitter instead of crashing the service?
- [ ] Are all asynchronous network calls bound to an explicit timeout budget (`CancelAfter`)?
- [ ] Is `FileShare.ReadWrite | FileShare.Delete` always specified to avoid locking files out from inspection machines?
- [ ] Do network catch blocks log diagnostics without crashing background worker threads?
