using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using OTPSoftware.Core.Models;
using OTPSoftware.Core.Services;

namespace OTPSoftware.Benchmarks;

/// <summary>
/// Benchmarks UTF-8 streaming log reading and inspection block extraction across a ~2 MB
/// synthetic AOI log containing realistic START/END inspection blocks interspersed with high-volume chatter.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[RankColumn]
public class LogTailReaderBenchmarks
{
    private readonly LogTailReaderService _reader = new();
    private readonly List<string> _linesBuffer = new(32768);
    private readonly List<UnitRecord> _unitsBuffer = new(256);
    private string _tempDir = string.Empty;
    private string _logFilePath = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "OTPBench_Tail_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _logFilePath = Path.Combine(_tempDir, "OTP_1.log");

        var sb = new StringBuilder(2 * 1024 * 1024);
        const string infoPrefix = "[2026-11-06 14:32:36.123] [info] [GovionLogger.cpp:110]";
        const string noiseLine = "[2026-11-06 14:32:36.125] [debug] [MotionCtrl.cpp:42] PLC poll register D2012=0 servo=IDLE alarm=0\r\n";

        // 100 inspection blocks, each surrounded/padded with ~180 chatter lines (~18,500 lines, ~2 MB)
        for (int b = 1; b <= 100; b++)
        {
            for (int n = 0; n < 90; n++)
            {
                sb.Append(noiseLine);
            }

            sb.Append(infoPrefix).Append(" *****************START*****************\r\n");
            sb.Append(CultureInfo.InvariantCulture, $"{infoPrefix} Register address:2010.0, read value:PMNT10{(b % 24):D2}A\r\n");
            sb.Append(CultureInfo.InvariantCulture, $"{infoPrefix} PANELNAME:TL699000{b:D4}\r\n");
            sb.Append(CultureInfo.InvariantCulture, $"{infoPrefix} BLUNAME:38C2270000{b:D3}\r\n");

            for (int n = 0; n < 90; n++)
            {
                sb.Append(noiseLine);
            }

            if ((b % 10) == 0)
            {
                sb.Append(infoPrefix).Append(" OTP-XYLV NG, White_lv=295.100\r\n");
            }

            sb.Append(infoPrefix).Append(" *****************END*****************\r\n");
        }

        File.WriteAllText(_logFilePath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            if (!string.IsNullOrEmpty(_tempDir) && Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors on benchmark teardown
        }
    }

    [Benchmark(Baseline = true)]
    public int ReadLogTailLines_Baseline()
    {
        _linesBuffer.Clear();
        LogTailState state = _reader.CreateTailState(_logFilePath, "OTP_1");
        _reader.ReadLogTailLines(state, _linesBuffer);
        return _linesBuffer.Count;
    }

    [Benchmark]
    public int ReadLogTailUnits_FullFile()
    {
        _unitsBuffer.Clear();
        LogTailState state = _reader.CreateTailState(_logFilePath, "OTP_1");
        return _reader.ReadLogTailUnits(state, _unitsBuffer);
    }

    [Benchmark]
    public int ReadLogUnitsFromFile_ArchiveLoad()
    {
        _unitsBuffer.Clear();
        LogTailState state = _reader.CreateTailState(_logFilePath, "OTP_1");
        _reader.ReadLogUnitsFromFile(state, _unitsBuffer);
        return _unitsBuffer.Count;
    }
}
