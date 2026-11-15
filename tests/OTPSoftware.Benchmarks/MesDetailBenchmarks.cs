using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using OTPSoftware.Core.Models;
using OTPSoftware.Core.Services;

namespace OTPSoftware.Benchmarks;

/// <summary>
/// Benchmarks reverse-chunked MES XML scanning, cold-miss disk traversal across 4+ MB logs,
/// and warm <see cref="OTPSoftware.Core.Caching.LruMemoryCache{TKey,TValue}"/> lookups in <see cref="MesDetailService"/>.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[RankColumn]
public class MesDetailBenchmarks
{
    private readonly MesDetailService _coldService = new();
    private readonly MesDetailService _warmService = new();
    private string _tempDir = string.Empty;
    private string _mesFilePath = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "OTPBench_Mes_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _mesFilePath = Path.Combine(_tempDir, "MES_1.log");

        var sb = new StringBuilder(5 * 1024 * 1024);

        // Place an early panel near byte 0 so full reverse-chunk traversal hits both 4 MB chunks.
        AppendMesEnvelope(sb, "TL699000A001", ng: true, index: 0);

        for (int i = 1; i <= 7500; i++)
        {
            AppendMesEnvelope(sb, $"TL699{(i % 900):D3}B{(i % 10):D1}X", ng: (i % 15) == 0, index: i);
        }

        // Place a recent panel near EOF for warm cache hit benchmarking.
        AppendMesEnvelope(sb, "TL699060A0AB", ng: true, index: 9999);

        File.WriteAllText(_mesFilePath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // Pre-warm the hot lookup cache
        _ = _warmService.FindMesPanelDetails(_mesFilePath, "TL699060A0AB");
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

    private static void AppendMesEnvelope(StringBuilder sb, string panelName, bool ng, int index)
    {
        string res = ng ? "NG" : "OK";
        string whiteLv = ng ? "294.500" : "318.200";
        int minute = index % 60;
        int sec = index % 60;

        sb.Append(CultureInfo.InvariantCulture,
            $"""
            [2026-11-06 15:{minute:D2}:{sec:D2}.100] [info] [GovionLogger.cpp:110] SEND MSG:
            <MESSAGE>
                <HEADER><MESSAGENAME>PanelTestResultRequest</MESSAGENAME></HEADER>
                <BODY>
                    <PANELNAME>{panelName}</PANELNAME>
                    <TESTRESULT>{res}</TESTRESULT>
                    <ROILOCATION>1,2713,15469,8700</ROILOCATION>
                    <DEFECTCODES>[MDBTD]</DEFECTCODES>
                    <OTPDEFECTCODE>[OTP-XYLV-{res}|OTP-FLICKER-OK]</OTPDEFECTCODE>
                    <OTPDEFECTVALUE>[White_lv={whiteLv},whitex=0.311,whitey=0.329,cct=6500,opticsresult={res}|flk=2.400,afresult=OK]</OTPDEFECTVALUE>
                </BODY>
            </MESSAGE>

            """);
    }

    [Benchmark(Baseline = true)]
    public int FindMesPanelDetails_ColdFullReverseScan()
    {
        _coldService.Cache.Clear();
        IReadOnlyList<MesPanelDetailRecord> records = _coldService.FindMesPanelDetails(_mesFilePath, "TL699000A001");
        return records.Count;
    }

    [Benchmark]
    public int FindMesPanelDetails_WarmCacheHit()
    {
        IReadOnlyList<MesPanelDetailRecord> records = _warmService.FindMesPanelDetails(_mesFilePath, "TL699060A0AB");
        return records.Count;
    }

    [Benchmark]
    public int ExtractRecentPanelRecords_Cold4MbScan()
    {
        _coldService.Cache.Clear();
        IReadOnlyList<MesPanelDetailRecord> records = _coldService.ExtractRecentPanelRecords(_mesFilePath, maxBytes: 4 * 1024 * 1024);
        return records.Count;
    }
}
