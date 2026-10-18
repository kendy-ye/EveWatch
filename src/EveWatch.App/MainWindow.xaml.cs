using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EveWatch.App.Hotkeys;
using EveWatch.App.ViewModels;
using EveWatch.App.Windows;
using EveWatch.Core.Capture;
using EveWatch.Core.Config;
using EveWatch.Core.Diagnostics;
using EveWatch.Core.Discovery;
using EveWatch.Core.Intel;
using EveWatch.Core.Palette;

namespace EveWatch.App;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly MainViewModel _model;
    private readonly DispatcherTimer _refreshTimer;

    private RoiWallWindow? _wallWindow;
    private SettingsSyncWindow? _syncWindow;
    private SettingsWindow? _settingsWindow;
    private IntelSettingsWindow? _intelSettingsWindow;
    private IntelAlertWindow? _intelWindow;

    private System.Windows.Point _dragOrigin;
    private AccountViewModel? _dragItem;
    private bool _allowClose;

    public MainWindow(AppServices services)
    {
        _services = services;

        InitializeComponent();

        _model = new MainViewModel(services)
        {
            PickAudioFile = PickAudioFile,
        };

        DataContext = _model;

        Hotkeys = new HotkeyService(this, _services.Config);
        Hotkeys.Triggered += OnHotkeyTriggered;

        _services.Intel.AlertRaised += OnIntelAlertRaised;

        // 句柄就绪后才能注册全局热键
        SourceInitialized += (_, _) => ApplyHotkeys();

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };

        _refreshTimer.Tick += (_, _) => _model.RefreshStatus();
        _refreshTimer.Start();

        Loaded += (_, _) => _model.RefreshStatus();
        Closing += OnClosing;
    }

    /// <summary>全局快捷键服务，设置面板需要复用同一实例来即时重注册。</summary>
    public HotkeyService Hotkeys { get; }

    /// <summary>允许真正关闭（托盘退出时调用）。</summary>
    public void AllowClose() => _allowClose = true;

    /// <summary>窗口发现变化后由 App 调用。</summary>
    public void OnAccountsDiscovered() => _model.OnAccountsDiscovered();

    /// <summary>托盘动作后刷新界面。</summary>
    public void RefreshFromServices() => _model.RefreshStatus();

    // ---------------------------------------------------------------- 窗口按钮

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            _refreshTimer.Stop();
            Hotkeys.Dispose();
            _services.Intel.AlertRaised -= OnIntelAlertRaised;
            _settingsWindow?.Close();
            _wallWindow?.Close();
            _syncWindow?.Close();
            _intelWindow?.Close();
            _intelWindow = null;
            return;
        }

        // 关到托盘，监控继续跑
        e.Cancel = true;
        Hide();
        _services.Events.Info("EveWatch", "已最小化到托盘", "右键托盘图标可停止监控或退出");
    }

    // ---------------------------------------------------------------- 工具栏

    private void OnToggleMonitorClick(object sender, RoutedEventArgs e)
    {
        _model.ToggleMonitoring();
        SyncHeaderState();
    }

    private void OnToggleMuteClick(object sender, RoutedEventArgs e)
    {
        _model.ToggleMute();
        SyncHeaderState();
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        // 只影响当前可见账号；隐藏的离线账号保留自己的启用状态
        var anyDisabled = _model.Accounts.Any(a => a.IsVisible && !a.Enabled);
        _model.SetAllAccounts(anyDisabled);
    }

    private void OnInvertClick(object sender, RoutedEventArgs e) => _model.InvertAccounts();

    private void OnClearEventsClick(object sender, RoutedEventArgs e)
    {
        _model.Events.Clear();
        _services.Events.Clear();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    private void OnShotClick(object sender, RoutedEventArgs e) => ExportShot(interactive: true);

    /// <summary>
    /// 导出当前选中账号的整窗截图。<paramref name="interactive"/> 为 false 时（快捷键触发）
    /// 不弹确认框，只把结果写进事件列表。
    /// </summary>
    private void ExportShot(bool interactive)
    {
        var account = _model.SelectedAccount;
        if (account is null || account.Hwnd == nint.Zero)
        {
            ReportShotFailure("该账号的窗口尚未发现。", interactive);
            return;
        }

        var frame = _services.Capture.CaptureFull(account.Hwnd);
        if (frame is null)
        {
            ReportShotFailure("取帧失败，窗口可能处于最小化状态。", interactive);
            return;
        }

        var directory = Path.Combine(AppContext.BaseDirectory, "shots");
        var path = Path.Combine(directory, $"{Sanitize(account.Name)}_full.png");

        try
        {
            PngWriter.WriteBgra(path, frame.Bgra, frame.Width, frame.Height);
        }
        catch (Exception ex)
        {
            ReportShotFailure($"写入失败：{ex.Message}", interactive);
            return;
        }

        _services.Events.Info(account.Name, "已导出整窗截图", path);

        if (!interactive)
        {
            return;
        }

        if (MessageBox.Show(this, $"已导出 {frame.Width}×{frame.Height} 的截图：\n{path}\n\n要打开所在目录吗？",
                "导出截图", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
    }

    private void ReportShotFailure(string message, bool interactive)
    {
        if (interactive)
        {
            MessageBox.Show(this, message, "导出截图", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            _services.Events.Warn("EveWatch", "导出截图失败", message);
        }
    }

    // ---------------------------------------------------------------- 账号列表

    private void OnAccountListMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 双击账号行 = 把焦点交给这个账号的客户端，语义与并排窗双击一致
        if (e.ClickCount == 2
            && (e.OriginalSource as DependencyObject) is { } hit
            && FindAncestor<ListBoxItem>(hit)?.DataContext is AccountViewModel account)
        {
            FocusAccountWindow(account.Name, account.Hwnd);
            return;
        }

        _dragOrigin = e.GetPosition(null);
        _dragItem = (e.OriginalSource as DependencyObject) is { } source
            ? FindAncestor<ListBoxItem>(source)?.DataContext as AccountViewModel
            : null;
    }

    private void OnAccountListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem is null)
        {
            return;
        }

        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop(AccountList, _dragItem, DragDropEffects.Move);
    }

    private void OnAccountListDrop(object sender, DragEventArgs e)
    {
        var dragged = _dragItem;
        _dragItem = null;

        if (dragged is null)
        {
            return;
        }

        var target = (e.OriginalSource as DependencyObject) is { } source
            ? FindAncestor<ListBoxItem>(source)?.DataContext as AccountViewModel
            : null;

        if (target is null || ReferenceEquals(target, dragged))
        {
            return;
        }

        var from = _model.Accounts.IndexOf(dragged);
        var to = _model.Accounts.IndexOf(target);
        if (from < 0 || to < 0)
        {
            return;
        }

        _model.Accounts.Move(from, to);
        _model.ApplyAccountOrder(_model.Accounts.Select(a => a.Name));
        _services.Events.Info("EveWatch", "账号顺序已保存");
    }

    // ---------------------------------------------------------------- ROI

    private void OnAddRoiClick(object sender, RoutedEventArgs e)
    {
        var account = _model.SelectedAccount;
        if (account is null)
        {
            return;
        }

        var window = _services.Directory.Find(account.Name);
        if (window is null)
        {
            MessageBox.Show(this, "找不到该账号的窗口，请确认客户端已启动。", "新增 ROI", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new CalibrationWindow(_services.Capture, window, null, account.Name)
        {
            Owner = this,
        };

        if (dialog.ShowDialog() != true || dialog.Result is not { } bounds)
        {
            return;
        }

        var roi = account.AddRoi(_model.Palette);
        roi.Setting.X = bounds.X;
        roi.Setting.Y = bounds.Y;
        roi.Setting.W = bounds.W;
        roi.Setting.H = bounds.H;

        var preset = AudioChoices().FirstOrDefault(p => p.Id == Core.Alarm.AudioPresets.DefaultId);
        roi.Setting.Alarm.Kind = AlarmSetting.KindPreset;
        roi.Setting.Alarm.Id = preset?.Id ?? Core.Alarm.AudioPresets.DefaultId;

        if (!string.IsNullOrWhiteSpace(dialog.ResultLabel))
        {
            roi.Label = dialog.ResultLabel;
        }

        _services.Events.Info(account.Name, "已新增 ROI", bounds.ToString());
        _model.NotifyConfigChanged();
    }

    private void OnRoiReboxClick(object sender, RoutedEventArgs e)
    {
        var account = _model.SelectedAccount;
        if (account is null || (sender as FrameworkElement)?.Tag is not RoiViewModel roi)
        {
            return;
        }

        var window = _services.Directory.Find(account.Name);
        if (window is null)
        {
            MessageBox.Show(this, "找不到该账号的窗口。", "重框", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new CalibrationWindow(_services.Capture, window, roi.Setting.Bounds, account.Name)
        {
            Owner = this,
        };

        if (dialog.ShowDialog() != true || dialog.Result is not { } bounds)
        {
            return;
        }

        // 只改坐标，颜色与音频配置原样保留
        roi.Setting.X = bounds.X;
        roi.Setting.Y = bounds.Y;
        roi.Setting.W = bounds.W;
        roi.Setting.H = bounds.H;

        roi.RefreshBounds();
        _services.Events.Info(account.Name, $"已重框 {roi.Label}", bounds.ToString());
        _model.NotifyConfigChanged();
    }

    private void OnRoiDeleteClick(object sender, RoutedEventArgs e)
    {
        var account = _model.SelectedAccount;
        if (account is null || (sender as FrameworkElement)?.Tag is not RoiViewModel roi)
        {
            return;
        }

        if (MessageBox.Show(this, $"删除「{roi.Label}」？", "删除 ROI", MessageBoxButton.OKCancel, MessageBoxImage.Question)
            != MessageBoxResult.OK)
        {
            return;
        }

        account.RemoveRoi(roi);
        _services.Events.Info(account.Name, $"已删除 ROI「{roi.Label}」");
    }

    /// <summary>
    /// 双击 ROI 名称进入重命名。
    /// <para>
    /// 名称平时是静态文本，双击才切换成输入框：常驻的透明输入框会被误当成输入区，
    /// 而且它会把点击吞掉。
    /// </para>
    /// </summary>
    private void OnRoiNameMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not TextBlock display || display.Parent is not Grid host)
        {
            return;
        }

        var edit = host.Children.OfType<TextBox>().FirstOrDefault();
        if (edit is null || edit.DataContext is not RoiViewModel roi)
        {
            return;
        }

        // 记下进入编辑时的原值，Esc 与「清空后提交」用它还原
        edit.Tag = roi.Label;

        display.Visibility = Visibility.Collapsed;
        edit.Visibility = Visibility.Visible;
        edit.Focus();
        Keyboard.Focus(edit);
        edit.SelectAll();

        e.Handled = true;
    }

    private void OnRoiNameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox edit)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            EndRoiNameEdit(edit, revert: false);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndRoiNameEdit(edit, revert: true);
            e.Handled = true;
        }
    }

    private void OnRoiNameEditDone(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox edit)
        {
            EndRoiNameEdit(edit, revert: false);
        }
    }

    private void EndRoiNameEdit(TextBox edit, bool revert)
    {
        var original = edit.Tag as string;

        // 名字被清空时退回原值：否则卡片会变成无名，事件提示也会成为「-命中 x3」
        if (revert || string.IsNullOrWhiteSpace(edit.Text))
        {
            edit.Text = original ?? string.Empty;
        }
        else
        {
            // 绑定用的是 Explicit：只有确认时才写回配置
            edit.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }

        edit.Visibility = Visibility.Collapsed;

        if (edit.Parent is Grid host)
        {
            foreach (var child in host.Children)
            {
                if (child is TextBlock block)
                {
                    block.Visibility = Visibility.Visible;
                }
            }
        }
    }

    private void OnRoiPreviewAudioClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RoiViewModel roi)
        {
            return;
        }

        if (roi.Setting.Alarm.IsPreset)
        {
            var preset = Core.Alarm.AudioPresets.Find(roi.Setting.Alarm.Id) ?? Core.Alarm.AudioPresets.Default;
            _ = _services.Alarm.Preview(preset, roi.Setting.Volume);
            return;
        }

        if (string.IsNullOrWhiteSpace(roi.Setting.Alarm.Path) || !File.Exists(roi.Setting.Alarm.Path))
        {
            MessageBox.Show(this, "该 ROI 绑定的音频文件不存在。", "试听", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _ = _services.Alarm.PreviewFile(roi.Setting.Alarm.Path, roi.Setting.Volume);
    }

    /// <summary>
    /// 打开颜色提取器：从当前 ROI 画面取色或手动输入，把新颜色加进颜色库并勾选到本条 ROI。
    /// </summary>
    private void OnRoiPickColorClick(object sender, RoutedEventArgs e)
    {
        if (_model.SelectedAccount is not { } account
            || (sender as FrameworkElement)?.Tag is not RoiViewModel roi)
        {
            return;
        }

        var (bgra, width, height) = GrabRoiPixels(account, roi);
        var dialog = new ColorPickerWindow(
            _services.Config.Current.Palette,
            bgra,
            width,
            height,
            $"{account.Name} · {roi.Label}")
        {
            Owner = this,
        };

        var accepted = dialog.ShowDialog() == true;

        if (accepted && dialog.Added is { } added && !roi.Setting.Colors.Contains(added.Id))
        {
            roi.Setting.Colors.Add(added.Id);
        }

        if (!dialog.Changed)
        {
            return;
        }

        // 颜色库是配置的一部分：色块按新库重建，并走统一的延迟保存出口
        _model.NotifyPaletteChanged();
        _services.Events.Info(
            account.Name,
            "颜色库已更新",
            dialog.Added is { } color ? $"新增「{color.Name}」{color.Hex}" : "已删除自定义颜色");
    }

    /// <summary>
    /// 取一条 ROI 的当前像素供取色用。
    /// 优先读并排窗发布的快照（无额外采集成本）；没有则临时建会话抓一次，抓完立刻摘除。
    /// </summary>
    private (byte[]? Bgra, int Width, int Height) GrabRoiPixels(AccountViewModel account, RoiViewModel roi)
    {
        var snapshot = _services.Frames.TryGet(account.Name, roi.Id);
        if (snapshot is not null && snapshot.Width > 0 && snapshot.Height > 0)
        {
            return ((byte[])snapshot.Bgra.Clone(), snapshot.Width, snapshot.Height);
        }

        if (account.Hwnd == nint.Zero || WindowFinder.IsMinimized(account.Hwnd))
        {
            return (null, 0, 0);
        }

        var attachedHere = false;

        try
        {
            if (!_services.Capture.IsAlive(account.Hwnd))
            {
                attachedHere = _services.Capture.Attach(account.Hwnd, 0);
            }

            var frame = _services.Capture.CaptureRoi(account.Hwnd, roi.Setting.Bounds);

            // 返回的缓冲由采集会话复用，必须立刻留一份
            return frame is null ? (null, 0, 0) : ((byte[])frame.Bgra.Clone(), frame.Width, frame.Height);
        }
        catch
        {
            return (null, 0, 0);
        }
        finally
        {
            if (attachedHere)
            {
                _services.Capture.Detach(account.Hwnd);
            }
        }
    }

    // ---------------------------------------------------------------- 切到客户端

    /// <summary>
    /// 双击 ROI 卡片 = 切到该 ROI 所属账号的客户端。
    /// <para>
    /// 卡片里的按钮、开关、下拉、音量条、名称编辑区都会自己吃掉鼠标事件，
    /// 不会冒泡到这里，所以能和「点空白处双击」区分开。
    /// </para>
    /// </summary>
    private void OnRoiCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
        {
            return;
        }

        // 卡片展示的就是当前选中账号的 ROI
        var account = _model.SelectedAccount;
        if (account is not null)
        {
            FocusAccountWindow(account.Name, account.Hwnd);
        }
    }

    /// <summary>
    /// 把焦点交给某个账号的游戏窗口。双击账号行、双击 ROI 卡片、双击并排窗走的是同一套语义。
    /// </summary>
    private void FocusAccountWindow(string accountName, nint hwndHint)
    {
        var hwnd = hwndHint != nint.Zero
            ? hwndHint
            : _services.Directory.Find(accountName)?.Hwnd ?? nint.Zero;

        // 抢焦点必须是本次交互的最后一步：之后任何对自身窗口的操作都可能把焦点抢回来
        var activated = hwnd != nint.Zero && WindowFinder.Activate(hwnd);

        if (!activated)
        {
            _services.Events.Warn(
                accountName,
                "切到客户端未成功",
                hwnd == nint.Zero ? "未找到该账号的窗口" : "系统前台锁拒绝了本次切换，请在任务栏手动点选");
        }
    }

    // ---------------------------------------------------------------- 弹出窗口

    private void OnCloneClick(object sender, RoutedEventArgs e)
    {
        if (_services.Config.Current.Accounts.Count == 0)
        {
            MessageBox.Show(this, "还没有发现任何账号。", "克隆设置", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 基准账号在窗口内可选，这里只提供默认值
        var dialog = new CloneWindow(_services, _model.SelectedAccount?.Setting)
        {
            Owner = this,
        };

        if (dialog.ShowDialog() == true)
        {
            _model.ReloadAccounts();
            _model.NotifyConfigChanged();
        }
    }

    private void OnWallClick(object sender, RoutedEventArgs e) => ToggleWall();

    private void ToggleWall()
    {
        if (_wallWindow is { IsLoaded: true })
        {
            _wallWindow.Close();
            return;
        }

        var wall = new RoiWallWindow(_services);

        wall.AccountActivated += name =>
        {
            var account = _model.Accounts.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
            if (account is not null)
            {
                _model.SelectedAccount = account;
                AccountList.ScrollIntoView(account);
            }

            // 这里刻意不调用 Show()/Activate()。
            // 并排窗双击的语义是把焦点交给游戏客户端；把主窗口提到前台
            // 会把刚切过去的焦点又抢回来，结果就是焦点落在本工具上。
            // 选中账号只是更新数据，不需要窗口处于前台。
        };

        // 关闭可能来自工具栏按钮，也可能来自窗口内的 Esc，
        // 因此按钮状态一律在这里复位，不依赖调用方
        wall.Closed += (_, _) =>
        {
            _wallWindow = null;
            _model.IsWallOpen = false;
        };

        _wallWindow = wall;
        wall.Show();
        _model.IsWallOpen = true;
    }

    private void OnLayoutSyncClick(object sender, RoutedEventArgs e)
    {
        if (_syncWindow is { IsLoaded: true })
        {
            _syncWindow.Activate();
            return;
        }

        _syncWindow = new SettingsSyncWindow(_services);
        _syncWindow.Closed += (_, _) => _syncWindow = null;
        _syncWindow.Show();
    }

    // ---------------------------------------------------------------- 快捷键

    private void ApplyHotkeys()
    {
        foreach (var error in Hotkeys.Apply())
        {
            _services.Events.Warn("快捷键", error);
        }
    }

    private void OnHotkeyTriggered(HotkeyActionInfo action)
    {
        switch (action.Id)
        {
            case HotkeyActions.ToggleMonitor:
                _model.ToggleMonitoring();
                SyncHeaderState();
                break;

            case HotkeyActions.ToggleMute:
                _model.ToggleMute();
                SyncHeaderState();
                break;

            case HotkeyActions.ToggleMainWindow:
                ToggleMainWindow();
                break;

            case HotkeyActions.ToggleWall:
                ToggleWall();
                break;

            case HotkeyActions.ExportShot:
                ExportShot(interactive: false);
                break;

            case HotkeyActions.NextAccount:
                SelectAccountByOffset(1);
                break;

            case HotkeyActions.PreviousAccount:
                SelectAccountByOffset(-1);
                break;

            case HotkeyActions.OpenSettings:
                OpenSettings();
                break;
        }
    }

    private void ToggleMainWindow()
    {
        if (IsVisible && IsActive)
        {
            Hide();
            return;
        }

        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>循环切换选中账号，并把焦点交给它的游戏窗口。只在可见账号之间循环。</summary>
    private void SelectAccountByOffset(int offset)
    {
        var accounts = _model.Accounts.Where(a => a.IsVisible).ToList();
        if (accounts.Count == 0)
        {
            return;
        }

        var index = _model.SelectedAccount is { } current ? accounts.IndexOf(current) : -1;
        var next = index < 0
            ? (offset > 0 ? 0 : accounts.Count - 1)
            : (index + offset) % accounts.Count;

        if (next < 0)
        {
            next += accounts.Count;
        }

        var account = accounts[next];
        _model.SelectedAccount = account;
        AccountList.ScrollIntoView(account);
        FocusAccountWindow(account.Name, account.Hwnd);
    }

    private void OnIntelSettingsClick(object sender, RoutedEventArgs e) => OpenIntelSettings();

    private void OpenIntelSettings()
    {
        if (_intelSettingsWindow is { IsLoaded: true })
        {
            _intelSettingsWindow.Activate();
            return;
        }

        _intelSettingsWindow = new IntelSettingsWindow(_services)
        {
            Owner = this,
        };

        _intelSettingsWindow.Closed += (_, _) => _intelSettingsWindow = null;
        _intelSettingsWindow.Show();
        _intelSettingsWindow.Activate();
    }

    private void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_services, Hotkeys)
        {
            Owner = this,
        };

        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OnIntelAlertRaised(IntelAlertBatch batch)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _intelWindow ??= new IntelAlertWindow(_services);
            _intelWindow.ShowBatch(batch);
        }));
    }

    // ---------------------------------------------------------------- 辅助

    private void SyncHeaderState()
    {
        _model.RefreshStatus();
        _services.Events.Info("EveWatch", _model.IsMonitoring ? "监控已启动" : "监控已停止");
    }

    private string? PickAudioFile(RoiSetting roi)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择报警音频",
            Filter = "音频文件|*.wav;*.mp3|所有文件|*.*",
            CheckFileExists = true,
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private static IReadOnlyList<Core.Alarm.AudioPreset> AudioChoices() => Core.Alarm.AudioPresets.All;

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    private static string Sanitize(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        }

        return builder.ToString();
    }
}
