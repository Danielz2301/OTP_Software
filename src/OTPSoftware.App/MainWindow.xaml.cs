using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OTPSoftware.App.ViewModels;
using OTPSoftware.Core.Display;
using OTPSoftware.Core.Models;

namespace OTPSoftware.App;

/// <summary>
/// Code-behind for <see cref="MainWindow"/> wiring <see cref="MainViewModel"/> to WPF view-specific
/// interactions (window lifecycle, Kiosk fullscreen transitions, chart Canvas rendering/zoom/pan,
/// and DataGrid cell/row/context-menu/auto-scroll interactions).
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel? _attachedViewModel;
    private ChartViewModel? _attachedChartViewModel;
    private readonly Views.Charts.ChartCanvasRenderer _desktopChartRenderer = new();
    private readonly Views.Charts.ChartCanvasRenderer _wallboardChartRenderer = new();
    private INotifyCollectionChanged? _attachedDisplayList;

    private bool _appliedKioskMode;
    private WindowState _prevWindowState = WindowState.Normal;
    private WindowStyle _prevWindowStyle = WindowStyle.SingleBorderWindow;

    private bool _isChartDragPending;
    private bool _isChartPanning;
    private bool _chartRenderPending;
    private bool _scrollPending;
    private string _rightClickColProp = string.Empty;
    private bool _hasRightClickColProp;
    private int _resultsMenuGeneration;
    private int _pmntMenuGeneration;
    private Point _chartPanStartPt;
    private double _chartPanStartH;
    private double _chartPanStartV;
    private Point? _pendingZoomAnchor;

    public MainWindow()
    {
        InitializeComponent();
        RegisterViewEvents();
    }

    public MainWindow(MainViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    public MainViewModel? ViewModel => DataContext as MainViewModel;

    private void RegisterViewEvents()
    {
        DataContextChanged += OnDataContextChanged;
        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;
        PreviewKeyDown += OnWindowPreviewKeyDown;

        if (icMachines is not null)
        {
            icMachines.KeyDown += OnMachinesKeyDown;
        }

        if (mainTabControl is not null)
        {
            mainTabControl.SelectionChanged += OnMainTabSelectionChanged;
        }

        if (bdrChartContainer is not null)
        {
            bdrChartContainer.SizeChanged += OnChartContainerSizeChanged;
            bdrChartContainer.PreviewMouseWheel += OnChartContainerPreviewMouseWheel;
        }

        if (cvErrorChart is not null)
        {
            cvErrorChart.SizeChanged += CvErrorChart_SizeChanged;
            cvErrorChart.MouseLeftButtonDown += OnChartCanvasMouseLeftButtonDown;
            cvErrorChart.MouseMove += OnChartCanvasMouseMove;
            cvErrorChart.MouseLeftButtonUp += OnChartCanvasMouseLeftButtonUp;
            cvErrorChart.LostMouseCapture += OnChartCanvasLostMouseCapture;
        }

        if (cvWallboardChart is not null)
        {
            cvWallboardChart.SizeChanged += CvErrorChart_SizeChanged;
        }

        if (dgResults is not null)
        {
            dgResults.PreviewMouseRightButtonDown += OnResultsPreviewMouseRightButtonDown;
            dgResults.ContextMenuOpening += OnResultsContextMenuOpening;
            dgResults.MouseDoubleClick += OnResultsMouseDoubleClick;
            dgResults.PreviewKeyDown += OnResultsPreviewKeyDown;
        }

        if (cmResults is not null)
        {
            cmResults.Closed += OnResultsContextMenuClosed;
        }

        if (dgPmntA is not null)
        {
            dgPmntA.PreviewMouseRightButtonDown += OnPmntPreviewMouseRightButtonDown;
            dgPmntA.ContextMenuOpening += OnPmntContextMenuOpening;
            dgPmntA.MouseLeftButtonUp += OnPmntMouseLeftButtonUp;
        }

        if (dgPmntB is not null)
        {
            dgPmntB.PreviewMouseRightButtonDown += OnPmntPreviewMouseRightButtonDown;
            dgPmntB.ContextMenuOpening += OnPmntContextMenuOpening;
            dgPmntB.MouseLeftButtonUp += OnPmntMouseLeftButtonUp;
        }

        if (dgPmntOther is not null)
        {
            dgPmntOther.PreviewMouseRightButtonDown += OnPmntPreviewMouseRightButtonDown;
            dgPmntOther.ContextMenuOpening += OnPmntContextMenuOpening;
            dgPmntOther.MouseLeftButtonUp += OnPmntMouseLeftButtonUp;
        }

        if (cmPmntA is not null) cmPmntA.Closed += OnPmntContextMenuClosed;
        if (cmPmntB is not null) cmPmntB.Closed += OnPmntContextMenuClosed;
        if (cmPmntOther is not null) cmPmntOther.Closed += OnPmntContextMenuClosed;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachViewModel();

        if (e.NewValue is MainViewModel vm)
        {
            AttachViewModel(vm);
        }
    }

    private void AttachViewModel(MainViewModel vm)
    {
        _attachedViewModel = vm;
        _attachedChartViewModel = vm.Chart;

        vm.PropertyChanged += OnViewModelPropertyChanged;
        vm.ChartRefreshRequested += OnViewModelChartRefreshRequested;
        vm.AutoScrollRequested += OnViewModelAutoScrollRequested;
        vm.KioskModeChanged += OnViewModelKioskModeChanged;
        vm.GridActions.SelectedRowsProvider = GetSelectedResultRows;

        if (_attachedChartViewModel is not null)
        {
            _attachedChartViewModel.PropertyChanged += OnChartViewModelPropertyChanged;
            _attachedChartViewModel.FilterChanged += OnChartViewModelFilterChanged;
            _attachedChartViewModel.ZoomChanged += OnChartViewModelZoomChanged;
            _attachedChartViewModel.LocalizationRefreshed += OnChartViewModelLocalizationRefreshed;
        }

        AttachDisplayList(vm.DisplayList);

        AssignContextMenuDataContext(vm);

        ApplyKioskMode(vm.IsKioskMode);
    }

    private void DetachViewModel()
    {
        DetachDisplayList();

        if (_attachedChartViewModel is not null)
        {
            _attachedChartViewModel.PropertyChanged -= OnChartViewModelPropertyChanged;
            _attachedChartViewModel.FilterChanged -= OnChartViewModelFilterChanged;
            _attachedChartViewModel.ZoomChanged -= OnChartViewModelZoomChanged;
            _attachedChartViewModel.LocalizationRefreshed -= OnChartViewModelLocalizationRefreshed;
            _attachedChartViewModel = null;
        }

        _desktopChartRenderer.Detach();
        _wallboardChartRenderer.Detach();

        if (_attachedViewModel is not null)
        {
            _attachedViewModel.GridActions.SelectedRowsProvider = null;
            _attachedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _attachedViewModel.ChartRefreshRequested -= OnViewModelChartRefreshRequested;
            _attachedViewModel.AutoScrollRequested -= OnViewModelAutoScrollRequested;
            _attachedViewModel.KioskModeChanged -= OnViewModelKioskModeChanged;
            _attachedViewModel = null;
        }

        AssignContextMenuDataContext(null);
    }

    private void AssignContextMenuDataContext(MainViewModel? vm)
    {
        if (cmResults is not null) cmResults.DataContext = vm;
        if (cmPmntA is not null) cmPmntA.DataContext = vm;
        if (cmPmntB is not null) cmPmntB.DataContext = vm;
        if (cmPmntOther is not null) cmPmntOther.DataContext = vm;
    }

    private void AttachDisplayList(INotifyCollectionChanged? list)
    {
        if (ReferenceEquals(_attachedDisplayList, list)) return;
        DetachDisplayList();
        _attachedDisplayList = list;
        if (_attachedDisplayList is not null)
        {
            _attachedDisplayList.CollectionChanged += OnDisplayListCollectionChanged;
        }
    }

    private void DetachDisplayList()
    {
        if (_attachedDisplayList is not null)
        {
            _attachedDisplayList.CollectionChanged -= OnDisplayListCollectionChanged;
            _attachedDisplayList = null;
        }
    }

    // --- Window Lifecycle & Shortcuts ---

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        try
        {
            ApplyKioskMode(vm.IsKioskMode);
            await vm.OnLoadedAsync();

            if (vm.SelectedTabIndex == 2)
            {
                ScheduleChartRender();
            }
        }
        catch (Exception ex)
        {
            vm.ReportUnexpectedUiError(ex, "MainWindow.OnWindowLoaded");
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        ViewModel?.OnWindowClosing();
        DetachViewModel();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        if (e.Key == Key.F11)
        {
            vm.IsKioskMode = !vm.IsKioskMode;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && vm.IsKioskMode)
        {
            vm.IsKioskMode = false;
            e.Handled = true;
        }
    }

    private void OnMachinesKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.OriginalSource is TextBox tb && ViewModel is { } vm)
        {
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            if (vm.LoadLogCommand.CanExecute(null))
            {
                vm.LoadLogCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    // --- ViewModel Notifications & Kiosk Mode ---

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsKioskMode):
                ApplyKioskMode(vm.IsKioskMode);
                break;

            case nameof(MainViewModel.WallboardCarouselIndex):
                if (vm.IsKioskMode && vm.IsWallboardChartSlide)
                {
                    ScheduleChartRender();
                }
                break;

            case nameof(MainViewModel.DisplayList):
                AttachDisplayList(vm.DisplayList);
                ScrollToLatestIfEnabled();
                break;

            case nameof(MainViewModel.SelectedTabIndex):
                if (vm.SelectedTabIndex == 2)
                {
                    ScheduleChartRender();
                }
                break;

            case nameof(MainViewModel.CurrentLanguage):
                RefreshDataGridColumnHeaders(this);
                break;
        }
    }

    private static void RefreshDataGridColumnHeaders(DependencyObject? root)
    {
        if (root is null) return;

        if (root is DataGrid dg)
        {
            foreach (var col in dg.Columns)
            {
                col.InvalidateProperty(DataGridColumn.HeaderProperty);
            }
            return;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is DependencyObject depChild)
            {
                RefreshDataGridColumnHeaders(depChild);
            }
        }
    }

    private void OnViewModelKioskModeChanged(object? sender, bool enable)
    {
        ApplyKioskMode(enable);
    }

    private void ApplyKioskMode(bool enable)
    {
        if (enable && !_appliedKioskMode)
        {
            _prevWindowState = WindowState;
            _prevWindowStyle = WindowStyle;
            _appliedKioskMode = true;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else if (!enable && _appliedKioskMode)
        {
            _appliedKioskMode = false;
            WindowStyle = _prevWindowStyle;
            WindowState = _prevWindowState;
        }
    }

    private void OnViewModelAutoScrollRequested(object? sender, DisplayRowViewModel row)
    {
        ScrollToLatestIfEnabled();
    }

    private void OnDisplayListCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
        {
            ScrollToLatestIfEnabled();
        }
    }

    private void ScrollToLatestIfEnabled()
    {
        if (dgResults is null || ViewModel is not { AutoScroll: true } || _scrollPending) return;
        _scrollPending = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _scrollPending = false;
            if (dgResults is null || ViewModel is not { AutoScroll: true } vm) return;
            if (vm.DisplayList.Count > 0)
            {
                try
                {
                    dgResults.ScrollIntoView(vm.DisplayList[^1]);
                }
                catch
                {
                    // Ignore virtualization layout race during rapid updates
                }
            }
        }));
    }

    // --- Chart Rendering, Zoom & Pan ---

    private void OnMainTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, mainTabControl) && mainTabControl.SelectedIndex == 2)
        {
            ScheduleChartRender();
        }
    }

    private void OnChartContainerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (mainTabControl is { SelectedIndex: 2 } || ViewModel is { IsKioskMode: true })
        {
            ScheduleChartRender();
        }
    }

    private void CvErrorChart_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (mainTabControl is { SelectedIndex: 2 } || ViewModel is { IsKioskMode: true })
        {
            ScheduleChartRender();
        }
    }

    private void OnViewModelChartRefreshRequested(object? sender, EventArgs e)
    {
        if (mainTabControl is { SelectedIndex: 2 } || ViewModel is { IsKioskMode: true })
        {
            ScheduleChartRender();
        }
    }

    private void OnChartViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChartViewModel.ShowLines)
            or nameof(ChartViewModel.ShowLabels))
        {
            if (mainTabControl is { SelectedIndex: 2 } || ViewModel is { IsKioskMode: true })
            {
                ScheduleChartRender();
            }
        }
    }

    private void OnChartViewModelFilterChanged(object? sender, EventArgs e)
    {
        if (mainTabControl is { SelectedIndex: 2 } || ViewModel is { IsKioskMode: true })
        {
            ScheduleChartRender();
        }
    }

    private void OnChartViewModelZoomChanged(object? sender, EventArgs e)
    {
        if (ViewModel?.Chart is { } chart)
        {
            var anchor = _pendingZoomAnchor;
            _pendingZoomAnchor = null;
            ApplyChartZoomAnimation(chart.ZoomFactor, anchor);
        }
    }

    private void OnChartViewModelLocalizationRefreshed(object? sender, EventArgs e)
    {
        _desktopChartRenderer.InvalidateTooltip();
        _wallboardChartRenderer.InvalidateTooltip();

        if (mainTabControl is { SelectedIndex: 2 } || ViewModel is { IsKioskMode: true })
        {
            ScheduleChartRender();
        }
    }

    private void ScheduleChartRender()
    {
        if (_chartRenderPending) return;
        _chartRenderPending = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _chartRenderPending = false;
            RefreshChartNow();
        }));
    }

    public void RefreshChartNow()
    {
        if (ViewModel is not { } vm) return;

        if (vm.IsKioskMode)
        {
            if (vm.IsWallboardChartSlide && cvWallboardChart is not null &&
                (cvWallboardChart.IsVisible || cvWallboardChart.ActualWidth > 0))
            {
                _wallboardChartRenderer.Render(
                    cvWallboardChart,
                    vm.Chart,
                    vm.ScopeUnits,
                    vm.OnChartPmntClick,
                    vm.OnChartPmntRightClick);
            }
            return;
        }

        if (cvErrorChart is not null && (vm.SelectedTabIndex == 2 || cvErrorChart.IsVisible))
        {
            _desktopChartRenderer.Render(
                cvErrorChart,
                vm.Chart,
                vm.ScopeUnits,
                vm.OnChartPmntClick,
                vm.OnChartPmntRightClick);
        }
    }

    private (double X, double Y) GetClampedChartPan(double panX, double panY, double zoom)
    {
        if (zoom <= 1.001 || bdrChartContainer is null)
        {
            return (0.0, 0.0);
        }

        double cw = Math.Max(1.0, bdrChartContainer.ActualWidth);
        double ch = Math.Max(1.0, bdrChartContainer.ActualHeight);
        double minX = -(cw * (zoom - 1.0));
        double minY = -(ch * (zoom - 1.0));

        return (
            Math.Clamp(panX, minX, 0.0),
            Math.Clamp(panY, minY, 0.0)
        );
    }

    private void ApplyChartZoomAnimation(double targetZoom, Point? mousePosInContainer = null)
    {
        if (chartScaleTransform is null || chartTranslateTransform is null) return;

        double clamped = Math.Clamp(Math.Round(targetZoom, 2), 1.0, 8.0);
        double curScale = Math.Max(1.0, chartScaleTransform.ScaleX);
        double curPanX = chartTranslateTransform.X;
        double curPanY = chartTranslateTransform.Y;

        double cw = bdrChartContainer is { ActualWidth: > 0 } ? bdrChartContainer.ActualWidth : 800.0;
        double ch = bdrChartContainer is { ActualHeight: > 0 } ? bdrChartContainer.ActualHeight : 400.0;

        double anchorX = mousePosInContainer?.X ?? (cw / 2.0);
        double anchorY = mousePosInContainer?.Y ?? (ch / 2.0);

        double targetPanX = 0.0;
        double targetPanY = 0.0;
        if (clamped > 1.001)
        {
            double ratio = clamped / curScale;
            double rawPanX = anchorX - ((anchorX - curPanX) * ratio);
            double rawPanY = anchorY - ((anchorY - curPanY) * ratio);
            (targetPanX, targetPanY) = GetClampedChartPan(rawPanX, rawPanY, clamped);
        }

        if (cvErrorChart is not null)
        {
            cvErrorChart.Cursor = clamped > 1.01 ? Cursors.SizeAll : Cursors.Arrow;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(180));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var animScale = new DoubleAnimation(curScale, clamped, duration) { EasingFunction = ease };
        animScale.Freeze();
        var animPanX = new DoubleAnimation(curPanX, targetPanX, duration) { EasingFunction = ease };
        animPanX.Freeze();
        var animPanY = new DoubleAnimation(curPanY, targetPanY, duration) { EasingFunction = ease };
        animPanY.Freeze();

        chartScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, animScale);
        chartScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, animScale);
        chartTranslateTransform.BeginAnimation(TranslateTransform.XProperty, animPanX);
        chartTranslateTransform.BeginAnimation(TranslateTransform.YProperty, animPanY);
    }

    private void OnChartContainerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ViewModel?.Chart is not { } chart || bdrChartContainer is null) return;

        e.Handled = true;
        _pendingZoomAnchor = e.GetPosition(bdrChartContainer);
        chart.StepZoom(e.Delta > 0 ? 1 : -1);
    }

    private void OnChartCanvasMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel?.Chart is not { } chart || cvErrorChart is null || bdrChartContainer is null || chartTranslateTransform is null)
            return;

        if (chart.ZoomFactor > 1.01)
        {
            _isChartDragPending = true;
            _chartPanStartPt = e.GetPosition(bdrChartContainer);
            _chartPanStartH = chartTranslateTransform.X;
            _chartPanStartV = chartTranslateTransform.Y;
        }
    }

    private void OnChartCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (ViewModel?.Chart is not { } chart || bdrChartContainer is null || chartTranslateTransform is null || cvErrorChart is null)
            return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            StopChartPanning();
            return;
        }

        Point curPt = e.GetPosition(bdrChartContainer);
        double dx = curPt.X - _chartPanStartPt.X;
        double dy = curPt.Y - _chartPanStartPt.Y;

        if (_isChartDragPending)
        {
            if (Math.Abs(dx) <= 4.0 && Math.Abs(dy) <= 4.0)
                return;

            _isChartDragPending = false;
            _isChartPanning = true;
            chart.IsPanning = true;
            cvErrorChart.CaptureMouse();
        }

        if (!_isChartPanning) return;

        var (clampedX, clampedY) = GetClampedChartPan(_chartPanStartH + dx, _chartPanStartV + dy, chart.ZoomFactor);

        chartTranslateTransform.BeginAnimation(TranslateTransform.XProperty, null);
        chartTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);
        chartTranslateTransform.X = clampedX;
        chartTranslateTransform.Y = clampedY;
    }

    private void OnChartCanvasMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        bool wasPanning = _isChartPanning;
        StopChartPanning();
        if (wasPanning)
        {
            e.Handled = true;
        }
    }

    private void OnChartCanvasLostMouseCapture(object sender, MouseEventArgs e) => StopChartPanning();

    private void StopChartPanning()
    {
        _isChartDragPending = false;
        if (!_isChartPanning) return;
        _isChartPanning = false;
        if (ViewModel?.Chart is { } chart)
        {
            chart.IsPanning = false;
        }
        cvErrorChart?.ReleaseMouseCapture();
    }

    // --- DataGrid Interactions (Right-Click, Double-Click, Ctrl+C) ---

    private List<DisplayRowViewModel> GetSelectedResultRows() =>
        dgResults?.SelectedItems.OfType<DisplayRowViewModel>().ToList() ?? [];

    private void OnResultsPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } vm || dgResults is null) return;

        var cell = FindVisualAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell is null) return;

        var row = DataGridRow.GetRowContainingElement(cell);
        if (row?.Item is not DisplayRowViewModel item) return;

        if (!dgResults.SelectedItems.Contains(item))
        {
            dgResults.SelectedItem = item;
        }

        _rightClickColProp = GridActionsViewModel.GetColumnProperty(cell.Column);
        _hasRightClickColProp = true;
    }

    private void OnResultsContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (ViewModel is not { } vm || dgResults is null)
        {
            e.Handled = true;
            return;
        }

        var item = dgResults.SelectedItem as DisplayRowViewModel;
        if (item is null)
        {
            _hasRightClickColProp = false;
            _rightClickColProp = string.Empty;
            e.Handled = true;
            return;
        }

        string colProp;
        if (_hasRightClickColProp)
        {
            colProp = _rightClickColProp;
            _hasRightClickColProp = false;
            _rightClickColProp = string.Empty;
        }
        else
        {
            colProp = GridActionsViewModel.GetColumnProperty(dgResults.CurrentCell.Column);
        }

        _resultsMenuGeneration++;
        int selectedCount = Math.Max(1, dgResults.SelectedItems.Count);
        vm.GridActions.UpdateContextMenuState(
            item,
            colProp,
            selectedCount,
            vm.Settings.MachinesByName);
    }

    private void OnResultsContextMenuClosed(object sender, RoutedEventArgs e)
    {
        int gen = _resultsMenuGeneration;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (gen == _resultsMenuGeneration && ViewModel is { } vm)
            {
                vm.GridActions.LastRightClickedItem = null;
            }
        }));
    }

    private void OnPmntPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not DataGrid grid) return;

        var row = FindVisualAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is not PmntDefectRankItem item) return;

        vm.SuppressPmntAutoNavigate = true;
        try
        {
            grid.SelectedItem = item;
        }
        finally
        {
            vm.SuppressPmntAutoNavigate = false;
        }

        vm.SetRightClickedPmnt(item);
    }

    private void OnPmntContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not DataGrid grid)
        {
            e.Handled = true;
            return;
        }

        if (grid.SelectedItem is not PmntDefectRankItem item)
        {
            e.Handled = true;
            return;
        }

        _pmntMenuGeneration++;
        vm.SetRightClickedPmnt(item);
    }

    private void OnPmntContextMenuClosed(object sender, RoutedEventArgs e)
    {
        int gen = _pmntMenuGeneration;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (gen == _pmntMenuGeneration)
            {
                ViewModel?.SetRightClickedPmnt(null);
            }
        }));
    }

    private void OnPmntMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not DataGrid grid) return;

        var row = FindVisualAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is PmntDefectRankItem item && ReferenceEquals(grid.SelectedItem, item))
        {
            grid.SelectedItem = null;
            if (vm.FilterByPmntCommand.CanExecute(item))
            {
                vm.FilterByPmntCommand.Execute(item);
            }
        }
    }

    private void OnResultsMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        var cell = FindVisualAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell?.DataContext is not DisplayRowViewModel item) return;

        string colHeader = cell.Column?.Header switch
        {
            TextBlock tb => tb.Text,
            string s => s,
            null => string.Empty,
            var other => other.ToString() ?? string.Empty
        };
        string colProp = GridActionsViewModel.GetColumnProperty(cell.Column);
        _ = vm.CopyDoubleClickedCellAsync(item, colProp, colHeader);
    }

    private void OnResultsPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || dgResults is null) return;

        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            var selectedRows = GetSelectedResultRows();
            if (vm.CopyRowCommand.CanExecute(selectedRows))
            {
                vm.CopyRowCommand.Execute(selectedRows);
            }
            e.Handled = true;
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
