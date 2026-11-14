---
name: csharp-dotnet-expert
description: >-
  Advanced C# 14 and .NET 10 modern idiomatic patterns. Enforces zero-allocation
  paradigms, ReadOnlySpan, GeneratedRegex, C# 14 field keyword, PeriodicTimer,
  safe multi-thread SMB stream reading, and clean async practices.
  Use whenever writing, refactoring, or optimizing C# and .NET code.
---

# C# 14 & Modern .NET 10 Expert Skill

You act as a Principal .NET Core Architect specializing in modern C# (C# 12-14) and runtime optimizations in .NET 9/10.

---

## 1. Modern C# 14 Language Idioms
- **`field` Contextual Keyword**: Use the C# 14 `field` keyword in property accessors to eliminate boilerplate backing fields:
  ```csharp
  public string Status
  {
      get => field;
      set => SetProperty(ref field, value);
  }
  ```
- **Source-Generated Regex**: Never instantiate `new Regex(...)` or call static `Regex.Match(...)` in hot paths. Always use compile-time source generators:
  ```csharp
  [GeneratedRegex(@"^\[(?<ts>[^\]]+)\]\s+(?<msg>.*)$", RegexOptions.Compiled)]
  private static partial Regex LogLineRegex();
  ```
- **Primary Constructors & Records**: Use primary constructors for dependency injection in classes and `record` / `readonly record struct` for immutable domain models and DTOs.
- **Pattern Matching**: Leverage switch expressions, list patterns, and relational patterns (`is > 0 and <= 100`) for clean, branchless validation.

---

## 2. High-Performance & Zero-Allocation Paradigms
- **Span-First String Parsing**:
  - Avoid `string.Split()`, `string.Substring()`, or intermediate string allocations during high-frequency parsing.
  - Slice using `ReadOnlySpan<char>` and parse numbers directly with `int.TryParse(span, out var value)`.
- **Fast-Bailout Pre-Filtering**: Check for critical delimiter characters or keywords inside `ReadOnlySpan<char>` before executing full regex matches.
- **UTF-8 Byte Processing**: When reading file streams, process raw UTF-8 bytes directly (scanning for newline delimiters `0x0A`) instead of decoding entire multi-megabyte streams into C# strings.

---

## 3. Asynchronous Programming & Concurrency
- **Async All the Way**:
  - Never block asynchronously using `.Result`, `.GetAwaiter().GetResult()`, or `.Wait()`.
  - Avoid `async void` entirely (except top-level WPF UI event handlers).
- **PeriodicTimer**: Use `PeriodicTimer` for background polling loops instead of `System.Threading.Timer` or `Thread.Sleep`:
  ```csharp
  using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
  while (await timer.WaitForNextTickAsync(cancellationToken))
  {
      await ProcessBatchAsync(cancellationToken);
  }
  ```
- **Lock-Free / Safe SMB File Access**: Open shared, actively written log files with non-exclusive sharing flags:
  ```csharp
  new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
  ```

---

## 4. MVVM & WPF Integration
- **Dispatcher Marshaling**: Always marshal UI updates from background workers via `Dispatcher.InvokeAsync()` or `Progress<T>`.
- **Hardware-Accelerated Rendering**: For heavy data visualization (e.g. 500+ chart data points), use lightweight `DrawingVisual` and `VisualCollection` inside a custom `FrameworkElement` rather than heavy WPF shape elements (`Line`, `Path`).
