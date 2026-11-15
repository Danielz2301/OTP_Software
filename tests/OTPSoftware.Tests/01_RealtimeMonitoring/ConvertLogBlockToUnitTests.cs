using OTPSoftware.Core.Services;
using Xunit;

namespace OTPSoftware.Tests;

public sealed class ConvertLogBlockToUnitTests
{
    private readonly LogParserService _parser = new();

    private static List<string> NewTestBlock(
        string pmnt = "PMNT1001A",
        string panel = "TL699041U0BF",
        string[]? errors = null,
        string time = "2026-11-06 14:32:36.123")
    {
        string prefix = $"[{time}] [info] [GovionLogger.cpp:110]";
        var lines = new List<string>();

        if (!string.IsNullOrEmpty(pmnt))
        {
            lines.Add($"{prefix} Register address:2010.0, read value:{pmnt}");
        }

        lines.Add($"{prefix} PANELNAME:{panel}");
        lines.Add($"{prefix} BLUNAME:38C2270000026");

        if (errors != null)
        {
            foreach (string e in errors)
            {
                lines.Add($"{prefix} OTP-{e} NG, reason");
            }
        }

        return lines;
    }

    [Fact]
    public void ExtractsPmntPanelNameBluNameAndTimestamp()
    {
        var block = NewTestBlock();
        var unit = _parser.ConvertLogBlockToUnit(block);

        Assert.Equal("PMNT1001A", unit.Pmnt);
        Assert.Equal("TL699041U0BF", unit.PanelName);
        Assert.Equal("38C2270000026", unit.BluName);
        Assert.NotNull(unit.Timestamp);
        Assert.Equal(2026, unit.Timestamp.Value.Year);
        Assert.Equal(11, unit.Timestamp.Value.Month);
        Assert.Equal(14, unit.Timestamp.Value.Hour);
        Assert.Empty(unit.Errors);
        Assert.False(unit.IsNg);
    }

    [Fact]
    public void RecordsEachNgErrorTypeOnce()
    {
        var block = NewTestBlock(errors: ["POWERON", "POWERON", "XYLV"]);
        var unit = _parser.ConvertLogBlockToUnit(block);

        Assert.Equal(2, unit.Errors.Count);
        Assert.Contains("POWERON", unit.Errors);
        Assert.Contains("XYLV", unit.Errors);
        Assert.True(unit.IsNg);
    }

    [Fact]
    public void ReturnsEmptyPmntAndZeroCountsWhenBlockHasNoPmnt()
    {
        var block = NewTestBlock(pmnt: string.Empty);
        var unit = _parser.ConvertLogBlockToUnit(block);

        Assert.Empty(unit.Pmnt);
        Assert.Equal(0, unit.PassCount);
        Assert.Equal(0, unit.TotalRuns);
    }

    [Fact]
    public void PureFunction_ResultsDoNotDependOnPreviousInvocationsAndAcceptExplicitIndex()
    {
        var u1 = _parser.ConvertLogBlockToUnit(NewTestBlock(), index: 0);
        var u2 = _parser.ConvertLogBlockToUnit(NewTestBlock(), index: 1);

        Assert.Equal(0, u1.Index);
        Assert.Equal(1, u2.Index);
        Assert.Equal(0, u1.PassCount);
        Assert.Equal(0, u2.PassCount);
    }

    [Fact]
    public void ParsesMesCommunicationStatuses()
    {
        const string prefix = "[2026-11-06 14:32:36.123] [info] [GovionLogger.cpp:110]";

        var okLines = NewTestBlock();
        okLines.Add($"{prefix} Query successful");
        okLines.Add($"{prefix} MES debt collection successful");
        Assert.Equal("Successful", _parser.ConvertLogBlockToUnit(okLines).MesResult);

        var qFailLines = NewTestBlock();
        qFailLines.Add($"{prefix} Query PMNT failed");
        Assert.Equal("Fail (Query PMNT)", _parser.ConvertLogBlockToUnit(qFailLines).MesResult);

        var dFailLines = NewTestBlock();
        dFailLines.Add($"{prefix} Query successful");
        dFailLines.Add($"{prefix} MES debt collection failed");
        Assert.Equal("Fail (MES Debt Collection)", _parser.ConvertLogBlockToUnit(dFailLines).MesResult);
    }

    [Fact]
    public void ReturnsDefaultRecordForEmptyBlock()
    {
        var emptyUnit = _parser.ConvertLogBlockToUnit([], index: 5, source: "MMAPI120");
        Assert.Equal(5, emptyUnit.Index);
        Assert.Equal("MMAPI120", emptyUnit.Source);
        Assert.Empty(emptyUnit.Pmnt);
        Assert.Empty(emptyUnit.Errors);
        Assert.Null(emptyUnit.Timestamp);
    }

    [Fact]
    public void OkUnits_ReuseSharedEmptyErrorsCollection()
    {
        var u1 = _parser.ConvertLogBlockToUnit(NewTestBlock(), index: 0);
        var u2 = _parser.ConvertLogBlockToUnit(NewTestBlock(), index: 1);

        Assert.Empty(u1.Errors);
        Assert.Same(u1.Errors, u2.Errors);
    }

    public static IEnumerable<object[]> MalformedLogBlocks()
    {
        yield return [Array.Empty<string>()];
        yield return [new[] { "" }];
        yield return [new[] { "\0\0\0\0\0\0\0" }];
        yield return [new[] { "2026-11-15 14:00:00 [NG] PMNT= ", "[GovionLogger.cpp:999] PANELNAME:", "OTP- " }];
        yield return [new[] { "9999-99-99 99:99:99.999 [GovionLogger.cpp:1] PANELNAME: [ONLY_OPEN", "BLUNAME: [[broken", "Query NG" }];
    }

    [Theory]
    [MemberData(nameof(MalformedLogBlocks))]
    public void ConvertLogBlockToUnit_GivenMalformedOrCorruptedLines_DoesNotThrow(string[] blockLines)
    {
        var ex = Record.Exception(() =>
        {
            var unit = _parser.ConvertLogBlockToUnit(blockLines, index: 42, source: "MMAPI120");
            Assert.NotNull(unit);
            Assert.NotNull(unit.Errors);
        });

        Assert.Null(ex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\0\0\0")]
    [InlineData("2026-99-99 25:61:61.999")]
    [InlineData("not-a-timestamp")]
    public void ParseLogDateTime_GivenInvalidTimestamps_ReturnsNullWithoutThrowing(string? input)
    {
        DateTime? parsed = null;
        var ex = Record.Exception(() => parsed = _parser.ParseLogDateTime(input, allowCurrentCulture: true));

        Assert.Null(ex);
        Assert.Null(parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\0\0\0\0")]
    [InlineData("<OTPDEFECTVALUE>[White_lv = ]</OTPDEFECTVALUE>")]
    [InlineData("<OTPDEFECTVALUE>[White_lv = NaN][contrast = Infinity][flk = ]</OTPDEFECTVALUE>")]
    [InlineData("<OTPDEFECTVALUE>[[White_lv : --abc-- ; ]|=")]
    public void TryExtractParameterFromDefectValue_GivenCorruptedPayloads_ReturnsFalseWithoutThrowing(string? payload)
    {
        bool ok = false;
        double value = double.NaN;
        var ex = Record.Exception(() =>
            ok = StatisticsService.TryExtractParameterFromDefectValue(
                payload.AsSpan(),
                "White_lv",
                ReadOnlySpan<char>.Empty,
                requirePositive: true,
                out value));

        Assert.Null(ex);
        Assert.False(ok);
        Assert.Equal(0d, value);
    }
}
