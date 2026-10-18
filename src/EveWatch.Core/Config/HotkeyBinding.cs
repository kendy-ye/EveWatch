namespace EveWatch.Core.Config;

/// <summary>热键修饰键标志，数值与 Win32 RegisterHotKey 的 fsModifiers 一致。</summary>
public static class HotkeyModifiers
{
    public const int Alt = 0x1;

    public const int Control = 0x2;

    public const int Shift = 0x4;

    public const int Win = 0x8;

    public const int All = Alt | Control | Shift | Win;
}

/// <summary>一个可绑定快捷键的功能。</summary>
public sealed record HotkeyActionInfo(string Id, string Name, string Description);

/// <summary>可绑定快捷键的功能清单。默认全部未绑定。</summary>
public static class HotkeyActions
{
    public const string ToggleMonitor = "toggle-monitor";

    public const string ToggleMute = "toggle-mute";

    public const string ToggleMainWindow = "toggle-main-window";

    public const string ToggleWall = "toggle-wall";

    public const string ExportShot = "export-shot";

    public const string NextAccount = "next-account";

    public const string PreviousAccount = "previous-account";

    public const string OpenSettings = "open-settings";

    /// <summary>按设置面板中的展示顺序排列。</summary>
    public static IReadOnlyList<HotkeyActionInfo> All { get; } =
    [
        new(ToggleMonitor, "开始 / 停止监控", "全局生效，与工具栏「开始监控」按钮一致"),
        new(ToggleMute, "全局静音", "切换后立即停止正在播放的报警音频"),
        new(ToggleMainWindow, "显示 / 隐藏主窗口", "主窗口在前台时隐藏，否则显示并置前"),
        new(ToggleWall, "打开 / 关闭 ROI 并排窗", "与工具栏「开启 ROI 并排」按钮一致"),
        new(ExportShot, "导出当前账号整窗截图", "保存到程序目录的 shots 文件夹，不弹确认框"),
        new(NextAccount, "切换到下一个账号客户端", "循环选中并切到游戏窗口"),
        new(PreviousAccount, "切换到上一个账号客户端", "循环选中并切到游戏窗口"),
        new(OpenSettings, "打开设置面板", "打开本面板"),
    ];

    private static readonly Dictionary<string, HotkeyActionInfo> Index =
        All.ToDictionary(action => action.Id, StringComparer.Ordinal);

    public static HotkeyActionInfo? Find(string? id) =>
        id is not null && Index.TryGetValue(id, out var action) ? action : null;
}

/// <summary>
/// 一条快捷键绑定。默认没有绑定，用户设置后随 <c>evewatch.json</c> 持久化。
/// </summary>
public sealed class HotkeyBinding
{
    /// <summary>被绑定的功能 id，见 <see cref="HotkeyActions"/>。</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>修饰键组合，见 <see cref="HotkeyModifiers"/>；0 表示单键。</summary>
    public int Modifiers { get; set; }

    /// <summary>Win32 虚拟键码。</summary>
    public int VirtualKey { get; set; }
}
