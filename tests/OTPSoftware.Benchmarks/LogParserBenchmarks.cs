using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using OTPSoftware.Core.Models;
using OTPSoftware.Core.Services;

namespace OTPSoftware.Benchmarks;

/// <summary>
/// Micro-benchmarks comparing <see cref="LogParserService"/> Span/SearchValues parsing
/// against naive string splitting baselines across realistic inspection blocks and noise streams.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[RankColumn]
public class LogParserBenchmarks
{
    private readonly LogParserService _parser = new();
    private List<string> _mixedBlock = null!;
    private List<string> _noiseOnlyBlock = null!;

    [GlobalSetup]
    public void Setup()
    {
        const string prefix = "[2026-11-06 14:32:36.123] [info] [GovionLogger.cpp:110]";
        const string noisePrefix = "[2026-11-06 14:32:36.124] [debug] [MotionCtrl.cpp:88]";

        _mixedBlock = new List<string>(1000)
        {
            $"{prefix} Register address:2010.0, read value:PMNT1001A",
            $"{prefix} PANELNAME:TL699041U0BF",
            $"{prefix} BLUNAME:38C2270000026"
        };

        for (int i = 0; i < 995; i++)
        {
            _mixedBlock.Add($"{noisePrefix} Axis polling idle state=0 pos=12.450 vel=0.000 seq={i}");
        }

        _mixedBlock.Add($"{prefix} OTP-XYLV NG, White_lv=298.300 below spec");
        _mixedBlock.Add($"{prefix} MES debt collection OK");

        _noiseOnlyBlock = new List<string>(1000);
        for (int i = 0; i < 1000; i++)
        {
            _noiseOnlyBlock.Add($"{noisePrefix} Camera exposure telemetry heartbeat frame={i} status=READY");
        }
    }

    [Benchmark(Baseline = true)]
    public int NaiveSplitParse_MixedBlock()
    {
        string pmnt = string.Empty;
        string panel = string.Empty;
        string blu = string.Empty;
        int defects = 0;

        foreach (string line in _mixedBlock)
        {
            string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i];
                if (token.Contains("read value:PMNT", StringComparison.Ordinal))
                {
                    pmnt = token;
                }
                else if (token.StartsWith("PANELNAME:", StringComparison.Ordinal))
                {
                    panel = token.Substring(10);
                }
                else if (token.StartsWith("BLUNAME:", StringComparison.Ordinal))
                {
                    blu = token.Substring(8);
                }
                else if (token.StartsWith("OTP-", StringComparison.Ordinal))
                {
                    defects++;
                }
            }
        }

        return pmnt.Length + panel.Length + blu.Length + defects;
    }

    [Benchmark]
    public UnitRecord ConvertLogBlockToUnit_MixedBlock() =>
        _parser.ConvertLogBlockToUnit(_mixedBlock, index: 1, source: "OTP_1");

    [Benchmark]
    public UnitRecord ConvertLogBlockToUnit_NoiseOnlyBlock() =>
        _parser.ConvertLogBlockToUnit(_noiseOnlyBlock, index: 2, source: "OTP_1");
}
