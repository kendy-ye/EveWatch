using System.Text.Encodings.Web;
using System.Text.Json;

namespace EveWatch.Core.Config;

/// <summary>浮动窗口在屏幕上的位置。</summary>
public sealed class WindowPlacement
{
    public double Left { get; set; }

    public double Top { get; set; }
}

/// <summary>日志预警置顶弹窗的界面状态。</summary>
public sealed class IntelAlertWindowState
{
    /// <summary><c>anchor</c> 或 <c>custom</c>。</summary>
    public string PositionMode { get; set; } = "anchor";

    /// <summary>锚点：top-left / top-right / bottom-left / bottom-right / center / wall-side。</summary>
    public string Anchor { get; set; } = "top-right";

    /// <summary>显示器设备名；空表示主屏。</summary>
    public string Monitor { get; set; } = string.Empty;

    /// <summary>自定义位置；仅 <see cref="PositionMode"/> 为 custom 时使用。</summary>
    public WindowPlacement? Placement { get; set; }

    public string Size { get; set; } = "standard";

    public double Opacity { get; set; } = 1.0;

    public int AutoCloseSeconds { get; set; } = 10;

    public bool Topmost { get; set; } = true;

    public string ViewMode { get; set; } = "route";

    public bool ShowRegion { get; set; } = true;

    public bool ShowSecurity { get; set; } = true;

    public bool ShowChannel { get; set; } = true;

    public bool ShowCharacter { get; set; } = true;

    public bool ShowSound { get; set; } = true;

    public void Normalize()
    {
        PositionMode = string.Equals(PositionMode, "custom", StringComparison.OrdinalIgnoreCase)
            ? "custom"
            : "anchor";

        Anchor = Anchor?.ToLowerInvariant() switch
        {
            "top-left" or "bottom-left" or "bottom-right" or "center" or "wall-side" => Anchor.ToLowerInvariant(),
            _ => "top-right",
        };

        Size = Size?.ToLowerInvariant() switch
        {
            "compact" or "large" => Size.ToLowerInvariant(),
            _ => "standard",
        };

        ViewMode = string.Equals(ViewMode, "neighborhood", StringComparison.OrdinalIgnoreCase)
            ? "neighborhood"
            : "route";

        Opacity = Math.Clamp(Opacity, 0.65, 1.0);
        AutoCloseSeconds = AutoCloseSeconds is 0 or 5 or 10 or 30 ? AutoCloseSeconds : 10;
        Monitor = Monitor?.Trim() ?? string.Empty;

        if (PositionMode == "custom" && Placement is null)
        {
            PositionMode = "anchor";
        }
    }
}

/// <summary>单个角色的预览窗状态。</summary>
public sealed class ClientPreviewAccountState
{
    /// <summary>是否显示该角色的预览。</summary>
    public bool Visible { get; set; } = true;

    /// <summary>预览窗左上角 X（WPF 单位）；<c>null</c> 表示尚未定位。</summary>
    public double? Left { get; set; }

    /// <summary>预览窗左上角 Y（WPF 单位）；<c>null</c> 表示尚未定位。</summary>
    public double? Top { get; set; }

    public double Width { get; set; } = 260;

    public double Height { get; set; } = 170;

    /// <summary>该角色的透明度；<c>null</c> 表示跟随全局。</summary>
    public double? Opacity { get; set; }

    public void Normalize(ClientPreviewState owner)
    {
        Width = Math.Clamp(
            Width <= 0 ? owner.DefaultWidth : Width,
            ClientPreviewState.MinWidth,
            ClientPreviewState.MaxWidth);

        Height = Math.Clamp(
            Height <= 0 ? owner.DefaultHeight : Height,
            ClientPreviewState.MinHeight,
            ClientPreviewState.MaxHeight);

        if (Opacity is { } opacity)
        {
            Opacity = Math.Clamp(opacity, 0.3, 1.0);
        }
    }
}

/// <summary>客户端预览的全局设置与逐角色布局。</summary>
public sealed class ClientPreviewState
{
    public const double MinWidth = 140;
    public const double MaxWidth = 800;
    public const double MinHeight = 100;
    public const double MaxHeight = 600;

    /// <summary>预览窗是否置顶。</summary>
    public bool Topmost { get; set; } = true;

    /// <summary>是否在画面下方显示角色名。</summary>
    public bool ShowLabel { get; set; } = true;

    /// <summary>客户端最小化时是否隐藏其预览（DWM 缩略图对最小化窗口可能停更）。</summary>
    public bool HideWhenMinimized { get; set; } = true;

    /// <summary>新角色首次出现时使用的宽度。</summary>
    public double DefaultWidth { get; set; } = 260;

    /// <summary>新角色首次出现时使用的高度。</summary>
    public double DefaultHeight { get; set; } = 170;

    /// <summary>全局透明度，0.3–1.0；逐角色可覆盖。</summary>
    public double Opacity { get; set; } = 0.95;

    /// <summary>当前焦点客户端的边框颜色。</summary>
    public string ActiveBorderColor { get; set; } = "#3DDC84";

    /// <summary>角色名的统一颜色。</summary>
    public string LabelColor { get; set; } = "#E3EBF2";

    /// <summary>边框粗细，0–6 px。</summary>
    public double ActiveBorderThickness { get; set; } = 2;

    /// <summary>逐角色的显示开关、位置与尺寸。</summary>
    public Dictionary<string, ClientPreviewAccountState> Accounts { get; set; } = [];

    public ClientPreviewAccountState For(string accountName)
    {
        if (!Accounts.TryGetValue(accountName, out var state))
        {
            state = new ClientPreviewAccountState
            {
                Width = DefaultWidth,
                Height = DefaultHeight,
            };

            Accounts[accountName] = state;
        }

        return state;
    }

    public void Normalize()
    {
        DefaultWidth = Math.Clamp(DefaultWidth, MinWidth, MaxWidth);
        DefaultHeight = Math.Clamp(DefaultHeight, MinHeight, MaxHeight);
        Opacity = Math.Clamp(Opacity, 0.3, 1.0);
        ActiveBorderThickness = Math.Clamp(ActiveBorderThickness, 0, 6);

        if (!ColorHex.IsValid(ActiveBorderColor))
        {
            ActiveBorderColor = "#3DDC84";
        }

        if (!ColorHex.IsValid(LabelColor))
        {
            LabelColor = "#E3EBF2";
        }

        Accounts ??= [];

        foreach (var account in Accounts.Values)
        {
            account.Normalize(this);
        }
    }

    private static class ColorHex
    {
        public static bool IsValid(string? hex) =>
            hex is { Length: 7 or 9 }
            && hex[0] == '#'
            && hex[1..].All(Uri.IsHexDigit);
    }
}

/// <summary>界面状态，持久化为 <c>evewatch.ui.json</c>。与功能配置分开，避免手改配置时误伤界面状态。</summary>
public sealed class UiState
{
    /// <summary>账号列表的显示顺序（按账号名）。不在列表中的账号排在后面。</summary>
    public List<string> AccountOrder { get; set; } = [];

    /// <summary>ROI 并排置顶窗的位置；空表示尚未定位。</summary>
    public WindowPlacement? Wall { get; set; }

    /// <summary>ROI 并排窗的画面缩放。1.0 为原始像素。</summary>
    public double WallScale { get; set; } = 0.4;

    /// <summary>游戏布局同步窗口的位置。</summary>
    public WindowPlacement? SyncWindow { get; set; }

    /// <summary>事件日志保留天数。</summary>
    public int LogRetentionDays { get; set; } = 14;

    /// <summary>日志预警置顶弹窗的位置与显示设置。</summary>
    public IntelAlertWindowState IntelAlertWindow { get; set; } = new();

    /// <summary>客户端预览的开关、边框与逐角色布局。</summary>
    public ClientPreviewState ClientPreview { get; set; } = new();

    /// <summary>按 <see cref="AccountOrder"/> 重排账号列表；未列出的保持原有相对次序排在末尾。</summary>
    public List<T> ApplyOrder<T>(IReadOnlyList<T> items, Func<T, string> nameOf)
    {
        if (AccountOrder.Count == 0)
        {
            return [.. items];
        }

        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < AccountOrder.Count; i++)
        {
            rank.TryAdd(AccountOrder[i], i);
        }

        return [.. items.OrderBy(t => rank.TryGetValue(nameOf(t), out var r) ? r : int.MaxValue)];
    }

    /// <summary>把当前顺序写回。</summary>
    public void CaptureOrder(IEnumerable<string> names) => AccountOrder = [.. names];
}

/// <summary>界面状态的读写。</summary>
public sealed class UiStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _sync = new();

    public UiStateStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(AppPaths.DataDirectory, "evewatch.ui.json");
        Current = new UiState();
    }

    public string Path { get; }

    public UiState Current { get; private set; }

    public UiState Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(Path))
                {
                    Current = new UiState();
                    return Current;
                }

                Current = JsonSerializer.Deserialize<UiState>(File.ReadAllText(Path), JsonOptions) ?? new UiState();
                Current.AccountOrder ??= [];
                Current.LogRetentionDays = Math.Clamp(Current.LogRetentionDays, 1, 365);
                Current.WallScale = Math.Clamp(Current.WallScale, 0.2, 1.5);
                Current.IntelAlertWindow ??= new IntelAlertWindowState();
                Current.IntelAlertWindow.Normalize();
                Current.ClientPreview ??= new ClientPreviewState();
                Current.ClientPreview.Normalize();
                return Current;
            }
            catch
            {
                Current = new UiState();
                return Current;
            }
        }
    }

    public void Save()
    {
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.WriteAllText(Path, JsonSerializer.Serialize(Current, JsonOptions));
            }
            catch
            {
                // 界面状态写失败不影响功能
            }
        }
    }
}

/// <summary>程序使用的固定路径。</summary>
public static class AppPaths
{
    /// <summary><c>%APPDATA%\EveWatch</c>。</summary>
    public static string DataDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EveWatch");

    /// <summary>崩溃日志路径。</summary>
    public static string CrashLog => System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log");
}
