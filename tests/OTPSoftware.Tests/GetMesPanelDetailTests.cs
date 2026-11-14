using System.Text;
using OTPSoftware.Core.Models;
using OTPSoftware.Core.Services;
using Xunit;

namespace OTPSoftware.Tests;

public sealed class GetMesPanelDetailTests : IDisposable
{
    private readonly string _tempDir;
    private readonly MesDetailService _service;

    public GetMesPanelDetailTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GetMesPanelDetailTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _service = new MesDetailService();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void ConvertFromOtpDefectDetail_SeparatesGroupsAndParameters()
    {
        string code = "[OTP-XYLV-OK|OTP-FLICKER-OK]";
        string val = "[White_lv=315.900,whitex=0.311,opticsresult=OK|flk=2.300,afresult=OK,result=OK]";

        var rows = _service.ConvertFromOtpDefectDetail(code, val);
        Assert.Equal(6, rows.Count);

        Assert.Equal("OTP-XYLV", rows[0].DefectCodeGroup);
        Assert.Equal("White_lv", rows[0].ParamName);
        Assert.Equal("315.900", rows[0].ParamValue);
        Assert.Equal(">= 300.000", rows[0].Spec);
        Assert.Equal("OK", rows[0].StatusText);

        Assert.Equal("OTP-FLICKER", rows[3].DefectCodeGroup);
        Assert.Equal("flk", rows[3].ParamName);
        Assert.Equal("2.300", rows[3].ParamValue);
        Assert.Equal("0.0 ~ 30.0", rows[3].Spec);
        Assert.Equal("OK", rows[3].StatusText);
    }

    [Fact]
    public void ConvertFromOtpDefectDetail_MarksNgForGroupSuffixAndParameterNg()
    {
        string code = "[OTP-XYLV-NG]";
        string val = "[White_lv=298.300,opticsresult=NG]";

        var rows = _service.ConvertFromOtpDefectDetail(code, val);
        Assert.Equal(2, rows.Count);
        Assert.Equal("OTP-XYLV", rows[0].DefectCodeGroup);
        Assert.Equal("NG", rows[0].StatusText);
        Assert.Equal("opticsresult", rows[1].ParamName);
        Assert.Equal("NG", rows[1].ParamValue);
        Assert.Equal("NG", rows[1].StatusText);
    }

    [Fact]
    public void ConvertFromOtpDefectDetail_EvaluatesNumericParametersAgainstSpecifications()
    {
        string code = "[OTP-XYLV-OK|OTP-FLICKER-OK]";
        string val = "[White_lv=290.000,whitex=0.350,whitey=0.320,cct=5200.0,Red_LV=64.180|flk=35.000,afresult=OK]";

        var rows = _service.ConvertFromOtpDefectDetail(code, val);

        // White_lv < 300 -> NG
        Assert.Equal("White_lv", rows[0].ParamName);
        Assert.Equal(">= 300.000", rows[0].Spec);
        Assert.Equal("NG", rows[0].StatusText);

        // whitex > 0.338 -> NG
        Assert.Equal("whitex", rows[1].ParamName);
        Assert.Equal("0.288 ~ 0.338", rows[1].Spec);
        Assert.Equal("NG", rows[1].StatusText);

        // whitey in range 0.304 ~ 0.354 -> OK
        Assert.Equal("whitey", rows[2].ParamName);
        Assert.Equal("0.304 ~ 0.354", rows[2].Spec);
        Assert.Equal("OK", rows[2].StatusText);

        // cct < 5500 -> NG
        Assert.Equal("cct", rows[3].ParamName);
        Assert.Equal("5500 ~ 8000", rows[3].Spec);
        Assert.Equal("NG", rows[3].StatusText);

        // Red_LV is informational reference -> OK, Spec Ref
        Assert.Equal("Red_LV", rows[4].ParamName);
        Assert.Equal("Ref", rows[4].Spec);
        Assert.Equal("OK", rows[4].StatusText);

        // flk > 30 -> NG
        Assert.Equal("flk", rows[5].ParamName);
        Assert.Equal("0.0 ~ 30.0", rows[5].Spec);
        Assert.Equal("NG", rows[5].StatusText);
    }

    [Fact]
    public void ConvertFromOtpDefectDetail_ReturnsEmptyRowWhenEmptyBrackets()
    {
        var rows = _service.ConvertFromOtpDefectDetail("[]", "[]");
        Assert.Single(rows);
        Assert.Equal("[]", rows[0].ParamValue);
        Assert.Equal("-", rows[0].Spec);
        Assert.Equal("N/A", rows[0].StatusText);
    }

    [Fact]
    public void FindMesPanelDetails_ExtractsBodyAndMergesAdjacentSendRecv()
    {
        string mesFile = Path.Combine(_tempDir, "MES_1.log");
        string sample = """
        [2026-11-06 15:55:23.000] [info] [GovionLogger.cpp:110] SEND MSG:
        <MESSAGE>
        	<HEADER>
        		<MESSAGENAME>PanelTestResultRequest</MESSAGENAME>
        	</HEADER>
        	<BODY>
        		<PANELNAME>TL699060A0AB</PANELNAME>
        		<TESTRESULT>NG</TESTRESULT>
        		<ROILOCATION>1,2713,15469,8700</ROILOCATION>
        		<DEFECTCODES>[MDBTD]</DEFECTCODES>
        		<OTPDEFECTCODE>[OTP-XYLV-NG]</OTPDEFECTCODE>
        		<OTPDEFECTVALUE>[White_lv=298.300,whitex=0.310,opticsresult=NG]</OTPDEFECTVALUE>
        	</BODY>
        </MESSAGE>

        [2026-11-06 15:55:23.558] [info] [GovionLogger.cpp:110] RECV MSG:
        {xmlData="<?xml version="1.0" encoding="UTF-8"?>
        <MESSAGE>
            <BODY>
                <PANELNAME>TL699060A0AB</PANELNAME>
                <TESTRESULT>NG</TESTRESULT>
                <ROILOCATION>1,2713,15469,8700</ROILOCATION>
                <DEFECTCODES>[MDBTD]</DEFECTCODES>
                <OTPDEFECTCODE>[OTP-XYLV-NG]</OTPDEFECTCODE>
                <OTPDEFECTVALUE>[White_lv=298.300,whitex=0.310,opticsresult=NG]</OTPDEFECTVALUE>
                <TRANSACTIONSTARTTIME>2026-11-06 16:00:32.143</TRANSACTIONSTARTTIME>
            </BODY>
        </MESSAGE>
        "}
        """;
        File.WriteAllText(mesFile, sample, new UTF8Encoding(false));

        var found = _service.FindMesPanelDetails(mesFile, "TL699060A0AB");
        Assert.Single(found);
        Assert.Equal("TL699060A0AB", found[0].PanelName);
        Assert.Equal("NG", found[0].TestResult);
        Assert.Equal("Successful", found[0].MesResult);
        Assert.Equal("[OTP-XYLV-NG]", found[0].OtpDefectCode);
        Assert.Equal("[White_lv=298.300,whitex=0.310,opticsresult=NG]", found[0].OtpDefectValue);
        Assert.Equal(3, found[0].Parameters.Count);
        Assert.Equal("NG", found[0].Parameters[2].ParamValue);
    }

    [Fact]
    public void FindMesPanelDetails_FallsBackToRotatedLogsWhenPanelNotInActive()
    {
        string rotDir = Path.Combine(_tempDir, "rot_test");
        Directory.CreateDirectory(rotDir);

        string mesActive = Path.Combine(rotDir, "MES_1.log");
        string mesRot1 = Path.Combine(rotDir, "MES_1.1.log");
        string mesRot2 = Path.Combine(rotDir, "MES_1.2.log");

        File.WriteAllText(mesActive, "[2026-11-09 12:00:00.000] [info] Active log without target\r\n");
        File.WriteAllText(mesRot1, "[2026-11-09 10:00:00.000] [info] Rotated 1 without target\r\n");

        string rot2Content = """
        [2026-11-08 09:15:00.000] [info] [GovionLogger.cpp:110] SEND MSG:
        <MESSAGE>
        	<BODY>
        		<PANELNAME>TL688888R2AA</PANELNAME>
        		<TESTRESULT>OK</TESTRESULT>
        		<OTPDEFECTCODE>[OTP-XYLV-OK|OTP-FLICKER-OK]</OTPDEFECTCODE>
        		<OTPDEFECTVALUE>[White_lv=340.100,opticsresult=OK|flk=1.800,afresult=OK]</OTPDEFECTVALUE>
        	</BODY>
        </MESSAGE>
        """;
        File.WriteAllText(mesRot2, rot2Content, new UTF8Encoding(false));

        var found = _service.FindMesPanelDetails(mesActive, "TL688888R2AA");
        Assert.Single(found);
        Assert.Equal("TL688888R2AA", found[0].PanelName);
        Assert.Equal(mesRot2, found[0].SourceFile);
        Assert.Equal("[OTP-XYLV-OK|OTP-FLICKER-OK]", found[0].OtpDefectCode);
    }

    [Fact]
    public void FindMesPanelDetails_ReadsLargeFilesInReverseChunksWithoutLosingRecords()
    {
        string chunkDir = Path.Combine(_tempDir, "chunk_test");
        Directory.CreateDirectory(chunkDir);
        string mesChunkFile = Path.Combine(chunkDir, "MES_1.log");

        string paddingLine = "[2026-11-09 08:00:00.000] [info] [GovionLogger.cpp:110] Filler padding line for chunk test 0123456789ABCDEF\r\n";
        var padBuilder = new StringBuilder();
        for (int i = 0; i < 450; i++) padBuilder.Append(paddingLine); // ~48KB per block
        string padBlock = padBuilder.ToString();

        string run1 = """
        [2026-11-09 08:10:00.000] [info] [GovionLogger.cpp:110] SEND MSG:
        <MESSAGE>
        	<BODY>
        		<PANELNAME>TL677777C0AA</PANELNAME>
        		<TESTRESULT>NG</TESTRESULT>
        		<OTPDEFECTCODE>[OTP-XYLV-NG]</OTPDEFECTCODE>
        		<OTPDEFECTVALUE>[White_lv=250.000,opticsresult=NG]</OTPDEFECTVALUE>
        	</BODY>
        </MESSAGE>
        """;

        string run2 = """
        [2026-11-09 08:20:00.000] [info] [GovionLogger.cpp:110] SEND MSG:
        <MESSAGE>
        	<BODY>
        		<PANELNAME>TL677777C0AA</PANELNAME>
        		<TESTRESULT>OK</TESTRESULT>
        		<OTPDEFECTCODE>[OTP-XYLV-OK]</OTPDEFECTCODE>
        		<OTPDEFECTVALUE>[White_lv=330.000,opticsresult=OK]</OTPDEFECTVALUE>
        	</BODY>
        </MESSAGE>
        """;

        var midBuilder = new StringBuilder();
        for (int i = 0; i < 200; i++) midBuilder.Append(paddingLine);

        string fullText = padBlock + "\r\n" + run1 + "\r\n" + midBuilder + "\r\n" + run2 + "\r\n" + padBlock;
        File.WriteAllText(mesChunkFile, fullText, new UTF8Encoding(false));

        var found = _service.FindMesPanelDetails(mesChunkFile, "TL677777C0AA", chunkBytes: 64 * 1024);
        Assert.Equal(2, found.Count);
        Assert.Equal("NG", found[0].TestResult);
        Assert.Equal("2026-11-09 08:10:00.000", found[0].LogTime);
        Assert.Equal("OK", found[1].TestResult);
        Assert.Equal("2026-11-09 08:20:00.000", found[1].LogTime);
    }

    [Fact]
    public void FindMesPanelDetails_InvalidatesCacheAutomaticallyWhenFileUpdated()
    {
        string cacheDir = Path.Combine(_tempDir, "cache_test");
        Directory.CreateDirectory(cacheDir);
        string mesCacheFile = Path.Combine(cacheDir, "MES_1.log");

        string initialContent = """
        [2026-11-09 09:00:00.000] [info] [GovionLogger.cpp:110] SEND MSG:
        <MESSAGE>
        	<BODY>
        		<PANELNAME>TL655555K0AA</PANELNAME>
        		<TESTRESULT>NG</TESTRESULT>
        		<OTPDEFECTCODE>[OTP-FLICKER-NG]</OTPDEFECTCODE>
        		<OTPDEFECTVALUE>[flk=8.500,afresult=NG]</OTPDEFECTVALUE>
        	</BODY>
        </MESSAGE>
        """;
        File.WriteAllText(mesCacheFile, initialContent, new UTF8Encoding(false));

        var first = _service.FindMesPanelDetails(mesCacheFile, "TL655555K0AA");
        Assert.Single(first);

        var initialMtime = File.GetLastWriteTimeUtc(mesCacheFile);

        string appendedContent = initialContent + "\r\n" + """
        [2026-11-09 09:05:00.000] [info] [GovionLogger.cpp:110] SEND MSG:
        <MESSAGE>
        	<BODY>
        		<PANELNAME>TL655555K0AA</PANELNAME>
        		<TESTRESULT>OK</TESTRESULT>
        		<OTPDEFECTCODE>[OTP-FLICKER-OK]</OTPDEFECTCODE>
        		<OTPDEFECTVALUE>[flk=2.100,afresult=OK]</OTPDEFECTVALUE>
        	</BODY>
        </MESSAGE>
        """;
        File.WriteAllText(mesCacheFile, appendedContent, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(mesCacheFile, initialMtime.AddSeconds(2));

        var second = _service.FindMesPanelDetails(mesCacheFile, "TL655555K0AA");
        Assert.Equal(2, second.Count);
        Assert.Equal("OK", second[1].TestResult);
    }

    [Fact]
    public void EvaluateParameter_NonFiniteNumbers_EvaluatedAsNg()
    {
        Assert.Equal("NG", _service.EvaluateParameter("White_lv", "NaN").StatusText);
        Assert.Equal("NG", _service.EvaluateParameter("White_lv", "Infinity").StatusText);
        Assert.Equal("NG", _service.EvaluateParameter("whitex", "-Infinity").StatusText);
        Assert.Equal("NG", _service.EvaluateParameter("contrast", "NaN").StatusText);
        Assert.Equal("NG", _service.EvaluateParameter("ntsc", "Infinity").StatusText);
        Assert.Equal("OK", _service.EvaluateParameter("White_lv", "320.5").StatusText);
    }

    [Fact]
    public void EvaluateParameter_CustomMesSpecsLoadedFromSettings_OverridesTargetedParameterWhilePreservingDefaults()
    {
        string cfgDir = Path.Combine(_tempDir, "mes_cfg");
        string configSubDir = Path.Combine(cfgDir, "config");
        Directory.CreateDirectory(configSubDir);
        string settingsJson = """
        {
          "mesSpecs": {
            "White_lv": { "min": 350.0 },
            "contrast": { "min": 1200.0 }
          }
        }
        """;
        File.WriteAllText(Path.Combine(configSubDir, "settings.json"), settingsJson, new UTF8Encoding(false));

        var loadedSettings = new SettingsService().LoadSettings(cfgDir);
        Assert.Null(loadedSettings.LoadError);
        Assert.NotNull(loadedSettings.MesSpecs);

        var customSvc = new MesDetailService(loadedSettings.MesSpecs);

        // Overridden thresholds enforce stricter limits
        var whiteEval = customSvc.EvaluateParameter("White_lv", "320.0");
        Assert.Equal("NG", whiteEval.StatusText);
        Assert.Equal(">= 350", whiteEval.Spec);

        var contrastEval = customSvc.EvaluateParameter("contrast", "1100");
        Assert.Equal("NG", contrastEval.StatusText);
        Assert.Equal(">= 1200", contrastEval.Spec);

        // Unspecified parameters fall back cleanly to built-in optical defaults
        var whiteXEval = customSvc.EvaluateParameter("whitex", "0.310");
        Assert.Equal("OK", whiteXEval.StatusText);
        Assert.Equal("0.288 ~ 0.338", whiteXEval.Spec);
    }

    [Fact]
    public void EmptyMesFile_ZeroBytes_ReturnsEmptyWithoutError()
    {
        // Arrange
        string mesFile = Path.Combine(_tempDir, "MES_1.log");
        File.WriteAllText(mesFile, string.Empty);

        // Act
        var found = _service.FindMesPanelDetails(mesFile, "TL699060A0AB");

        // Assert
        Assert.Empty(found);
    }

    [Fact]
    public void ReverseSearch_RecordStraddlingChunkBoundary_IsNotDropped()
    {
        // Arrange
        string chunkDir = Path.Combine(_tempDir, "boundary_test");
        Directory.CreateDirectory(chunkDir);
        string mesFile = Path.Combine(chunkDir, "MES_1.log");

        const int targetFileSize = 100 * 1024; // 102,400 bytes
        const int chunkSize = 64 * 1024;       // 65,536 bytes
        const int headGuardBytes = 2048;

        // When reading backwards, last chunk starts at fileLen - chunkSize = 36,864
        // headGuard region is [36,864, 38,912). We place Record 1 at byte ~37,500.
        int chunkStart = targetFileSize - chunkSize;
        int targetRecord1Pos = chunkStart + 600;

        string paddingLine = "[2026-11-09 08:00:00.000] [info] [GovionLogger.cpp:110] Filler boundary line 0123456789ABCDEF\r\n";
        int padLineBytes = Encoding.UTF8.GetByteCount(paddingLine);

        var sb = new StringBuilder();
        while (Encoding.UTF8.GetByteCount(sb.ToString()) + padLineBytes < targetRecord1Pos - 80)
        {
            sb.Append(paddingLine);
        }

        int currentBytes = Encoding.UTF8.GetByteCount(sb.ToString());
        int needed = (targetRecord1Pos - 80) - currentBytes;
        if (needed > 0)
        {
            sb.Append("// ").Append(new string('X', Math.Max(0, needed - 5))).Append("\r\n");
        }

        string record1 = """
        [2026-11-09 08:15:00.000] [info] [GovionLogger.cpp:110] SEND MSG:
        <MESSAGE>
        	<BODY>
        		<PANELNAME>TL677777S0AA</PANELNAME>
        		<TESTRESULT>NG</TESTRESULT>
        		<OTPDEFECTCODE>[OTP-XYLV-NG]</OTPDEFECTCODE>
        		<OTPDEFECTVALUE>[White_lv=240.000,opticsresult=NG]</OTPDEFECTVALUE>
        	</BODY>
        </MESSAGE>
        """;
        sb.Append(record1).Append("\r\n");

        while (Encoding.UTF8.GetByteCount(sb.ToString()) < 95 * 1024)
        {
            sb.Append(paddingLine);
        }

        string record2 = """
        [2026-11-09 08:30:00.000] [info] [GovionLogger.cpp:110] SEND MSG:
        <MESSAGE>
        	<BODY>
        		<PANELNAME>TL677777S0AA</PANELNAME>
        		<TESTRESULT>OK</TESTRESULT>
        		<OTPDEFECTCODE>[OTP-XYLV-OK]</OTPDEFECTCODE>
        		<OTPDEFECTVALUE>[White_lv=320.000,opticsresult=OK]</OTPDEFECTVALUE>
        	</BODY>
        </MESSAGE>
        """;
        sb.Append(record2).Append("\r\n");

        while (Encoding.UTF8.GetByteCount(sb.ToString()) < targetFileSize)
        {
            sb.Append(paddingLine);
        }

        string fullContent = sb.ToString();
        File.WriteAllText(mesFile, fullContent, new UTF8Encoding(false));

        long fileLen = new FileInfo(mesFile).Length;
        Assert.True(fileLen >= targetFileSize);

        long effectiveChunk = 64 * 1024;
        long lastChunkStart = fileLen - effectiveChunk;
        int rec1TagIdx = fullContent.IndexOf("<PANELNAME>TL677777S0AA</PANELNAME>", StringComparison.Ordinal);
        int rec2TagIdx = fullContent.LastIndexOf("<PANELNAME>TL677777S0AA</PANELNAME>", StringComparison.Ordinal);

        Assert.True(rec1TagIdx >= lastChunkStart, $"Record 1 tag index {rec1TagIdx} should be >= lastChunkStart {lastChunkStart}");
        Assert.True(rec1TagIdx < lastChunkStart + headGuardBytes, $"Record 1 tag index {rec1TagIdx} should be < headGuard end {lastChunkStart + headGuardBytes}");
        Assert.True(rec2TagIdx > lastChunkStart + headGuardBytes, "Record 2 should be in valid zone of last chunk");

        // Act
        var found = _service.FindMesPanelDetails(mesFile, "TL677777S0AA", chunkBytes: 64 * 1024);

        // Assert
        Assert.Equal(2, found.Count);
        Assert.Equal("NG", found[0].TestResult);
        Assert.Equal("2026-11-09 08:15:00.000", found[0].LogTime);
        Assert.Equal("OK", found[1].TestResult);
        Assert.Equal("2026-11-09 08:30:00.000", found[1].LogTime);
    }
}
