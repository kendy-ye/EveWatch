using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EveWatch.App.ViewModels;
using EveWatch.Core.Config;
using EveWatch.Core.Discovery;
using EveWatch.Core.Status;

namespace EveWatch.App.Windows;

/// <summary>
/// ROI 并排置顶窗。
/// <para>
/// 按账号分组：一个账号一组，组内该账号的各条 ROI 并排显示各自的实时画面。
/// 同一条账号的多条 ROI 不再拆成互不相干的独立列。
/// </para>
/// <para>
/// 画面来自 <see cref="FrameStore"/>，只在窗口打开期间发布，关闭即停止。
/// </para>
/// </summary>
public partial class RoiWallWindow : Window
{
    /// <summary>超过这个时长没有新帧就认为画面已失效（监控停止或窗口不可采）。</summary>
    private const long StaleAfterMs = 3000;

    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private readonly ObservableCollection<WallGroup> _groups = [];
    private string _signature = string.Empty;
    private double _scale = 0.5;

    /// <summary>双击某一组时把账号名抛给宿主。</summary>
    public event Action<string>? AccountActivated;

    /// <summary>当前打开的并排窗；日志预警的「并排窗旁」锚点使用它。</summary>
    public static RoiWallWindow? Current { get; private set; }

    public RoiWallWindow(AppServices services)
    {
        _services = services;
        Current = this;

        InitializeComponent();
        WallHost.ItemsSource = _groups;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _timer.Tick += (_, _) => Refresh();

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    // ---------------------------------------------------------------- 生命周期

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateLayout();

        // 无边框且按内容自适应尺寸，所以要给窗口加上限，超出部分交给滚动条
        var area = SystemParameters.WorkArea;
        MaxHeight = Math.Max(260, area.Height * 0.85);
        MaxWidth = Math.Max(360, area.Width * 0.9);

        _services.Frames.Enabled = true;
        _scale = _services.UiState.Current.WallScale;

        // 位置必须在窗口尺寸定稿之后再落：SizeToContent 的窗口在打开时会先按
        // 尚未撑开的尺寸走一次定位，早于内容建好就设 Left/Top，位置会被那次定位盖掉，
        // 表现出来就是"每次打开都要重新摆一次"。
        Rebuild();
        UpdateLayout();
        ApplyPlacement();

        // 布局收尾阶段还可能再动一次窗口位置，在 Loaded 优先级上再兜一次
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ApplyPlacement));

        _timer.Start();
    }

    /// <summary>
    /// 恢复上次的位置。夹在虚拟屏幕之内：显示器拔掉或分辨率变小之后，
    /// 旧坐标可能落在看不见的地方，那样窗口就"消失"了。
    /// </summary>
    private void ApplyPlacement()
    {
        var saved = _services.UiState.Current.Wall;
        if (saved is null)
        {
            var area = SystemParameters.WorkArea;
            Left = Math.Max(area.Left, area.Right - ActualWidth - 26);
            Top = Math.Max(area.Top, area.Bottom - ActualHeight - 54);
            return;
        }

        // 至少留 40 个单位在屏幕内，剩下的允许探出去
        const double minVisible = 40;
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;

        Left = Math.Clamp(saved.Left, screenLeft, Math.Max(screenLeft, screenRight - minVisible));
        Top = Math.Clamp(saved.Top, screenTop, Math.Max(screenTop, screenBottom - minVisible));
    }

    /// <summary>把当前位置写进界面状态。</summary>
    private void SavePlacement()
    {
        _services.UiState.Current.Wall = new WindowPlacement { Left = Left, Top = Top };
        _services.UiState.Save();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }

        _timer.Stop();

        // 关掉画面发布，轮询线程随即不再拷贝帧
        _services.Frames.Enabled = false;

        _services.UiState.Current.WallScale = _scale;
        SavePlacement();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        base.OnKeyDown(e);
    }

    // ---------------------------------------------------------------- 拖动窗口

    // 无边框窗口没有标题栏，改成拖动窗口任意位置。两处必须注意：
    //
    // 1. 处理器必须挂在 MouseDown / MouseUp 上。
    //    MouseLeftButtonDown / MouseLeftButtonUp 只对左键触发，
    //    挂在它们上面时"ChangedButton == Right"的分支是死代码，右键永远拖不动。
    //
    // 2. 左右键都不能用 Window.DragMove()。
    //    它内部要求左键处于按下状态，右键调用会抛 InvalidOperationException；
    //    而且它会立刻进入嵌套消息循环，把双击的第二下吃掉，双击跳转就废了。
    //    所以统一自己按位移搬窗口。
    //
    // 左键额外加拖动阈值：它同时承担双击跳转，按下即移动会让轻微抖动也拖窗口。

    private MouseButton _panButton;
    private bool _panPending;
    private bool _panning;
    private System.Windows.Point _panScreenOrigin;
    private double _panWindowLeft;
    private double _panWindowTop;

    private void OnRootMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right))
        {
            return;
        }

        if (IsInteractive(e.OriginalSource as DependencyObject))
        {
            return;
        }

        _panButton = e.ChangedButton;
        _panScreenOrigin = PointToScreen(e.GetPosition(this));
        _panWindowLeft = Left;
        _panWindowTop = Top;

        // 右键没有其它用途，按下即开始拖；左键要先过阈值
        _panning = e.ChangedButton == MouseButton.Right;
        _panPending = !_panning;

        if (_panning)
        {
            _ = Root.CaptureMouse();
            e.Handled = true;
        }
    }

    private void OnRootMouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning && !_panPending)
        {
            return;
        }

        if (!IsPanButtonDown(e))
        {
            EndPan();
            return;
        }

        // 位移必须按屏幕坐标算：用相对窗口的坐标会因为窗口自身在移动而变成自反馈，算不稳。
        // PointToScreen 返回设备像素，Left/Top 是 WPF 单位，高 DPI 下要按比例换算。
        var dpi = VisualTreeHelper.GetDpi(this);
        var current = PointToScreen(e.GetPosition(this));
        var dx = (current.X - _panScreenOrigin.X) / dpi.DpiScaleX;
        var dy = (current.Y - _panScreenOrigin.Y) / dpi.DpiScaleY;

        if (!_panning)
        {
            if (Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _panning = true;
            _panPending = false;
            _ = Root.CaptureMouse();
        }

        Left = _panWindowLeft + dx;
        Top = _panWindowTop + dy;
    }

    private void OnRootMouseUp(object sender, MouseButtonEventArgs e)
    {
        if ((_panning || _panPending) && e.ChangedButton == _panButton)
        {
            EndPan();
        }
    }

    /// <summary>
    /// Ctrl + 滚轮调整画面缩放。
    /// 窗口没有任何控件，缩放只能靠这类手势；结果写入界面状态，下次打开沿用。
    /// </summary>
    private void OnRootMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || e.Delta == 0)
        {
            return;
        }

        e.Handled = true;

        var next = Math.Clamp(_scale * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.2, 1.5);
        if (Math.Abs(next - _scale) < 1e-6)
        {
            return;
        }

        _scale = next;

        foreach (var group in _groups)
        {
            foreach (var column in group.Columns)
            {
                column.Scale = _scale;
            }

            group.RefreshContentWidth();
        }

        _services.UiState.Current.WallScale = _scale;
        _services.UiState.Save();
    }

    /// <summary>
    /// 手工摆放统计浮层。
    /// 放在 Canvas 里是因为 Canvas 的期望尺寸恒为 0——统计文字再长也不会把窗口撑宽。
    /// </summary>
    private void OnOverlaySizeChanged(object sender, SizeChangedEventArgs e)
    {
        System.Windows.Controls.Canvas.SetLeft(
            StatText,
            Math.Max(0, e.NewSize.Width - StatText.ActualWidth - 4));

        System.Windows.Controls.Canvas.SetTop(StatText, 3);
    }

    private bool IsPanButtonDown(MouseEventArgs e) => _panButton switch
    {
        MouseButton.Left => e.LeftButton == MouseButtonState.Pressed,
        MouseButton.Right => e.RightButton == MouseButtonState.Pressed,
        _ => false,
    };

    private void EndPan()
    {
        // 拖动一结束就把位置落盘。只靠 Closing 保存的话，程序异常退出、
        // 或者窗口一直开着直到整个程序被结束，这次拖到哪就白拖了。
        var moved = _panning;

        _panning = false;
        _panPending = false;

        if (Root.IsMouseCaptured)
        {
            Root.ReleaseMouseCapture();
        }

        if (moved)
        {
            SavePlacement();
        }
    }

    /// <summary>判断事件源是否落在按钮或滚动条上；这些位置不应该触发窗口拖动。</summary>
    private static bool IsInteractive(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.Primitives.ScrollBar)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    // ---------------------------------------------------------------- 刷新

    private void Refresh()
    {
        var signature = BuildSignature();
        if (!string.Equals(signature, _signature, StringComparison.Ordinal))
        {
            Rebuild();
            return;
        }

        UpdateGroups();
    }

    /// <summary>
    /// 参与并排的账号，**顺序与左栏账号列表一致**（含拖动排序的结果）。
    /// 顺序也进签名，所以在窗口开着的时候拖动左栏，并排窗会在下一拍跟着重排。
    /// </summary>
    private IReadOnlyList<AccountSetting> OrderedAccounts() =>
        _services.UiState.Current.ApplyOrder(_services.Config.Current.Accounts, a => a.Name);

    private string BuildSignature()
    {
        var parts = new List<string>();
        foreach (var account in OrderedAccounts())
        {
            if (!account.Enabled)
            {
                continue;
            }

            foreach (var roi in account.Rois)
            {
                if (roi.Enabled)
                {
                    parts.Add($"{account.Name}#{roi.Id}");
                }
            }
        }

        return string.Join('|', parts);
    }

    /// <summary>按账号建组，组内按配置顺序放入该账号的启用 ROI。</summary>
    private void Rebuild()
    {
        _signature = BuildSignature();

        _groups.Clear();

        foreach (var account in OrderedAccounts())
        {
            if (!account.Enabled)
            {
                continue;
            }

            WallGroup? group = null;

            for (var i = 0; i < account.Rois.Count; i++)
            {
                var roi = account.Rois[i];
                if (!roi.Enabled)
                {
                    continue;
                }

                group ??= new WallGroup(account.Name);
                group.Columns.Add(new WallColumn(group, i, roi.Id, roi.Label, roi.Bounds.W) { Scale = _scale });
            }

            if (group is not null)
            {
                group.RefreshContentWidth();
                _groups.Add(group);
            }
        }

        UpdateGroups();
    }

    private void UpdateGroups()
    {
        var names = new List<string>();
        foreach (var group in _groups)
        {
            names.Add(group.AccountName);
        }

        var map = new Dictionary<string, AccountStatus>(StringComparer.Ordinal);
        foreach (var status in _services.Status.Snapshot(names))
        {
            map[status.Name] = status;
        }

        var now = Environment.TickCount64;
        var routes = 0;
        var alarms = 0;

        foreach (var group in _groups)
        {
            map.TryGetValue(group.AccountName, out var status);
            group.Hwnd = status?.Hwnd ?? nint.Zero;

            var groupHit = false;

            foreach (var column in group.Columns)
            {
                routes++;

                RoiStatus? roi = null;
                if (status is not null && column.RoiIndex >= 0 && column.RoiIndex < status.Rois.Length)
                {
                    roi = status.Rois[column.RoiIndex];
                }

                var hit = roi?.HitBlocks > 0;
                column.Alarm = hit;

                if (hit)
                {
                    groupHit = true;
                    alarms++;
                }

                // 画面：拿最新一帧；过期就清掉，避免监控停止后还挂着旧画面
                var snapshot = _services.Frames.TryGet(column.AccountName, column.RoiId);
                if (snapshot is not null && now - snapshot.TimestampMs <= StaleAfterMs)
                {
                    column.UpdateFrame(snapshot);
                }
                else
                {
                    column.ClearFrame();
                }
            }

            group.Alarm = groupHit;
        }

        StatText.Text = $"{_groups.Count} 账号 · {routes} 路 · 报警 {alarms}";
        EmptyHint.Visibility = _groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnGroupClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
        {
            return;
        }

        if ((sender as FrameworkElement)?.DataContext is not WallGroup group)
        {
            return;
        }

        var hwnd = group.Hwnd != nint.Zero
            ? group.Hwnd
            : _services.Directory.Find(group.AccountName)?.Hwnd ?? nint.Zero;

        // 先通知宿主更新选中项，切焦点放在最末尾。
        // 切焦点必须是本次交互的最后一步，否则之后任何对自身窗口的操作
        // 都可能把焦点从游戏窗口抢回来。
        AccountActivated?.Invoke(group.AccountName);

        // 窗口最小化时先还原，再抢焦点；抢不到只记一条提示，不静默失败
        var activated = hwnd != nint.Zero && WindowFinder.Activate(hwnd);

        if (!activated)
        {
            _services.Events.Warn(
                group.AccountName,
                "切到客户端未成功",
                hwnd == nint.Zero ? "未找到该账号的窗口" : "系统前台锁拒绝了本次切换，请在任务栏手动点选");
        }
    }
}

/// <summary>并排窗里的一组：一个账号，组内是它的各条 ROI。</summary>
internal sealed class WallGroup : ObservableObject
{
    private bool _alarm;
    private double _contentWidth;

    public WallGroup(string accountName)
    {
        AccountName = accountName;
    }

    public string AccountName { get; }

    public nint Hwnd { get; set; }

    public ObservableCollection<WallColumn> Columns { get; } = [];

    /// <summary>
    /// 组内横向可用宽度（各列宽度之和）。
    /// <para>
    /// 账号名按这个宽度截断，不能反过来由名字决定组宽：名字用等宽字体约 60 px，
    /// 而一条 45 px 宽的 ROI 缩到 0.5 只有 28 px，名字会变成宽度下限，
    /// 于是缩小画面时窗口宽度纹丝不动。
    /// </para>
    /// </summary>
    public double ContentWidth
    {
        get => _contentWidth;
        private set => Set(ref _contentWidth, value);
    }

    /// <summary>按各列当前缩放重算横向宽度。</summary>
    public void RefreshContentWidth()
    {
        var total = 0.0;
        foreach (var column in Columns)
        {
            total += column.ColumnWidth;
        }

        ContentWidth = total;
    }

    /// <summary>组内任一条 ROI 命中。</summary>
    public bool Alarm
    {
        get => _alarm;
        set => Set(ref _alarm, value);
    }
}

/// <summary>组内的一列：一条 ROI 的实时画面。</summary>
internal sealed class WallColumn : ObservableObject
{
    private readonly int _roiWidth;

    private WriteableBitmap? _image;
    private int _frameWidth;
    private int _frameHeight;
    private long _writtenTimestamp = -1;
    private bool _alarm;
    private double _scale = 1.0;

    public WallColumn(WallGroup group, int roiIndex, string roiId, string label, int roiWidth)
    {
        Group = group;
        RoiIndex = roiIndex;
        RoiId = roiId;
        Label = label;
        _roiWidth = roiWidth;
    }

    public WallGroup Group { get; }

    public string AccountName => Group.AccountName;

    public int RoiIndex { get; }

    public string RoiId { get; }

    public string Label { get; }

    public string IndexTag => $"R{RoiIndex + 1}";

    /// <summary>画面缩放。1.0 为原始像素；并排窗整体尺寸主要由它决定。</summary>
    public double Scale
    {
        get => _scale;
        set
        {
            if (Set(ref _scale, value))
            {
                RaiseMany(nameof(ImageWidth), nameof(ImageHeight), nameof(ColumnWidth));
            }
        }
    }

    /// <summary>画面在界面上的宽度（原始像素 × 缩放）。</summary>
    public double ImageWidth => _roiWidth * _scale;

    /// <summary>画面在界面上的高度。</summary>
    public double ImageHeight => _frameHeight * _scale;

    /// <summary>
    /// 一列占用的横向宽度。
    /// 画面之外只剩两项：条自身的 1 px 描边（两侧共 2，平时透明、命中时显红）与列间距 1。
    /// 宽度主要由画面决定，所以紧凑靠缩放，而不是靠继续砍边距。
    /// </summary>
    public double ColumnWidth => Math.Max(14, ImageWidth + 3);

    public WriteableBitmap? Image
    {
        get => _image;
        private set
        {
            if (Set(ref _image, value))
            {
                Raise(nameof(HasImage));
            }
        }
    }

    public bool HasImage => _image is not null;

    public bool Alarm
    {
        get => _alarm;
        set => Set(ref _alarm, value);
    }

    /// <summary>把最新一帧画到列上。尺寸不变时复用同一张位图，避免反复分配。</summary>
    public void UpdateFrame(RoiFrameSnapshot snapshot)
    {
        // 帧没换过就不重复写。窗口的刷新节奏与轮询节奏不一定对齐，
        // 不对齐时同一帧会被反复写进位图，白白触发整窗重绘——这是卡顿的直接来源之一。
        if (_image is not null && _writtenTimestamp == snapshot.TimestampMs)
        {
            return;
        }

        if (_image is null || _frameWidth != snapshot.Width || _frameHeight != snapshot.Height)
        {
            _frameWidth = snapshot.Width;
            _frameHeight = snapshot.Height;
            Image = new WriteableBitmap(snapshot.Width, snapshot.Height, 96, 96, PixelFormats.Bgra32, null);
            RaiseMany(nameof(ImageHeight));
        }

        _image!.WritePixels(
            new Int32Rect(0, 0, snapshot.Width, snapshot.Height),
            snapshot.Bgra,
            snapshot.Stride,
            0);

        _writtenTimestamp = snapshot.TimestampMs;
    }

    /// <summary>画面失效：清空以显示占位提示。</summary>
    public void ClearFrame()
    {
        if (_image is null)
        {
            return;
        }

        _frameWidth = 0;
        _frameHeight = 0;
        _writtenTimestamp = -1;
        Image = null;
        RaiseMany(nameof(ImageHeight));
    }
}
