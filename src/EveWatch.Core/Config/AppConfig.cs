using EveWatch.Core.Alarm;
using EveWatch.Core.Capture;
using EveWatch.Core.Detect;
using EveWatch.Core.Palette;

namespace EveWatch.Core.Config;

/// <summary>一条 ROI 的报警音频设置。</summary>
public sealed class AlarmSetting
{
    /// <summary>音频来源：内置预设。</summary>
    public const string KindPreset = "preset";

    /// <summary>音频来源：本机音频文件。</summary>
    public const string KindFile = "file";

    /// <summary><see cref="KindPreset"/> 或 <see cref="KindFile"/>。</summary>
    public string Kind { get; set; } = KindPreset;

    /// <summary><see cref="Kind"/> 为 <see cref="KindPreset"/> 时，内置预设的标识。</summary>
    public string Id { get; set; } = "alarm";

    /// <summary><see cref="Kind"/> 为 <see cref="KindFile"/> 时，音频文件的绝对路径。</summary>
    public string Path { get; set; } = string.Empty;

    public bool IsPreset => !string.Equals(Kind, KindFile, StringComparison.OrdinalIgnoreCase);
}

/// <summary>一条 ROI 的配置。</summary>
public sealed class RoiSetting
{
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名，由用户编辑。</summary>
    public string Label { get; set; } = string.Empty;

    public int X { get; set; }

    public int Y { get; set; }

    public int W { get; set; } = 25;

    public int H { get; set; } = 500;

    /// <summary>参与本 ROI 报警判定的颜色库项 id。</summary>
    public List<string> Colors { get; set; } = [];

    public AlarmSetting Alarm { get; set; } = new();

    /// <summary>音量，0 到 100。</summary>
    public int Volume { get; set; } = 80;

    public bool Enabled { get; set; } = true;

    public RoiRect Bounds => new(X, Y, W, H);
}

/// <summary>一个账号的配置。</summary>
public sealed class AccountSetting
{
    /// <summary>账号标识，取自窗口标题（通常为角色名）。</summary>
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public List<RoiSetting> Rois { get; set; } = [];
}

/// <summary>采集层参数。</summary>
public sealed class CaptureOptions
{
    /// <summary>
    /// WGC 帧池的最小更新间隔（毫秒），0 表示不限帧。
    /// 建议取轮询节拍的一半，既压掉用不上的帧生产，又留出一倍余量避免取帧落空。
    /// </summary>
    public int MinUpdateIntervalMs { get; set; } = 100;
}

/// <summary>功能配置，持久化为 <c>evewatch.json</c>。</summary>
public sealed class AppConfig
{
    public string ProcessName { get; set; } = "exefile";

    /// <summary>
    /// 轮询节拍（毫秒）。与去抖帧数一起决定报警延迟：默认 200 ms × 2 帧 = 0.2~0.4 秒。
    /// </summary>
    public int TickMs { get; set; } = 200;

    public bool GlobalMute { get; set; }

    /// <summary>同一账号两次报警之间的最短间隔（毫秒）。</summary>
    public int CooldownMs { get; set; } = 2000;

    /// <summary>连续多少帧命中才升级为报警。</summary>
    public int DebounceFrames { get; set; } = 2;

    public CaptureOptions Capture { get; set; } = new();

    public List<PaletteColor> Palette { get; set; } = DefaultPalette();

    public DetectOptions Detect { get; set; } = new();

    /// <summary>快捷键绑定。默认没有任何绑定，由用户在设置面板中自行指定。</summary>
    public List<HotkeyBinding> Hotkeys { get; set; } = [];

    /// <summary>日志预警配置。</summary>
    public IntelOptions Intel { get; set; } = new();

    public List<AccountSetting> Accounts { get; set; } = [];

    /// <summary>内置四项 EVE standing 色。</summary>
    public static List<PaletteColor> DefaultPalette() =>
    [
        new() { Id = "red", Name = "敌对红", Hex = "#AD0606", Builtin = true },
        new() { Id = "orange", Name = "警惕橙", Hex = "#D2731F", Builtin = true },
        new() { Id = "blue", Name = "友军蓝", Hex = "#3C78DC", Builtin = true },
        new() { Id = "grey", Name = "中立灰", Hex = "#6E6E6E", Builtin = true },
    ];

    public static AppConfig CreateDefault() => new();

    public AccountSetting? FindAccount(string name) =>
        Accounts.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));

    /// <summary>取账号配置，不存在则创建。</summary>
    public AccountSetting GetOrAddAccount(string name)
    {
        var existing = FindAccount(name);
        if (existing is not null)
        {
            return existing;
        }

        var created = new AccountSetting { Name = name };
        Accounts.Add(created);
        return created;
    }

    /// <summary>把发现到但配置里没有的账号补进列表（默认启用、无 ROI）。</summary>
    public bool SyncAccounts(IEnumerable<string> discoveredNames)
    {
        var changed = false;
        foreach (var name in discoveredNames)
        {
            if (string.IsNullOrWhiteSpace(name) || FindAccount(name) is not null)
            {
                continue;
            }

            Accounts.Add(new AccountSetting { Name = name });
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// 校正从文件读入的配置：补齐缺失对象、去重 id、清理指向已删除颜色的引用。
    /// </summary>
    public void Normalize()
    {
        ProcessName = string.IsNullOrWhiteSpace(ProcessName) ? "exefile" : ProcessName.Trim();
        TickMs = Math.Clamp(TickMs, 100, 10000);
        CooldownMs = Math.Clamp(CooldownMs, 0, 600_000);
        DebounceFrames = Math.Clamp(DebounceFrames, 1, 20);
        Capture ??= new CaptureOptions();
        Capture.MinUpdateIntervalMs = Math.Clamp(Capture.MinUpdateIntervalMs, 0, 5000);
        Detect ??= new DetectOptions();

        // 明度下限同时是「多暗还算同一个颜色」的界限。旧配置里的 0.18 会把本地列表中
        // 暗红的人字形 UI 图标（明度 0.18~0.31）判成敌对红——实测误报；这里兜底到 0.35，
        // 真实的 -10 声望图标明度在 0.44 以上，不受影响。仍允许用户往高调。
        Detect.MinValue = Math.Clamp(Detect.MinValue, 0.35, 1.0);
        Detect.MinSaturation = Math.Clamp(Detect.MinSaturation, 0.18, 1.0);

        // 旧默认值本身会造成误报：20° 色相容差会把棕色划进橙色，0.28 中性色容差会把
        // 文字/边框划进灰色。只迁移「仍是旧默认值」的配置，用户手动调过的不动。
        if (Math.Abs(Detect.HueToleranceDegrees - 20) < 0.001)
        {
            Detect.HueToleranceDegrees = 12;
        }

        if (Math.Abs(Detect.NeutralValueTolerance - 0.28) < 0.001)
        {
            Detect.NeutralValueTolerance = 0.10;
        }

        Palette ??= DefaultPalette();
        Accounts ??= [];

        // 内置色缺失就补回；重复 id 只保留第一个
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var palette = new List<PaletteColor>();
        foreach (var color in Palette)
        {
            if (string.IsNullOrWhiteSpace(color.Id) || !seen.Add(color.Id))
            {
                continue;
            }

            color.Name = string.IsNullOrWhiteSpace(color.Name) ? color.Id : color.Name;
            if (!ColorLut.TryParseHex(color.Hex, out _, out _, out _))
            {
                color.Hex = "#000000";
            }

            palette.Add(color);
        }

        foreach (var builtin in DefaultPalette())
        {
            if (seen.Add(builtin.Id))
            {
                palette.Insert(0, builtin);
            }
        }

        Palette = palette;

        Hotkeys ??= [];

        // 未知功能、重复功能、重复组合一律丢弃：注册阶段无法处理这些冲突
        var seenActions = new HashSet<string>(StringComparer.Ordinal);
        var seenCombos = new HashSet<(int Modifiers, int VirtualKey)>();
        var hotkeys = new List<HotkeyBinding>();
        foreach (var hotkey in Hotkeys)
        {
            if (hotkey is null || HotkeyActions.Find(hotkey.Action) is null)
            {
                continue;
            }

            hotkey.Modifiers &= HotkeyModifiers.All;
            if (hotkey.VirtualKey <= 0
                || !seenCombos.Add((hotkey.Modifiers, hotkey.VirtualKey))
                || !seenActions.Add(hotkey.Action))
            {
                continue;
            }

            hotkeys.Add(hotkey);
        }

        Hotkeys = hotkeys;

        Intel ??= new IntelOptions();
        Intel.ChatLogDirectory = string.IsNullOrWhiteSpace(Intel.ChatLogDirectory)
            ? string.Empty
            : Intel.ChatLogDirectory.Trim();
        Intel.CooldownMs = Math.Clamp(Intel.CooldownMs, 0, 600_000);
        Intel.BatchWindowMs = Math.Clamp(Intel.BatchWindowMs, 500, 5_000);

        Intel.Listeners ??= [];
        var listenerKeys = new HashSet<string>(StringComparer.Ordinal);
        var listeners = new List<IntelListener>();
        foreach (var listener in Intel.Listeners)
        {
            if (listener is null)
            {
                continue;
            }

            listener.CharacterName = listener.CharacterName?.Trim() ?? string.Empty;
            listener.ChannelId = listener.ChannelId?.Trim() ?? string.Empty;
            listener.ChannelName = listener.ChannelName?.Trim() ?? string.Empty;

            if (listener.CharacterId <= 0
                || (string.IsNullOrWhiteSpace(listener.ChannelId) && string.IsNullOrWhiteSpace(listener.ChannelName)))
            {
                continue;
            }

            var channelKey = string.IsNullOrWhiteSpace(listener.ChannelId)
                ? listener.ChannelName.ToLowerInvariant()
                : listener.ChannelId;
            if (!listenerKeys.Add($"{listener.CharacterId}\u001f{channelKey}"))
            {
                continue;
            }

            listeners.Add(listener);
        }

        Intel.Listeners = listeners;

        Intel.Rules ??= [];
        var rules = new List<IntelRule>();
        foreach (var rule in Intel.Rules)
        {
            if (rule is null)
            {
                continue;
            }

            rule.MinJumps = Math.Clamp(rule.MinJumps, 0, 100);
            rule.MaxJumps = Math.Clamp(rule.MaxJumps, 0, 100);
            if (rule.MaxJumps < rule.MinJumps)
            {
                continue;
            }

            rule.Volume = Math.Clamp(rule.Volume, 0, 100);
            rule.Alarm ??= new AlarmSetting();

            if (rule.Alarm.IsPreset)
            {
                if (AudioPresets.Find(rule.Alarm.Id) is null)
                {
                    rule.Alarm.Id = AudioPresets.DefaultId;
                }
            }
            else if (string.IsNullOrWhiteSpace(rule.Alarm.Path))
            {
                rule.Alarm.Kind = AlarmSetting.KindPreset;
                rule.Alarm.Id = AudioPresets.DefaultId;
            }

            rules.Add(rule);
        }

        Intel.Rules = rules;

        var known = new HashSet<string>(Palette.Select(p => p.Id), StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var accounts = new List<AccountSetting>();

        foreach (var account in Accounts)
        {
            if (string.IsNullOrWhiteSpace(account.Name) || !names.Add(account.Name))
            {
                continue;
            }

            account.Rois ??= [];
            foreach (var roi in account.Rois)
            {
                if (string.IsNullOrWhiteSpace(roi.Id))
                {
                    roi.Id = Guid.NewGuid().ToString("N")[..8];
                }

                roi.Label = string.IsNullOrWhiteSpace(roi.Label) ? "ROI" : roi.Label.Trim();
                roi.Colors ??= [];
                roi.Colors.RemoveAll(c => !known.Contains(c));
                roi.Alarm ??= new AlarmSetting();
                roi.Volume = Math.Clamp(roi.Volume, 0, 100);
                roi.W = Math.Max(0, roi.W);
                roi.H = Math.Max(0, roi.H);
            }

            accounts.Add(account);
        }

        Accounts = accounts;
    }
}
