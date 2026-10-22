using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace EveWatch.Core.Discovery;

/// <summary>一个被发现的目标窗口。</summary>
public sealed record WindowInfo(
    nint Hwnd,
    string Title,
    string ClassName,
    uint ProcessId,
    string ProcessName,
    int ClientWidth,
    int ClientHeight,
    bool Minimized)
{
    /// <summary>窗口标题里 "EVE - " 前缀之后的账号标识。</summary>
    public string AccountName
    {
        get
        {
            const string prefix = "EVE - ";
            return Title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? Title[prefix.Length..].Trim()
                : Title.Trim();
        }
    }
}

/// <summary>
/// 枚举本机顶层窗口。
/// <para>
/// 多开环境下必须按「进程名 + 窗口」双条件筛选：EVE-O Preview 一类工具会创建标题相似的窗口，
/// 只按标题找会抓到缩略图。
/// </para>
/// </summary>
public static class WindowFinder
{
    private static readonly object CacheSync = new();

    /// <summary>进程 id 到进程名的缓存。取进程名的开销约 10ms/次，不值得每拍都做。</summary>
    private static readonly Dictionary<uint, string> ProcessNameCache = [];

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private const int SwRestore = 9;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly nint HwndTop = nint.Zero;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>窗口是否仍然存在。</summary>
    public static bool IsAlive(nint hwnd) => hwnd != nint.Zero && IsWindow(hwnd);

    /// <summary>当前前台窗口句柄；没有前台窗口时为 <see cref="nint.Zero"/>。</summary>
    public static nint ForegroundHandle => GetForegroundWindow();

    /// <summary>窗口是否已最小化（最小化时 WGC 取不到帧）。</summary>
    public static bool IsMinimized(nint hwnd) => hwnd != nint.Zero && IsIconic(hwnd);

    /// <summary>把窗口置前并激活；已最小化时先还原。</summary>
    public static bool Activate(nint hwnd)
    {
        if (hwnd == nint.Zero || !IsWindow(hwnd))
        {
            return false;
        }

        if (IsIconic(hwnd))
        {
            _ = ShowWindow(hwnd, SwRestore);
        }

        if (GetForegroundWindow() == hwnd)
        {
            return true;
        }

        if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd)
        {
            return true;
        }

        // SetForegroundWindow 受系统前台锁限制，不属于当前前台进程时会被静默拒绝。
        // 退一步把目标提到同显示器 Z 序顶部：不抢焦点，但至少让它盖在其它窗口之上。
        // 这里刻意不使用 AttachThreadInput 之类的做法——把本进程的输入队列附到游戏线程上
        // 超出了"只读屏幕"的行为边界，收益也不值得这个风险。
        _ = BringWindowToTop(hwnd);
        _ = SetWindowPos(hwnd, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

        return GetForegroundWindow() == hwnd;
    }

    /// <summary>
    /// 枚举指定进程的可见顶层窗口，只收「已进入游戏」的窗口。
    /// <para>
    /// 判据是标题形如 <c>EVE - 角色名</c>：客户端在启动与登录 / 选择角色阶段标题只有 <c>EVE</c>，
    /// 那时拿不到角色名。若把它当账号，账号列表就会因为客户端重启凭空多出一个叫 EVE 的条目，
    /// 而且它一旦写进配置就不会自己消失。
    /// </para>
    /// </summary>
    /// <param name="processName">进程名，不含扩展名，例如 <c>exefile</c>。</param>
    public static IReadOnlyList<WindowInfo> Enumerate(string processName) =>
        Enumerate(processName, IsGameWindow);

    /// <summary>
    /// 枚举停在登录 / 角色选择界面的客户端：标题只有 <c>EVE</c>，还没有角色名。
    /// <para>这些窗口不进入账号台账，只用于在预览里给一个固定的位置。</para>
    /// </summary>
    public static IReadOnlyList<WindowInfo> EnumerateCharacterSelection(string processName) =>
        Enumerate(processName, title => string.Equals(title.Trim(), "EVE", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<WindowInfo> Enumerate(string processName, Func<string, bool> titleFilter)
    {
        var result = new List<WindowInfo>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
            {
                return true;
            }

            if (!string.Equals(ProcessNameOf(pid), processName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var title = TitleOf(hwnd);
            if (!titleFilter(title))
            {
                return true;
            }

            GetClientRect(hwnd, out var rect);

            result.Add(new WindowInfo(
                hwnd,
                title,
                ClassNameOf(hwnd),
                pid,
                processName,
                Math.Max(0, rect.Right - rect.Left),
                Math.Max(0, rect.Bottom - rect.Top),
                IsIconic(hwnd)));

            return true;
        }, nint.Zero);

        return result;
    }

    /// <summary>标题是否为进入了游戏的客户端窗口（<c>EVE - 角色名</c>）。</summary>
    public static bool IsGameWindow(string title)
    {
        const string prefix = "EVE - ";
        return title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && title.Length > prefix.Length
            && !string.IsNullOrWhiteSpace(title[prefix.Length..]);
    }

    /// <summary>找出某个账号对应的窗口；找不到返回 <c>null</c>。</summary>
    public static WindowInfo? FindByAccount(string processName, string accountName)
    {
        foreach (var window in Enumerate(processName))
        {
            if (string.Equals(window.AccountName, accountName, StringComparison.Ordinal))
            {
                return window;
            }
        }

        return null;
    }

    /// <summary>清空进程名缓存。进程可能被重启并复用 pid，长时间运行后需要刷新。</summary>
    public static void ClearProcessNameCache()
    {
        lock (CacheSync)
        {
            ProcessNameCache.Clear();
        }
    }

    private static string ProcessNameOf(uint pid)
    {
        lock (CacheSync)
        {
            if (ProcessNameCache.TryGetValue(pid, out var cached))
            {
                return cached;
            }
        }

        string name;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch
        {
            // 进程已退出或无权访问：按不匹配处理
            name = string.Empty;
        }

        lock (CacheSync)
        {
            ProcessNameCache[pid] = name;
        }

        return name;
    }

    private static string TitleOf(nint hwnd)
    {
        var buffer = new StringBuilder(512);
        var length = GetWindowTextW(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : string.Empty;
    }

    private static string ClassNameOf(nint hwnd)
    {
        var buffer = new StringBuilder(256);
        var length = GetClassNameW(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : string.Empty;
    }
}
