using System.Collections.ObjectModel;
using OTPSoftware.Core.Display;
using OTPSoftware.Core.Interfaces;
using OTPSoftware.Core.Models;

namespace OTPSoftware.App.ViewModels;

/// <summary>
/// MVVM wrapper around <see cref="GuiDisplayState"/> exposing observable presentation collections
/// and summary counts with C# 14 <c>field</c>-backed property setters.
/// </summary>
public sealed class DisplayStateViewModel : ViewModelBase
{
    public DisplayStateViewModel(GuiDisplayState? state = null)
    {
        State = state ?? new GuiDisplayState();
        DisplayList = State.DisplayList;
        State.DisplayListReplaced += (_, _) =>
        {
            DisplayList = State.DisplayList;
            SyncSummaryProperties();
        };
    }

    public GuiDisplayState State { get; }

    public ObservableCollection<DisplayRowViewModel> DisplayList
    {
        get => field;
        private set
        {
            if (!ReferenceEquals(field, value))
            {
                field = value;
                OnPropertyChanged();
            }
        }
    } = [];

    public int EvictedCount
    {
        get => field;
        private set { if (field != value) { field = value; OnPropertyChanged(); } }
    }

    public List<UnitRecord> AllUnits => State.AllUnits;
    public IReadOnlyList<UnitRecord> BaseScopeUnits => State.BaseScopeUnits;
    public IReadOnlyList<UnitRecord> ScopeUnits => State.ScopeUnits;
    public List<UnitRecord> ShownUnits => State.ShownUnits;
    public string? BaseScopeSig => State.BaseScopeSig;
    public string? ScopeSig => State.ScopeSig;

    public void Clear()
    {
        State.Clear();
        SyncSummaryProperties();
    }

    public bool Update(
        UnitFilterSpec spec,
        IReadOnlyDictionary<string, MachineConfig>? settings = null,
        int maxUnits = 0,
        int trimBatch = -1,
        string archivePath = "",
        IExportService? exportService = null)
    {
        bool changed = State.Update(spec, settings, maxUnits, trimBatch, archivePath, exportService);
        SyncSummaryProperties();
        return changed;
    }

    public int GetUnitConsecutiveNg(UnitRecord? unit) => State.GetUnitConsecutiveNg(unit);

    private void SyncSummaryProperties()
    {
        DisplayList = State.DisplayList;
        EvictedCount = State.EvictedCount;
    }
}
