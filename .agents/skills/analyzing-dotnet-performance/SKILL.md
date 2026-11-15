---
name: analyzing-dotnet-performance
description: Use this skill when reviewing, writing, or optimizing C# / .NET 10 code for high performance, memory footprint, zero-allocation constraints, LOH protection, and SIMD vector operations.
---

# High-Performance .NET & Zero-Allocation Engineering

This skill provides guidelines and patterns for maintaining the zero-allocation and high-throughput architectural invariants required by the OTP Log Monitor & Optical Inspection System.

## 1. Zero-Allocation & Span-Based Processing

- **Use Spans over Strings**: Never allocate intermediate `string` objects when parsing or tokenizing high-frequency log lines. Prefer `ReadOnlySpan<char>` and `ReadOnlySpan<byte>`.
- **SIMD SearchValues**: Use `SearchValues<string>` or `SearchValues<char>` for multi-token scanning (e.g. searching for markers in log streams).
- **Alternate Lookups**: When querying collections (`Dictionary<string, T>` or `HashSet<string>`) with spans, use `.GetAlternateLookup<ReadOnlySpan<char>>()` to avoid `span.ToString()` allocations.
- **Avoid LINQ in Hot Paths**: Avoid `.Select()`, `.Where()`, `.ToList()`, and closure captures in hot polling loops (`LogTailReaderService`, `LogParserService`, `MesDetailService`). Use simple `for` / `foreach` loops with indexing.

## 2. Buffer Pooling & Large Object Heap (LOH) Safety

- **ArrayPool Usage**: For reading large chunks (e.g. 4 MB MES chunks), always rent from `ArrayPool<byte>.Shared`.
  ```csharp
  byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
  try
  {
      // Process buffer via ReadOnlySpan<byte>
  }
  finally
  {
      ArrayPool<byte>.Shared.Return(buffer);
  }
  ```
- **LOH Threshold Awareness**: Objects >= 85,000 bytes land directly on the Large Object Heap. Re-allocating these causes rapid GC pauses and heap fragmentation. Always pool or slice buffers directly.

## 3. High-Performance File & SMB I/O

- **Non-Locking Sharing**: Always open streaming logs with:
  `FileShare.ReadWrite | FileShare.Delete` and `FileAccess.Read`.
- **Sequential Scan Hint**: Pass `FileOptions.SequentialScan` when reading sequential logs to help Windows kernel pre-fetching.
- **Avoid Repeated Length Probes**: Keep track of read offsets rather than querying `FileInfo.Length` continuously in tight loops.

## 4. Async & Threading Efficiency

- **Avoid Async in Sub-Millisecond Loops**: If work is pure CPU in-memory span slicing, keep it synchronous to eliminate `ValueTask` / state machine allocations.
- **ValueTask over Task**: When methods frequently complete synchronously (e.g., cache hits), return `ValueTask<T>` instead of `Task<T>`.
- **ConfigureAwait**: In `OTPSoftware.Core`, always enforce `.ConfigureAwait(false)` on all awaited tasks to prevent synchronization context capture.

## 5. Performance Verification Checklist

- [ ] Does the method allocate temporary strings in the loop? (Replace with `ReadOnlySpan<char>`).
- [ ] Are collections pre-sized if their item count is known in advance?
- [ ] Are buffers >= 85 KB rented from `ArrayPool<byte>.Shared`?
- [ ] Are heavy read operations cancellation-safe with `CancellationToken`?
