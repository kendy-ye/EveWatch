using System.Collections.ObjectModel;
using EveWatch.Core.Alarm;
using EveWatch.Core.Config;
using EveWatch.Core.Palette;
using EveWatch.Core.Status;

namespace EveWatch.App.ViewModels;

/// <summary>一个颜色色块（颜色库某项在某条 ROI 上是否启用）。</summary>
public sealed class ColorChipViewModel : ObservableObject
{
    private readonly RoiSetting _roi;
    private readonly Action _onChanged;

    public ColorChipViewModel(PaletteColor color, RoiSetting roi, Action onChanged)
    {
        Color = color;
        _roi = roi;
        _onChanged = onChanged;
    }

    public PaletteColor Color { get; }

    public string Name => Color.Name;

    public string Hex => Color.Hex;

    public bool IsSelected
    {
        get => _roi.Colors.Contains(Color.Id);
        set
        {
            var has = _roi.Colors.Contains(Color.Id);
            if (has == value)
            {
                return;
            }

            if (value)
            {
                _roi.Colors.Add(Color.Id);
            }
            else
            {
                _roi.Colors.Remove(Color.Id);
            }

            Raise();
            _onChanged();
        }
    }
}

/// <summary>下拉框里的一个音频候选项。</summary>
public sealed record AudioChoice(string Display, string Kind, string Id, string Path)
{
    public bool IsFile => Kind == AlarmSetting.KindFile;

    public bool IsCustomPlaceholder { get; init; }

    public static AudioChoice FromAlarm(AlarmSetting alarm)
    {
        if (!alarm.IsPreset)
        {
            var name = string.IsNullOrWhiteSpace(alarm.Path)
                ? "（未选择文件）"
                : System.IO.Path.GetFileName(alarm.Path);

            return new AudioChoice(name, AlarmSetting.KindFile, string.Empty, alarm.Path);
        }

        var preset = AudioPresets.Find(alarm.Id) ?? AudioPresets.Default;
        return new AudioChoice(preset.Name, AlarmSetting.KindPreset, preset.Id, string.Empty);
    }
}

/// <summary>一条 ROI 的视图模型。</summary>
public sealed class RoiViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private readonly Func<RoiSetting, string?> _pickAudioFile;

    private int _hitBlocks;
    private string _hitText = string.Empty;
    private long _totalHits;

    public RoiViewModel(RoiSetting setting, IReadOnlyList<PaletteColor> palette, Action onChanged, Func<RoiSetting, string?> pickAudioFile)
    {
        Setting = setting;
        _onChanged = onChanged;
        _pickAudioFile = pickAudioFile;

        AudioChoices = BuildAudioChoices();
        Colors = [.. palette.Select(p => new ColorChipViewModel(p, setting, onChanged))];
    }

    public RoiSetting Setting { get; }

    public string Id => Setting.Id;

    public ObservableCollection<ColorChipViewModel> Colors { get; }

    public IReadOnlyList<AudioChoice> AudioChoices { get; private set; }

    public string Label
    {
        get => Setting.Label;
        set
        {
            var text = value ?? string.Empty;
            if (Setting.Label == text)
            {
                return;
            }

            Setting.Label = text;
            Raise();
            _onChanged();
        }
    }

    public string BoundsText => $"x {Setting.X} · y {Setting.Y} · w {Setting.W} · h {Setting.H}";

    /// <summary>坐标被外部改动后刷新显示。</summary>
    public void RefreshBounds() => Raise(nameof(BoundsText));

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
            Raise();
            _onChanged();
        }
    }

    public double Volume
    {
        get => Setting.Volume;
        set
        {
            var rounded = (int)Math.Round(value);
            if (Setting.Volume == rounded)
            {
                return;
            }

            Setting.Volume = rounded;
            Raise();
            Raise(nameof(VolumeText));
            _onChanged();
        }
    }

    public string VolumeText => Setting.Volume.ToString();

    public AudioChoice? SelectedAudio
    {
        get => AudioChoices.FirstOrDefault(c => Matches(Setting.Alarm, c));
        set
        {
            if (value is null)
            {
                return;
            }

            // 选择「自定义音频文件…」：弹文件对话框，取消则不改动
            if (value.IsCustomPlaceholder)
            {
                var path = _pickAudioFile(Setting);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    ApplyAlarm(AlarmSetting.KindFile, string.Empty, path);
                }

                return;
            }

            // 这道判断是必须的，不能省。
            // ComboBox 的 SelectedItem 是双向绑定：下面 ApplyAlarm 会通知 SelectedAudio，
            // 控件收到通知后会把同一项再写回这个 setter。若此处直接放行，
            // 通知 → 写回 → 再通知就会无限递归，最终栈溢出。
            // 栈溢出无法被任何托管处理器捕获，进程会直接消失且不留下日志。
            if (Matches(Setting.Alarm, value))
            {
                return;
            }

            ApplyAlarm(value.Kind, value.Id, value.Path);
        }
    }

    /// <summary>写入音频配置并通知界面。</summary>
    private void ApplyAlarm(string kind, string id, string path)
    {
        Setting.Alarm.Kind = kind;
        Setting.Alarm.Id = id;
        Setting.Alarm.Path = path;

        var rebuilt = RebuildAudioChoicesIfNeeded();

        RaiseMany(nameof(SelectedAudio), nameof(AudioText));

        if (rebuilt)
        {
            Raise(nameof(AudioChoices));
        }

        _onChanged();
    }

    /// <summary>
    /// 只在候选项构成真正变化时替换集合（即自定义文件项的出现与消失）。
    /// 每次选择都换一个新集合会让下拉框反复重置，是上面那类递归的温床。
    /// </summary>
    private bool RebuildAudioChoicesIfNeeded()
    {
        var next = BuildAudioChoices();

        if (next.Count == AudioChoices.Count)
        {
            var same = true;
            for (var i = 0; i < next.Count; i++)
            {
                if (!string.Equals(next[i].Display, AudioChoices[i].Display, StringComparison.Ordinal))
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                return false;
            }
        }

        AudioChoices = next;
        return true;
    }

    public string AudioText => AudioChoice.FromAlarm(Setting.Alarm).Display;

    public int HitBlocks => _hitBlocks;

    public bool Hit => _hitBlocks > 0;

    public string HitText => _hitText;

    public long TotalHits => _totalHits;

    public string LastHitText => _totalHits == 0 ? "—" : $"累计 {_totalHits} 次";

    /// <summary>用运行时状态刷新命中相关字段。</summary>
    public void RefreshFrom(RoiStatus? status)
    {
        var blocks = status?.HitBlocks ?? 0;
        var text = status?.HitText ?? string.Empty;
        var total = status?.TotalHits ?? 0;

        if (_hitBlocks == blocks && _hitText == text && _totalHits == total)
        {
            return;
        }

        _hitBlocks = blocks;
        _hitText = text;
        _totalHits = total;
        RaiseMany(nameof(HitBlocks), nameof(Hit), nameof(HitText), nameof(TotalHits), nameof(LastHitText));
    }

    /// <summary>颜色库发生变化后重建色块。</summary>
    public void RebuildColors(IReadOnlyList<PaletteColor> palette)
    {
        Colors.Clear();
        foreach (var color in palette)
        {
            Colors.Add(new ColorChipViewModel(color, Setting, _onChanged));
        }

        RaiseMany(nameof(Colors));
    }

    private IReadOnlyList<AudioChoice> BuildAudioChoices()
    {
        var list = new List<AudioChoice>();

        foreach (var preset in AudioPresets.All)
        {
            list.Add(new AudioChoice(preset.Name, AlarmSetting.KindPreset, preset.Id, string.Empty));
        }

        if (!Setting.Alarm.IsPreset && !string.IsNullOrWhiteSpace(Setting.Alarm.Path))
        {
            list.Add(AudioChoice.FromAlarm(Setting.Alarm));
        }

        list.Add(new AudioChoice("自定义音频文件…", AlarmSetting.KindFile, string.Empty, string.Empty)
        {
            IsCustomPlaceholder = true,
        });

        return list;
    }

    private static bool Matches(AlarmSetting alarm, AudioChoice choice)
    {
        if (choice.IsCustomPlaceholder)
        {
            return false;
        }

        if (choice.IsFile != !alarm.IsPreset)
        {
            return false;
        }

        return choice.IsFile
            ? string.Equals(choice.Path, alarm.Path, StringComparison.OrdinalIgnoreCase)
            : string.Equals(choice.Id, alarm.Id, StringComparison.OrdinalIgnoreCase);
    }
}
