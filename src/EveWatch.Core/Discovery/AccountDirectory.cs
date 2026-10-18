namespace EveWatch.Core.Discovery;

/// <summary>
/// 账号台账：在后台线程定期枚举窗口，界面只读快照。
/// <para>
/// 枚举本身很便宜（EnumWindows 加取标题约 0.2ms），但取进程名约 10ms/次，
/// 放到界面线程上周期性执行会造成可感知的卡顿，因此固定放在后台定时器里，并做重入保护。
/// </para>
/// </summary>
public sealed class AccountDirectory : IDisposable
{
    private readonly string _processName;
    private readonly Timer _timer;
    private WindowInfo[] _snapshot = [];
    private int _refreshing;
    private bool _disposed;

    public AccountDirectory(string processName, int refreshMs = 1000)
    {
        _processName = processName;
        _timer = new Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);
        RefreshMs = Math.Max(250, refreshMs);
    }

    /// <summary>刷新间隔（毫秒）。</summary>
    public int RefreshMs { get; }

    /// <summary>快照发生变化时触发（可能来自后台线程）。</summary>
    public event Action? Changed;

    public void Start() => _timer.Change(0, RefreshMs);

    public void Stop() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    /// <summary>当前快照，可直接遍历（数组引用是原子的）。</summary>
    public IReadOnlyList<WindowInfo> Windows => _snapshot;

    /// <summary>按账号名找窗口。</summary>
    public WindowInfo? Find(string accountName)
    {
        foreach (var window in _snapshot)
        {
            if (string.Equals(window.AccountName, accountName, StringComparison.Ordinal))
            {
                return window;
            }
        }

        return null;
    }

    /// <summary>立即刷新一次（同步）。</summary>
    public void Refresh()
    {
        if (_disposed || Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        try
        {
            var found = WindowFinder.Enumerate(_processName);

            if (!SameAs(_snapshot, found))
            {
                _snapshot = [.. found];
                Changed?.Invoke();
            }
        }
        catch
        {
            // 枚举失败：保留上一次快照，下一拍再试
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private static bool SameAs(WindowInfo[] left, IReadOnlyList<WindowInfo> right)
    {
        if (left.Length != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (left[i].Hwnd != right[i].Hwnd
                || !string.Equals(left[i].Title, right[i].Title, StringComparison.Ordinal)
                || left[i].Minimized != right[i].Minimized)
            {
                return false;
            }
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
    }
}
