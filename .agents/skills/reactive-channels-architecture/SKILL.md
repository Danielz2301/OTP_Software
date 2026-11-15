---
name: reactive-channels-architecture
description: Use this skill when designing, implementing, or reviewing high-throughput Producer-Consumer pipelines, backpressure management, telemetry stream buffering, and batch UI dispatching using System.Threading.Channels in OTPSoftware.
---

# Reactive Pipelines & Backpressure Architecture with System.Threading.Channels

This skill establishes the architectural patterns for decoupling high-frequency log stream ingestion, CPU-bound telemetry parsing, and WPF UI dispatching using `System.Threading.Channels` in `OTPSoftware`.

---

## 1. Why `System.Threading.Channels`?

In multi-station 24/7 manufacturing operations, log data can arrive in sudden bursts (e.g., 20,000 lines when an operator runs batch re-inspection). 

Using naive approaches like `BlockingCollection<T>`, unbounded `ConcurrentQueue<T>`, or invoking `Dispatcher.InvokeAsync` for each parsed log record causes:
- **Thread Pool Exhaustion**: Too many queued tasks waiting for I/O or CPU.
- **Unbounded Memory Spikes**: RAM balloons out of control when producers outpace consumers.
- **WPF UI Freezing**: Flooding the Windows message queue with individual line updates starves user input handling.

`System.Threading.Channels<T>` solves this by providing **lock-free, async-native, allocation-minimized** streaming pipelines with explicit **Backpressure**.

---

## 2. Three-Tier Pipeline Architecture

```text
[Stage 1: Producer]           [Stage 2: Processing Core]         [Stage 3: UI Dispatcher]
LogTailReaderService   --->   LogParserService & Stats    --->   MainViewModel (Wallboard)
(SMB Stream I/O)              (SIMD / Span<char> CPU)            (Batch UI Dispatch 50ms)
       │                               │                                 │
       ▼                               ▼                                 ▼
Bounded Channel A              Bounded Channel B                 ObservableCollection
(Capacity: 10,000)             (Capacity: 2,000)                 (Batch Add / Reset)
```

---

## 3. Configuring Bounded Channels & Backpressure

Always prefer **Bounded Channels** over Unbounded Channels to enforce strict memory safety limits:

```csharp
public static class PipelineFactory
{
    public static Channel<LogRawChunk> CreateLogIngestionChannel(int capacity = 10_000)
    {
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait, // Pause reader if parser is saturated
            SingleWriter = false,                  // Multiple station log tailers writing
            SingleReader = true,                   // Dedicated single CPU parsing thread
            AllowSynchronousContinuations = false  // Critical: prevents running parser on I/O completion thread
        };

        return Channel.CreateBounded<LogRawChunk>(options);
    }
}
```

### FullMode Selection Matrix:
| Operational Scenario | Selected `FullMode` | Behavioral Invariant |
| :--- | :--- | :--- |
| **Audit Logs & MES Records** | `BoundedChannelFullMode.Wait` | **Zero Data Loss**: Slows down reader slightly; guarantees every unit record is parsed and counted. |
| **Live GUI Telemetry Ticks** | `BoundedChannelFullMode.DropOldest` | **Freshness Priority**: Keeps only newest UI telemetry metrics; discards stale intermediate frames. |
| **Camera Thumbnail Previews** | `BoundedChannelFullMode.DropWrite` | **Resource Protection**: Rejects excess preview requests if thumbnail decoder queue is backlogged. |

---

## 4. Batch UI Dispatching (Zero Dispatcher Stutter)

Never push records to WPF one-by-one. Instead, drain the channel in micro-batches or on a 50ms timer window:

```csharp
public sealed class BatchUiDispatcher(ChannelReader<UnitRecord> reader, Action<IReadOnlyList<UnitRecord>> onBatchReady)
{
    private const int MaxBatchSize = 100;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(50);

    public async Task StartProcessingAsync(CancellationToken ct)
    {
        var batch = new List<UnitRecord>(MaxBatchSize);
        var stopwatch = Stopwatch.StartNew();

        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (reader.TryRead(out var item))
            {
                batch.Add(item);

                // Flush if batch is full or time threshold exceeded
                if (batch.Count >= MaxBatchSize || stopwatch.Elapsed >= FlushInterval)
                {
                    FlushToUi(batch);
                    batch.Clear();
                    stopwatch.Restart();
                }
            }

            // Flush any remaining items before waiting again
            if (batch.Count > 0)
            {
                FlushToUi(batch);
                batch.Clear();
                stopwatch.Restart();
            }
        }
    }

    private void FlushToUi(List<UnitRecord> items)
    {
        var snapshot = items.ToArray();
        App.Current.Dispatcher.InvokeAsync(() => onBatchReady(snapshot));
    }
}
```

---

## 5. Graceful Pipeline Shutdown

Ensure producers and consumers terminate cleanly during application exit or station reconfiguration:

```csharp
public async Task ShutdownAsync()
{
    // 1. Mark the writer as complete so consumers know no more data is coming
    _channel.Writer.TryComplete();

    // 2. Await the consumer loop to finish processing remaining buffered items
    try
    {
        await _consumerTask.ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        // Expected on cancellation
    }
}
```

---

## 6. Reactive Channels Checklist for Code Reviews

- [ ] Is the channel configured with `BoundedChannelOptions` (avoiding unbounded memory growth)?
- [ ] Is `AllowSynchronousContinuations = false` set to avoid deadlocks and thread hijacking?
- [ ] Are producers and consumers decoupled with appropriate `SingleWriter` / `SingleReader` optimizations?
- [ ] Are UI updates batched (e.g. 50ms or 100 items) rather than dispatched per single log line?
- [ ] Is `TryComplete()` called on application shutdown to drain buffered records cleanly?
