namespace OTPSoftware.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using OTPSoftware.Core.Extensions;
using OTPSoftware.Core.Models;
using OTPSoftware.Core.Services;
using Xunit;

public class TestUnitFilterTests
{
    private readonly UnitFilterService _filter = new();

    private static UnitRecord CreateUnit(
        string source = "MMAPI120",
        string pmnt = "PMNT1012A",
        string panel = "TL699041U0BF",
        IReadOnlyList<string>? errors = null,
        DateTime? time = null,
        bool explicitNullTime = false)
    {
        return new UnitRecord
        {
            Source = source,
            Pmnt = pmnt,
            PanelName = panel,
            Errors = errors ?? [],
            Timestamp = explicitNullTime ? null : (time ?? new DateTime(2026, 11, 6, 14, 30, 0))
        };
    }

    private static UnitFilterSpec CreateSpec(
        string sourceName = "",
        UnitStatusFilter status = UnitStatusFilter.All,
        string pmntPattern = "",
        string panelPattern = "",
        DateTime? start = null,
        DateTime? end = null)
    {
        return new UnitFilterSpec
        {
            SourceName = sourceName,
            Status = status,
            PmntPattern = pmntPattern,
            PanelPattern = panelPattern,
            StartTime = start,
            EndTime = end
        };
    }

    [Fact]
    public void TextContains_EmptyPatternAlwaysMatches()
    {
        Assert.True(_filter.TextContains("abc", ""));
        Assert.True(_filter.TextContains(null, null));
        Assert.True(_filter.TextContains("abc", null));
    }

    [Fact]
    public void TextContains_IsCaseInsensitive()
    {
        Assert.True(_filter.TextContains("PMNT1012A", "pmnt1012"));
    }

    [Fact]
    public void TextContains_MatchesSubstring()
    {
        Assert.True(_filter.TextContains("PMNT1012A", "PMNT101"));
        Assert.False(_filter.TextContains("PMNT1012A", "PMNT2"));
    }

    [Fact]
    public void TextContains_SpecialCharactersTreatedLiterallyWithoutRegexInterpretation()
    {
        Assert.False(_filter.TextContains("PMNT1012A", "["));
        Assert.False(_filter.TextContains("PMNT1012A", "PMNT10.2A"));
    }

    [Fact]
    public void TextContains_EmptyTextDoesNotMatchNonEmptyPattern()
    {
        Assert.False(_filter.TextContains("", "x"));
        Assert.False(_filter.TextContains(null, "x"));
    }

    [Fact]
    public void UnitInScope_FiltersByMachineSource()
    {
        var u = CreateUnit(source: "MMAPI110");
        Assert.True(_filter.IsUnitInScope(u, CreateSpec(sourceName: "0")));
        Assert.False(_filter.IsUnitInScope(u, CreateSpec(sourceName: "1")));
        Assert.True(_filter.IsUnitInScope(u, CreateSpec(sourceName: "2")));

        var u3 = CreateUnit(source: "MMAPI130");
        Assert.True(_filter.IsUnitInScope(u3, CreateSpec(sourceName: "")));
        Assert.True(_filter.IsUnitInScope(u3, CreateSpec(sourceName: "MMAPI130")));
        Assert.False(_filter.IsUnitInScope(u3, CreateSpec(sourceName: "MMAPI120")));
    }

    [Fact]
    public void UnitInScope_FiltersByPmntAndPanel()
    {
        var u = CreateUnit();
        Assert.True(_filter.IsUnitInScope(u, CreateSpec(pmntPattern: "1012")));
        Assert.False(_filter.IsUnitInScope(u, CreateSpec(pmntPattern: "1012", panelPattern: "ZZZ")));
        Assert.True(_filter.IsUnitInScope(u, CreateSpec(pmntPattern: "1012", panelPattern: "u0bf")));
    }

    [Fact]
    public void UnitInScope_FiltersByTimeRangeInclusive()
    {
        var u = CreateUnit();
        var start = new DateTime(2026, 11, 6, 14, 30, 0);
        var end = new DateTime(2026, 11, 6, 14, 30, 0);

        Assert.True(_filter.IsUnitInScope(u, CreateSpec(start: start, end: end)));
        Assert.False(_filter.IsUnitInScope(u, CreateSpec(start: new DateTime(2026, 11, 6, 15, 0, 0))));
        Assert.False(_filter.IsUnitInScope(u, CreateSpec(end: new DateTime(2026, 11, 6, 14, 0, 0))));
    }

    [Fact]
    public void UnitInScope_RecordsWithoutTimestampNotExcludedByTimeFilter()
    {
        var u = CreateUnit(explicitNullTime: true);
        Assert.True(_filter.IsUnitInScope(u, CreateSpec(start: new DateTime(2030, 1, 1))));
    }

    [Fact]
    public void UnitDisplayed_FiltersByStatus()
    {
        var ok = CreateUnit();
        var ng = CreateUnit(errors: ["XYLV"]);

        Assert.True(_filter.IsUnitDisplayed(ok, CreateSpec(status: UnitStatusFilter.All)));
        Assert.True(_filter.IsUnitDisplayed(ng, CreateSpec(status: UnitStatusFilter.All)));
        Assert.False(_filter.IsUnitDisplayed(ok, CreateSpec(status: UnitStatusFilter.NgOnly)));
        Assert.True(_filter.IsUnitDisplayed(ng, CreateSpec(status: UnitStatusFilter.NgOnly)));
        Assert.True(_filter.IsUnitDisplayed(ok, CreateSpec(status: UnitStatusFilter.OkOnly)));
        Assert.False(_filter.IsUnitDisplayed(ng, CreateSpec(status: UnitStatusFilter.OkOnly)));
    }

    [Fact]
    public void ExtensionMethods_PerformFilteringCorrectly()
    {
        var ok = CreateUnit(pmnt: "PMNT1001A");
        var ng = CreateUnit(pmnt: "PMNT1002B", errors: ["XYLV"]);
        var spec = CreateSpec(pmntPattern: "1002", status: UnitStatusFilter.NgOnly);

        Assert.False(ok.IsInScope(spec));
        Assert.True(ng.IsInScope(spec));
        Assert.False(ok.IsDisplayed(spec));
        Assert.True(ng.IsDisplayed(spec));

        var list = new List<UnitRecord> { ok, ng };
        var inScope = list.WhereInScope(spec).ToList();
        Assert.Single(inScope);
        Assert.Equal("PMNT1002B", inScope[0].Pmnt);

        var displayed = list.WhereDisplayed(spec).ToList();
        Assert.Single(displayed);
        Assert.Equal("PMNT1002B", displayed[0].Pmnt);
    }

    [Fact]
    public void WhereDisplayed_ExcludesNgUnitOutsideScope()
    {
        var inScopeNg = CreateUnit(pmnt: "PMNT1002B", errors: ["XYLV"]);
        var outOfScopeNg = CreateUnit(pmnt: "PMNT1001A", errors: ["XYLV"]);
        var spec = CreateSpec(pmntPattern: "1002", status: UnitStatusFilter.NgOnly);

        var displayed = new[] { inScopeNg, outOfScopeNg }.WhereDisplayed(spec).ToList();

        Assert.Single(displayed);
        Assert.Equal("PMNT1002B", displayed[0].Pmnt);
    }

    [Fact]
    public void UnitInScope_CustomMachinesList_ResolvesOrdinalIndexesDynamicallyAndAssignsPaletteColors()
    {
        var machines = new List<MachineConfig>
        {
            new() { Name = "LINE_A" },
            new() { Name = "LINE_B" },
            new() { Name = "LINE_C" }
        };
        var nFilter = new UnitFilterService(machines);

        var unitA = CreateUnit(source: "LINE_A");
        var unitC = CreateUnit(source: "LINE_C");

        Assert.True(nFilter.IsUnitInScope(unitC, CreateSpec(sourceName: "3")));
        Assert.False(nFilter.IsUnitInScope(unitA, CreateSpec(sourceName: "3")));
        Assert.True(nFilter.IsUnitInScope(unitC, CreateSpec(sourceName: "LINE_C")));

        // Verify dynamic palette resolves at least 8 distinct corporate color pairs across N machines
        int distinctColors = Enumerable.Range(1, 40)
            .Select(i => OTPSoftware.Core.Display.GuiDisplayState.GetMachineVisualStyle($"LINE_{i}"))
            .Distinct()
            .Count();
        Assert.True(distinctColors >= 8);
    }
}
