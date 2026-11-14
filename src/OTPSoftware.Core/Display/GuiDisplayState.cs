using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using OTPSoftware.Core.Interfaces;
using OTPSoftware.Core.Models;
using OTPSoftware.Core.Services;

namespace OTPSoftware.Core.Display;

/// <summary>
/// Reusable <see cref="ObservableCollection{T}"/> that swaps its contents in a single Reset batch
/// so WPF DataGrid bindings keep their ItemsSource reference and UI virtualization state intact.
/// </summary>
internal sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    private static readonly PropertyChangedEventArgs CountChangedArgs = new(nameof(Count));
    private static readonly PropertyChangedEventArgs IndexerChangedArgs = new("Item[]");
    private static readonly NotifyCollectionChangedEventArgs ResetChangedArgs = new(NotifyCollectionChangedAction.Reset);

    public void ReplaceAll(IReadOnlyList<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        if (Items is List<T> backingList)
        {
            backingList.EnsureCapacity(items.Count);
        }

        for (int i = 0; i < items.Count; i++)
        {
            Items.Add(items[i]);
        }

        OnPropertyChanged(CountChangedArgs);
        OnPropertyChanged(IndexerChangedArgs);
        OnCollectionChanged(ResetChangedArgs);
    }

    public void AddRange(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
            return;

        if (items.Count <= 32)
        {
            for (int i = 0; i < items.Count; i++)
            {
                Add(items[i]);
            }
            return;
        }

        CheckReentrancy();
        if (Items is List<T> backingList)
        {
            backingList.EnsureCapacity(Items.Count + items.Count);
        }

        for (int i = 0; i < items.Count; i++)
        {
            Items.Add(items[i]);
        }

        OnPropertyChanged(CountChangedArgs);
        OnPropertyChanged(IndexerChangedArgs);
        OnCollectionChanged(ResetChangedArgs);
    }
}

/// <summary>
/// Presentation row bound to the result DataGrid. Every property raises <see cref="PropertyChanged"/>
/// so cached rows can be updated in place (e.g. when a fixture's total run count grows).
/// </summary>
public sealed class DisplayRowViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public int No
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }

    public int PassCount
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }

    public int TotalRuns
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }

    public string RunDisplay
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = string.Empty;

    public string Source
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = string.Empty;

    public string SourceBg
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = string.Empty;

    public string SourceFg
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = string.Empty;

    public DateTime? Timestamp
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }

    public string TimeFormatted
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = string.Empty;

    public string PMNT
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = string.Empty;

    public string PanelName
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = string.Empty;

    public string BluName
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = string.Empty;

    public string MesResult
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = string.Empty;

    public bool IsNG
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }

    public string Errors
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = string.Empty;

    public string StatusText
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = string.Empty;

    public int ConsecutiveNg
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }
}

/// <summary>Position of a displayed row for a "{Source}_{PMNT}" key, used for in-place run-count updates.</summary>
public readonly record struct KeyRowEntry(int Pos, UnitRecord Unit);

/// <summary>
/// Pure state machine managing result grid filtering, fixture counters and display collections.
/// Four-tier scope hierarchy:
/// <list type="number">
/// <item><see cref="AllUnits"/>: every parsed unit.</item>
/// <item><see cref="BaseScopeUnits"/>: Source, Panel, Date/Time (EXCLUDES PMNT so the Faulty PMNT tab keeps its full ranking).</item>
/// <item><see cref="ScopeUnits"/>: BaseScope + PMNT search; fixture run totals are computed over this tier.</item>
/// <item><see cref="ShownUnits"/> / <see cref="DisplayList"/>: ScopeUnits + status filter (All / NG / OK).</item>
/// </list>
/// Presentation rows are cached per unit (the PowerShell <c>_DisplayRow</c>) so filter changes reuse them.
/// </summary>
public sealed class GuiDisplayState : IDisposable
{
    private readonly ConditionalWeakTable<UnitRecord, DisplayRowViewModel> _rowCache = new();
    private readonly BulkObservableCollection<DisplayRowViewModel> _displayList = [];
    private List<UnitRecord> _baseScopeUnits = [];
    private List<UnitRecord> _scopeUnits = [];

    public List<UnitRecord> AllUnits { get; } = [];
    public ObservableCollection<DisplayRowViewModel> DisplayList => _displayList;
    public IReadOnlyList<UnitRecord> BaseScopeUnits => _baseScopeUnits;
    public IReadOnlyList<UnitRecord> ScopeUnits => _scopeUnits;
    public List<UnitRecord> ShownUnits { get; private set; } = [];
    public Dictionary<string, int> ScopeTotals { get; private set; } = [];
    public Dictionary<string, int> ConsecutiveNg { get; private set; } = [];
    public Dictionary<string, List<KeyRowEntry>> KeyRows { get; private set; } = [];

    public string? BaseScopeSig { get; private set; }
    public string? ScopeSig { get; private set; }
    public string? FilterSig { get; private set; }
    public UnitRecord? LastUnitRef { get; private set; }
    private UnitRecord? _firstUnitRef;
    public int AppliedCount { get; private set; }
    public int MaxRetainedUnits { get; set; } = 30000;
    public int TrimBatch { get; set; } = 2000;
    public int EvictedCount { get; private set; }

    private readonly object _archiveQueueGate = new();
    private readonly SemaphoreSlim _archiveLock = new(1, 1);
    private Dictionary<UnitRecord, int> _scopedPassCounts = new(ReferenceEqualityComparer.Instance);
    private Dictionary<UnitRecord, int> _unitConsecutiveNgs = new(ReferenceEqualityComparer.Instance);
    private Task _pendingArchiveTask = Task.CompletedTask;
    private bool _disposed;
    public Task PendingArchiveTask => _pendingArchiveTask;

    /// <summary>Raised whenever <see cref="DisplayList"/> undergoes a full rebuild or batch replacement.</summary>
    public event EventHandler? DisplayListReplaced;

    /// <summary>True while the unit still has a cached presentation row.</summary>
    public bool HasDisplayRow(UnitRecord unit) => _rowCache.TryGetValue(unit, out _);

    /// <summary>Gets the cached presentation row for <paramref name="unit"/> if present.</summary>
    public DisplayRowViewModel? GetCachedDisplayRow(UnitRecord unit) =>
        _rowCache.TryGetValue(unit, out var row) ? row : null;

    /// <summary>Gets the latest scope-computed consecutive NG streak for <paramref name="unit"/> without mutating the record.</summary>
    public int GetUnitConsecutiveNg(UnitRecord? unit)
    {
        if (unit is null) return 0;
        if (_unitConsecutiveNgs.TryGetValue(unit, out int count))
        {
            return count;
        }
        return unit.IsNg ? 1 : 0;
    }

    internal static readonly MachineVisualStyle[] CorporatePalette =
    [
        new("#55EFC4", "#1E463A"),
        new("#FDCB6E", "#4D381E"),
        new("#FD79A8", "#4A233E"),
        new("#81ECEC", "#1E3F4D"),
        new("#FF7675", "#4D2222"),
        new("#A3CB38", "#2E3D19"),
        new("#F8A5C2", "#4B2639"),
        new("#63CDDA", "#1B3B44"),
        new("#F7D794", "#4A3D1D"),
        new("#778BEB", "#252B4D")
    ];

    private static uint ComputeStableOrdinalHash(string text)
    {
        uint hash = 2166136261u;
        foreach (char ch in text)
        {
            hash ^= char.ToUpperInvariant(ch);
            hash *= 16777619u;
        }
        return hash;
    }

    // Resolves machine badge colors from active settings or the fallback palette.
    public static MachineVisualStyle GetMachineVisualStyle(
        string? source,
        IReadOnlyDictionary<string, MachineConfig>? settings = null)
    {
        string src = string.IsNullOrWhiteSpace(source) ? "MMAPI120" : source;

        if (settings is not null && settings.TryGetValue(src, out var machine) &&
            !string.IsNullOrWhiteSpace(machine.BgColor) && !string.IsNullOrWhiteSpace(machine.Color))
        {
            return new MachineVisualStyle(machine.Color, machine.BgColor);
        }

        if (string.Equals(src, "MMAPI110", StringComparison.OrdinalIgnoreCase)) return new MachineVisualStyle("#A29BFE", "#3A2A54");
        if (string.Equals(src, "MMAPI120", StringComparison.OrdinalIgnoreCase)) return new MachineVisualStyle("#74B9FF", "#1B3B52");

        // Fallback palette for additional machines (3rd, 4th, ...) if not specified in settings.json.
        uint hash = ComputeStableOrdinalHash(src);
        return CorporatePalette[hash % (uint)CorporatePalette.Length];
    }

    public static MachineVisualStyle GetMachineVisualStyle(string? source, OtpSettings settings) =>
        GetMachineVisualStyle(source, settings?.MachinesByName);

    public static DisplayRowViewModel NewDisplayItem(
        int index,
        UnitRecord unit,
        IReadOnlyDictionary<string, MachineConfig>? settings = null,
        Dictionary<string, MachineVisualStyle>? styleCache = null,
        int? scopedPassCount = null,
        int? scopedTotalRuns = null,
        int consecutiveNg = 0)
    {
        ArgumentNullException.ThrowIfNull(unit);

        int passCount = scopedPassCount ?? unit.PassCount;
        int total = scopedTotalRuns ?? (unit.TotalRuns > 0 ? unit.TotalRuns : passCount);
        bool isOk = !unit.IsNg;
        string errors = string.Join(" | ", unit.Errors);

        string src = string.IsNullOrEmpty(unit.Source) ? "MMAPI120" : unit.Source;
        MachineVisualStyle style;
        if (styleCache is not null)
        {
            if (!styleCache.TryGetValue(src, out style))
            {
                style = GetMachineVisualStyle(src, settings);
                styleCache[src] = style;
            }
        }
        else
        {
            style = GetMachineVisualStyle(src, settings);
        }

        return new DisplayRowViewModel
        {
            No = index,
            Source = src,
            SourceBg = style.Background,
            SourceFg = style.Foreground,
            Timestamp = unit.Timestamp,
            TimeFormatted = unit.Timestamp?.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) ?? "Unknown",
            PMNT = unit.Pmnt,
            PassCount = passCount,
            TotalRuns = total,
            RunDisplay = FormatRun(passCount, total),
            PanelName = unit.PanelName,
            BluName = unit.BluName,
            MesResult = unit.MesResult ?? string.Empty,
            IsNG = !isOk,
            Errors = errors,
            StatusText = isOk ? "PASS (OK)" : "NG: " + errors,
            ConsecutiveNg = consecutiveNg
        };
    }

    private static string FormatRun(int pass, int total) => $"{pass:D2}/{total:D2}";

    public void Clear()
    {
        AllUnits.Clear();
        _displayList.Clear();
        _baseScopeUnits.Clear();
        _scopeUnits.Clear();
        ShownUnits.Clear();
        ScopeTotals.Clear();
        _scopedPassCounts.Clear();
        _unitConsecutiveNgs.Clear();
        ConsecutiveNg.Clear();
        KeyRows.Clear();
        BaseScopeSig = null;
        ScopeSig = null;
        FilterSig = null;
        _firstUnitRef = null;
        LastUnitRef = null;
        AppliedCount = 0;
        EvictedCount = 0;
    }

    public void RecordPreTrimmedEvicted(int count)
    {
        if (count > 0)
        {
            EvictedCount += count;
        }
    }

    /// <summary>
    /// Enforces the RAM sliding window cap on <see cref="AllUnits"/>: evicts the oldest records (optionally archiving
    /// them to CSV first), releases cached rows and invalidates scope signatures for a clean rebuild.
    /// Returns the number of evicted units.
    /// </summary>
    public int InvokeSlidingWindowTrim(
        int maxUnits = 0,
        int trimBatch = -1,
        string archivePath = "",
        IExportService? exportService = null)
    {
        int limit = maxUnits > 0 ? maxUnits : MaxRetainedUnits > 0 ? MaxRetainedUnits : 0;
        if (limit <= 0 || AllUnits.Count <= limit) return 0;

        int batch = trimBatch >= 0 ? trimBatch : TrimBatch >= 0 ? TrimBatch : 0;
        int excess = AllUnits.Count - limit;
        int removeCount = excess + batch;
        if (AllUnits.Count - removeCount < 1) removeCount = excess;

        if (!string.IsNullOrWhiteSpace(archivePath))
        {
            lock (_archiveQueueGate)
            {
                if (!_disposed)
                {
                    var evicted = AllUnits.GetRange(0, removeCount);
                    var exporter = exportService ?? new ExportService();
                    var previous = _pendingArchiveTask;
                    _pendingArchiveTask = ArchiveEvictedUnitsAsync(previous, exporter, evicted, archivePath);
                }
            }
        }

        for (int k = 0; k < removeCount; k++)
        {
            _rowCache.Remove(AllUnits[k]);
            _unitConsecutiveNgs.Remove(AllUnits[k]);
        }

        AllUnits.RemoveRange(0, removeCount);
        for (int i = 0; i < AllUnits.Count; i++) AllUnits[i].Index = i;

        EvictedCount += removeCount;
        AppliedCount = 0;
        BaseScopeSig = null;
        ScopeSig = null;
        FilterSig = null;
        _firstUnitRef = null;
        LastUnitRef = null;

        return removeCount;
    }

    private async Task ArchiveEvictedUnitsAsync(
        Task previousTask,
        IExportService exporter,
        IReadOnlyList<UnitRecord> evicted,
        string archivePath)
    {
        try
        {
            await previousTask.ConfigureAwait(false);
        }
        catch
        {
            // Keep subsequent archive batches running even if a prior batch failed.
        }

        await _archiveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await exporter.ExportUnitDataAsync(evicted, archivePath, append: true, includeMachine: true).ConfigureAwait(false);
        }
        finally
        {
            _archiveLock.Release();
        }
    }

    /// <summary>
    /// Waits for all previously queued sliding-window archive writes to finish.
    /// </summary>
    public async Task FlushArchiveAsync()
    {
        Task pending;
        lock (_archiveQueueGate)
        {
            pending = _pendingArchiveTask;
        }
        await pending.ConfigureAwait(false);
    }

    public void Dispose()
    {
        Task pending;
        lock (_archiveQueueGate)
        {
            if (_disposed) return;
            _disposed = true;
            pending = _pendingArchiveTask;
        }

        if (pending.IsCompleted)
        {
            _archiveLock.Dispose();
            return;
        }

        _ = pending.ContinueWith(
            (t, state) =>
            {
                _ = t.Exception;
                ((SemaphoreSlim)state!).Dispose();
            },
            _archiveLock,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static string ResolveExpectedSource(UnitFilterSpec spec) =>
        UnitFilterService.ResolveExpectedSource(spec.SourceName);

    private static bool Contains(string? text, string pattern) =>
        !string.IsNullOrEmpty(text) && text.Contains(pattern, StringComparison.OrdinalIgnoreCase);

    private static string Key(UnitRecord u) => $"{u.Source}_{u.Pmnt}";

    public void Rebuild(UnitFilterSpec spec, OtpSettings settings) =>
        Rebuild(spec, settings?.MachinesByName);

    // Rebuild entire display table (on filter change or new log load), reusing cached rows.
    public void Rebuild(UnitFilterSpec spec, IReadOnlyDictionary<string, MachineConfig>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(spec);

        string expectedSrc = ResolveExpectedSource(spec);
        bool hasSrc = expectedSrc.Length > 0;
        string pmntPat = spec.PmntPattern ?? string.Empty;
        bool hasPmnt = pmntPat.Length > 0;
        string panelPat = spec.PanelPattern ?? string.Empty;
        bool hasPanel = panelPat.Length > 0;
        DateTime? startDt = spec.StartTime;
        DateTime? endDt = spec.EndTime;
        var status = spec.Status;

        string baseScopeSig = $"{expectedSrc}|{panelPat}|{startDt:O}|{endDt:O}";
        string scopeSig = $"{baseScopeSig}|{pmntPat}";
        UnitRecord? firstUnit = AllUnits.Count > 0 ? AllUnits[0] : null;
        UnitRecord? lastUnit = AllUnits.Count > 0 ? AllUnits[^1] : null;
        bool dataChanged = AppliedCount != AllUnits.Count
            || !ReferenceEquals(_firstUnitRef, firstUnit)
            || !ReferenceEquals(LastUnitRef, lastUnit);

        // 1. Base scope (Machine, Panel, Time - excludes PMNT filter to retain faulty PMNT stats on Tab 2)
        bool baseRebuilt = false;
        if (dataChanged || BaseScopeSig != baseScopeSig)
        {
            baseRebuilt = true;
            var baseList = new List<UnitRecord>(AllUnits.Count);
            if (!hasSrc && !hasPanel && startDt is null && endDt is null)
            {
                baseList.AddRange(AllUnits);
            }
            else
            {
                foreach (var u in AllUnits)
                {
                    if (!PassesBase(u, hasSrc, expectedSrc, hasPanel, panelPat, startDt, endDt)) continue;
                    baseList.Add(u);
                }
            }
            _baseScopeUnits = baseList;
            BaseScopeSig = baseScopeSig;
        }

        // 2. Full scope (adds PMNT filter) + total runs and consecutive NG streak per machine & PMNT
        if (baseRebuilt || ScopeSig != scopeSig)
        {
            var scopeList = new List<UnitRecord>(_baseScopeUnits.Count);
            var scopeTotals = new Dictionary<string, int>();
            var scopedPassCounts = new Dictionary<UnitRecord, int>(ReferenceEqualityComparer.Instance);
            var unitConsecutiveNgs = new Dictionary<UnitRecord, int>(ReferenceEqualityComparer.Instance);
            var consecNg = new Dictionary<string, int>();

            foreach (var u in _baseScopeUnits)
            {
                if (hasPmnt && !Contains(u.Pmnt, pmntPat)) continue;
                scopeList.Add(u);
                if (!string.IsNullOrEmpty(u.Pmnt))
                {
                    string key = Key(u);
                    scopeTotals.TryGetValue(key, out int cnt);
                    scopeTotals[key] = ++cnt;
                    scopedPassCounts[u] = cnt;

                    if (u.IsNg)
                    {
                        consecNg.TryGetValue(key, out int stCnt);
                        consecNg[key] = ++stCnt;
                        unitConsecutiveNgs[u] = stCnt;
                    }
                    else
                    {
                        consecNg[key] = 0;
                        unitConsecutiveNgs[u] = 0;
                    }
                }
                else
                {
                    unitConsecutiveNgs[u] = 0;
                }
            }

            _scopeUnits = scopeList;
            ScopeTotals = scopeTotals;
            _scopedPassCounts = scopedPassCounts;
            _unitConsecutiveNgs = unitConsecutiveNgs;
            ConsecutiveNg = consecNg;
            ScopeSig = scopeSig;
        }

        // 3. Display list from the status filter (OK/NG), reusing cached rows
        ShownUnits = new List<UnitRecord>(_scopeUnits.Count);
        KeyRows = [];
        var styleCache = new Dictionary<string, MachineVisualStyle>();
        var items = new List<DisplayRowViewModel>(_scopeUnits.Count);

        foreach (var u in _scopeUnits)
        {
            bool isNg = u.IsNg;
            if (status == UnitStatusFilter.NgOnly && !isNg) continue;
            if (status == UnitStatusFilter.OkOnly && isNg) continue;

            int pos = ShownUnits.Count;
            ShownUnits.Add(u);

            bool hasPmntKey = !string.IsNullOrEmpty(u.Pmnt);
            int pass = hasPmntKey && _scopedPassCounts.TryGetValue(u, out int scopedPass)
                ? scopedPass
                : u.PassCount;
            int tot = hasPmntKey && ScopeTotals.TryGetValue(Key(u), out int scopedTotal)
                ? scopedTotal
                : (u.TotalRuns > 0 ? u.TotalRuns : pass);
            int consec = _unitConsecutiveNgs.TryGetValue(u, out int unitConsec) ? unitConsec : 0;
            if (!_rowCache.TryGetValue(u, out var row))
            {
                row = NewDisplayItem(
                    pos + 1,
                    u,
                    settings,
                    styleCache,
                    hasPmntKey ? pass : null,
                    hasPmntKey ? tot : null,
                    consec);
                _rowCache.AddOrUpdate(u, row);
            }
            else
            {
                row.No = pos + 1;
                row.ConsecutiveNg = consec;
                if (row.PassCount != pass || row.TotalRuns != tot)
                {
                    row.PassCount = pass;
                    row.TotalRuns = tot;
                    row.RunDisplay = FormatRun(pass, tot);
                }
            }
            items.Add(row);

            if (!string.IsNullOrEmpty(u.Pmnt))
            {
                string key = Key(u);
                if (!KeyRows.TryGetValue(key, out var list))
                {
                    list = [];
                    KeyRows[key] = list;
                }
                list.Add(new KeyRowEntry(pos, u));
            }
        }

        _displayList.ReplaceAll(items);
        AppliedCount = AllUnits.Count;
        _firstUnitRef = firstUnit;
        LastUnitRef = lastUnit;
        FilterSig = spec.Signature;
        DisplayListReplaced?.Invoke(this, EventArgs.Empty);
    }

    private static bool PassesBase(
        UnitRecord u, bool hasSrc, string expectedSrc, bool hasPanel, string panelPat, DateTime? startDt, DateTime? endDt)
    {
        if (hasSrc && !string.Equals(u.Source, expectedSrc, StringComparison.OrdinalIgnoreCase)) return false;
        if (hasPanel && !Contains(u.PanelName, panelPat)) return false;
        if ((startDt is not null || endDt is not null) && u.Timestamp is { } ts)
        {
            if (startDt is { } s && ts < s) return false;
            if (endDt is { } e && ts > e) return false;
        }
        return true;
    }

    public bool AddIncremental(UnitFilterSpec spec, OtpSettings settings) =>
        AddIncremental(spec, settings?.MachinesByName);

    // Process NEW records only (AllUnits from AppliedCount onwards). Returns true if the filter scope changed.
    public bool AddIncremental(UnitFilterSpec spec, IReadOnlyDictionary<string, MachineConfig>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(spec);

        bool changed = false;
        var dirtyKeys = new HashSet<string>();
        var styleCache = new Dictionary<string, MachineVisualStyle>();
        string expectedSrc = ResolveExpectedSource(spec);
        bool hasSrc = expectedSrc.Length > 0;
        string pmntPat = spec.PmntPattern ?? string.Empty;
        bool hasPmnt = pmntPat.Length > 0;
        string panelPat = spec.PanelPattern ?? string.Empty;
        bool hasPanel = panelPat.Length > 0;
        List<DisplayRowViewModel>? newRows = null;

        for (int i = AppliedCount; i < AllUnits.Count; i++)
        {
            var u = AllUnits[i];
            if (!PassesBase(u, hasSrc, expectedSrc, hasPanel, panelPat, spec.StartTime, spec.EndTime)) continue;

            _baseScopeUnits.Add(u);
            changed = true;

            if (hasPmnt && !Contains(u.Pmnt, pmntPat)) continue;
            _scopeUnits.Add(u);

            string? key = null;
            int scopedPass = 0;
            int consec = 0;
            if (!string.IsNullOrEmpty(u.Pmnt))
            {
                key = Key(u);
                ScopeTotals.TryGetValue(key, out int cnt);
                ScopeTotals[key] = ++cnt;
                scopedPass = cnt;
                _scopedPassCounts[u] = cnt;
                dirtyKeys.Add(key);

                if (u.IsNg)
                {
                    ConsecutiveNg.TryGetValue(key, out int stCnt);
                    ConsecutiveNg[key] = ++stCnt;
                    consec = stCnt;
                }
                else
                {
                    ConsecutiveNg[key] = 0;
                    consec = 0;
                }
            }
            _unitConsecutiveNgs[u] = consec;

            bool isNg = u.IsNg;
            if (spec.Status == UnitStatusFilter.NgOnly && !isNg) continue;
            if (spec.Status == UnitStatusFilter.OkOnly && isNg) continue;

            int pos = ShownUnits.Count;
            ShownUnits.Add(u);
            var row = NewDisplayItem(
                pos + 1,
                u,
                settings,
                styleCache,
                key is not null ? scopedPass : null,
                key is not null ? ScopeTotals.GetValueOrDefault(key, scopedPass) : null,
                consec);
            _rowCache.AddOrUpdate(u, row);
            newRows ??= [];
            newRows.Add(row);
            if (key is not null)
            {
                if (!KeyRows.TryGetValue(key, out var list))
                {
                    list = [];
                    KeyRows[key] = list;
                }
                list.Add(new KeyRowEntry(pos, u));
            }
        }

        if (newRows is { Count: > 0 })
        {
            _displayList.AddRange(newRows);
        }

        AppliedCount = AllUnits.Count;
        _firstUnitRef ??= AllUnits.Count > 0 ? AllUnits[0] : null;
        LastUnitRef = AllUnits.Count > 0 ? AllUnits[^1] : null;

        // "Run Count" column is n/TOTAL: when the total changes, older rows for the same machine & PMNT update in place.
        foreach (string key in dirtyKeys)
        {
            if (!KeyRows.TryGetValue(key, out var entries)) continue;
            int total = ScopeTotals[key];
            foreach (var entry in entries)
            {
                var existingRow = DisplayList[entry.Pos];
                if (existingRow.TotalRuns != total)
                {
                    existingRow.TotalRuns = total;
                    existingRow.RunDisplay = FormatRun(existingRow.PassCount, total);
                }
            }
        }
        return changed;
    }

    public bool Update(
        UnitFilterSpec spec,
        OtpSettings settings,
        int maxUnits = 0,
        int trimBatch = -1,
        string archivePath = "",
        IExportService? exportService = null) =>
        Update(spec, settings?.MachinesByName, maxUnits, trimBatch, archivePath, exportService);

    public bool Update(
        UnitFilterSpec spec,
        IReadOnlyDictionary<string, MachineConfig>? settings = null,
        int maxUnits = 0,
        int trimBatch = -1,
        string archivePath = "",
        IExportService? exportService = null)
    {
        ArgumentNullException.ThrowIfNull(spec);

        InvokeSlidingWindowTrim(maxUnits, trimBatch, archivePath, exportService);
        bool sameHead = AppliedCount == 0 || (AllUnits.Count > 0 && ReferenceEquals(AllUnits[0], _firstUnitRef));
        bool sameTail = AppliedCount == 0 || (AppliedCount <= AllUnits.Count && ReferenceEquals(AllUnits[AppliedCount - 1], LastUnitRef));
        if (FilterSig is not null && spec.Signature == FilterSig && AllUnits.Count >= AppliedCount && sameHead && sameTail)
        {
            return AddIncremental(spec, settings);
        }
        Rebuild(spec, settings);
        return true;
    }
}
