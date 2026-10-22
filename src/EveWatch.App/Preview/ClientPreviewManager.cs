using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using EveWatch.App.Windows;
using EveWatch.Core.Config;
using EveWatch.Core.Discovery;

namespace EveWatch.App.Preview;

/// <summary>
/// 客户端预览管理器：按运行中的客户端创建 / 显示 / 隐藏预览窗，
/// 维护前台账号高亮，并把拖动、缩放后的位置尺寸写回界面状态。
/// </summary>
public sealed class ClientPreviewManager : IDisposable
{
    private const double Gap = 10;
    private const double Edge = 12;

    /// <summary>首次摆放时每行的预览窗数量。</summary>
    private const int ColumnsPerRow = 5;

    /// <summary>窗口至少要有这么多像素留在虚拟桌面内，否则拖不回来。</summary>
    private const double MinVisible = 48;

    private const uint EventSystemForeground = 0x0003;
    private const uint WineventSkipOwnProcess = 0x0002;

    private readonly AppServices _services;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, ClientPreviewWindow> _windows = new(StringComparer.Ordinal);

    /// <summary>停在登录 / 角色选择界面的客户端预览，按客户端窗口句柄索引。</summary>
    private readonly Dictionary<nint, ClientPreviewWindow> _selectionWindows = [];

    /// <summary>角色选择预览的稳定顺序；窗口枚举顺序会随 Z 序变化，不能用它定位。</summary>
    private readonly List<nint> _selectionOrder = [];
    private readonly ClientPreviewAccountState _selectionAccount = new();
    private readonly DispatcherTimer _foregroundTimer;
    private readonly DispatcherTimer _saveTimer;

    private WinEventProc? _foregroundProc;
    private nint _foregroundHook;
    private string? _activeKey;
    private bool _enabled;
    private bool _disposed;

    public ClientPreviewManager(AppServices services)
    {
        _services = services;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _foregroundTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(700),
        };
        _foregroundTimer.Tick += (_, _) =>
        {
            SyncActive(WindowFinder.ForegroundHandle);
            SyncSelectionWindows();
        };

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(600),
        };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _services.UiState.Save();
        };

        _services.Directory.Changed += OnDirectoryChanged;
    }

    /// <summary>全局预览开关变化（供主界面按钮同步文案）。</summary>
    public event Action? StateChanged;

    /// <summary>布局或可见性变化（供设置窗口刷新逐角色列表）。</summary>
    public event Action? LayoutChanged;

    /// <summary>本次运行的预览总开关；不写入配置。</summary>
    public bool Enabled => _enabled;

    private ClientPreviewState Config => _services.UiState.Current.ClientPreview;

    /// <summary>启动预览。软件启动后默认开启。</summary>
    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        _enabled = true;
        HookForeground();
        _foregroundTimer.Start();
        Sync();
        StateChanged?.Invoke();
    }

    /// <summary>总开关只影响本次运行，不改写逐角色配置。</summary>
    public void SetEnabled(bool enabled)
    {
        if (_disposed || _enabled == enabled)
        {
            return;
        }

        _enabled = enabled;

        if (enabled)
        {
            HookForeground();
            _foregroundTimer.Start();
            Sync();
        }
        else
        {
            UnhookForeground();
            _foregroundTimer.Stop();
            HideAll();
            LayoutChanged?.Invoke();
        }

        StateChanged?.Invoke();
    }

    /// <summary>重新读取配置并应用到全部预览窗。</summary>
    public void ApplySettings()
    {
        if (_disposed)
        {
            return;
        }

        var config = Config;
        foreach (var (name, window) in _windows)
        {
            window.Apply(config, config.For(name));
        }

        QueueSave();
        Sync();
        LayoutChanged?.Invoke();
    }

    /// <summary>立即按当前窗口快照同步一次（创建、绑定、显示或隐藏）。</summary>
    public void Sync()
    {
        if (_disposed)
        {
            return;
        }

        if (!_enabled)
        {
            HideAll();
            return;
        }

        var config = Config;
        var running = new Dictionary<string, WindowInfo>(StringComparer.Ordinal);
        foreach (var window in _services.Directory.Windows)
        {
            running[window.AccountName] = window;
        }

        foreach (var (name, info) in running)
        {
            var account = config.For(name);

            if (!_windows.TryGetValue(name, out var window))
            {
                window = CreateWindow(name, account);
                _windows[name] = window;
            }

            window.Attach(info.Hwnd);
            window.Apply(config, account);

            var show = account.Visible && (!config.HideWhenMinimized || !info.Minimized);
            if (show)
            {
                if (!window.IsVisible)
                {
                    window.Show();
                }

                window.RefreshThumbnail();
            }
            else if (window.IsVisible)
            {
                window.Hide();
            }
        }

        foreach (var (name, window) in _windows)
        {
            if (!running.ContainsKey(name) && window.IsVisible)
            {
                window.Hide();
            }
        }

        SyncActive(WindowFinder.ForegroundHandle);
        SyncSelectionWindows();
        LayoutChanged?.Invoke();
    }

    /// <summary>把所有可见客户端在显示器工作区内排成网格。</summary>
    public void AutoArrange()
    {
        var names = _services.Directory.Windows.Select(window => window.AccountName).ToList();
        if (names.Count == 0)
        {
            return;
        }

        var config = Config;
        var area = SystemParameters.WorkArea;
        var width = config.DefaultWidth;
        var height = config.DefaultHeight;
        var columns = Math.Min(ColumnsPerRow, names.Count);

        var startX = area.Left + Edge;
        var startY = area.Top + Edge;

        for (var index = 0; index < names.Count; index++)
        {
            var account = config.For(names[index]);
            account.Width = width;
            account.Height = height;
            account.Left = Math.Round(startX + index % columns * (width + Gap));
            account.Top = Math.Round(startY + index / columns * (height + Gap));

            if (_windows.TryGetValue(names[index], out var window))
            {
                window.Place(account.Left.Value, account.Top.Value, width, height);
            }
        }

        QueueSave();
        LayoutChanged?.Invoke();
    }

    /// <summary>清空全部已保存位置并重新自动排列。</summary>
    public void ResetPositions()
    {
        foreach (var account in Config.Accounts.Values)
        {
            account.Left = null;
            account.Top = null;
        }

        AutoArrange();
    }

    /// <summary>把单个预览窗重新放回空位。</summary>
    public void ResetPosition(string accountName)
    {
        var account = Config.For(accountName);
        account.Left = null;
        account.Top = null;

        var bounds = ResolveBounds(account);
        if (_windows.TryGetValue(accountName, out var window))
        {
            window.Place(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        }

        QueueSave();
        LayoutChanged?.Invoke();
    }

    /// <summary>按当前配置重新摆放某个角色的预览窗（设置窗口改宽高后调用）。</summary>
    public void ApplyAccountBounds(string accountName)
    {
        var bounds = ResolveBounds(Config.For(accountName));

        if (_windows.TryGetValue(accountName, out var window))
        {
            window.Place(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        }

        QueueSave();
        LayoutChanged?.Invoke();
    }

    /// <summary>把默认宽高直接应用到所有预览窗：位置不变，逐角色尺寸被覆盖。</summary>
    public void ApplyDefaultSize()
    {
        var config = Config;

        foreach (var account in config.Accounts.Values)
        {
            account.Width = config.DefaultWidth;
            account.Height = config.DefaultHeight;
        }

        if (_windows.Count == 0)
        {
            QueueSave();
            LayoutChanged?.Invoke();
            return;
        }

        foreach (var name in _windows.Keys.ToArray())
        {
            ApplyAccountBounds(name);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _services.Directory.Changed -= OnDirectoryChanged;
        UnhookForeground();
        _foregroundTimer.Stop();
        _saveTimer.Stop();

        foreach (var window in _windows.Values)
        {
            window.Close();
        }

        foreach (var window in _selectionWindows.Values)
        {
            window.Close();
        }

        _windows.Clear();
        _selectionWindows.Clear();
        _selectionOrder.Clear();
        _services.UiState.Save();
    }

    // ---------------------------------------------------------------- 窗口

    private ClientPreviewWindow CreateWindow(string name, ClientPreviewAccountState account)
    {
        var bounds = ResolveBounds(account);

        var window = new ClientPreviewWindow(name);
        window.Place(bounds.Left, bounds.Top, bounds.Width, bounds.Height);

        window.BoundsCommitted += () => CommitBounds(name, window);
        window.ClickActivated += () => SetActiveKey(name);

        if (string.Equals(_activeKey, name, StringComparison.Ordinal))
        {
            window.SetActive(true);
        }

        return window;
    }

    private void HideAll()
    {
        foreach (var window in _windows.Values)
        {
            if (window.IsVisible)
            {
                window.Hide();
            }
        }

        foreach (var window in _selectionWindows.Values)
        {
            if (window.IsVisible)
            {
                window.Hide();
            }
        }
    }

    private void CommitBounds(string name, ClientPreviewWindow window)
    {
        var account = Config.For(name);
        account.Left = window.Left;
        account.Top = window.Top;
        account.Width = window.Width;
        account.Height = window.Height;

        QueueSave();
        LayoutChanged?.Invoke();
    }

    private void QueueSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>
    /// 取保存的位置；没有位置的账号从主屏右上角开始向左、向下找第一个不重叠的空位。
    /// 屏幕变化后位置可能落在桌面外，这里把它拉回到至少留 <see cref="MinVisible"/> 像素可见。
    /// </summary>
    private Rect ResolveBounds(ClientPreviewAccountState account)
    {
        var width = Math.Clamp(account.Width, ClientPreviewState.MinWidth, ClientPreviewState.MaxWidth);
        var height = Math.Clamp(account.Height, ClientPreviewState.MinHeight, ClientPreviewState.MaxHeight);

        var changed = account.Left is null
            || account.Top is null
            || Math.Abs(account.Width - width) > 0.5
            || Math.Abs(account.Height - height) > 0.5;

        var left = account.Left ?? 0;
        var top = account.Top ?? 0;

        if (account.Left is null || account.Top is null)
        {
            var slot = NextFreeSlot(width, height);
            left = slot.X;
            top = slot.Y;
        }

        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        var clampedLeft = Math.Clamp(left, virtualLeft - width + MinVisible, virtualRight - MinVisible);
        var clampedTop = Math.Clamp(top, virtualTop - height + MinVisible, virtualBottom - MinVisible);
        changed |= Math.Abs(clampedLeft - left) > 0.5 || Math.Abs(clampedTop - top) > 0.5;

        account.Left = clampedLeft;
        account.Top = clampedTop;
        account.Width = width;
        account.Height = height;

        if (changed)
        {
            QueueSave();
        }

        return new Rect(clampedLeft, clampedTop, width, height);
    }

    private Point NextFreeSlot(double width, double height)
    {
        var area = SystemParameters.WorkArea;

        // 首次使用的默认布局：主屏左上角开始，每行 5 个，向下换行
        for (var row = 0; row < 200; row++)
        {
            var y = area.Top + Edge + row * (height + Gap);
            if (y + height > area.Bottom)
            {
                break;
            }

            for (var column = 0; column < ColumnsPerRow; column++)
            {
                var x = area.Left + Edge + column * (width + Gap);
                if (x + width > area.Right)
                {
                    break;
                }

                if (!OverlapsAny(x, y, width, height))
                {
                    return new Point(x, y);
                }
            }
        }

        return new Point(area.Left + Edge, area.Top + Edge);
    }

    private bool OverlapsAny(double left, double top, double width, double height)
    {
        var candidate = new Rect(left, top, width, height);

        foreach (var account in Config.Accounts.Values)
        {
            if (account.Left is not { } accountLeft || account.Top is not { } accountTop)
            {
                continue;
            }

            var other = new Rect(accountLeft, accountTop, account.Width, account.Height);
            if (other.IntersectsWith(candidate))
            {
                return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- 角色选择界面

    private static string SelectionKey(nint hwnd) => $"selection:{hwnd}";

    /// <summary>
    /// 给停在登录 / 角色选择界面的客户端（标题只有 <c>EVE</c>）一个预览窗，
    /// 首次出现在主屏工作区左上角，之后位置由用户拖动决定。
    /// <para>
    /// 这类窗口没有账号名，不进入逐角色配置，位置也不持久化。
    /// 枚举顺序会随 Z 序变化，所以用 <see cref="_selectionOrder"/> 保持稳定顺序，
    /// 避免点击某个预览后两个预览互换位置。
    /// </para>
    /// </summary>
    private void SyncSelectionWindows()
    {
        if (_disposed)
        {
            return;
        }

        var found = WindowFinder.EnumerateCharacterSelection(_services.Config.Current.ProcessName);
        var byHandle = found.ToDictionary(info => info.Hwnd);

        // 已经进入游戏或退出的客户端：关闭对应的临时预览
        foreach (var hwnd in _selectionOrder.ToArray())
        {
            if (byHandle.ContainsKey(hwnd))
            {
                continue;
            }

            _selectionOrder.Remove(hwnd);

            if (_selectionWindows.Remove(hwnd, out var gone))
            {
                gone.Close();
            }
        }

        if (!_enabled)
        {
            return;
        }

        var config = Config;
        var width = config.DefaultWidth;
        var height = config.DefaultHeight;
        var area = SystemParameters.WorkArea;

        _selectionAccount.Width = width;
        _selectionAccount.Height = height;
        _selectionAccount.Visible = true;

        // 新出现的客户端追加到末尾，已有窗口保持用户摆放的位置
        foreach (var info in found)
        {
            if (_selectionWindows.ContainsKey(info.Hwnd))
            {
                continue;
            }

            var slot = _selectionOrder.Count;
            var hwnd = info.Hwnd;
            var window = new ClientPreviewWindow("角色选择");

            window.ClickActivated += () => SetActiveKey(SelectionKey(hwnd));
            _selectionWindows[hwnd] = window;
            _selectionOrder.Add(hwnd);

            window.Attach(hwnd);
            window.Place(area.Left + Edge, area.Top + Edge + slot * (height + Gap), width, height);
        }

        for (var slot = 0; slot < _selectionOrder.Count; slot++)
        {
            var hwnd = _selectionOrder[slot];

            if (!byHandle.TryGetValue(hwnd, out var info)
                || !_selectionWindows.TryGetValue(hwnd, out var window))
            {
                continue;
            }

            window.Apply(config, _selectionAccount);
            window.SetAccountName(slot == 0 ? "角色选择" : $"角色选择 {slot + 1}");

            var show = !config.HideWhenMinimized || !info.Minimized;
            if (show && !window.IsVisible)
            {
                window.Show();
                window.RefreshThumbnail();
            }
            else if (!show && window.IsVisible)
            {
                window.Hide();
            }
        }
    }

    // ---------------------------------------------------------------- 前台账号

    private void SetActiveKey(string? key)
    {
        if (string.Equals(_activeKey, key, StringComparison.Ordinal))
        {
            return;
        }

        _activeKey = key;

        foreach (var (name, window) in _windows)
        {
            window.SetActive(string.Equals(name, key, StringComparison.Ordinal));
        }

        foreach (var (hwnd, window) in _selectionWindows)
        {
            window.SetActive(string.Equals(SelectionKey(hwnd), key, StringComparison.Ordinal));
        }
    }

    private void SyncActive(nint hwnd)
    {
        if (_disposed || hwnd == nint.Zero)
        {
            return;
        }

        foreach (var window in _services.Directory.Windows)
        {
            if (window.Hwnd != hwnd)
            {
                continue;
            }

            SetActiveKey(window.AccountName);
            return;
        }

        if (_selectionWindows.ContainsKey(hwnd))
        {
            SetActiveKey(SelectionKey(hwnd));
        }
    }

    private void OnDirectoryChanged()
    {
        if (_disposed)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            Sync();
        }
        else
        {
            _dispatcher.BeginInvoke(new Action(Sync));
        }
    }

    private void HookForeground()
    {
        if (_foregroundHook != nint.Zero)
        {
            return;
        }

        _foregroundProc = OnForegroundChanged;
        _foregroundHook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            nint.Zero,
            _foregroundProc,
            0,
            0,
            WineventSkipOwnProcess);
    }

    private void UnhookForeground()
    {
        if (_foregroundHook == nint.Zero)
        {
            return;
        }

        UnhookWinEvent(_foregroundHook);
        _foregroundHook = nint.Zero;
        _foregroundProc = null;
    }

    private void OnForegroundChanged(
        nint hook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint threadId,
        uint time)
    {
        if (_disposed || eventType != EventSystemForeground)
        {
            return;
        }

        SyncActive(hwnd);
    }

    private delegate void WinEventProc(
        nint hook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint threadId,
        uint time);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint eventHookModule,
        WinEventProc eventProc,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hook);
}
