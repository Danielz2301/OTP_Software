namespace OTPSoftware.Tests;

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using OTPSoftware.Core.Display;
using OTPSoftware.Core.Models;
using Xunit;

public class DisplayStateTests
{
    private static UnitRecord NewTestUnitItem(
        string source = "MMAPI120",
        string pmnt = "PMNT1001A",
        string panel = "TL699041U0BF",
        IReadOnlyList<string>? errors = null,
        DateTime? time = null)
    {
        return new UnitRecord
        {
            Source = source,
            Pmnt = pmnt,
            PanelName = panel,
            BluName = "BLU001",
            Errors = errors ?? [],
            Timestamp = time ?? new DateTime(2026, 11, 6, 14, 0, 0),
            PassCount = 0,
            TotalRuns = 0
        };
    }

    private static UnitFilterSpec NewFilterSpec(
        string source = "0",
        UnitStatusFilter status = UnitStatusFilter.All,
        string pmnt = "",
        string panel = "",
        DateTime? start = null,
        DateTime? end = null)
    {
        return new UnitFilterSpec
        {
            SourceName = source,
            Status = status,
            PmntPattern = pmnt,
            PanelPattern = panel,
            StartTime = start,
            EndTime = end
        };
    }

    [Fact]
    public void CalculatesRunDisplaySeparatelyPerMachine_SourcePlusPmnt()
    {
        var st = new GuiDisplayState();
        st.AllUnits.Add(NewTestUnitItem(source: "MMAPI120", pmnt: "PMNT1001A"));
        st.AllUnits.Add(NewTestUnitItem(source: "MMAPI110", pmnt: "PMNT1001A"));
        st.AllUnits.Add(NewTestUnitItem(source: "MMAPI120", pmnt: "PMNT1001A"));

        st.Update(NewFilterSpec());

        Assert.Equal(3, st.DisplayList.Count);
        Assert.Equal("01/02", st.DisplayList[0].RunDisplay);
        Assert.Equal("01/01", st.DisplayList[1].RunDisplay);
        Assert.Equal("02/02", st.DisplayList[2].RunDisplay);
    }

    [Fact]
    public void IncrementalUpdate_AddingNewRecordWithSamePmntUpdatesTotalOnExistingRows()
    {
        var st = new GuiDisplayState();
        var spec = NewFilterSpec();
        st.AllUnits.Add(NewTestUnitItem(source: "MMAPI120", pmnt: "PMNT1001A"));
        st.Update(spec);
        Assert.Equal("01/01", st.DisplayList[0].RunDisplay);

        var changedProps = new List<string?>();
        st.DisplayList[0].PropertyChanged += (_, e) => changedProps.Add(e.PropertyName);

        // Add 2nd record in Real-time mode (same filter Signature)
        st.AllUnits.Add(NewTestUnitItem(source: "MMAPI120", pmnt: "PMNT1001A"));
        bool changed = st.Update(spec);

        Assert.True(changed);
        Assert.Equal(2, st.DisplayList.Count);
        Assert.Equal("01/02", st.DisplayList[0].RunDisplay);
        Assert.Equal("02/02", st.DisplayList[1].RunDisplay);
        Assert.Contains(nameof(DisplayRowViewModel.TotalRuns), changedProps);
        Assert.Contains(nameof(DisplayRowViewModel.RunDisplay), changedProps);
    }

    [Fact]
    public void WhenFilteringNgOnly_TotalRunsStillCalculatesOverAllTestsInScope()
    {
        var st = new GuiDisplayState();
        st.AllUnits.Add(NewTestUnitItem(source: "MMAPI120", pmnt: "PMNT1001A", errors: []));
        st.AllUnits.Add(NewTestUnitItem(source: "MMAPI120", pmnt: "PMNT1001A", errors: ["XYLV"]));

        st.Update(NewFilterSpec(status: UnitStatusFilter.NgOnly));

        Assert.Equal(2, st.ScopeUnits.Count);
        Assert.Single(st.ShownUnits);
        Assert.Equal("02/02", st.DisplayList[0].RunDisplay);
    }

    [Fact]
    public void RetrievesBadgeColorsFromMachineConfigurationWhenAvailable()
    {
        var settings = new Dictionary<string, MachineConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["MMAPI130"] = new MachineConfig
            {
                Name = "MMAPI130",
                Color = "#55EFC4",
                BgColor = "#1E463A"
            }
        };

        var item = GuiDisplayState.NewDisplayItem(1, NewTestUnitItem(source: "MMAPI130"), settings);

        Assert.Equal("MMAPI130", item.Source);
        Assert.Equal("#55EFC4", item.SourceFg);
        Assert.Equal("#1E463A", item.SourceBg);
    }

    [Fact]
    public void WhenFilteringBySinglePmnt_BaseScopeUnitsRetainsAllPmntsSoFaultyPmntTabDoesNotLoseList()
    {
        var st = new GuiDisplayState();
        st.AllUnits.Add(NewTestUnitItem(source: "MMAPI120", pmnt: "PMNT1001A", errors: ["XYLV"]));
        st.AllUnits.Add(NewTestUnitItem(source: "MMAPI120", pmnt: "PMNT1023B", errors: ["XYLV"]));

        st.Update(NewFilterSpec(pmnt: "PMNT1023B"));

        Assert.Single(st.ScopeUnits);
        Assert.Equal(2, st.BaseScopeUnits.Count);
    }

    [Fact]
    public void TracksConsecutiveNgAcrossRebuildAndIncrementalUpdatesAndResetsOnOk()
    {
        var st = new GuiDisplayState();
        var spec = NewFilterSpec();
        var u1 = NewTestUnitItem(source: "MMAPI120", pmnt: "PMNT1001A", errors: ["XYLV"]);
        var u2 = NewTestUnitItem(source: "MMAPI120", pmnt: "PMNT1001A", errors: ["POWERON"]);
        st.AllUnits.Add(u1);
        st.AllUnits.Add(u2);
        st.Update(spec);
        Assert.Equal(1, st.GetUnitConsecutiveNg(u1));
        Assert.Equal(2, st.GetUnitConsecutiveNg(u2));

        // Incremental 3rd NG -> triggers ConsecutiveNg = 3
        var u3 = NewTestUnitItem(source: "MMAPI120", pmnt: "PMNT1001A", errors: ["FLICKER"]);
        st.AllUnits.Add(u3);
        st.Update(spec);
        Assert.Equal(3, st.GetUnitConsecutiveNg(u3));

        // Incremental OK -> resets ConsecutiveNg to 0, followed by 1 NG -> ConsecutiveNg = 1
        var u4 = NewTestUnitItem(source: "MMAPI120", pmnt: "PMNT1001A", errors: []);
        var u5 = NewTestUnitItem(source: "MMAPI120", pmnt: "PMNT1001A", errors: ["XYLV"]);
        st.AllUnits.Add(u4);
        st.AllUnits.Add(u5);
        st.Update(spec);
        Assert.Equal(0, st.GetUnitConsecutiveNg(u4));
        Assert.Equal(1, st.GetUnitConsecutiveNg(u5));
    }

    [Fact]
    public async Task EnforcesSlidingWindowMemoryCap_ReleasesDisplayRowOnEvictedUnits_AndArchivesToCsv()
    {
        var st = new GuiDisplayState
        {
            MaxRetainedUnits = 4,
            TrimBatch = 1
        };
        var spec = NewFilterSpec();

        for (int i = 1; i <= 4; i++)
        {
            st.AllUnits.Add(NewTestUnitItem(source: "MMAPI120", pmnt: "PMNT1001A", panel: $"PANEL_{i}"));
        }
        st.Update(spec);
        Assert.Equal(4, st.AllUnits.Count);
        Assert.Equal(4, st.DisplayList.Count);
        Assert.Equal(0, st.EvictedCount);
        var oldestUnit = st.AllUnits[0];
        Assert.True(st.HasDisplayRow(oldestUnit));

        string tempDir = Path.Combine(Path.GetTempPath(), $"otp_disp_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string archiveCsv = Path.Combine(tempDir, "sliding_archive.csv");
        try
        {
            // Adding 5th unit exceeds MaxRetainedUnits (4) -> evicts excess (1) + TrimBatch (1) = 2 oldest units
            st.AllUnits.Add(NewTestUnitItem(source: "MMAPI120", pmnt: "PMNT1001A", panel: "PANEL_5"));
            st.Update(spec, archivePath: archiveCsv);
            await st.PendingArchiveTask;

            Assert.Equal(3, st.AllUnits.Count);
            Assert.Equal(3, st.DisplayList.Count);
            Assert.Equal(2, st.EvictedCount);
            Assert.Equal("PANEL_3", st.AllUnits[0].PanelName);
            Assert.Equal(0, st.AllUnits[0].Index);
            Assert.Equal("PANEL_5", st.AllUnits[2].PanelName);
            Assert.Equal(2, st.AllUnits[2].Index);
            Assert.False(st.HasDisplayRow(oldestUnit));

            Assert.True(File.Exists(archiveCsv));
            string[] lines = File.ReadAllLines(archiveCsv);
            Assert.Equal(3, lines.Length); // Header + 2 archived rows
            Assert.Contains("PANEL_1", lines[1]);
            Assert.Contains("PANEL_2", lines[2]);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Rebuild_PreservesDisplayListInstance_RaisesSingleResetAndDisplayListReplaced()
    {
        var st = new GuiDisplayState();
        var originalCollection = st.DisplayList;
        var actions = new List<NotifyCollectionChangedAction>();
        int replacedCount = 0;
        originalCollection.CollectionChanged += (_, e) => actions.Add(e.Action);
        st.DisplayListReplaced += (_, _) => replacedCount++;

        st.AllUnits.Add(NewTestUnitItem(pmnt: "PMNT1001A", panel: "P1"));
        st.AllUnits.Add(NewTestUnitItem(pmnt: "PMNT1002B", panel: "P2"));

        st.Update(NewFilterSpec());
        Assert.Same(originalCollection, st.DisplayList);
        Assert.Equal([NotifyCollectionChangedAction.Reset], actions);
        Assert.Equal(1, replacedCount);

        actions.Clear();
        st.Update(NewFilterSpec(pmnt: "1002"));
        Assert.Same(originalCollection, st.DisplayList);
        Assert.Single(st.DisplayList);
        Assert.Equal([NotifyCollectionChangedAction.Reset], actions);
        Assert.Equal(2, replacedCount);
    }

    [Fact]
    public void AddIncremental_PopulatesTotalRunsAndReadOnlyScopes()
    {
        var st = new GuiDisplayState();
        var spec = NewFilterSpec();
        st.Update(spec);

        st.AllUnits.Add(NewTestUnitItem(source: "MMAPI120", pmnt: "PMNT1001A", panel: "P1"));
        Assert.True(st.Update(spec));
        Assert.Single(st.DisplayList);
        Assert.Equal(1, st.DisplayList[0].TotalRuns);
        Assert.Equal("01/01", st.DisplayList[0].RunDisplay);

        st.AllUnits.Add(NewTestUnitItem(source: "MMAPI120", pmnt: "PMNT1001A", panel: "P2"));
        Assert.True(st.Update(spec));
        Assert.Equal(2, st.DisplayList.Count);
        Assert.Equal(2, st.DisplayList[0].TotalRuns);
        Assert.Equal(2, st.DisplayList[1].TotalRuns);
        Assert.Equal("01/02", st.DisplayList[0].RunDisplay);
        Assert.Equal("02/02", st.DisplayList[1].RunDisplay);

        IReadOnlyList<UnitRecord> baseScope = st.BaseScopeUnits;
        IReadOnlyList<UnitRecord> scope = st.ScopeUnits;
        Assert.Equal(2, baseScope.Count);
        Assert.Equal(2, scope.Count);
    }
}
