---
name: csharp-robustness-and-fault-testing
description: Use this skill when authoring robust xUnit tests in OTPSoftware.Tests, including time-travel testing with FakeTimeProvider, SMB fault injection (sharing locks 0x80070020, port 445 timeouts), fuzz/mutation testing for stream slicers, and headless MVVM testing.
---

# Robustness, Fault Injection & Deterministic Time Testing

This skill establishes testing patterns, mock strategies, and mutation/fuzzing guidelines in `OTPSoftware.Tests` to verify system behavior under abnormal factory conditions (network dropouts, midnight shift rollover, locked files, and malformed log streams).

---

## 1. Deterministic Time-Travel Testing with `FakeTimeProvider`

Never use `Thread.Sleep` or `Task.Delay` in xUnit tests to verify timeouts, cache TTLs, or scheduled shift exports. Instead, inject `TimeProvider` into services and use `FakeTimeProvider` (`Microsoft.Extensions.Time.Testing`) for instantaneous, deterministic verification.

### A. Testing Auto Shift Export Handover (`08:30` / `20:30`)
```csharp
[Fact]
public async Task AutoShiftExporter_WhenClockReachesDayShiftEnd_ShouldTriggerExport()
{
    // Arrange: Set clock to 20:29:59 (1 second before shift change)
    var fakeTime = new FakeTimeProvider();
    fakeTime.SetUtcNow(new DateTimeOffset(2026, 11, 15, 20, 29, 59, TimeSpan.FromHours(7)));

    var exportMock = new Mock<IShiftExportService>();
    var scheduler = new ShiftExportScheduler(fakeTime, exportMock.Object);

    // Act: Advance time by 2 seconds (crosses 20:30 boundary instantly)
    fakeTime.Advance(TimeSpan.FromSeconds(2));

    // Assert: Verify export was invoked exactly once with DAY shift tag
    exportMock.Verify(x => x.ExportCurrentShiftAsync("DAY", It.IsAny<CancellationToken>()), Times.Once);
}
```

### B. Testing Circuit Breaker Cooldown Transitions
```csharp
[Fact]
public void CircuitBreaker_WhenCooldownExpires_ShouldTransitionToHalfOpen()
{
    var fakeTime = new FakeTimeProvider();
    var breaker = new SmbCircuitBreaker(fakeTime, cooldownDuration: TimeSpan.FromSeconds(10));

    breaker.Trip(); // Simulate network failure
    Assert.True(breaker.IsOpen);

    // Advance 9.9s -> Must still be OPEN (fail-fast)
    fakeTime.Advance(TimeSpan.FromSeconds(9.9));
    Assert.True(breaker.IsOpen);

    // Advance another 0.2s (total 10.1s) -> Must transition out of Open state
    fakeTime.Advance(TimeSpan.FromSeconds(0.2));
    Assert.False(breaker.IsOpen);
}
```

---

## 2. SMB Fault Injection & Network Chaos Testing

Industrial file shares encounter transient locking and dropped connections. Use fault-injecting streams in unit tests to verify resilience policies.

### A. Simulating Windows File Sharing Violations (`0x80070020`)
Verify that `ExecuteWithJitterRetryAsync` recovers when an inspection machine temporarily locks `OTP_1.log`:

```csharp
[Fact]
public async Task ReadLogWithRetry_WhenSharingViolationOccurs_ShouldRetryAndSucceed()
{
    int attempts = 0;
    Task<string> FlakySmbRead(CancellationToken ct)
    {
        attempts++;
        if (attempts < 3)
        {
            // Throw Win32 ERROR_SHARING_VIOLATION
            throw new IOException("The process cannot access the file because it is being used by another process.",
                unchecked((int)0x80070020));
        }
        return Task.FromResult("2026-11-15 14:00:00 [INFO] PASS_TL687304F0ED_OK");
    }

    // Act
    string result = await RetryPolicy.ExecuteWithJitterRetryAsync(FlakySmbRead, maxRetries: 3, baseDelayMs: 5);

    // Assert
    Assert.Equal(3, attempts);
    Assert.Contains("PASS_TL687304F0ED_OK", result);
}
```

---

## 3. Fuzz Testing & Stream Mutation for Parsers

Feed malformed, truncated, and corrupted byte chunks into `MesDetailService` and `LogParserService` to guarantee zero unhandled exceptions:

```csharp
[Theory]
[InlineData("<OTPDEFECTCODE>")]                           // Missing closing tag & value
[InlineData("<OTPDEFECTCODE>POWERON</OTPDEFECTCODE")]     // Missing '>' at end
[InlineData("\0\0\0\0\0\0\0\0")]                          // Raw null bytes
[InlineData("<<<>>>><<<///XML_CORRUPTED")]               // Random XML garbage
[InlineData("2026-11-15 14:00:00 [NG] PMNT= ")]           // Truncated PMNT token
public void MesDetailService_WhenPayloadIsMalformed_ShouldFailGracefullyWithoutCrashing(string corruptedXml)
{
    byte[] utf8Bytes = Encoding.UTF8.GetBytes(corruptedXml);

    // Act & Assert: Must never throw IndexOutOfRangeException, NullReferenceException, or loop forever
    var exception = Record.Exception(() =>
    {
        bool success = MesDetailService.TryParseXmlChunk(utf8Bytes.AsSpan(), out var detail);
        Assert.False(success);
    });

    Assert.Null(exception);
}
```

---

## 4. Headless MVVM & Rapid Cancellation Testing

Verify that ViewModels remain stable when operators rapidly click through rows or cancel in-flight SMB image loads:

```csharp
[Fact]
public async Task MesDetailViewModel_WhenUserQuicklySwitchesRows_ShouldCancelInFlightImageLoads()
{
    var imageLoaderMock = new Mock<ICameraImageLoader>();
    var slowTcs = new TaskCompletionSource<BitmapSource>();

    imageLoaderMock.Setup(x => x.LoadImageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                   .Returns(slowTcs.Task);

    var vm = new MesDetailViewModel(imageLoaderMock.Object);

    // 1. Select Row A (starts slow load)
    var task1 = vm.SelectUnitAsync("SERIAL_A");

    // 2. Rapidly select Row B before Row A finishes
    var task2 = vm.SelectUnitAsync("SERIAL_B");

    // Assert: Previous cancellation token must have been signaled
    Assert.True(vm.ActiveImageLoadCts?.IsCancellationRequested == false); // Row B CTS is active
    
    // Complete Row B
    slowTcs.SetResult(new BitmapImage());
    await task2;
}
```

---

## 5. Robustness Testing Checklist

- [ ] Are time-dependent features tested with `FakeTimeProvider` rather than `Thread.Sleep`?
- [ ] Are file sharing violation errors (`0x80070020`) covered by retry-and-recovery tests?
- [ ] Are partial/truncated XML chunks tested to ensure parsers do not throw `IndexOutOfRangeException`?
- [ ] Does rapid cancellation in ViewModels abort in-flight asynchronous operations cleanly?
- [ ] Do all tests run deterministically in under 100 milliseconds?
