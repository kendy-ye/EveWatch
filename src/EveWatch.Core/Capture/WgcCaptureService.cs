using EveWatch.Core.Capture.Wgc;

namespace EveWatch.Core.Capture;

/// <summary>
/// 采集服务：按窗口句柄持有 WGC 会话，对外只暴露"给我这个 ROI 的像素"。
/// <para>
/// 一个窗口只有一路会话，该窗口下的多条 ROI 在同一帧上分别裁切。
/// </para>
/// <para>本类型线程安全；采集源内部的 GPU 操作由 <see cref="WgcDevice.Gate"/> 串行化。</para>
/// </summary>
public sealed class WgcCaptureService : IDisposable
{
    private readonly Dictionary<long, WgcCaptureSource> _sources = [];
    private readonly object _sync = new();
    private bool _disposed;

    /// <summary>本机是否支持 Windows Graphics Capture。</summary>
    public static bool IsSupported() => WgcCaptureSource.IsSupported();

    /// <summary>本机是否支持关闭捕获边框（Win11 及以上）。</summary>
    public static bool SupportsBorderToggle => WgcCaptureSource.SupportsBorderToggle;

    /// <summary>本机是否支持限帧（Win11 22H2 及以上）。</summary>
    public static bool SupportsMinUpdateInterval => WgcCaptureSource.SupportsMinUpdateInterval;

    /// <summary>当前已建立的会话数。</summary>
    public int SessionCount
    {
        get
        {
            lock (_sync)
            {
                return _sources.Count;
            }
        }
    }

    /// <summary>为窗口建立采集会话；已存在则直接返回。</summary>
    public bool Attach(nint hwnd, int minUpdateIntervalMs, bool hideBorder = true)
    {
        if (_disposed || hwnd == nint.Zero)
        {
            return false;
        }

        lock (_sync)
        {
            if (_sources.TryGetValue((long)hwnd, out var existing) && existing.IsAlive)
            {
                return true;
            }

            if (existing is not null)
            {
                existing.Dispose();
                _sources.Remove((long)hwnd);
            }

            try
            {
                var source = WgcCaptureSource.Create(
                    $"0x{(long)hwnd:X}",
                    hwnd,
                    hideBorder,
                    minUpdateIntervalMs);

                _sources[(long)hwnd] = source;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>释放某个窗口的会话。</summary>
    public void Detach(nint hwnd)
    {
        WgcCaptureSource? source;
        lock (_sync)
        {
            if (!_sources.Remove((long)hwnd, out source))
            {
                return;
            }
        }

        source?.Dispose();
    }

    public void DetachAll()
    {
        List<WgcCaptureSource> all;
        lock (_sync)
        {
            all = [.. _sources.Values];
            _sources.Clear();
        }

        foreach (var source in all)
        {
            source.Dispose();
        }
    }

    public bool IsAlive(nint hwnd)
    {
        lock (_sync)
        {
            return _sources.TryGetValue((long)hwnd, out var source) && source.IsAlive;
        }
    }

    public int ContentWidth(nint hwnd)
    {
        lock (_sync)
        {
            return _sources.TryGetValue((long)hwnd, out var source) ? source.ContentWidth : 0;
        }
    }

    public int ContentHeight(nint hwnd)
    {
        lock (_sync)
        {
            return _sources.TryGetValue((long)hwnd, out var source) ? source.ContentHeight : 0;
        }
    }

    /// <summary>实际生效的限帧间隔；0 表示未限帧或会话不存在。</summary>
    public int AppliedMinIntervalMs(nint hwnd)
    {
        lock (_sync)
        {
            return _sources.TryGetValue((long)hwnd, out var source) ? source.AppliedMinIntervalMs : 0;
        }
    }

    /// <summary>是否已关闭捕获边框。</summary>
    public bool BorderHidden(nint hwnd)
    {
        lock (_sync)
        {
            return _sources.TryGetValue((long)hwnd, out var source) && source.BorderHidden;
        }
    }

    /// <summary>最近一次采集的 GPU 裁切加回读耗时（毫秒）。</summary>
    public double LastCaptureCostMs(nint hwnd)
    {
        lock (_sync)
        {
            return _sources.TryGetValue((long)hwnd, out var source) ? source.LastCaptureCostMs : 0;
        }
    }

    /// <summary>
    /// 开始一次 tick：为该窗口取一帧并持有，供本次 tick 内的各条 ROI 复用。
    /// 会话不存在时为空操作，后续 <see cref="CaptureRoi"/> 会自动建立会话。
    /// </summary>
    public void BeginFrame(nint hwnd)
    {
        WgcCaptureSource? source;
        lock (_sync)
        {
            _sources.TryGetValue((long)hwnd, out source);
        }

        source?.BeginTickFrame();
    }

    /// <summary>结束一次 tick：释放 <see cref="BeginFrame"/> 持有的帧。</summary>
    public void EndFrame(nint hwnd)
    {
        WgcCaptureSource? source;
        lock (_sync)
        {
            _sources.TryGetValue((long)hwnd, out source);
        }

        source?.EndTickFrame();
    }

    /// <summary>
    /// 抓取某个 ROI 的像素。返回的缓冲由会话持有并复用，下一次采集即被覆盖。
    /// 会话不存在、窗口失活或取帧失败时返回 <c>null</c>。
    /// </summary>
    public RoiFrame? CaptureRoi(nint hwnd, RoiRect roi)
    {
        WgcCaptureSource? source;
        lock (_sync)
        {
            _sources.TryGetValue((long)hwnd, out source);
        }

        if (source is null)
        {
            return null;
        }

        var bgra = source.CaptureRoiBgra(roi, out var width, out var height);
        if (bgra is null)
        {
            return null;
        }

        return new RoiFrame
        {
            Width = width,
            Height = height,
            Bgra = bgra,
            TimestampMs = Environment.TickCount64,
        };
    }

    /// <summary>抓取整幅内容区（框选用）。</summary>
    public RoiFrame? CaptureFull(nint hwnd)
    {
        WgcCaptureSource? source;
        lock (_sync)
        {
            _sources.TryGetValue((long)hwnd, out source);
        }

        if (source is null)
        {
            return null;
        }

        var bgra = source.CaptureFullBgra(out var width, out var height);
        if (bgra is null)
        {
            return null;
        }

        return new RoiFrame
        {
            Width = width,
            Height = height,
            Bgra = bgra,
            TimestampMs = Environment.TickCount64,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DetachAll();
    }
}
