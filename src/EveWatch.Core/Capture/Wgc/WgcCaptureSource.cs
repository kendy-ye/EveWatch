using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace EveWatch.Core.Capture.Wgc;

/// <summary>
/// Windows Graphics Capture 采集源，对应一个窗口。
///
/// 工作方式：取最新帧 → GPU 侧裁出 ROI → 拷到暂存纹理 → 只回读 ROI 字节。
/// 不做整窗渲染、不整窗回读，且不会强制目标窗口重绘（游戏不再被采集拽着掉帧）。
///
/// 坐标系：窗口内容区，左上角为 (0,0)。全链路（框选 / 采集 / 预览）都用这个坐标系，不做偏移换算。
///
/// 返回的像素缓冲由本对象持有并复用，下一次采集即被覆盖，调用方须立即消费。
/// 一个窗口只有一份采集源，该窗口下的多条 ROI 共用同一帧。
/// </summary>
public sealed class WgcCaptureSource : IDisposable
{
    /// <summary>取帧的有界等待上限。刚 StartCapture 后、或两拍之间没有新帧时，避免直接判失败。</summary>
    private const int FrameWaitMs = 100;

    /// <summary>暂存纹理按尺寸缓存的上限。同一窗口的多条 ROI 尺寸可能各不相同。</summary>
    private const int MaxStagingTextures = 16;

    private readonly WgcDevice _gpu;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly nint _hwnd;
    private readonly object _sync = new();
    private readonly List<Staging> _stagings = [];

    private volatile bool _closed;
    private bool _disposed;

    /// <summary>本 tick 已取到、供该窗口下所有 ROI 复用的那一帧。</summary>
    private Direct3D11CaptureFrame? _tickFrame;

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    private WgcCaptureSource(
        string key,
        nint hwnd,
        WgcDevice gpu,
        GraphicsCaptureItem item,
        Direct3D11CaptureFramePool pool,
        GraphicsCaptureSession session)
    {
        Key = key;
        _hwnd = hwnd;
        _gpu = gpu;
        _item = item;
        _pool = pool;
        _session = session;

        // 捕获项被关闭（窗口销毁等）时标记失效
        _item.Closed += (_, _) => _closed = true;
    }

    /// <summary>采集会话标识，通常为账号名。</summary>
    public string Key { get; }

    /// <summary>捕获项宽度（内容区）。</summary>
    public int ContentWidth => _item.Size.Width;

    /// <summary>捕获项高度（内容区）。</summary>
    public int ContentHeight => _item.Size.Height;

    public bool IsAlive => !_disposed && !_closed && IsWindow(_hwnd);

    /// <summary>是否已成功关闭捕获边框（Win11 的黄框）。</summary>
    public bool BorderHidden { get; private set; }

    /// <summary>实际生效的限帧间隔（毫秒）；0 表示未限帧。</summary>
    public int AppliedMinIntervalMs { get; private set; }

    /// <summary>最近一次 ROI 采集的「GPU 裁切 + 回读」耗时（毫秒），不含取帧等待。</summary>
    public double LastCaptureCostMs { get; private set; }

    /// <summary>本机是否支持 Windows Graphics Capture。</summary>
    public static bool IsSupported()
    {
        try
        {
            return GraphicsCaptureSession.IsSupported();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>本机是否支持关闭捕获边框（Win11 及以上）。</summary>
    public static bool SupportsBorderToggle =>
        ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired");

    /// <summary>本机是否支持限帧（Win11 22H2 及以上）。</summary>
    public static bool SupportsMinUpdateInterval =>
        ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "MinUpdateInterval");

    /// <summary>创建采集源。</summary>
    /// <param name="key">会话标识。</param>
    /// <param name="hwnd">目标窗口句柄。</param>
    /// <param name="hideBorder">尝试关闭 Win11 的捕获边框（需 Borderless 授权，被拒或失败则忽略）。</param>
    /// <param name="minUpdateIntervalMs">限帧间隔（毫秒），0 表示不限帧。</param>
    public static WgcCaptureSource Create(string key, nint hwnd, bool hideBorder = true, int minUpdateIntervalMs = 0)
    {
        if (!IsSupported())
        {
            throw new NotSupportedException("本机不支持 Windows Graphics Capture。");
        }

        if (hwnd == nint.Zero || !IsWindow(hwnd))
        {
            throw new ArgumentException("窗口句柄无效或已关闭。", nameof(hwnd));
        }

        var gpu = WgcDevice.Shared;
        var item = WgcNative.CreateItemForWindow(hwnd);

        var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            gpu.WinRtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            numberOfBuffers: 2,
            item.Size);

        var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;   // 光标无意义，省一次合成

        var borderHidden = hideBorder && TryHideBorder(session);

        var source = new WgcCaptureSource(key, hwnd, gpu, item, pool, session)
        {
            BorderHidden = borderHidden,
        };

        // 限帧要在 StartCapture 之前设，和 Borderless 一样属于会话参数
        if (minUpdateIntervalMs > 0 && source.TrySetMinUpdateInterval(TimeSpan.FromMilliseconds(minUpdateIntervalMs)))
        {
            source.AppliedMinIntervalMs = minUpdateIntervalMs;
        }

        session.StartCapture();
        return source;
    }

    /// <summary>设置限帧间隔（不支持或失败返回 false）。</summary>
    public bool TrySetMinUpdateInterval(TimeSpan interval)
    {
        if (!SupportsMinUpdateInterval)
        {
            return false;
        }

        try
        {
            _session.MinUpdateInterval = interval;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- 采集

    /// <summary>
    /// 开始一次 tick：取一帧并持有，供该窗口下的各条 ROI 复用。
    /// <para>
    /// 一次 tick 只等一次帧：不这样做的话每条 ROI 都要各自等一次（每次最多 <see cref="FrameWaitMs"/> ms），
    /// 账号下 ROI 越多，单次 tick 的耗时越线性增长，且各条 ROI 判定到的可能不是同一时刻的画面。
    /// </para>
    /// <para>取不到帧时不作失败处理：后续 <see cref="CaptureRoiBgra"/> 会自行再取一次。</para>
    /// </summary>
    public void BeginTickFrame()
    {
        if (_disposed || _closed)
        {
            return;
        }

        lock (_sync)
        {
            _tickFrame ??= DequeueFrame();
        }
    }

    /// <summary>结束一次 tick：释放持有的那一帧。未持有帧时为空操作。</summary>
    public void EndTickFrame()
    {
        Direct3D11CaptureFrame? frame;
        lock (_sync)
        {
            frame = _tickFrame;
            _tickFrame = null;
        }

        frame?.Dispose();
    }

    /// <summary>
    /// 抓取 ROI，返回 BGRA 字节（行紧排，步长 = width × 4）。
    /// 返回的是**复用缓冲**，下一次采集即被覆盖；窗口不可用或 ROI 越界时返回 <c>null</c>。
    /// </summary>
    public byte[]? CaptureRoiBgra(RoiRect roi, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (_disposed || _closed || !roi.IsValid)
        {
            return null;
        }

        // 夹到内容区内，避免越界拷贝
        var x = Math.Clamp(roi.X, 0, Math.Max(0, ContentWidth - 1));
        var y = Math.Clamp(roi.Y, 0, Math.Max(0, ContentHeight - 1));
        var w = Math.Min(roi.W, ContentWidth - x);
        var h = Math.Min(roi.H, ContentHeight - y);

        if (w <= 0 || h <= 0)
        {
            return null;
        }

        lock (_sync)
        {
            var frame = AcquireFrame(out var owned);

            try
            {
                if (frame is null)
                {
                    return null;
                }

                var stopwatch = Stopwatch.StartNew();
                var bgra = ReadRegion(frame, x, y, w, h);
                stopwatch.Stop();
                LastCaptureCostMs = stopwatch.Elapsed.TotalMilliseconds;

                if (bgra.Length == 0)
                {
                    return null;
                }

                width = w;
                height = h;
                return bgra;
            }
            finally
            {
                owned?.Dispose();
            }
        }
    }

    /// <summary>抓整幅内容区（框选底图与诊断对照用），返回复用缓冲。</summary>
    public byte[]? CaptureFullBgra(out int width, out int height)
    {
        width = 0;
        height = 0;

        if (_disposed || _closed)
        {
            return null;
        }

        var w = ContentWidth;
        var h = ContentHeight;
        if (w <= 0 || h <= 0)
        {
            return null;
        }

        lock (_sync)
        {
            var frame = AcquireFrame(out var owned);

            try
            {
                if (frame is null)
                {
                    return null;
                }

                var stopwatch = Stopwatch.StartNew();
                var bgra = ReadRegion(frame, 0, 0, w, h);
                stopwatch.Stop();
                LastCaptureCostMs = stopwatch.Elapsed.TotalMilliseconds;

                if (bgra.Length == 0)
                {
                    return null;
                }

                width = w;
                height = h;
                return bgra;
            }
            finally
            {
                owned?.Dispose();
            }
        }
    }

    // ---------------------------------------------------------------- 内部

    /// <summary>
    /// 取本次要用的帧：优先用 tick 持有的那一帧（不归调用方释放），
    /// 没有则现取一帧（归调用方释放，通过 <paramref name="owned"/> 返回）。
    /// 调用方必须已持有 <see cref="_sync"/>。
    /// </summary>
    private Direct3D11CaptureFrame? AcquireFrame(out Direct3D11CaptureFrame? owned)
    {
        if (_tickFrame is not null)
        {
            owned = null;
            return _tickFrame;
        }

        owned = DequeueFrame();
        return owned;
    }

    /// <summary>
    /// 取下一帧；池里暂时没有就短等一会儿（有界）。
    /// StartCapture 后第一帧要过一到两个刷新周期才到，取走一帧后若新帧未到会返回 null。
    /// </summary>
    private Direct3D11CaptureFrame? DequeueFrame(int timeoutMs = FrameWaitMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (true)
        {
            var frame = _pool.TryGetNextFrame();
            if (frame is not null)
            {
                return frame;
            }

            if (_disposed || _closed || Environment.TickCount64 >= deadline)
            {
                return null;
            }

            Thread.Sleep(6);
        }
    }

    /// <summary>
    /// 把帧纹理的指定矩形拷到暂存纹理并回读成 BGRA（行紧排）。
    /// 返回的是**复用缓冲**，调用方须立即消费。
    /// </summary>
    private byte[] ReadRegion(Direct3D11CaptureFrame frame, int x, int y, int w, int h)
    {
        lock (_gpu.Gate)
        {
            var staging = EnsureStaging(w, h);

            var texturePtr = WgcNative.GetTexturePointer(frame.Surface);
            if (texturePtr == nint.Zero)
            {
                return [];
            }

            // 指针构造函数只是接管这一个引用（SharpGen 的 CppObject 不 AddRef），
            // 由 using 在作用域结束时 Release 一次。这里再 Marshal.Release 就是双重释放：
            // 帧池仍持有的纹理会被提前销毁，随后在 D3D11/驱动侧崩溃。
            using var source = new ID3D11Texture2D(texturePtr);
            var box = new Box(x, y, 0, x + w, y + h, 1);
            _gpu.Context.CopySubresourceRegion(staging.Texture, 0u, 0u, 0u, 0u, source, 0u, box);

            _gpu.Context.Map(staging.Texture, 0u, MapMode.Read, MapFlags.None, out var mapped);
            try
            {
                var rowBytes = w * 4;
                var rowPitch = (int)mapped.RowPitch;

                if (rowPitch == rowBytes)
                {
                    Marshal.Copy(mapped.DataPointer, staging.Buffer, 0, rowBytes * h);
                }
                else
                {
                    for (var row = 0; row < h; row++)
                    {
                        Marshal.Copy(mapped.DataPointer + (row * rowPitch), staging.Buffer, row * rowBytes, rowBytes);
                    }
                }
            }
            finally
            {
                _gpu.Context.Unmap(staging.Texture, 0u);
            }

            return staging.Buffer;
        }
    }

    /// <summary>按尺寸取暂存纹理；同一会话可能有多个 ROI 尺寸，故按尺寸缓存。</summary>
    private Staging EnsureStaging(int width, int height)
    {
        foreach (var existing in _stagings)
        {
            if (existing.Width == width && existing.Height == height)
            {
                return existing;
            }
        }

        var created = new Staging(_gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        }), width, height);

        _stagings.Add(created);

        // ROI 尺寸种类有限，留个上限防止配置被改坏后无限增长
        while (_stagings.Count > MaxStagingTextures)
        {
            _stagings[0].Dispose();
            _stagings.RemoveAt(0);
        }

        return created;
    }

    private static bool TryHideBorder(GraphicsCaptureSession session)
    {
        if (!SupportsBorderToggle)
        {
            return false;
        }

        try
        {
            var access = GraphicsCaptureAccess
                .RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)
                .AsTask()
                .GetAwaiter()
                .GetResult();

            if (access != AppCapabilityAccessStatus.Allowed)
            {
                return false;
            }

            session.IsBorderRequired = false;
            return true;
        }
        catch
        {
            // 授权被拒或系统不支持：保留边框即可，不影响采集
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _session.Dispose();
        }
        catch
        {
            // 会话可能未成功启动
        }

        _pool.Dispose();

        lock (_sync)
        {
            // tick 持有的帧随会话一起释放，避免 EndTickFrame 因会话已摘除而漏掉它
            _tickFrame?.Dispose();
            _tickFrame = null;

            foreach (var staging in _stagings)
            {
                staging.Dispose();
            }

            _stagings.Clear();
        }

        // 共享的 WgcDevice 不在此释放（进程级）
    }

    /// <summary>一块暂存纹理及其托管缓冲。</summary>
    private sealed class Staging(ID3D11Texture2D texture, int width, int height) : IDisposable
    {
        public ID3D11Texture2D Texture { get; } = texture;

        public int Width { get; } = width;

        public int Height { get; } = height;

        /// <summary>回读目标：BGRA，行紧排。</summary>
        public byte[] Buffer { get; } = new byte[width * height * 4];

        public void Dispose() => Texture.Dispose();
    }
}
