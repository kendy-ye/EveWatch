using System.Diagnostics;
using EveWatch.Core.Alarm;
using EveWatch.Core.Capture;
using EveWatch.Core.Config;
using EveWatch.Core.Detect;
using EveWatch.Core.Discovery;
using EveWatch.Core.Events;
using EveWatch.Core.Palette;
using EveWatch.Core.Status;

namespace EveWatch.Core.Watch;

/// <summary>
/// 一个账号的轮询线程。
/// <para>
/// 每个 tick：确认窗口可用 → 逐条 ROI 采集与判定 → 去抖与冷却 → 必要时报警 → 写状态快照。
/// 账号下的多条 ROI 共用本 tick 取到的那一帧（<c>BeginFrame</c> 取一次，各条 ROI 在同一帧上分别裁切）。
/// </para>
/// <para>整个 tick 包在异常捕获里：任一账号异常只影响该账号，不影响其它账号。</para>
/// </summary>
public sealed class RoiWatcher : IDisposable
{
    private sealed class RoiRuntime
    {
        public required string Id { get; init; }

        /// <summary>按本条 ROI 启用的颜色单独构建的查找表。</summary>
        public ColorLut Lut { get; } = new();

        /// <summary>颜色集合的签名，用于判断是否需要重建查找表。</summary>
        public string LutSignature { get; set; } = string.Empty;

        /// <summary>连续命中帧数。</summary>
        public int HitStreak { get; set; }

        public long LastHitAtMs { get; set; }

        /// <summary>累计命中次数：一次连续命中算一次，不是每帧块数相加。</summary>
        public long HitEpisodes { get; set; }

        public int LastHitBlocks { get; set; }

        /// <summary>最近一次命中的描述文本。</summary>
        /// <remarks>
        /// 必须逐 ROI 存下来。判定结果对象是轮询循环共用的，
        /// 循环结束后里面只剩最后一条 ROI 的数据，回填状态时再算会串到别的 ROI 上。
        /// </remarks>
        public string LastHitText { get; set; } = string.Empty;
    }

    private readonly string _accountName;
    private readonly nint _hwnd;
    private readonly Func<AppConfig> _config;
    private readonly WgcCaptureService _capture;
    private readonly StatusStore _status;
    private readonly AlarmPlayer _alarm;
    private readonly EventJournal _events;
    private readonly FrameStore _frames;

    private readonly IconBlockDetector _detector = new();
    private readonly DetectResult _result = new();
    private readonly List<RoiRuntime> _runtimes = [];
    private readonly ManualResetEventSlim _stop = new(false);

    private readonly Stopwatch _fpsWindow = Stopwatch.StartNew();

    private Thread? _thread;
    private long _ticksInWindow;
    private bool _disposed;

    public RoiWatcher(
        string accountName,
        nint hwnd,
        Func<AppConfig> config,
        WgcCaptureService capture,
        StatusStore status,
        AlarmPlayer alarm,
        EventJournal events,
        FrameStore frames)
    {
        _accountName = accountName;
        _hwnd = hwnd;
        _config = config;
        _capture = capture;
        _status = status;
        _alarm = alarm;
        _events = events;
        _frames = frames;
    }

    public string AccountName => _accountName;

    /// <summary>目标窗口句柄。</summary>
    public nint Hwnd => _hwnd;

    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>启动轮询线程。</summary>
    /// <param name="initialDelayMs">
    /// 首次触发前的延迟。多账号同时启动会给 CPU 造成周期性尖峰，
    /// 调用方按序号给出 <c>i × (节拍 / 账号数)</c> 的延迟，把采集时刻摊开。
    /// </param>
    public void Start(int initialDelayMs = 0)
    {
        if (_disposed || IsRunning)
        {
            return;
        }

        _stop.Reset();
        _thread = new Thread(() => Loop(initialDelayMs))
        {
            IsBackground = true,
            Name = $"EveWatch-{_accountName}",
            Priority = ThreadPriority.BelowNormal,
        };

        _thread.Start();
    }

    public void Stop()
    {
        _stop.Set();

        var thread = _thread;
        _thread = null;

        if (thread is not null && thread.IsAlive)
        {
            _ = thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    // ---------------------------------------------------------------- 主循环

    private void Loop(int initialDelayMs)
    {
        if (initialDelayMs > 0 && _stop.Wait(initialDelayMs))
        {
            return;
        }

        while (!_stop.IsSet)
        {
            var started = Environment.TickCount64;

            try
            {
                Tick(started);
            }
            catch (Exception ex)
            {
                _events.Error(_accountName, "轮询异常", ex.Message);
            }

            var spent = (int)(Environment.TickCount64 - started);
            var wait = Math.Max(20, _config().TickMs - spent);

            if (_stop.Wait(wait))
            {
                break;
            }
        }
    }

    private void Tick(long now)
    {
        var config = _config();
        var status = _status.GetOrAdd(_accountName);
        status.Hwnd = _hwnd;

        var account = config.FindAccount(_accountName);
        if (account is null || !account.Enabled)
        {
            status.MonitorEnabled = false;
            ClearHitStatus(account, status);
            return;
        }

        status.MonitorEnabled = true;

        // 最小化时 WGC 不产帧，直接跳过并计数
        if (WindowFinder.IsMinimized(_hwnd) || !WindowFinder.IsAlive(_hwnd))
        {
            status.Minimized = true;
            status.DropFrames++;
            status.SessionReady = _capture.IsAlive(_hwnd);

            // 取不到帧就不能留上一帧的命中结论，否则界面会一直挂着「敌对」
            ClearHitStatus(account, status);
            return;
        }

        status.Minimized = false;

        if (!_capture.IsAlive(_hwnd) && !_capture.Attach(_hwnd, config.Capture.MinUpdateIntervalMs))
        {
            status.SessionReady = false;
            status.DropFrames++;
            ClearHitStatus(account, status);
            return;
        }

        status.SessionReady = true;

        SyncRuntimes(account);

        // 本 tick 只取一帧：账号下的各条 ROI 都在同一帧上裁切，
        // 既保证同一次判定看到的是同一时刻的画面，也把取帧等待从「每条一次」压到「每 tick 一次」
        _capture.BeginFrame(_hwnd);
        try
        {
            status.Hostile = ScanRois(account, config, status, now);
        }
        finally
        {
            _capture.EndFrame(_hwnd);
        }

        status.Rois = BuildRoiStatus(account);
        status.Ticks++;
        status.LastTickCostMs = Environment.TickCount64 - now;
        status.LastCaptureCostMs = _capture.LastCaptureCostMs(_hwnd);
        UpdateFps(status);
    }

    /// <summary>逐条 ROI 采集与判定；返回本 tick 是否有命中。</summary>
    private bool ScanRois(AccountSetting account, AppConfig config, AccountStatus status, long now)
    {
        var detectOptions = config.Detect;
        var debounce = Math.Max(1, config.DebounceFrames);
        var cooldown = Math.Max(0, config.CooldownMs);
        var anyHit = false;

        foreach (var roi in account.Rois)
        {
            if (!roi.Enabled || !roi.Bounds.IsValid)
            {
                continue;
            }

            var runtime = FindRuntime(roi.Id);
            if (runtime is null)
            {
                continue;
            }

            EnsureLut(runtime, roi, account, config);

            var frame = _capture.CaptureRoi(_hwnd, roi.Bounds);
            if (frame is null)
            {
                status.DropFrames++;
                continue;
            }

            _detector.Detect(frame.Bgra, frame.Width, frame.Height, runtime.Lut, detectOptions, _result);

            // 并排窗打开时才发布画面；帧缓冲下一次采集就会被复用，所以必须在此时拷走
            if (_frames.Enabled)
            {
                _frames.Publish(_accountName, roi.Id, frame.Bgra, frame.Width, frame.Height, now);
            }

            runtime.LastHitBlocks = _result.HitCount;

            if (_result.HitCount == 0)
            {
                runtime.HitStreak = 0;
                runtime.LastHitText = string.Empty;
                continue;
            }

            anyHit = true;
            runtime.HitStreak++;
            runtime.LastHitAtMs = now;

            // 卡片角标用的是按色明细
            runtime.LastHitText = DescribeColors(runtime.Lut, _result);

            // 事件提示用总数：xN 便于扫读，按色明细挪进副文本
            var hitTotal = _result.TotalHits;

            if (runtime.HitStreak == 1)
            {
                runtime.HitEpisodes++;

                _events.Add(
                    AlarmEventKind.Hit,
                    _accountName,
                    $"{roi.Label}-命中 x{hitTotal}",
                    $"{runtime.LastHitText} · 去抖 {debounce} 帧");
            }

            if (runtime.HitStreak < debounce)
            {
                continue;
            }

            var sinceLastAlarm = now - status.LastAlarmAtMs;
            if (sinceLastAlarm < cooldown)
            {
                continue;
            }

            status.LastAlarmAtMs = now;

            var play = _alarm.Play(_accountName, roi.Alarm, roi.Volume);

            _events.Add(
                AlarmEventKind.Alarm,
                _accountName,
                $"{roi.Label}-命中 x{hitTotal}",
                play switch
                {
                    AlarmPlayResult.Played => $"{runtime.LastHitText} · 报警音 {DescribeAlarm(roi.Alarm)}",
                    AlarmPlayResult.Busy => $"{runtime.LastHitText} · {_alarm.ActiveKey ?? "其它账号"} 正在报警，本次让位",
                    AlarmPlayResult.Muted => $"{runtime.LastHitText} · 全局静音，仅记录",
                    _ => $"{runtime.LastHitText} · 音频不可用，仅记录",
                });
        }

        return anyHit;
    }

    /// <summary>
    /// 窗口不可采或账号停用时复位命中结论。
    /// <para>
    /// 必须连逐 ROI 状态一起写：界面按数组下标读 <c>Rois[i]</c>，只把数组清空会让下标全部越界，
    /// 逐条 ROI 的命中角标反而停在最后一帧的红。这里保留累计次数，只清「本帧」命中。
    /// </para>
    /// </summary>
    private void ClearHitStatus(AccountSetting? account, AccountStatus status)
    {
        status.Hostile = false;

        if (account is null)
        {
            foreach (var roi in status.Rois)
            {
                roi.HitBlocks = 0;
                roi.HitText = string.Empty;
            }

            return;
        }

        var result = new RoiStatus[account.Rois.Count];
        for (var i = 0; i < account.Rois.Count; i++)
        {
            var roi = account.Rois[i];
            var runtime = FindRuntime(roi.Id);

            result[i] = new RoiStatus
            {
                Id = roi.Id,
                Label = roi.Label,
                Enabled = roi.Enabled,
                HitBlocks = 0,
                HitText = string.Empty,
                LastHitAtMs = runtime?.LastHitAtMs ?? 0,
                TotalHits = runtime?.HitEpisodes ?? 0,
            };
        }

        status.Rois = result;
    }

    // ---------------------------------------------------------------- 辅助

    private void UpdateFps(AccountStatus status)
    {
        _ticksInWindow++;
        var elapsed = _fpsWindow.Elapsed.TotalSeconds;
        if (elapsed >= 1.0)
        {
            status.ActualFps = _ticksInWindow / elapsed;
            _ticksInWindow = 0;
            _fpsWindow.Restart();
        }
    }

    private RoiRuntime? FindRuntime(string roiId)
    {
        foreach (var runtime in _runtimes)
        {
            if (string.Equals(runtime.Id, roiId, StringComparison.Ordinal))
            {
                return runtime;
            }
        }

        return null;
    }

    /// <summary>同步 ROI 运行时集合：新增缺失项、移除已删除项。</summary>
    private void SyncRuntimes(AccountSetting account)
    {
        foreach (var roi in account.Rois)
        {
            if (string.IsNullOrEmpty(roi.Id) || FindRuntime(roi.Id) is not null)
            {
                continue;
            }

            _runtimes.Add(new RoiRuntime { Id = roi.Id });
        }

        for (var i = _runtimes.Count - 1; i >= 0; i--)
        {
            var id = _runtimes[i].Id;
            var stillThere = false;

            foreach (var roi in account.Rois)
            {
                if (string.Equals(roi.Id, id, StringComparison.Ordinal))
                {
                    stillThere = true;
                    break;
                }
            }

            if (!stillThere)
            {
                _runtimes.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// 按本条 ROI 启用的颜色构建查找表。
    /// 每条 ROI 单独建表是必要的：否则其它颜色也会被分类出标签，导致本 ROI 误报。
    /// </summary>
    private static void EnsureLut(RoiRuntime runtime, RoiSetting roi, AccountSetting account, AppConfig config)
    {
        var signature = string.Join(',', roi.Colors);
        if (string.Equals(signature, runtime.LutSignature, StringComparison.Ordinal))
        {
            return;
        }

        var picked = new List<PaletteColor>();
        foreach (var id in roi.Colors)
        {
            foreach (var color in config.Palette)
            {
                if (string.Equals(color.Id, id, StringComparison.Ordinal))
                {
                    picked.Add(color);
                    break;
                }
            }
        }

        runtime.Lut.Rebuild(picked, config.Detect.ToLutTuning());
        runtime.LutSignature = signature;
    }

    /// <summary>按颜色汇总命中块数，用于卡片角标与事件副文本。</summary>
    private static string DescribeColors(ColorLut lut, DetectResult result)
    {
        var parts = new List<string>();
        for (var label = 1; label < result.LabelCounts.Length && parts.Count < 3; label++)
        {
            var count = result.LabelCounts[label];
            if (count > 0)
            {
                parts.Add($"{lut.NameOfLabel(label) ?? $"#{label}"} ×{count}");
            }
        }

        return parts.Count > 0 ? string.Join('、', parts) : $"{result.HitCount} 块";
    }

    private static string DescribeAlarm(AlarmSetting alarm)
    {
        if (!alarm.IsPreset)
        {
            return System.IO.Path.GetFileName(alarm.Path);
        }

        return AudioPresets.Find(alarm.Id)?.Name ?? alarm.Id;
    }

    private RoiStatus[] BuildRoiStatus(AccountSetting account)
    {
        var result = new RoiStatus[account.Rois.Count];
        for (var i = 0; i < account.Rois.Count; i++)
        {
            var roi = account.Rois[i];
            var runtime = FindRuntime(roi.Id);

            result[i] = new RoiStatus
            {
                Id = roi.Id,
                Label = roi.Label,
                Enabled = roi.Enabled,
                HitBlocks = runtime?.LastHitBlocks ?? 0,
                LastHitAtMs = runtime?.LastHitAtMs ?? 0,
                TotalHits = runtime?.HitEpisodes ?? 0,
                HitText = runtime?.LastHitText ?? string.Empty,
            };
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();

        // 这里刻意不释放 _stop：Stop() 的 Join 有 2 秒上限，超时后线程可能仍停在
        // Loop 里的 _stop.Wait(...) 上（该调用不在 tick 的 try 内），释放会让它抛
        // ObjectDisposedException；后台线程上的未处理异常会直接终止进程。
        // 一个事件对象占用的资源可以忽略，交给 GC 即可。
    }
}
