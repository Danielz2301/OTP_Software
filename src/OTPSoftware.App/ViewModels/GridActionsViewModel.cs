using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using OTPSoftware.App.Services;
using OTPSoftware.Core.Display;
using OTPSoftware.Core.Models;
using OTPSoftware.Core.Services;

namespace OTPSoftware.App.ViewModels;

/// <summary>
/// ViewModel handling DataGrid context-menu state, resilient clipboard operations, row formatting,
/// and the dark-mode MES Parameter Details modal window.
/// Uses C# 14 <c>field</c> keyword in all property setters.
/// </summary>
public sealed class GridActionsViewModel : ViewModelBase
{
    private readonly ILocalizationService _localization;
    private Func<IReadOnlyList<DisplayRowViewModel>>? _selectedRowsProvider;
    private int _lastSelectedCount = 1;
    private IReadOnlyDictionary<string, MachineConfig>? _lastSettings;

    public GridActionsViewModel(ILocalizationService? localizationService = null)
    {
        _localization = localizationService ?? LocalizationService.Default;
        ResetDefaultHeaders();
    }

    private void ResetDefaultHeaders()
    {
        CopyCellHeader = _localization.Get("Menu_CopySelectedCell");
        CopyRowHeader = _localization.Get("Menu_CopyEntireRow");
        CopyRowWithHeaderHeader = _localization.Get("Menu_CopyRowWithHeaders");
        CopyPanelHeader = _localization.Get("Menu_CopyPanelName");
        CopyBluHeader = _localization.Get("Menu_CopyBluName");
        CopyPmntHeader = _localization.Get("Menu_CopyPmntCode");
        FilterPmntHeader = _localization.Get("Menu_FilterByThisPmnt");
        FilterPanelHeader = _localization.Get("Menu_FilterByThisPanel");
        OpenImagesBothHeader = _localization.Get("Menu_OpenImagesBothDefault");
        OpenImagesLeftHeader = _localization.Get("Menu_OpenImageLeftDefault");
        OpenImagesRightHeader = _localization.Get("Menu_OpenImageRightDefault");
    }

    public void RefreshLocalization(IReadOnlyDictionary<string, MachineConfig>? settings = null)
    {
        if (LastRightClickedItem is not null)
        {
            UpdateContextMenuState(LastRightClickedItem, LastRightClickedColProp, _lastSelectedCount, settings ?? _lastSettings);
        }
        else
        {
            ResetDefaultHeaders();
        }
    }

    /// <summary>
    /// Optional view callback returning currently selected rows dynamically on copy command execution,
    /// avoiding holding strong list references on <see cref="MenuItem.CommandParameterProperty"/>.
    /// </summary>
    public Func<IReadOnlyList<DisplayRowViewModel>>? SelectedRowsProvider
    {
        get => _selectedRowsProvider;
        set => _selectedRowsProvider = value;
    }

    public DisplayRowViewModel? LastRightClickedItem
    {
        get => field;
        set { if (!ReferenceEquals(field, value)) { field = value; OnPropertyChanged(); } }
    }

    public string LastRightClickedColProp
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = string.Empty;

    public string CopyCellHeader
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "Copy Selected Cell";

    public string CopyRowHeader
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "Copy Entire Row";

    public string CopyRowWithHeaderHeader
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "Copy Row (With Headers)";

    public string CopyPanelHeader
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "Copy Panel Name";

    public string CopyBluHeader
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "Copy Blu Name";

    public string CopyPmntHeader
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "Copy PMNT Code";

    public string FilterPmntHeader
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "Filter by this PMNT";

    public string FilterPanelHeader
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "Filter by this Panel";

    public string OpenImagesBothHeader
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "Open Images (Left & Right)";

    public string OpenImagesLeftHeader
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "Open Left Image (Cam 1)";

    public string OpenImagesRightHeader
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "Open Right Image (Cam 2)";

    public bool IsCopyCellEnabled
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = true;

    public bool IsCopyPanelEnabled
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = true;

    public bool IsCopyBluEnabled
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = true;

    public bool IsCopyPmntEnabled
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = true;

    public bool IsFilterPmntEnabled
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = true;

    public bool IsFilterPanelEnabled
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = true;

    public bool IsOpenImagesBothEnabled
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = true;

    public bool IsOpenImagesLeftEnabled
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = true;

    public bool IsOpenImagesRightEnabled
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = true;

    /// <summary>
    /// Writes <paramref name="text"/> to the Windows clipboard asynchronously with retry/backoff when locked by another process.
    /// </summary>
    public static async Task<bool> SetSafeClipboardAsync(string? text, int retries = 5, int delayMs = 40)
    {
        if (string.IsNullOrEmpty(text)) return false;
        int attempts = Math.Max(1, retries);
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch
            {
                if (delayMs > 0 && i + 1 < attempts)
                {
                    await Task.Delay(delayMs).ConfigureAwait(true);
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Extracts the bound property name from a WPF <see cref="DataGridColumn"/>.
    /// </summary>
    public static string GetColumnProperty(DataGridColumn? column)
    {
        if (column is null) return string.Empty;
        BindingBase? binding = column switch
        {
            DataGridBoundColumn bound => bound.Binding,
            DataGridTemplateColumn template => template.ClipboardContentBinding,
            _ => null
        };
        return binding is Binding { Path.Path: { Length: > 0 } path } ? path : string.Empty;
    }

    /// <summary>
    /// Resolves the string representation of a <see cref="DisplayRowViewModel"/> property by name.
    /// </summary>
    public static string GetCellValueByProperty(DisplayRowViewModel? item, string? propName)
    {
        if (item is null || string.IsNullOrEmpty(propName)) return string.Empty;
        return propName switch
        {
            nameof(DisplayRowViewModel.No) => item.No.ToString(CultureInfo.InvariantCulture),
            nameof(DisplayRowViewModel.PassCount) => item.PassCount.ToString(CultureInfo.InvariantCulture),
            nameof(DisplayRowViewModel.TotalRuns) => item.TotalRuns.ToString(CultureInfo.InvariantCulture),
            nameof(DisplayRowViewModel.RunDisplay) => item.RunDisplay ?? string.Empty,
            nameof(DisplayRowViewModel.Source) => item.Source ?? string.Empty,
            nameof(DisplayRowViewModel.Timestamp) => item.Timestamp?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty,
            nameof(DisplayRowViewModel.TimeFormatted) => item.TimeFormatted ?? string.Empty,
            nameof(DisplayRowViewModel.PMNT) => item.PMNT ?? string.Empty,
            nameof(DisplayRowViewModel.PanelName) => item.PanelName ?? string.Empty,
            nameof(DisplayRowViewModel.BluName) => item.BluName ?? string.Empty,
            nameof(DisplayRowViewModel.MesResult) => item.MesResult ?? string.Empty,
            nameof(DisplayRowViewModel.IsNG) => item.IsNG.ToString(),
            nameof(DisplayRowViewModel.Errors) => item.Errors ?? string.Empty,
            nameof(DisplayRowViewModel.StatusText) => item.StatusText ?? string.Empty,
            _ => string.Empty
        };
    }

    /// <summary>
    /// Updates context menu headers and enabled flags based on the right-clicked row and column.
    /// </summary>
    public void UpdateContextMenuState(
        DisplayRowViewModel? item,
        string? colProp,
        int selectedCount = 1,
        IReadOnlyDictionary<string, MachineConfig>? settings = null)
    {
        LastRightClickedItem = item;
        LastRightClickedColProp = colProp ?? string.Empty;
        _lastSelectedCount = Math.Max(1, selectedCount);
        _lastSettings = settings;

        string cellVal = GetCellValueByProperty(item, colProp);
        string cellPreview = cellVal.Length > 24 ? cellVal[..24] + "..." : cellVal;

        CopyCellHeader = !string.IsNullOrEmpty(cellPreview)
            ? _localization.Format("Menu_CopyCellFormat", cellPreview)
            : _localization.Get("Menu_CopySelectedCell");
        IsCopyCellEnabled = !string.IsNullOrEmpty(cellVal);

        string panel = item?.PanelName ?? string.Empty;
        CopyPanelHeader = _localization.Format("Menu_CopyPanelFormat", panel);
        IsCopyPanelEnabled = !string.IsNullOrWhiteSpace(panel);

        string blu = item?.BluName ?? string.Empty;
        string bluPrev = blu.Length > 20 ? blu[..20] + "..." : blu;
        CopyBluHeader = _localization.Format("Menu_CopyBluFormat", bluPrev);
        IsCopyBluEnabled = !string.IsNullOrWhiteSpace(blu);

        string pmnt = item?.PMNT ?? string.Empty;
        CopyPmntHeader = _localization.Format("Menu_CopyPmntFormat", pmnt);
        IsCopyPmntEnabled = !string.IsNullOrWhiteSpace(pmnt);

        FilterPmntHeader = _localization.Format("Menu_FilterPmntFormat", pmnt);
        IsFilterPmntEnabled = !string.IsNullOrWhiteSpace(pmnt);

        FilterPanelHeader = _localization.Format("Menu_FilterPanelFormat", panel);
        IsFilterPanelEnabled = !string.IsNullOrWhiteSpace(panel);

        CopyRowHeader = selectedCount > 1
            ? _localization.Format("Menu_CopySelectedRowsFormat", selectedCount)
            : _localization.Get("Menu_CopyEntireRow");
        CopyRowWithHeaderHeader = selectedCount > 1
            ? _localization.Format("Menu_CopyRowsWithHeadersFormat", selectedCount)
            : _localization.Get("Menu_CopyRowWithHeaders");

        if (string.IsNullOrWhiteSpace(panel))
        {
            OpenImagesBothHeader = _localization.Get("Menu_OpenImagesNoPanel");
            IsOpenImagesBothEnabled = false;
            IsOpenImagesLeftEnabled = false;
            IsOpenImagesRightEnabled = false;
        }
        else
        {
            string pShort = panel.Length > 15 ? panel[..15] + "..." : panel;
            string src = item?.Source ?? string.Empty;
            IReadOnlyList<CameraImageConfig> imgs = [];
            if (settings is not null && !string.IsNullOrWhiteSpace(src) && settings.TryGetValue(src, out var mCfg))
            {
                imgs = mCfg.Images;
            }

            if (imgs.Count > 0)
            {
                string notCfg = _localization.Get("Menu_NotConfigured");
                string ipL = imgs.Count >= 1 ? @"\\" + SettingsService.GetUncServerName(imgs[0].Root) : notCfg;
                string ipR = imgs.Count >= 2 ? @"\\" + SettingsService.GetUncServerName(imgs[1].Root) : notCfg;
                string codeL = imgs.Count >= 1 ? imgs[0].Code : "-";
                string codeR = imgs.Count >= 2 ? imgs[1].Code : "-";

                OpenImagesBothHeader = _localization.Format("Menu_OpenImagesBothFormat", pShort);
                IsOpenImagesBothEnabled = imgs.Count >= 1;

                OpenImagesLeftHeader = _localization.Format("Menu_OpenImageLeftFormat", codeL, ipL);
                IsOpenImagesLeftEnabled = imgs.Count >= 1;

                OpenImagesRightHeader = _localization.Format("Menu_OpenImageRightFormat", codeR, ipR);
                IsOpenImagesRightEnabled = imgs.Count >= 2;
            }
            else
            {
                OpenImagesBothHeader = _localization.Format("Menu_OpenImagesBothFormat", pShort);
                IsOpenImagesBothEnabled = settings is null;
                IsOpenImagesLeftEnabled = settings is null;
                IsOpenImagesRightEnabled = settings is null;
            }
        }
    }

    /// <summary>
    /// Formats one or more <see cref="DisplayRowViewModel"/> items as tab-separated values for clipboard copy.
    /// </summary>
    public static string FormatSelectedRows(IEnumerable<DisplayRowViewModel> rows, bool includeHeader)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var sb = new StringBuilder();
        if (includeHeader)
        {
            sb.AppendLine("#\tMachine\tTimestamp\tPMNT Code\tRun Count\tPanel Name\tBlu Name\tStatus");
        }

        foreach (var it in rows)
        {
            if (it is null) continue;
            sb.Append(it.No).Append('\t')
              .Append(it.Source).Append('\t')
              .Append(it.TimeFormatted).Append('\t')
              .Append(it.PMNT).Append('\t')
              .Append(it.RunDisplay).Append('\t')
              .Append(it.PanelName).Append('\t')
              .Append(it.BluName).Append('\t')
              .Append(it.StatusText).AppendLine();
        }

        return sb.ToString().TrimEnd('\r', '\n');
    }
}
