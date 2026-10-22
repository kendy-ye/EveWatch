using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using EveWatch.Core.Config;

namespace EveWatch.App.Hotkeys;

/// <summary>
/// 全局快捷键注册与分发。
/// <para>
/// 使用 <c>RegisterHotKey</c> 挂在主窗口句柄上，EVE 在前台时同样能触发；
/// 窗口隐藏不影响注册，句柄只在窗口真正销毁时失效。
/// </para>
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;

    /// <summary>按住不放时只触发一次。</summary>
    private const int ModNoRepeat = 0x4000;

    private readonly Window _window;
    private readonly ConfigStore _config;
    private readonly Dictionary<int, HotkeyBinding> _registered = [];

    private HwndSource? _source;
    private nint _handle;
    private int _nextId = 1;
    private bool _disposed;

    public HotkeyService(Window window, ConfigStore config)
    {
        _window = window;
        _config = config;
    }

    /// <summary>某个快捷键被按下。</summary>
    public event Action<HotkeyActionInfo>? Triggered;

    /// <summary>录制按键期间暂停分发，避免把已注册的热键再触发一次。</summary>
    public bool IsSuspended { get; set; }

    /// <summary>
    /// 按当前配置重新注册全部快捷键。
    /// </summary>
    /// <returns>注册失败的说明；全部成功时为空。</returns>
    public IReadOnlyList<string> Apply()
    {
        var errors = new List<string>();

        if (_disposed)
        {
            return errors;
        }

        if (!EnsureSource())
        {
            errors.Add("无法建立热键消息通道，快捷键暂不可用。");
            return errors;
        }

        UnregisterAll();

        foreach (var binding in _config.Current.Hotkeys)
        {
            var action = HotkeyActions.Resolve(binding, _config.Current.ClientGroups);
            if (action is null)
            {
                continue;
            }

            var id = _nextId++;
            if (!RegisterHotKey(_handle, id, binding.Modifiers | ModNoRepeat, binding.VirtualKey))
            {
                errors.Add(
                    $"{action.Name}：{HotkeyFormatter.Format(binding)} 注册失败，可能已被系统或其他程序占用。");
                continue;
            }

            _registered[id] = binding;
        }

        return errors;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterAll();

        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private bool EnsureSource()
    {
        if (_source is not null)
        {
            return true;
        }

        _handle = new WindowInteropHelper(_window).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);

        return _source is not null;
    }

    private void UnregisterAll()
    {
        if (_handle == nint.Zero)
        {
            return;
        }

        foreach (var id in _registered.Keys)
        {
            UnregisterHotKey(_handle, id);
        }

        _registered.Clear();
        _nextId = 1;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WmHotkey)
        {
            return nint.Zero;
        }

        var id = wParam.ToInt32();
        if (!_registered.TryGetValue(id, out var binding))
        {
            return nint.Zero;
        }

        handled = true;

        if (IsSuspended)
        {
            return nint.Zero;
        }

        if (HotkeyActions.Resolve(binding, _config.Current.ClientGroups) is { } action)
        {
            Triggered?.Invoke(action);
        }

        return nint.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
