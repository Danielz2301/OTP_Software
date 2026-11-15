---
name: dotnet-best-practices
description: Use this skill when reviewing, refactoring, or authoring C# 14 and .NET 10 code, ensuring strict adherence to Functional Core - Imperative Shell architecture, modern language idioms, and null safety.
---

# Modern .NET 10 & C# 14 Clean Architecture

This skill governs coding standards, modern C# conventions, and architectural boundaries across all projects in the solution.

## 1. Architectural Boundaries (Functional Core – Imperative Shell)

- **Pure Functional Core (`OTPSoftware.Core`)**:
  - **Zero UI Dependencies**: Absolutely no references to WPF, `System.Windows`, XAML, or presentation assemblies in `OTPSoftware.Core`.
  - Must remain pure, deterministic, and easily testable without UI mocking or Dispatcher context.
- **Imperative Shell (`OTPSoftware.App`)**:
  - Handles external side effects: WPF windows, user input, audio alarms, clipboard operations, and process launches.
  - Injects Core services via Dependency Injection (DI) and coordinates reactive data updates.

## 2. Modern C# 14 & .NET 10 Idioms

- **Primary Constructors**: Prefer primary constructors for classes and structs with direct dependency injection:
  ```csharp
  public sealed class MesDetailService(ILogger<MesDetailService> logger, IMemoryCache cache)
  ```
- **Collection Expressions**: Use concise collection expressions `[...]` instead of verbose `new List<T>()` or `new T[]`:
  ```csharp
  ReadOnlySpan<char> delimiters = [',', ';', '|'];
  ```
- **Null Safety**: Treat `#nullable enable` as mandatory. Never suppress null warnings with `!` unless proven safe with an explanatory comment. Prefer null-coalescing and pattern matching:
  ```csharp
  if (record is { Status: "NG", DefectCode.Length: > 0 })
  ```
- **Source-Generated JSON Serialization**:
  - When serializing or deserializing configurations and telemetry, always use `System.Text.Json` Source Generators (`OtpJsonContext`) for zero runtime reflection and trimmed AOT readiness.

## 3. Dependency Injection & Service Lifetimes

- **Singleton Services**: State-holding or cache-managing background workers (`LogTailReaderService`, `CameraImageService`, `LruMemoryCache`) must be registered as Singletons.
- **Transient / Scoped Services**: Short-lived helpers or view-specific models should be Transient.
- **Interface Segregation**: Prefer small, focused interfaces (`ILogTailReader`, `IMesDetailService`, `IImageLocator`).

## 4. Code Review Invariant Checks

- [ ] Does `OTPSoftware.Core` remain free of any WPF or UI namespaces?
- [ ] Are public methods properly documented with clear parameter expectations?
- [ ] Are JSON models registered in `OtpJsonContext` for source generation?
- [ ] Is cancellation token propagated through all long-running or I/O-bound asynchronous methods?
