using EveWatch.Core.Config;

namespace EveWatch.Core.Cloning;

/// <summary>账号配置克隆的可选项。</summary>
public sealed class CloneOptions
{
    /// <summary>ROI 列表与区域，含每条 ROI 的名称、坐标、尺寸与启用状态。</summary>
    public bool Rois { get; set; } = true;

    /// <summary>每条 ROI 引用的颜色库项。</summary>
    public bool Colors { get; set; } = true;

    /// <summary>每条 ROI 的报警音频与音量。</summary>
    public bool Audio { get; set; } = true;

    /// <summary>账号是否参与监控。默认不克隆。</summary>
    public bool Enabled { get; set; }

    public bool Any => Rois || Colors || Audio || Enabled;
}

/// <summary>
/// 账号配置克隆：把一个账号的配置复制到其它账号。
/// <para>只作用于本程序配置文件，不接触游戏客户端。</para>
/// </summary>
public static class AccountCloner
{
    /// <summary>
    /// 执行克隆。
    /// </summary>
    /// <returns>实际被改动的目标账号数。</returns>
    public static int Apply(
        AccountSetting source,
        IReadOnlyList<AccountSetting> targets,
        CloneOptions options)
    {
        if (!options.Any)
        {
            return 0;
        }

        var changed = 0;

        foreach (var target in targets)
        {
            if (ReferenceEquals(target, source)
                || string.Equals(target.Name, source.Name, StringComparison.Ordinal))
            {
                continue;
            }

            if (options.Rois)
            {
                target.Rois = [.. source.Rois.Select(CopyRoi)];
            }
            else if (options.Colors || options.Audio)
            {
                var count = Math.Min(target.Rois.Count, source.Rois.Count);
                for (var i = 0; i < count; i++)
                {
                    if (options.Colors)
                    {
                        target.Rois[i].Colors = [.. source.Rois[i].Colors];
                    }

                    if (options.Audio)
                    {
                        target.Rois[i].Alarm = CopyAlarm(source.Rois[i].Alarm);
                        target.Rois[i].Volume = source.Rois[i].Volume;
                    }
                }
            }

            if (options.Enabled)
            {
                target.Enabled = source.Enabled;
            }

            changed++;
        }

        return changed;
    }

    /// <summary>复制一条 ROI，并分配新的 id（避免跨账号 id 冲突）。</summary>
    private static RoiSetting CopyRoi(RoiSetting source) => new()
    {
        Id = Guid.NewGuid().ToString("N")[..8],
        Label = source.Label,
        X = source.X,
        Y = source.Y,
        W = source.W,
        H = source.H,
        Colors = [.. source.Colors],
        Alarm = CopyAlarm(source.Alarm),
        Volume = source.Volume,
        Enabled = source.Enabled,
    };

    private static AlarmSetting CopyAlarm(AlarmSetting source) => new()
    {
        Kind = source.Kind,
        Id = source.Id,
        Path = source.Path,
    };
}
