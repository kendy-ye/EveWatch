using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using EveWatch.Core.Config;
using EveWatch.Core.Events;

namespace EveWatch.App;

public partial class App : Application
{
    private AppServices? _services;
    private System.Windows.Forms.NotifyIcon? _tray;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        _services = AppServices.Create();
        var services = _services;

        services.Directory.Start();
        services.Directory.Changed += () => Dispatcher.BeginInvoke(new Action(() => _mainWindow?.OnAccountsDiscovered()));

        if (services.Config.LastError is { } configError)
        {
            services.Events.Warn("EveWatch", "配置读取异常", configError);
        }

        CreateTrayIcon(services);

        _mainWindow = new MainWindow(services);
        _mainWindow.Show();

        services.Events.Info(
            "EveWatch",
            "已启动",
            $"WGC 支持：{AppServices.IsCaptureSupported}　配置：{services.Config.Path}");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();

        try
        {
            _services?.Config.Save();
            _services?.UiState.Save();
        }
        catch
        {
            // 退出阶段忽略
        }

        _services?.Dispose();
        base.OnExit(e);
    }

    /// <summary>请求退出（托盘菜单与主窗口关闭按钮共用）。</summary>
    public void RequestShutdown()
    {
        _mainWindow?.AllowClose();
        Shutdown();
    }

    // ---------------------------------------------------------------- 托盘

    private void CreateTrayIcon(AppServices services)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();

        var showItem = new System.Windows.Forms.ToolStripMenuItem("显示主窗口");
        showItem.Click += (_, _) => Dispatcher.Invoke(() => ShowMainWindow());
        menu.Items.Add(showItem);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var monitorItem = new System.Windows.Forms.ToolStripMenuItem("开始监控");
        monitorItem.Click += (_, _) => Dispatcher.Invoke(() =>
        {
            if (services.Monitor.Running)
            {
                services.Monitor.Stop();
            }
            else
            {
                services.Monitor.Start();
            }

            SyncTrayText(services);
            _mainWindow?.RefreshFromServices();
        });
        menu.Items.Add(monitorItem);

        var muteItem = new System.Windows.Forms.ToolStripMenuItem("全局静音");
        muteItem.Click += (_, _) => Dispatcher.Invoke(() =>
        {
            services.Config.Current.GlobalMute = !services.Config.Current.GlobalMute;
            services.Config.Save();

            if (services.Config.Current.GlobalMute)
            {
                services.Alarm.StopAll();
            }

            SyncTrayText(services);
            _mainWindow?.RefreshFromServices();
        });
        menu.Items.Add(muteItem);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var exitItem = new System.Windows.Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => Dispatcher.Invoke(RequestShutdown);
        menu.Items.Add(exitItem);

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = CreateTrayImage(),
            Text = "EveWatch · 本地预警",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => ShowMainWindow());
        _tray.BalloonTipClicked += (_, _) => Dispatcher.Invoke(() => ShowMainWindow());

        // 托盘菜单每次打开时同步勾选状态
        menu.Opening += (_, _) =>
        {
            monitorItem.Text = services.Monitor.Running ? "停止监控" : "开始监控";
            muteItem.Checked = services.Config.Current.GlobalMute;
        };
    }

    private void SyncTrayText(AppServices services)
    {
        if (_tray is null)
        {
            return;
        }

        var state = services.Monitor.Running ? "监控中" : "已停止";
        var mute = services.Config.Current.GlobalMute ? " · 静音" : string.Empty;
        _tray.Text = $"EveWatch · {state}{mute} · {services.Monitor.ActiveWatchers} 路";
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    /// <summary>
    /// 托盘与 exe、任务栏共用同一份多尺寸图标（Assets/evewatch.ico）。
    /// <para>
    /// 这里按目标尺寸取最接近的一帧，而不是先取一帧再让系统缩：托盘只有 16×16，
    /// 从 256 帧缩下来会糊。
    /// </para>
    /// </summary>
    private static Icon CreateTrayImage()
    {
        var resource = GetResourceStream(new Uri("Assets/evewatch.ico", UriKind.Relative));
        if (resource is null)
        {
            return SystemIcons.Application;
        }

        using var stream = resource.Stream;
        return new Icon(stream, new System.Drawing.Size(16, 16));
    }

    // ---------------------------------------------------------------- 全局异常

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            WriteCrashLog(exception);
        }
    }

    private static void WriteCrashLog(Exception exception)
    {
        try
        {
            var builder = new StringBuilder();
            builder.AppendLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
            builder.AppendLine(exception.ToString());
            builder.AppendLine();

            File.AppendAllText(AppPaths.CrashLog, builder.ToString(), Encoding.UTF8);
        }
        catch
        {
            // 连日志都写不了就放弃，不能再抛
        }
    }
}
