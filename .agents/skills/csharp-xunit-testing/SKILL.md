---
name: csharp-xunit-testing
description: Use this skill when writing, running, or expanding automated xUnit tests, testing SMB log tailing, XML stream slicing, boundary straddling, cache eviction, and localization parity in OTPSoftware.Tests.
---

# xUnit Testing Patterns for High-Throughput & Industrial Services

This skill details conventions for creating deterministic, fast, and comprehensive unit tests within `OTPSoftware.Tests`.

## 1. Test Architecture & Structure

- **Naming Conventions**: Follow `MethodName_GivenCondition_ShouldExpectedResult` (e.g., `ReadTailAsync_WhenFileRotated_ShouldDrainArchivedLogAndSwitchToNewFile`).
- **Arrange-Act-Assert (AAA)**: Keep clear visual boundaries between test setup, execution, and assertion.
- **Fast & Isolated**: Tests must not rely on live factory SMB shares or physical network hosts. Use synthetic memory streams or temporary local test directories created and torn down in `IDisposable` fixtures.

## 2. Core Functional Test Scenarios

- **Reverse Chunked Stream Slicing (`MesDetailService`)**:
  - Test boundary conditions where an XML record straddles across two consecutive 4 MB buffers.
  - Verify correct parsing of truncated, malformed, or missing `<OTPDEFECTVALUE>` tags.
- **Log Rotation & Fingerprinting (`LogTailReaderService`)**:
  - Verify that when a log file (`OTP_1.log`) is archived to `OTP_1.1.log` and a fresh `OTP_1.log` is created, the service detects the change via 64-bit FNV-1a hash, drains the remaining records from the archive, and switches seamlessly.
- **LRU Cache Eviction & Bounded Memory**:
  - Validate that `LruMemoryCache` enforces capacity limits and accurately evicts the Least-Recently-Used entries without leaking references.

## 3. UI Resource Parity & Security Tests

- **100% Localization Key Parity**:
  - The test suite must parse `Strings.en.xaml`, `Strings.vi.xaml`, and `Strings.zh.xaml`, extracting all `x:Key` identifiers and verifying that 100% of keys exist across all three files.
- **CSV Formula Injection Sanitization (OWASP)**:
  - Test that strings starting with dangerous formula triggers (`=`, `+`, `-`, `@`, `\t`, `\r`) are safely escaped before CSV export.

## 4. Running the Test Suite via CLI

```powershell
# Run the complete test suite in Release configuration
dotnet test OTPSoftware.sln -c Release --no-build --logger "console;verbosity=normal"

# Run a targeted test filter
dotnet test tests/OTPSoftware.Tests/OTPSoftware.Tests.csproj --filter "FullyQualifiedName~MesDetailServiceTests"
```
