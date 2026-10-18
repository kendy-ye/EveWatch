using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EveWatch.Core.Capture;
using EveWatch.Core.Discovery;

namespace EveWatch.App.Windows;

/// <summary>
/// 框选 ROI：以整窗截图作底图，用户拖出矩形，保存时换算回窗口内容区的原始像素坐标。
/// <para>底图按窗口大小等比缩放显示，缩放下仍精确换算回原始像素。</para>
/// </summary>
public partial class CalibrationWindow : Window
{
    private readonly WgcCaptureService _capture;
    private readonly WindowInfo _window;
    private readonly RoiRect? _existing;

    private int _imageWidth;
    private int _imageHeight;
    private WriteableBitmap? _bitmap;

    /// <summary>手动缩放的上限（相对原始像素）。</summary>
    private const double MaxScale = 8.0;

    /// <summary>适应窗口时的缩放，同时作为缩放下限。</summary>
    private double _fitScale = 1;

    /// <summary>当前缩放：图像像素到 Stage 像素的比例。</summary>
    private double _scale = 1;

    /// <summary>图像左上角落在 Stage 中的位置；放大后为负值。</summary>
    private double _offsetX;
    private double _offsetY;

    /// <summary>用户是否手动改过缩放。为 false 时随窗口尺寸自适应。</summary>
    private bool _manualZoom;

    private bool _dragging;
    private bool _hasSelection;
    private System.Windows.Point _start;
    private System.Windows.Point _current;

    private bool _panning;
    private System.Windows.Point _panStart;
    private double _panOriginX;
    private double _panOriginY;

    private bool _failed;

    /// <summary>换算后的原始像素矩形；<c>ShowDialog()</c> 返回 true 时有效。</summary>
    public RoiRect? Result { get; private set; }

    /// <summary>用户给 ROI 起的名字，可为空。</summary>
    public string ResultLabel { get; private set; } = string.Empty;

    public CalibrationWindow(WgcCaptureService capture, WindowInfo window, RoiRect? existing, string accountName)
    {
        _capture = capture;
        _window = window;
        _existing = existing;

        InitializeComponent();

        TitleText.Text = $"框选 ROI · {accountName}";
        NameBox.Text = "本地图标列";

        Loaded += OnLoaded;
    }

    // ---------------------------------------------------------------- 取帧

    private void OnLoaded(object sender, RoutedEventArgs e) => Capture();

    private void Capture()
    {
        if (!AppServices.IsCaptureSupported)
        {
            ShowError("本机不支持 Windows Graphics Capture，无法框选 ROI。");
            return;
        }

        if (WindowFinder.IsMinimized(_window.Hwnd))
        {
            ShowError("目标窗口已最小化，WGC 无法取帧。请先恢复窗口，再重新打开框选。");
            return;
        }

        byte[]? bgra = null;
        var width = 0;
        var height = 0;
        var attachedHere = false;

        try
        {
            if (!_capture.IsAlive(_window.Hwnd))
            {
                attachedHere = _capture.Attach(_window.Hwnd, 0);
            }

            var frame = _capture.CaptureFull(_window.Hwnd);
            if (frame is not null)
            {
                // 返回的缓冲由采集会话复用，必须立刻留一份。
                bgra = (byte[])frame.Bgra.Clone();
                width = frame.Width;
                height = frame.Height;
            }
        }
        catch
        {
            // 取帧异常按失败处理
        }
        finally
        {
            if (attachedHere)
            {
                _capture.Detach(_window.Hwnd);
            }
        }

        if (bgra is null || width <= 0 || height <= 0)
        {
            ShowError("取帧失败：窗口可能已关闭，或采集通道暂不可用。请确认客户端窗口可见后重试。");
            return;
        }

        _imageWidth = width;
        _imageHeight = height;

        _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);
        Preview.Source = _bitmap;

        ImageSizeText.Text = $"底图 {width} × {height}";

        if (_existing is { IsValid: true } bounds)
        {
            _start = new System.Windows.Point(bounds.X, bounds.Y);
            _current = new System.Windows.Point(bounds.X + bounds.W, bounds.Y + bounds.H);
            _hasSelection = true;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _manualZoom = false;
            RefreshLayout(recenter: true);
            UpdateScaleHint();
            UpdateSelectionVisual();
        }));
    }

    private void ShowError(string message)
    {
        _failed = true;
        Preview.Source = null;
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
        ScaleText.Text = "—";
        CoordText.Text = "x — · y — · w — · h —";
        BtnSave.IsEnabled = false;
    }

    // ---------------------------------------------------------------- 视图变换

    /// <summary>
    /// 重算适应缩放与偏移。
    /// </summary>
    /// <param name="recenter">
    /// true 表示回到「适应窗口」：缩放取适应值并把图像居中。
    /// false 表示保留当前缩放，只把偏移夹回合法范围（窗口尺寸变化时用）。
    /// </param>
    private void RefreshLayout(bool recenter)
    {
        if (_imageWidth <= 0 || _imageHeight <= 0)
        {
            return;
        }

        var cw = Stage.ActualWidth;
        var ch = Stage.ActualHeight;
        if (cw <= 0 || ch <= 0)
        {
            return;
        }

        _fitScale = Math.Min(cw / _imageWidth, ch / _imageHeight);

        if (recenter)
        {
            _scale = _fitScale;
            _offsetX = (cw - _imageWidth * _scale) / 2;
            _offsetY = (ch - _imageHeight * _scale) / 2;
            return;
        }

        _scale = Math.Clamp(_scale, _fitScale, Math.Max(MaxScale, _fitScale));
        ClampOffset(cw, ch);
    }

    /// <summary>把偏移夹到合法范围：图比视口小时居中，比视口大时不允许拖出边界。</summary>
    private void ClampOffset(double cw, double ch)
    {
        var w = _imageWidth * _scale;
        var h = _imageHeight * _scale;

        _offsetX = w <= cw ? (cw - w) / 2 : Math.Clamp(_offsetX, cw - w, 0);
        _offsetY = h <= ch ? (ch - h) / 2 : Math.Clamp(_offsetY, ch - h, 0);
    }

    /// <summary>把当前缩放与偏移换算成底图的显示尺寸与位置。</summary>
    private void ApplyTransform()
    {
        var w = _imageWidth * _scale;
        var h = _imageHeight * _scale;

        Preview.Width = w;
        Preview.Height = h;
        Canvas.SetLeft(Preview, _offsetX);
        Canvas.SetTop(Preview, _offsetY);

        // 放大时用最近邻保留原始像素边界，缩小时用高质量避免锯齿
        RenderOptions.SetBitmapScalingMode(
            Preview,
            _scale >= 1.5 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    private bool TryToImage(System.Windows.Point point, out double ix, out double iy)
    {
        ix = iy = 0;
        if (_scale <= 0)
        {
            return false;
        }

        ix = Math.Clamp((point.X - _offsetX) / _scale, 0, _imageWidth);
        iy = Math.Clamp((point.Y - _offsetY) / _scale, 0, _imageHeight);
        return true;
    }

    /// <summary>以 <paramref name="anchor"/>（Stage 坐标）为中心把缩放设为 <paramref name="target"/>。</summary>
    private void ZoomTo(double target, System.Windows.Point anchor)
    {
        var cw = Stage.ActualWidth;
        var ch = Stage.ActualHeight;
        if (cw <= 0 || ch <= 0)
        {
            return;
        }

        var max = Math.Max(MaxScale, _fitScale);
        var clamped = Math.Clamp(target, _fitScale, max);
        if (Math.Abs(clamped - _scale) < 1e-9)
        {
            UpdateScaleHint();
            return;
        }

        // 锚点下方的图像坐标在缩放前后保持不变
        var ix = (anchor.X - _offsetX) / _scale;
        var iy = (anchor.Y - _offsetY) / _scale;

        _scale = clamped;
        _offsetX = anchor.X - ix * _scale;
        _offsetY = anchor.Y - iy * _scale;
        ClampOffset(cw, ch);

        UpdateScaleHint();
        UpdateSelectionVisual();
    }

    private void UpdateScaleHint()
    {
        if (_bitmap is null)
        {
            return;
        }

        // 适应窗口时额外标出来，避免用户以为缩放被固定住了
        var prefix = _manualZoom ? string.Empty : "适应窗口 · ";
        ScaleText.Text = $"{prefix}{_scale * 100:F0}%";
    }

    private void UpdateSelectionVisual()
    {
        if (_bitmap is null)
        {
            return;
        }

        ApplyTransform();

        if (!_hasSelection)
        {
            SelRect.Visibility = Visibility.Collapsed;
            return;
        }

        var minX = Math.Min(_start.X, _current.X);
        var minY = Math.Min(_start.Y, _current.Y);
        var w = Math.Abs(_current.X - _start.X);
        var h = Math.Abs(_current.Y - _start.Y);

        SelRect.Visibility = Visibility.Visible;
        SelRect.Width = w * _scale;
        SelRect.Height = h * _scale;
        Canvas.SetLeft(SelRect, _offsetX + minX * _scale);
        Canvas.SetTop(SelRect, _offsetY + minY * _scale);

        CoordText.Text = $"x {(int)Math.Round(minX)} · y {(int)Math.Round(minY)} · w {(int)Math.Round(w)} · h {(int)Math.Round(h)}";
    }

    // ---------------------------------------------------------------- 鼠标交互

    private void OnStageMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_failed || _bitmap is null)
        {
            return;
        }

        // 右键或中键拖动 = 平移画面
        if (e.ChangedButton is MouseButton.Middle or MouseButton.Right)
        {
            _panning = true;
            _panStart = e.GetPosition(Stage);
            _panOriginX = _offsetX;
            _panOriginY = _offsetY;
            _ = Stage.CaptureMouse();
            Stage.Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (!TryToImage(e.GetPosition(Stage), out var ix, out var iy))
        {
            return;
        }

        _dragging = true;
        _hasSelection = true;
        _start = new System.Windows.Point(ix, iy);
        _current = _start;
        _ = Stage.CaptureMouse();
        UpdateSelectionVisual();
    }

    private void OnStageMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_panning)
        {
            var position = e.GetPosition(Stage);
            _offsetX = _panOriginX + (position.X - _panStart.X);
            _offsetY = _panOriginY + (position.Y - _panStart.Y);
            ClampOffset(Stage.ActualWidth, Stage.ActualHeight);
            UpdateSelectionVisual();
            return;
        }

        if (!_dragging)
        {
            return;
        }

        if (!TryToImage(e.GetPosition(Stage), out var ix, out var iy))
        {
            return;
        }

        _current = new System.Windows.Point(ix, iy);
        UpdateSelectionVisual();
    }

    private void OnStageMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_panning && e.ChangedButton is MouseButton.Middle or MouseButton.Right)
        {
            _panning = false;
            Stage.ReleaseMouseCapture();
            Stage.Cursor = Cursors.Cross;
            e.Handled = true;
            return;
        }

        if (!_dragging || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _dragging = false;
        Stage.ReleaseMouseCapture();
        UpdateSelectionVisual();
    }

    /// <summary>滚轮缩放，以光标所在位置为中心：光标下的那个像素缩放前后停在原地。</summary>
    private void OnStageMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_failed || _bitmap is null || e.Delta == 0)
        {
            return;
        }

        var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;

        _manualZoom = true;
        ZoomTo(_scale * factor, e.GetPosition(Stage));

        e.Handled = true;
    }

    private void OnZoomPresetClick(object sender, RoutedEventArgs e)
    {
        if (_failed || _bitmap is null || (sender as FrameworkElement)?.Tag is not string tag)
        {
            return;
        }

        var target = tag switch
        {
            "fit" => _fitScale,
            "1" => 1.0,
            "2" => 2.0,
            "4" => 4.0,
            _ => _scale,
        };

        // 回到适应窗口后要跟随窗口尺寸，其余预设视为固定缩放
        _manualZoom = tag != "fit";

        ZoomTo(target, new System.Windows.Point(Stage.ActualWidth / 2, Stage.ActualHeight / 2));
    }

    private void OnStageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RefreshLayout(recenter: !_manualZoom);
        UpdateScaleHint();
        UpdateSelectionVisual();
    }

    // ---------------------------------------------------------------- 操作

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_failed || _bitmap is null)
        {
            DialogResult = false;
            return;
        }

        if (!_hasSelection)
        {
            Warn("请先在底图上按住左键拖出一个矩形区域。");
            return;
        }

        var minX = Math.Min(_start.X, _current.X);
        var minY = Math.Min(_start.Y, _current.Y);
        var w = Math.Abs(_current.X - _start.X);
        var h = Math.Abs(_current.Y - _start.Y);

        var x = (int)Math.Round(Math.Clamp(minX, 0, _imageWidth));
        var y = (int)Math.Round(Math.Clamp(minY, 0, _imageHeight));
        var rw = Math.Clamp((int)Math.Round(w), 0, _imageWidth - x);
        var rh = Math.Clamp((int)Math.Round(h), 0, _imageHeight - y);

        if (rw <= 0 || rh <= 0)
        {
            Warn("矩形宽度与高度必须大于 0，请重新框选。");
            return;
        }

        Result = new RoiRect(x, y, rw, rh);
        ResultLabel = NameBox.Text.Trim();
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Warn(string text) =>
        MessageBox.Show(this, text, "框选 ROI", MessageBoxButton.OK, MessageBoxImage.Warning);
}
