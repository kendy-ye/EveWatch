using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace EveWatch.App.Windows;

/// <summary>
/// 预览窗的透明覆盖层：在 DWM 缩略图之上显示角色名与当前焦点的彩色描边。
/// <para>
/// DWM 缩略图绘制在目标窗口内容之上，同窗口内的 WPF 文字会被盖住；
/// 因此标注必须是独立的顶层窗，并设置 <c>WS_EX_TRANSPARENT</c> 让鼠标穿透到预览窗。
/// </para>
/// </summary>
public partial class PreviewLabelWindow : Window
{
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExNoactivate = 0x08000000;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private bool _active;
    private Color _activeColor = Color.FromRgb(0x3D, 0xDC, 0x84);
    private double _borderThickness = 2;
    private Color _labelColor = Color.FromRgb(0xE3, 0xEB, 0xF2);
    private bool _labelVisible = true;
    private Rect _lastRect;
    private bool _hasRect;

    public PreviewLabelWindow()
    {
        InitializeComponent();

        SourceInitialized += (_, _) => MakeClickThrough();
        SizeChanged += (_, _) => NameChip.MaxWidth = Math.Max(40, ActualWidth - 10);
    }

    /// <summary>角色名；整个名字用粗体。</summary>
    public void SetAccountName(string name) => LabelText.Text = name;

    public void SetLabelVisible(bool visible)
    {
        if (_labelVisible == visible)
        {
            return;
        }

        _labelVisible = visible;
        NameChip.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetLabelColor(Color color)
    {
        if (_labelColor == color)
        {
            return;
        }

        _labelColor = color;
        LabelText.Foreground = Frozen(color);
    }

    public void SetActive(bool active, Color activeColor, double borderThickness)
    {
        if (_active == active && _activeColor == activeColor && Math.Abs(_borderThickness - borderThickness) < 0.01)
        {
            return;
        }

        _active = active;
        _activeColor = activeColor;
        _borderThickness = borderThickness;
        UpdateOutline();
    }

    private void UpdateOutline()
    {
        var thickness = _active ? _borderThickness : 0;
        ActiveOutline.BorderThickness = new Thickness(thickness);
        ActiveOutline.BorderBrush = thickness > 0 ? Frozen(_activeColor) : Brushes.Transparent;
    }

    /// <summary>显示前先按 WPF 单位摆好，避免第一帧出现在默认位置造成闪动。</summary>
    public void AlignTo(double left, double top, double width, double height)
    {
        Left = left;
        Top = top;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        _hasRect = false;
    }

    /// <summary>把覆盖层精确贴合到预览窗的物理矩形上。</summary>
    public void Follow(nint previewHandle)
    {
        if (previewHandle == nint.Zero || !GetWindowRect(previewHandle, out var rect))
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        var bounds = new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        if (_hasRect && _lastRect == bounds)
        {
            return;
        }

        _lastRect = bounds;
        _hasRect = true;

        _ = SetWindowPos(
            handle,
            nint.Zero,
            (int)bounds.Left,
            (int)bounds.Top,
            (int)Math.Max(1, bounds.Width),
            (int)Math.Max(1, bounds.Height),
            SwpNoZOrder | SwpNoActivate);
    }

    private void MakeClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        var style = GetWindowLongPtr(handle, GwlExstyle).ToInt64();
        style |= WsExTransparent | WsExToolwindow | WsExNoactivate;
        _ = SetWindowLongPtr(handle, GwlExstyle, new nint(style));
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int index, nint value);
}
