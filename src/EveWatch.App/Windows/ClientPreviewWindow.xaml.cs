using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EveWatch.Core.Config;
using EveWatch.Core.Discovery;
using EveWatch.Core.Palette;

namespace EveWatch.App.Windows;

/// <summary>
/// 单个角色的客户端预览窗：完全无边框顶层窗 + 一个铺满客户区的 DWM 实时缩略图。
/// <para>
/// 画面由桌面窗口管理器直接合成，不经过 WGC 取帧，CPU 与内存开销接近零。
/// 角色名与焦点描边画在独立的 <see cref="PreviewLabelWindow"/> 覆盖层上。
/// </para>
/// </summary>
public partial class ClientPreviewWindow : Window
{
    private const int DwmTnpRectDestination = 0x1;
    private const int DwmTnpOpacity = 0x4;
    private const int DwmTnpVisible = 0x8;
    private const int DwmTnpSourceClientAreaOnly = 0x10;

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;

    private static readonly Color DefaultActiveColor = Color.FromRgb(0x3D, 0xDC, 0x84);
    private static readonly Color DefaultLabelColor = Color.FromRgb(0xE3, 0xEB, 0xF2);

    private readonly PreviewLabelWindow _label;
    private readonly DispatcherTimer _resizeCommit;

    private nint _sourceHwnd;
    private nint _thumbnail;

    private string _accountName;
    private byte _thumbnailOpacity = 242;
    private bool _hasThumbnailProperties;
    private NativeRect _lastDestination;
    private byte _lastThumbnailOpacity;
    private Color _activeColor = DefaultActiveColor;
    private Color _labelColor = DefaultLabelColor;
    private double _outlineThickness = 2;
    private bool _showLabel = true;
    private bool _isActive;

    private Rect _requestedBounds;
    private bool _hasRequestedBounds;

    private bool _rightPressed;
    private bool _dragging;
    private int _dragStartX;
    private int _dragStartY;
    private int _dragWindowX;
    private int _dragWindowY;

    public ClientPreviewWindow(string accountName)
    {
        _accountName = accountName;

        InitializeComponent();

        Title = $"客户端预览 · {accountName}";

        _label = new PreviewLabelWindow();
        _label.SetAccountName(accountName);
        _label.SetLabelColor(_labelColor);
        _label.SetActive(false, _activeColor, _outlineThickness);

        _resizeCommit = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };
        _resizeCommit.Tick += (_, _) =>
        {
            _resizeCommit.Stop();
            BoundsCommitted?.Invoke();
        };

        // 位置在 Show 之后再用 SetWindowPos 落一次：多显示器 / 缩放下 WPF 偶尔会把
        // 首次显示的窗口放到默认级联位置，只有物理坐标是确定的。
        Loaded += (_, _) =>
        {
            // 覆盖层用所有权而不是抢 Z 序：被拥有的窗口始终在预览窗之上，
            // 不会因为点击预览窗把名字盖住再被提回来，从而产生闪烁。
            _label.Owner = this;

            RegisterThumbnail();
            ApplyRequestedBounds();
            ShowLabelWindow();
        };
        SizeChanged += (_, _) =>
        {
            UpdateThumbnail();
            SyncLabel();

            if (IsVisible)
            {
                _resizeCommit.Stop();
                _resizeCommit.Start();
            }
        };
        LocationChanged += (_, _) => SyncLabel();
        DpiChanged += (_, _) =>
        {
            UpdateThumbnail();
            SyncLabel();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                ShowLabelWindow();
            }
            else
            {
                _label.Hide();
            }
        };
        Closed += (_, _) =>
        {
            UnregisterThumbnail();

            if (_label.IsLoaded)
            {
                _label.Close();
            }
        };
    }

    /// <summary>拖动或缩放结束后触发，由管理器写回布局。</summary>
    public event Action? BoundsCommitted;

    /// <summary>单击预览窗（未拖动）时触发。</summary>
    public event Action? ClickActivated;

    public string AccountName => _accountName;

    /// <summary>更新窗口标题与画面上的角色名（角色选择预览的编号会随位置变化）。</summary>
    public void SetAccountName(string name)
    {
        if (string.Equals(_accountName, name, StringComparison.Ordinal))
        {
            return;
        }

        _accountName = name;
        Title = $"客户端预览 · {name}";
        _label.SetAccountName(name);
    }

    public void Attach(nint hwnd)
    {
        if (hwnd == _sourceHwnd)
        {
            return;
        }

        UnregisterThumbnail();
        _sourceHwnd = hwnd;

        if (IsLoaded)
        {
            RegisterThumbnail();
        }
    }

    /// <summary>应用全局设置与逐角色设置。</summary>
    public void Apply(ClientPreviewState config, ClientPreviewAccountState account)
    {
        Topmost = config.Topmost;
        _label.Topmost = config.Topmost;

        _showLabel = config.ShowLabel;
        _outlineThickness = config.ActiveBorderThickness;
        _activeColor = ParseColor(config.ActiveBorderColor, DefaultActiveColor);
        _labelColor = ParseColor(config.LabelColor, DefaultLabelColor);

        var opacity = Math.Clamp(account.Opacity ?? config.Opacity, 0.3, 1.0);
        _thumbnailOpacity = (byte)Math.Round(opacity * 255);

        _label.SetLabelVisible(_showLabel);
        _label.SetLabelColor(_labelColor);
        _label.SetActive(_isActive, _activeColor, _outlineThickness);

        UpdateThumbnail();
        SyncLabel();
    }

    /// <summary>切换活动高亮；只有当前焦点账号的预览显示描边。</summary>
    public void SetActive(bool active)
    {
        if (_isActive == active)
        {
            return;
        }

        _isActive = active;
        _label.SetActive(active, _activeColor, _outlineThickness);
        SyncLabel();
    }

    /// <summary>按保存的位置与尺寸摆放（不触发布局回写）。</summary>
    public void Place(double left, double top, double width, double height)
    {
        _requestedBounds = new Rect(
            left,
            top,
            Math.Clamp(width, ClientPreviewState.MinWidth, ClientPreviewState.MaxWidth),
            Math.Clamp(height, ClientPreviewState.MinHeight, ClientPreviewState.MaxHeight));

        _hasRequestedBounds = true;

        Left = _requestedBounds.Left;
        Top = _requestedBounds.Top;
        Width = _requestedBounds.Width;
        Height = _requestedBounds.Height;

        if (IsLoaded)
        {
            ApplyRequestedBounds();
        }
    }

    private void ApplyRequestedBounds()
    {
        if (!_hasRequestedBounds)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);

        _ = SetWindowPos(
            handle,
            nint.Zero,
            (int)Math.Round(_requestedBounds.Left * dpi.DpiScaleX),
            (int)Math.Round(_requestedBounds.Top * dpi.DpiScaleY),
            (int)Math.Round(_requestedBounds.Width * dpi.DpiScaleX),
            (int)Math.Round(_requestedBounds.Height * dpi.DpiScaleY),
            SwpNoZOrder | SwpNoActivate);

        SyncLabel();
    }

    /// <summary>窗口重新可见后强制刷新一次缩略图目标矩形。</summary>
    public void RefreshThumbnail() => UpdateThumbnail();

    private static Color ParseColor(string? hex, Color fallback) =>
        ColorLut.TryParseHex(hex, out var r, out var g, out var b)
            ? Color.FromRgb(r, g, b)
            : fallback;

    // ---------------------------------------------------------------- 覆盖层

    private void ShowLabelWindow()
    {
        if (!IsLoaded || !IsVisible)
        {
            return;
        }

        if (!_label.IsVisible)
        {
            // 先摆到预览窗位置再显示，避免覆盖层在 (0,0) 闪一帧
            _label.AlignTo(Left, Top, ActualWidth, ActualHeight);
            _label.Show();
        }

        SyncLabel();
    }

    private void SyncLabel()
    {
        if (!IsLoaded || !IsVisible || !_label.IsVisible)
        {
            return;
        }

        _label.Follow(new WindowInteropHelper(this).Handle);
    }

    // ---------------------------------------------------------------- 缩略图

    private void RegisterThumbnail()
    {
        if (_sourceHwnd == nint.Zero || !WindowFinder.IsAlive(_sourceHwnd))
        {
            SetFailed(true, "客户端窗口已失效");
            return;
        }

        var destination = new WindowInteropHelper(this).Handle;
        if (destination == nint.Zero)
        {
            return;
        }

        if (DwmRegisterThumbnail(destination, _sourceHwnd, out var thumbnail) != 0)
        {
            _thumbnail = nint.Zero;
            SetFailed(true, "无法预览（客户端可能处于独占全屏）");
            return;
        }

        _thumbnail = thumbnail;
        _hasThumbnailProperties = false;
        SetFailed(false, null);
        UpdateThumbnail();
    }

    private void UpdateThumbnail()
    {
        if (_thumbnail == nint.Zero || !IsLoaded)
        {
            return;
        }

        var width = ThumbHost.ActualWidth;
        var height = ThumbHost.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var origin = ThumbHost.TranslatePoint(new Point(0, 0), this);
        var dpi = VisualTreeHelper.GetDpi(this);

        var destination = new NativeRect
        {
            Left = (int)Math.Round(origin.X * dpi.DpiScaleX),
            Top = (int)Math.Round(origin.Y * dpi.DpiScaleY),
            Right = (int)Math.Round((origin.X + width) * dpi.DpiScaleX),
            Bottom = (int)Math.Round((origin.Y + height) * dpi.DpiScaleY),
        };

        // 目标矩形与透明度都没变时不要再喂 DWM：重复更新会让缩略图重新合成一次，看起来像闪一下
        if (_hasThumbnailProperties
            && _lastThumbnailOpacity == _thumbnailOpacity
            && _lastDestination.Left == destination.Left
            && _lastDestination.Top == destination.Top
            && _lastDestination.Right == destination.Right
            && _lastDestination.Bottom == destination.Bottom)
        {
            return;
        }

        _lastDestination = destination;
        _lastThumbnailOpacity = _thumbnailOpacity;
        _hasThumbnailProperties = true;

        var properties = new DwmThumbnailProperties
        {
            Flags = DwmTnpRectDestination | DwmTnpOpacity | DwmTnpVisible | DwmTnpSourceClientAreaOnly,
            Destination = destination,
            Opacity = _thumbnailOpacity,
            Visible = true,
            SourceClientAreaOnly = true,
        };

        DwmUpdateThumbnailProperties(_thumbnail, ref properties);
    }

    private void UnregisterThumbnail()
    {
        if (_thumbnail == nint.Zero)
        {
            return;
        }

        DwmUnregisterThumbnail(_thumbnail);
        _thumbnail = nint.Zero;
        _hasThumbnailProperties = false;
    }

    private void SetFailed(bool failed, string? message)
    {
        if (failed && !string.IsNullOrEmpty(message))
        {
            StatusText.Text = message;
        }

        StatusText.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- 交互

    private void OnSurfaceMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        // 左键只负责切换客户端：按下立即响应，不等松开
        ActivateAccount();
        e.Handled = true;
    }

    /// <summary>
    /// 先更新描边再切前台：SetForegroundWindow 会把游戏窗口切到前台并阻塞几十毫秒，
    /// 放到后台优先级执行，让预览窗的视觉反馈先绘制出来。
    /// </summary>
    private void ActivateAccount()
    {
        ClickActivated?.Invoke();

        var hwnd = _sourceHwnd;
        if (!WindowFinder.IsAlive(hwnd))
        {
            return;
        }

        Dispatcher.BeginInvoke(
            new Action(() => WindowFinder.Activate(hwnd)),
            DispatcherPriority.Background);
    }

    private void OnSurfaceMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right)
        {
            return;
        }

        // 右键按住拖动预览窗（没有右键菜单）
        _rightPressed = true;
        _dragging = false;
        BeginPointer();
        e.Handled = true;
    }

    private void BeginPointer()
    {
        if (!GetCursorPos(out var point))
        {
            return;
        }

        _dragStartX = point.X;
        _dragStartY = point.Y;

        // 必须在 Surface 上捕获：捕获到 Window 上时，鼠标抬起与移动会绕过 Grid 的处理器
        _ = Surface.CaptureMouse();
    }

    private void OnSurfaceMouseMove(object sender, MouseEventArgs e)
    {
        if (!_rightPressed)
        {
            return;
        }

        if (e.RightButton != MouseButtonState.Pressed)
        {
            FinishPointer();
            return;
        }

        if (!GetCursorPos(out var point))
        {
            return;
        }

        var dx = point.X - _dragStartX;
        var dy = point.Y - _dragStartY;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        if (!_dragging)
        {
            if (Math.Abs(dx) + Math.Abs(dy) < 4)
            {
                return;
            }

            if (!GetWindowRect(handle, out var rect))
            {
                return;
            }

            _dragWindowX = rect.Left;
            _dragWindowY = rect.Top;
            _dragging = true;

            // 拖动会让预览窗浮到覆盖层之上，重新把描边与名字提回最前
            SyncLabel();
        }

        _ = SetWindowPos(
            handle,
            nint.Zero,
            _dragWindowX + dx,
            _dragWindowY + dy,
            0,
            0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private void OnSurfaceMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        _rightPressed = false;
        FinishPointer();
        e.Handled = true;
    }

    private void FinishPointer()
    {
        var moved = _dragging;
        _dragging = false;

        Surface.ReleaseMouseCapture();

        if (!moved)
        {
            return;
        }

        SyncLabel();
        BoundsCommitted?.Invoke();
    }

    // ---------------------------------------------------------------- Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmThumbnailProperties
    {
        public int Flags;
        public NativeRect Destination;
        public NativeRect Source;
        public byte Opacity;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Visible;

        [MarshalAs(UnmanagedType.Bool)]
        public bool SourceClientAreaOnly;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(nint thumbnail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(nint thumbnail, ref DwmThumbnailProperties properties);
}
