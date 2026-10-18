using System.Collections.Concurrent;

namespace EveWatch.Core.Status;

/// <summary>一帧 ROI 画面的副本。</summary>
public sealed class RoiFrameSnapshot
{
    public byte[] Bgra { get; init; } = [];

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>BGRA 步长。</summary>
    public int Stride => Width * 4;

    public long TimestampMs { get; set; }
}

/// <summary>
/// ROI 画面快照仓库，供并排窗显示真实画面。
/// <para>
/// 采集服务返回的帧缓冲是复用的，出了下一次采集就失效，所以这里必须拷一份。
/// 拷贝有成本（一条 30×500 的 ROI 约 60 KB），因此默认不发布，
/// 只在并排窗打开期间由界面打开 <see cref="Enabled"/>，窗口关闭后立即停止并清空。
/// </para>
/// <para>
/// 同一条 ROI 尺寸不变时复用同一块缓冲，避免每帧产生垃圾。
/// 代价是界面可能读到正在被写入的半个新帧，表现为偶发的一帧画面撕裂；
/// 画面仅供观察，判定路径不读这里，因此可以接受。
/// </para>
/// </summary>
public sealed class FrameStore
{
    private readonly ConcurrentDictionary<string, RoiFrameSnapshot> _frames = new(StringComparer.Ordinal);
    private volatile bool _enabled;

    /// <summary>是否发布画面。</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;

            if (!value)
            {
                _frames.Clear();
            }
        }
    }

    /// <summary>发布一帧。缓冲内容会被复制，调用方可以立刻复用原缓冲。</summary>
    public void Publish(string accountName, string roiId, byte[] bgra, int width, int height, long timestampMs)
    {
        var length = width * height * 4;
        if (length <= 0 || bgra.Length < length)
        {
            return;
        }

        var key = Key(accountName, roiId);

        if (_frames.TryGetValue(key, out var existing)
            && existing.Width == width
            && existing.Height == height
            && existing.Bgra.Length == length)
        {
            Buffer.BlockCopy(bgra, 0, existing.Bgra, 0, length);
            existing.TimestampMs = timestampMs;
            return;
        }

        var copy = new byte[length];
        Buffer.BlockCopy(bgra, 0, copy, 0, length);

        _frames[key] = new RoiFrameSnapshot
        {
            Bgra = copy,
            Width = width,
            Height = height,
            TimestampMs = timestampMs,
        };
    }

    public RoiFrameSnapshot? TryGet(string accountName, string roiId) =>
        _frames.TryGetValue(Key(accountName, roiId), out var snapshot) ? snapshot : null;

    /// <summary>账号被删除时清掉它的画面。</summary>
    public void RemoveAccount(string accountName)
    {
        var prefix = accountName + "\u001f";

        foreach (var key in _frames.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                _frames.TryRemove(key, out _);
            }
        }
    }

    public void Clear() => _frames.Clear();

    // 账号名与 ROI Id 都不含 US 分隔符，用它拼接不会产生歧义
    private static string Key(string accountName, string roiId) => accountName + "\u001f" + roiId;
}
