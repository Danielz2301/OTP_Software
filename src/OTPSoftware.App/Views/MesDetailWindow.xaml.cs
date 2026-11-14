namespace OTPSoftware.App.Views;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OTPSoftware.App.Services;
using OTPSoftware.App.ViewModels;

/// <summary>
/// Interaction logic for MesDetailWindow.xaml, including hardware-accelerated
/// mouse-wheel zoom, click-and-drag pan, and double-click reset for camera defect inspection.
/// </summary>
public partial class MesDetailWindow : Window
{
    private readonly MesDetailViewModel _viewModel;
    private bool _isImagePanning;
    private Point _imagePanStartPt;
    private double _imagePanStartH;
    private double _imagePanStartV;
    private Point? _pendingZoomAnchor;
    private Border? _activeViewportContainer;

    public MesDetailWindow(MesDetailViewModel viewModel)
    {
        InitializeComponent();
        Language = XmlLanguage.GetLanguage(LocalizationService.GetIetfLanguageTag(LocalizationService.Default.CurrentLanguage));
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;
        viewModel.ImageZoomChanged += OnViewModelImageZoomChanged;
        Closed += OnWindowClosed;
    }

    private void OnRequestClose() => Close();

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _viewModel.RequestClose -= OnRequestClose;
        _viewModel.ImageZoomChanged -= OnViewModelImageZoomChanged;
        _viewModel.Dispose();
    }

    private Border? GetActiveViewportContainer(Point? hostRelativePoint = null)
    {
        if (!_viewModel.IsSideBySideMode)
        {
            _activeViewportContainer = bdrCameraViewportHost;
            return bdrCameraViewportHost;
        }

        if (hostRelativePoint is Point pt &&
            bdrRightComparePane is { ActualWidth: > 0 } &&
            bdrCameraViewportHost is not null)
        {
            Point rightOrigin = bdrRightComparePane.TranslatePoint(new Point(0, 0), bdrCameraViewportHost);
            if (pt.X >= rightOrigin.X)
            {
                _activeViewportContainer = bdrRightComparePane;
                return bdrRightComparePane;
            }

            if (bdrLeftComparePane is { ActualWidth: > 0 })
            {
                _activeViewportContainer = bdrLeftComparePane;
                return bdrLeftComparePane;
            }
        }

        if (_activeViewportContainer == bdrRightComparePane && bdrRightComparePane is { ActualWidth: > 0 })
        {
            return bdrRightComparePane;
        }

        if (bdrLeftComparePane is { ActualWidth: > 0 })
        {
            _activeViewportContainer = bdrLeftComparePane;
            return bdrLeftComparePane;
        }

        return bdrCameraViewportHost;
    }

    private (double X, double Y) GetClampedImagePan(
        double panX,
        double panY,
        double zoom,
        Border? containerOverride = null)
    {
        if (zoom <= 1.001)
        {
            return (0.0, 0.0);
        }

        var container = containerOverride ?? GetActiveViewportContainer();
        if (container is null)
        {
            return (0.0, 0.0);
        }

        double cw = Math.Max(1.0, container.ActualWidth);
        double ch = Math.Max(1.0, container.ActualHeight);
        double minX = -(cw * (zoom - 1.0));
        double minY = -(ch * (zoom - 1.0));

        return (
            Math.Clamp(panX, minX, 0.0),
            Math.Clamp(panY, minY, 0.0)
        );
    }

    private (ScaleTransform? Scale, TranslateTransform? Translate) GetPrimaryTransforms()
    {
        if (!_viewModel.IsSideBySideMode)
        {
            return (imgScaleTransform, imgTranslateTransform);
        }

        return ReferenceEquals(_activeViewportContainer, bdrRightComparePane)
            ? (rightCompareScaleTransform, rightCompareTranslateTransform)
            : (leftCompareScaleTransform, leftCompareTranslateTransform);
    }

    private void OnViewModelImageZoomChanged(object? sender, EventArgs e)
    {
        var anchor = _pendingZoomAnchor;
        _pendingZoomAnchor = null;
        ApplyImageZoomAnimation(_viewModel.ImageZoomFactor, anchor);
    }

    private void ApplyImageZoomAnimation(double targetZoom, Point? anchorInPane)
    {
        var (primaryScale, primaryTranslate) = GetPrimaryTransforms();
        if (primaryScale is null || primaryTranslate is null) return;

        double clamped = Math.Clamp(targetZoom, MesDetailViewModel.MinImageZoom, MesDetailViewModel.MaxImageZoom);
        double curScale = Math.Max(MesDetailViewModel.MinImageZoom, primaryScale.ScaleX);
        double curPanX = primaryTranslate.X;
        double curPanY = primaryTranslate.Y;

        var container = GetActiveViewportContainer();
        double cw = container is { ActualWidth: > 0 } ? container.ActualWidth : 480.0;
        double ch = container is { ActualHeight: > 0 } ? container.ActualHeight : 360.0;

        double anchorX = anchorInPane?.X ?? (cw / 2.0);
        double anchorY = anchorInPane?.Y ?? (ch / 2.0);

        double targetPanX = 0.0;
        double targetPanY = 0.0;
        if (clamped > 1.001)
        {
            double ratio = clamped / curScale;
            double rawPanX = anchorX - ((anchorX - curPanX) * ratio);
            double rawPanY = anchorY - ((anchorY - curPanY) * ratio);
            (targetPanX, targetPanY) = GetClampedImagePan(rawPanX, rawPanY, clamped, container);
        }
        else
        {
            _activeViewportContainer = null;
        }

        if (bdrCameraViewportHost is not null)
        {
            bdrCameraViewportHost.Cursor = clamped > 1.01 ? Cursors.SizeAll : Cursors.Arrow;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(160));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var animScale = new DoubleAnimation(curScale, clamped, duration) { EasingFunction = ease };
        animScale.Freeze();

        if (_viewModel.IsSideBySideMode)
        {
            var (leftPanX, leftPanY) = clamped > 1.001
                ? GetClampedImagePan(targetPanX, targetPanY, clamped, bdrLeftComparePane)
                : (0.0, 0.0);
            var (rightPanX, rightPanY) = clamped > 1.001
                ? GetClampedImagePan(targetPanX, targetPanY, clamped, bdrRightComparePane)
                : (0.0, 0.0);

            AnimateTransformPair(imgScaleTransform, imgTranslateTransform, animScale,
                CreateFrozenDoubleAnimation(curPanX, targetPanX, duration, ease),
                CreateFrozenDoubleAnimation(curPanY, targetPanY, duration, ease));

            double curLeftX = leftCompareTranslateTransform?.X ?? curPanX;
            double curLeftY = leftCompareTranslateTransform?.Y ?? curPanY;
            AnimateTransformPair(leftCompareScaleTransform, leftCompareTranslateTransform, animScale,
                CreateFrozenDoubleAnimation(curLeftX, leftPanX, duration, ease),
                CreateFrozenDoubleAnimation(curLeftY, leftPanY, duration, ease));

            double curRightX = rightCompareTranslateTransform?.X ?? curPanX;
            double curRightY = rightCompareTranslateTransform?.Y ?? curPanY;
            AnimateTransformPair(rightCompareScaleTransform, rightCompareTranslateTransform, animScale,
                CreateFrozenDoubleAnimation(curRightX, rightPanX, duration, ease),
                CreateFrozenDoubleAnimation(curRightY, rightPanY, duration, ease));
        }
        else
        {
            var animPanX = CreateFrozenDoubleAnimation(curPanX, targetPanX, duration, ease);
            var animPanY = CreateFrozenDoubleAnimation(curPanY, targetPanY, duration, ease);

            AnimateTransformPair(imgScaleTransform, imgTranslateTransform, animScale, animPanX, animPanY);
            AnimateTransformPair(leftCompareScaleTransform, leftCompareTranslateTransform, animScale, animPanX, animPanY);
            AnimateTransformPair(rightCompareScaleTransform, rightCompareTranslateTransform, animScale, animPanX, animPanY);
        }
    }

    private static DoubleAnimation CreateFrozenDoubleAnimation(
        double from,
        double to,
        Duration duration,
        IEasingFunction ease)
    {
        var anim = new DoubleAnimation(from, to, duration) { EasingFunction = ease };
        anim.Freeze();
        return anim;
    }

    private static void AnimateTransformPair(
        ScaleTransform? scale,
        TranslateTransform? translate,
        DoubleAnimation animScale,
        DoubleAnimation animPanX,
        DoubleAnimation animPanY)
    {
        if (scale is null || translate is null) return;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animScale);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animScale);
        translate.BeginAnimation(TranslateTransform.XProperty, animPanX);
        translate.BeginAnimation(TranslateTransform.YProperty, animPanY);
    }

    private static void SetTransformTranslationImmediate(TranslateTransform? translate, double x, double y)
    {
        if (translate is null) return;
        translate.BeginAnimation(TranslateTransform.XProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        translate.X = x;
        translate.Y = y;
    }

    private void OnCameraViewportPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (bdrCameraViewportHost is null) return;
        e.Handled = true;

        Point hostPt = e.GetPosition(bdrCameraViewportHost);
        var container = GetActiveViewportContainer(hostPt);
        _pendingZoomAnchor = container is not null ? e.GetPosition(container) : hostPt;

        _viewModel.ZoomImageBy(e.Delta > 0 ? 1.25 : (1.0 / 1.25));
    }

    private bool IsFromInspectorToolbar(DependencyObject? source)
    {
        if (pnlInspectorToolbar is null || source is null) return false;

        DependencyObject? current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, pnlInspectorToolbar))
            {
                return true;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private void OnInspectorToolbarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void OnCameraViewportMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (bdrCameraViewportHost is null) return;
        if (IsFromInspectorToolbar(e.OriginalSource as DependencyObject)) return;

        if (e.ClickCount == 2)
        {
            StopImagePanning();
            _pendingZoomAnchor = null;
            _viewModel.ResetImageZoom();
            e.Handled = true;
            return;
        }

        if (_viewModel.ImageZoomFactor <= 1.01) return;

        Point hostPt = e.GetPosition(bdrCameraViewportHost);
        _activeViewportContainer = GetActiveViewportContainer(hostPt);

        var (_, primaryTranslate) = GetPrimaryTransforms();
        if (primaryTranslate is null) return;

        _isImagePanning = true;
        _imagePanStartPt = hostPt;
        _imagePanStartH = primaryTranslate.X;
        _imagePanStartV = primaryTranslate.Y;
        bdrCameraViewportHost.CaptureMouse();
        e.Handled = true;
    }

    private void OnParameterSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape &&
            (!string.IsNullOrEmpty(_viewModel.ParameterSearchText) || _viewModel.ShowNgOnly))
        {
            _viewModel.ParameterSearchText = string.Empty;
            _viewModel.ShowNgOnly = false;
            e.Handled = true;
        }
    }

    private void OnCameraViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isImagePanning || bdrCameraViewportHost is null) return;

        Point curPt = e.GetPosition(bdrCameraViewportHost);
        double dx = curPt.X - _imagePanStartPt.X;
        double dy = curPt.Y - _imagePanStartPt.Y;
        double rawX = _imagePanStartH + dx;
        double rawY = _imagePanStartV + dy;
        double zoom = _viewModel.ImageZoomFactor;

        if (_viewModel.IsSideBySideMode)
        {
            var (leftX, leftY) = GetClampedImagePan(rawX, rawY, zoom, bdrLeftComparePane);
            var (rightX, rightY) = GetClampedImagePan(rawX, rawY, zoom, bdrRightComparePane);
            SetTransformTranslationImmediate(leftCompareTranslateTransform, leftX, leftY);
            SetTransformTranslationImmediate(rightCompareTranslateTransform, rightX, rightY);
        }
        else
        {
            var (clampedX, clampedY) = GetClampedImagePan(rawX, rawY, zoom, bdrCameraViewportHost);
            SetTransformTranslationImmediate(imgTranslateTransform, clampedX, clampedY);
            SetTransformTranslationImmediate(leftCompareTranslateTransform, clampedX, clampedY);
            SetTransformTranslationImmediate(rightCompareTranslateTransform, clampedX, clampedY);
        }
    }

    private void OnCameraViewportMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => StopImagePanning();

    private void OnCameraViewportLostMouseCapture(object sender, MouseEventArgs e) => StopImagePanning();

    private void StopImagePanning()
    {
        if (!_isImagePanning) return;
        _isImagePanning = false;
        bdrCameraViewportHost?.ReleaseMouseCapture();
    }
}

