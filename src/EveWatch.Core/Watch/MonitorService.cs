using EveWatch.Core.Alarm;
using EveWatch.Core.Capture;
using EveWatch.Core.Config;
using EveWatch.Core.Discovery;
using EveWatch.Core.Events;
using EveWatch.Core.Status;

namespace EveWatch.Core.Watch;

/// <summary>
/// 轮询调度：按配置与当前发现的窗口，启停各账号的轮询线程。
/// <para>
/// 未勾选的账号不创建线程，而不是创建后丢弃结果。
/// 全部线程按序号分配错峰延迟，避免采集时刻撞在一起造成周期性 CPU 尖峰。
/// </para>
/// </summary>
public sealed class MonitorService : IDisposable
{
    private readonly ConfigStore _store;
    private readonly WgcCaptureService _capture;
    private readonly AccountDirectory _directory;
    private readonly StatusStore _status;
    private readonly AlarmPlayer _alarm;
    private readonly EventJournal _events;
    private readonly FrameStore _frames;
    private readonly Dictionary<string, RoiWatcher> _watchers = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private bool _disposed;

    public MonitorService(
        ConfigStore store,
        WgcCaptureService capture,
        AccountDirectory directory,
        StatusStore status,
        AlarmPlayer alarm,
        EventJournal events,
        FrameStore frames)
    {
        _store = store;
        _capture = capture;
        _directory = directory;
        _status = status;
        _alarm = alarm;
        _events = events;
        _frames = frames;
    }

    /// <summary>是否处于监控状态。</summary>
    public bool Running { get; private set; }

    /// <summary>当前活跃的轮询线程数。</summary>
    public int ActiveWatchers
    {
        get
        {
            lock (_sync)
            {
                return _watchers.Count;
            }
        }
    }

    /// <summary>开始监控。</summary>
    public void Start()
    {
        if (_disposed || Running)
        {
            return;
        }

        Running = true;
        Sync();
        _events.Info("EveWatch", "监控已启动", $"{ActiveWatchers} / {_store.Current.Accounts.Count} 个账号");
    }

    /// <summary>停止监控，释放全部采集会话与播放通道。</summary>
    public void Stop()
    {
        if (!Running)
        {
            return;
        }

        Running = false;

        List<RoiWatcher> all;
        lock (_sync)
        {
            all = [.. _watchers.Values];
            _watchers.Clear();
        }

        foreach (var watcher in all)
        {
            watcher.Stop();
            watcher.Dispose();
            _capture.Detach(watcher.Hwnd);
        }

        _alarm.StopAll();

        foreach (var status in _status.All())
        {
            status.MonitorEnabled = false;
            status.Hostile = false;
            status.ActualFps = 0;

            // 逐 ROI 的本帧命中也要清：界面按它显示命中角标，不清就会一直停在停止前的红色
            foreach (var roi in status.Rois)
            {
                roi.HitBlocks = 0;
                roi.HitText = string.Empty;
            }
        }

        _events.Info("EveWatch", "监控已停止");
    }

    /// <summary>按当前配置与窗口快照重新对齐监控范围。配置改动或窗口增删后调用。</summary>
    public void Sync()
    {
        if (_disposed || !Running)
        {
            return;
        }

        var config = _store.Current;
        var wanted = new List<(string Name, nint Hwnd)>();

        foreach (var account in config.Accounts)
        {
            var window = _directory.Find(account.Name);

            // 窗口消失说明已掉线或退回登录界面：立即停掉采集并清空运行时状态，
            // 否则 watcher 会继续拿旧句柄采集登录画面，命中结论也会残留
            if (window is null)
            {
                // 先停线程再清状态：反过来的话，线程可能在清理后又写回一帧旧结果
                StopWatcher(account.Name);
                _status.ResetRuntime(account.Name);
                continue;
            }

            var status = _status.GetOrAdd(account.Name);
            status.Hwnd = window.Hwnd;
            status.Minimized = window.Minimized;

            var hasEnabledRoi = false;
            foreach (var roi in account.Rois)
            {
                if (roi.Enabled && roi.Bounds.IsValid)
                {
                    hasEnabledRoi = true;
                    break;
                }
            }

            if (Running && account.Enabled && hasEnabledRoi)
            {
                wanted.Add((account.Name, window.Hwnd));
            }
            else
            {
                StopWatcher(account.Name);

                status.MonitorEnabled = false;
                status.SessionReady = false;
                status.ActualFps = 0;
                status.Hostile = false;

                foreach (var roi in status.Rois)
                {
                    roi.HitBlocks = 0;
                    roi.HitText = string.Empty;
                }
            }
        }

        // 窗口句柄变化的账号需要重建会话
        List<RoiWatcher> stale;
        lock (_sync)
        {
            stale = [];
            foreach (var (name, watcher) in _watchers)
            {
                var match = wanted.Find(w => string.Equals(w.Name, name, StringComparison.Ordinal));
                if (match.Hwnd == nint.Zero || match.Hwnd != watcher.Hwnd)
                {
                    stale.Add(watcher);
                }
            }
        }

        foreach (var watcher in stale)
        {
            StopWatcher(watcher.AccountName);
        }

        var total = Math.Max(1, wanted.Count);
        var tick = Math.Max(100, config.TickMs);

        for (var i = 0; i < wanted.Count; i++)
        {
            var (name, hwnd) = wanted[i];

            lock (_sync)
            {
                if (_watchers.TryGetValue(name, out var existing))
                {
                    if (!existing.IsRunning)
                    {
                        existing.Start(i * (tick / total));
                    }
                }
                else
                {
                    var watcher = new RoiWatcher(name, hwnd, () => _store.Current, _capture, _status, _alarm, _events, _frames);
                    _watchers[name] = watcher;
                    watcher.Start(i * (tick / total));
                }
            }
        }
    }

    private void StopWatcher(string accountName)
    {
        RoiWatcher? watcher;
        lock (_sync)
        {
            if (!_watchers.Remove(accountName, out watcher))
            {
                return;
            }
        }

        watcher.Stop();
        watcher.Dispose();
        _capture.Detach(watcher.Hwnd);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }
}
