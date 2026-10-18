using System.Collections.Concurrent;

namespace EveWatch.Core.Status;

/// <summary>一条 ROI 的运行时状态。</summary>
public sealed class RoiStatus
{
    public string Id { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public bool Enabled { get; init; }

    /// <summary>本帧命中块数。</summary>
    public int HitBlocks { get; set; }

    /// <summary>本帧命中描述，例如「红 ×2」。</summary>
    public string HitText { get; set; } = string.Empty;

    public long LastHitAtMs { get; set; }

    public long TotalHits { get; set; }

    public bool Hostile => HitBlocks > 0;
}

/// <summary>一个账号的运行时状态快照。</summary>
public sealed class AccountStatus
{
    public string Name { get; init; } = string.Empty;

    public nint Hwnd { get; set; }

    /// <summary>配置里是否勾选监控。</summary>
    public bool MonitorEnabled { get; set; }

    /// <summary>窗口是否处于最小化（最小化时采集取不到帧）。</summary>
    public bool Minimized { get; set; }

    /// <summary>采集会话是否已建立。</summary>
    public bool SessionReady { get; set; }

    /// <summary>累计丢帧数。</summary>
    public long DropFrames { get; set; }

    /// <summary>累计 tick 数。</summary>
    public long Ticks { get; set; }

    /// <summary>最近一次 tick 的耗时（毫秒）。</summary>
    public double LastTickCostMs { get; set; }

    /// <summary>最近一次采集的 GPU 耗时（毫秒）。</summary>
    public double LastCaptureCostMs { get; set; }

    /// <summary>实际轮询帧率。</summary>
    public double ActualFps { get; set; }

    /// <summary>当前是否有敌对命中（任一条启用 ROI 命中）。</summary>
    public bool Hostile { get; set; }

    public long LastAlarmAtMs { get; set; }

    /// <summary>逐 ROI 状态；写入时整体替换数组，读到的要么是旧数组要么是新数组。</summary>
    public RoiStatus[] Rois { get; set; } = [];
}

/// <summary>
/// 状态快照仓库。
/// <para>
/// 轮询线程写、界面线程读。字段级读写是原子的，界面看到的一帧数据可能存在极小的时序偏差，
/// 对状态展示无影响；如需严格一致，界面侧只做整体复制即可。
/// </para>
/// </summary>
public sealed class StatusStore
{
    private readonly ConcurrentDictionary<string, AccountStatus> _accounts = new(StringComparer.Ordinal);

    /// <summary>取一个账号的状态对象，不存在则创建。</summary>
    public AccountStatus GetOrAdd(string accountName) =>
        _accounts.GetOrAdd(accountName, static name => new AccountStatus { Name = name });

    /// <summary>移除账号状态（账号从配置里消失时调用）。</summary>
    public void Remove(string accountName) => _accounts.TryRemove(accountName, out _);

    /// <summary>
    /// 账号窗口消失后清空运行时状态，避免残留的敌对标记、帧率与命中角标继续显示。
    /// 累计丢帧与累计命中次数保留。
    /// </summary>
    public void ResetRuntime(string accountName)
    {
        if (!_accounts.TryGetValue(accountName, out var status))
        {
            return;
        }

        status.Hwnd = 0;
        status.MonitorEnabled = false;
        status.Minimized = false;
        status.SessionReady = false;
        status.ActualFps = 0;
        status.Hostile = false;

        foreach (var roi in status.Rois)
        {
            roi.HitBlocks = 0;
            roi.HitText = string.Empty;
        }
    }

    /// <summary>清空。</summary>
    public void Clear() => _accounts.Clear();

    /// <summary>按给定顺序返回快照；顺序由调用方决定。</summary>
    public IReadOnlyList<AccountStatus> Snapshot(IEnumerable<string> orderedNames)
    {
        var result = new List<AccountStatus>();
        foreach (var name in orderedNames)
        {
            if (_accounts.TryGetValue(name, out var status))
            {
                result.Add(status);
            }
        }

        return result;
    }

    /// <summary>全部状态（无序）。</summary>
    public IReadOnlyList<AccountStatus> All() => [.. _accounts.Values];
}
