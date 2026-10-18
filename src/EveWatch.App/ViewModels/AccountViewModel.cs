using System.Collections.ObjectModel;
using EveWatch.Core.Config;
using EveWatch.Core.Events;
using EveWatch.Core.Palette;
using EveWatch.Core.Status;

namespace EveWatch.App.ViewModels;

/// <summary>事件列表的一行。</summary>
public sealed class EventItemViewModel
{
    public EventItemViewModel(AlarmEvent source)
    {
        Time = source.TimeText;
        Account = source.Account;
        Text = source.Text;
        Note = source.Note;
        Kind = source.Kind;

        Foreground = source.Kind switch
        {
            AlarmEventKind.Error => "#FFFF8F8F",
            AlarmEventKind.Warn => "#FFE0A45C",
            AlarmEventKind.Alarm => "#FFFFB4B4",
            AlarmEventKind.Hit => "#FFFFB4B4",
            _ => "#FF8A98A6",
        };

        Accent = source.Kind switch
        {
            AlarmEventKind.Alarm or AlarmEventKind.Hit => "#FFFF4D4D",
            AlarmEventKind.Warn => "#FFC98A2E",
            _ => "#FF212B34",
        };

        Background = source.Kind is AlarmEventKind.Alarm or AlarmEventKind.Hit ? "#22FF4D4D" : "#00000000";
    }

    public string Time { get; }

    public string Account { get; }

    public string Text { get; }

    public string Note { get; }

    public AlarmEventKind Kind { get; }

    public string Foreground { get; }

    public string Accent { get; }

    public string Background { get; }
}

/// <summary>账号在当前会话中的在线状态。</summary>
public enum AccountPresence
{
    /// <summary>没有客户端窗口：未登录或已彻底离线。</summary>
    Offline,

    /// <summary>掉线未超过宽限期，列表暂时保留。</summary>
    Reconnecting,

    /// <summary>能找到已进入游戏的客户端窗口。</summary>
    Online,
}

/// <summary>一个账号的视图模型。</summary>
public sealed class AccountViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private readonly Func<RoiSetting, string?> _pickAudioFile;

    private AccountPresence _presence = AccountPresence.Offline;
    private bool _hostile;
    private bool _minimized;
    private bool _sessionReady;
    private bool _monitoring;
    private double _fps;
    private long _dropFrames;
    private int _enabledRoiCount;

    public AccountViewModel(
        AccountSetting setting,
        IReadOnlyList<PaletteColor> palette,
        Action onChanged,
        Func<RoiSetting, string?> pickAudioFile)
    {
        Setting = setting;
        _onChanged = onChanged;
        _pickAudioFile = pickAudioFile;

        RebuildRois(palette);
    }

    public AccountSetting Setting { get; }

    public string Name => Setting.Name;

    /// <summary>目标窗口句柄；未发现窗口时为 0。</summary>
    public nint Hwnd { get; set; }

    public ObservableCollection<RoiViewModel> Rois { get; } = [];

    public bool HasRois => Rois.Count > 0;

    // ---------------------------------------------------------------- 在线状态

    public AccountPresence Presence => _presence;

    public bool IsOnline => _presence == AccountPresence.Online;

    public bool IsReconnecting => _presence == AccountPresence.Reconnecting;

    /// <summary>是否进入账号列表。离线账号彻底隐藏，重连中保留。</summary>
    public bool IsVisible => _presence != AccountPresence.Offline;

    /// <summary>更新在线状态；返回是否发生了变化。</summary>
    public bool SetPresence(AccountPresence presence)
    {
        if (_presence == presence)
        {
            return false;
        }

        _presence = presence;
        RaiseMany(
            nameof(Presence), nameof(IsOnline), nameof(IsReconnecting), nameof(IsVisible),
            nameof(MetaText), nameof(BadgeText), nameof(HasBadge));

        return true;
    }

    public bool Enabled
    {
        get => Setting.Enabled;
        set
        {
            if (Setting.Enabled == value)
            {
                return;
            }

            Setting.Enabled = value;
            RaiseMany(nameof(Enabled), nameof(MetaText), nameof(BadgeText), nameof(HasBadge));
            _onChanged();
        }
    }

    public bool Hostile
    {
        get => _hostile;
        set
        {
            if (Set(ref _hostile, value))
            {
                RaiseMany(nameof(BadgeText), nameof(HasBadge));
            }
        }
    }

    public bool Minimized => _minimized;

    public bool SessionReady => _sessionReady;

    /// <summary>累计丢帧数（取不到帧的 tick 数）。</summary>
    public long DropFrames => _dropFrames;

    /// <summary>列表第二行的摘要文本。</summary>
    public string MetaText
    {
        get
        {
            if (_presence == AccountPresence.Reconnecting)
            {
                return "重连中 · 等待客户端窗口";
            }

            if (!Enabled)
            {
                return $"{Setting.Name} · 未启用";
            }

            var parts = new List<string>
            {
                $"ROI {_enabledRoiCount}/{Rois.Count}",
            };

            if (!_monitoring)
            {
                parts.Add("监控已停止");
            }
            else if (_minimized)
            {
                parts.Add("窗口已最小化");
            }
            else if (_sessionReady)
            {
                parts.Add($"{_fps:F1} fps");
            }
            else
            {
                parts.Add("等待窗口");
            }

            if (_dropFrames > 0)
            {
                parts.Add($"丢帧 {_dropFrames}");
            }

            return string.Join("  ·  ", parts);
        }
    }

    public string BadgeText
    {
        get
        {
            if (_presence == AccountPresence.Reconnecting)
            {
                return "重连中";
            }

            if (_hostile)
            {
                return "敌对";
            }

            if (!Enabled)
            {
                return "停用";
            }

            if (_minimized)
            {
                return "已最小化";
            }

            return string.Empty;
        }
    }

    public bool HasBadge => !string.IsNullOrEmpty(BadgeText);

    /// <summary>把运行时状态刷进视图模型。</summary>
    public void RefreshFrom(AccountStatus? status, bool monitoring)
    {
        _monitoring = monitoring;

        // 掉线期间不接受旧状态：停止采集后这些值不再更新，留着会显示过期的敌对/帧率
        if (_presence != AccountPresence.Online)
        {
            Hwnd = 0;
            _minimized = false;
            _sessionReady = false;
            _fps = 0;
            Hostile = false;

            foreach (var roi in Rois)
            {
                roi.RefreshFrom(null);
            }

            RaiseMany(nameof(MetaText), nameof(Minimized), nameof(SessionReady), nameof(BadgeText), nameof(HasBadge));
            return;
        }

        Hwnd = status?.Hwnd ?? 0;
        _minimized = status?.Minimized ?? false;
        _sessionReady = status?.SessionReady ?? false;
        _fps = status?.ActualFps ?? 0;
        _dropFrames = status?.DropFrames ?? 0;
        Hostile = status?.Hostile ?? false;

        var enabled = 0;
        foreach (var roi in Rois)
        {
            if (roi.Enabled)
            {
                enabled++;
            }
        }

        var roiChanged = _enabledRoiCount != enabled;
        _enabledRoiCount = enabled;

        if (roiChanged || Rois.Count != (status?.Rois.Length ?? 0))
        {
            Raise(nameof(MetaText));
        }

        if (status is not null)
        {
            for (var i = 0; i < Rois.Count && i < status.Rois.Length; i++)
            {
                Rois[i].RefreshFrom(status.Rois[i]);
            }
        }
        else
        {
            foreach (var roi in Rois)
            {
                roi.RefreshFrom(null);
            }
        }

        RaiseMany(nameof(MetaText), nameof(Minimized), nameof(SessionReady), nameof(BadgeText), nameof(HasBadge));
    }

    /// <summary>按配置重建 ROI 列表。</summary>
    public void RebuildRois(IReadOnlyList<PaletteColor> palette)
    {
        Rois.Clear();
        foreach (var roi in Setting.Rois)
        {
            Rois.Add(new RoiViewModel(roi, palette, _onChanged, _pickAudioFile));
        }

        _enabledRoiCount = Rois.Count(r => r.Enabled);
        RaiseMany(nameof(Rois), nameof(HasRois), nameof(MetaText));
    }

    /// <summary>颜色库变化后重建色块。</summary>
    public void RebuildColors(IReadOnlyList<PaletteColor> palette)
    {
        foreach (var roi in Rois)
        {
            roi.RebuildColors(palette);
        }
    }

    /// <summary>新增一条 ROI 并返回它。</summary>
    public RoiViewModel AddRoi(IReadOnlyList<PaletteColor> palette)
    {
        var setting = new RoiSetting
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Label = $"新 ROI {Setting.Rois.Count + 1}",
            X = 0,
            Y = 0,
            W = 25,
            H = 500,
            Colors = palette.Count > 0 ? [palette[0].Id] : [],
            Alarm = new AlarmSetting(),
            Volume = 80,
            Enabled = true,
        };

        Setting.Rois.Add(setting);

        var viewModel = new RoiViewModel(setting, palette, _onChanged, _pickAudioFile);
        Rois.Add(viewModel);
        _enabledRoiCount++;

        RaiseMany(nameof(Rois), nameof(HasRois), nameof(MetaText));
        _onChanged();
        return viewModel;
    }

    /// <summary>删除一条 ROI。</summary>
    public void RemoveRoi(RoiViewModel roi)
    {
        Setting.Rois.Remove(roi.Setting);
        Rois.Remove(roi);

        if (roi.Enabled)
        {
            _enabledRoiCount--;
        }

        RaiseMany(nameof(Rois), nameof(HasRois), nameof(MetaText));
        _onChanged();
    }

    /// <summary>替换为从其它账号克隆来的 ROI 列表。</summary>
    public void ReloadFromSetting(IReadOnlyList<PaletteColor> palette)
    {
        RebuildRois(palette);
        Raise(nameof(Enabled));
    }
}
