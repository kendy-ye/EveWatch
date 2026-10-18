using EveWatch.Core.Alarm;
using EveWatch.Core.Capture;
using EveWatch.Core.Config;
using EveWatch.Core.Discovery;
using EveWatch.Core.Events;
using EveWatch.Core.Intel;
using EveWatch.Core.Status;
using EveWatch.Core.Watch;

namespace EveWatch.App;

/// <summary>
/// 应用级服务容器。
/// <para>核心层的组件都是有状态的长生命周期对象，在这里创建一次并共享。</para>
/// </summary>
public sealed class AppServices : IDisposable
{
    private bool _disposed;

    public AppServices()
    {
        Config = new ConfigStore();
        Config.Load();

        UiState = new UiStateStore();
        UiState.Load();

        Capture = new WgcCaptureService();
        Directory = new AccountDirectory(Config.Current.ProcessName);
        Status = new StatusStore();
        Frames = new FrameStore();
        Alarm = new AlarmPlayer(() => Config.Current);
        Events = new EventJournal();
        Monitor = new MonitorService(Config, Capture, Directory, Status, Alarm, Events, Frames);
        Intel = new IntelAlertService(Config, Alarm, Events);

        // 音频设备或音频文件不可用时要留下痕迹：否则用户只会看到「命中但没响」，无从判断原因
        Alarm.Failed += message => Events.Warn("EveWatch", "报警音频异常", message);

        Config.Changed += Intel.ApplyConfig;
        Intel.ApplyConfig();
    }

    /// <summary>进程内唯一实例。</summary>
    public static AppServices? Current { get; private set; }

    public ConfigStore Config { get; }

    public UiStateStore UiState { get; }

    public WgcCaptureService Capture { get; }

    public AccountDirectory Directory { get; }

    public StatusStore Status { get; }

    /// <summary>ROI 画面快照；只在并排窗打开期间发布。</summary>
    public FrameStore Frames { get; }

    public AlarmPlayer Alarm { get; }

    public EventJournal Events { get; }

    public MonitorService Monitor { get; }

    /// <summary>日志预警：日志扫描、位置识别与批次报警。</summary>
    public IntelAlertService Intel { get; }

    /// <summary>创建并设为当前实例。</summary>
    public static AppServices Create()
    {
        var services = new AppServices();
        Current = services;
        return services;
    }

    /// <summary>本机是否支持 WGC。不支持时界面需要给出明确提示，而不是静默不工作。</summary>
    public static bool IsCaptureSupported => WgcCaptureService.IsSupported();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Config.Changed -= Intel.ApplyConfig;

        try
        {
            Intel.Dispose();
        }
        catch
        {
            // 退出阶段忽略
        }

        try
        {
            Monitor.Dispose();
        }
        catch
        {
            // 退出阶段忽略
        }

        Directory.Dispose();
        Alarm.Dispose();
        Capture.Dispose();

        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }
    }
}
