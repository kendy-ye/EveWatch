using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Threading;
using EveWatch.Core.Config;
using EveWatch.Core.Events;
using EveWatch.Core.Palette;

namespace EveWatch.App.ViewModels;

/// <summary>
/// 主窗口视图模型。
/// <para>
/// 配置改动走同一条出口：<see cref="NotifyConfigChanged"/> 触发延迟保存与监控范围重算，
/// 避免拖动滑块时每个刻度都写一次磁盘。
/// </para>
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private const int MaxEventRows = 200;

    /// <summary>掉线后条目在列表中保留多久，超过后彻底隐藏。</summary>
    private const long ReconnectGraceMs = 60_000;

    private readonly AppServices _services;
    private readonly DispatcherTimer _saveTimer;
    private readonly Dictionary<string, AccountViewModel> _cache = new(StringComparer.Ordinal);

    /// <summary>本次运行中曾经在线过的账号；只有它们掉线后才享受重连宽限。</summary>
    private readonly HashSet<string> _seenOnline = new(StringComparer.Ordinal);

    /// <summary>账号名 → 掉线时刻（Environment.TickCount64）。</summary>
    private readonly Dictionary<string, long> _offlineSince = new(StringComparer.Ordinal);

    private readonly Process _process = Process.GetCurrentProcess();

    private AccountViewModel? _selectedAccount;
    private bool _isWallOpen;
    private TimeSpan _lastCpuSample;
    private long _lastCpuSampleAt;
    private double _cpuPercent;

    public MainViewModel(AppServices services)
    {
        _services = services;

        PickAudioFile = _ => null;

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(700),
        };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Commit();
        };

        _services.Events.Appended += OnEventAppended;

        // 列表只显示在线与重连中的账号；离线账号保留在配置里但不再进入视图
        AccountsView = CollectionViewSource.GetDefaultView(Accounts);
        AccountsView.Filter = item => item is AccountViewModel account && account.IsVisible;

        Rebuild();
    }

    /// <summary>选择自定义音频文件；由窗口注入文件对话框。</summary>
    public Func<RoiSetting, string?> PickAudioFile { get; set; }

    /// <summary>全量账号（含离线），是配置与排序的唯一来源。</summary>
    public ObservableCollection<AccountViewModel> Accounts { get; } = [];

    /// <summary>账号列表的显示视图：只包含在线与重连中的账号。</summary>
    public ICollectionView AccountsView { get; }

    public ObservableCollection<EventItemViewModel> Events { get; } = [];

    public IReadOnlyList<PaletteColor> Palette => _services.Config.Current.Palette;

    public AppServices Services => _services;

    public AccountViewModel? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (Set(ref _selectedAccount, value))
            {
                Raise(nameof(HasSelection));
            }
        }
    }

    public bool HasSelection => _selectedAccount is not null;

    // ---------------------------------------------------------------- 工具栏

    public bool IsMonitoring => _services.Monitor.Running;

    public string MonitorButtonText => IsMonitoring ? "停止监控" : "开始监控";

    public bool IsMuted => _services.Config.Current.GlobalMute;

    public string MuteButtonText => IsMuted ? "已静音" : "全局静音";

    /// <summary>并排窗是否打开。由主窗口维护，用于让工具栏按钮反映状态。</summary>
    public bool IsWallOpen
    {
        get => _isWallOpen;
        set
        {
            if (Set(ref _isWallOpen, value))
            {
                Raise(nameof(WallButtonText));
            }
        }
    }

    public string WallButtonText => _isWallOpen ? "关闭 ROI 显示" : "开启 ROI 并排";

    /// <summary>列表中可见的账号数（在线 + 重连中）。</summary>
    public int AccountCount => Accounts.Count(a => a.IsVisible);

    public int EnabledCount => Accounts.Count(a => a.IsVisible && a.Enabled);

    public int HostileCount => Accounts.Count(a => a.IsVisible && a.Hostile);

    public bool HasVisibleAccounts => AccountCount > 0;

    public bool HasHostile => HostileCount > 0;

    public string MonitorSummary => $"{EnabledCount} / {AccountCount}";

    // ---------------------------------------------------------------- 状态栏

    public string ChannelText => AppServices.IsCaptureSupported
        ? $"WGC · {_services.Capture.SessionCount} 会话"
        : "WGC 不可用";

    public string TickText => $"{_services.Config.Current.TickMs} ms";

    public string CpuText => $"{_cpuPercent:F2}%";

    public string MemoryText => $"{_process.WorkingSet64 / 1024 / 1024} MB";

    public string DropText => Accounts.Where(a => a.IsVisible).Sum(a => a.DropFrames).ToString();

    public string ConfigPathText => Path.GetFileName(_services.Config.Path);

    // ---------------------------------------------------------------- 构建与刷新

    /// <summary>按配置重建账号列表，尽量复用已有的视图模型对象以免打断选中与滚动位置。</summary>
    public void Rebuild()
    {
        var config = _services.Config.Current;
        var settings = config.Accounts;

        var ordered = _services.UiState.Current.ApplyOrder(settings, a => a.Name);

        var selectedName = _selectedAccount?.Name;
        Accounts.Clear();

        foreach (var setting in ordered)
        {
            if (!_cache.TryGetValue(setting.Name, out var viewModel) || !ReferenceEquals(viewModel.Setting, setting))
            {
                viewModel = new AccountViewModel(setting, config.Palette, NotifyConfigChanged, roi => PickAudioFile(roi));
                _cache[setting.Name] = viewModel;
            }

            Accounts.Add(viewModel);
        }

        // 清掉配置里已经不存在的账号
        var alive = new HashSet<string>(settings.Select(a => a.Name), StringComparer.Ordinal);
        foreach (var name in _cache.Keys.ToList())
        {
            if (!alive.Contains(name))
            {
                _cache.Remove(name);
                _services.Status.Remove(name);
                _seenOnline.Remove(name);
                _offlineSince.Remove(name);
            }
        }

        SelectedAccount = selectedName is null
            ? Accounts.FirstOrDefault()
            : Accounts.FirstOrDefault(a => string.Equals(a.Name, selectedName, StringComparison.Ordinal)) ?? Accounts.FirstOrDefault();

        RefreshStatus();
        AccountsView.Refresh();
        EnsureSelection();

        RaiseMany(nameof(AccountCount), nameof(EnabledCount), nameof(MonitorSummary), nameof(HasSelection));
    }

    /// <summary>把运行时状态刷进视图模型，并更新工具栏与状态栏。</summary>
    public void RefreshStatus()
    {
        var monitoring = _services.Monitor.Running;
        var online = new HashSet<string>(
            _services.Directory.Windows.Select(w => w.AccountName), StringComparer.Ordinal);

        var names = Accounts.Select(a => a.Name).ToList();
        var statuses = _services.Status.Snapshot(names);
        var now = Environment.TickCount64;
        var membershipChanged = false;
        var needSync = false;

        for (var i = 0; i < Accounts.Count; i++)
        {
            var account = Accounts[i];

            if (online.Contains(account.Name))
            {
                _seenOnline.Add(account.Name);
                _offlineSince.Remove(account.Name);
            }
            else if (_seenOnline.Contains(account.Name) && !_offlineSince.ContainsKey(account.Name))
            {
                // 本次运行中在线过，现在窗口消失：进入 60 秒重连宽限
                _offlineSince[account.Name] = now;
            }

            var presence = online.Contains(account.Name)
                ? AccountPresence.Online
                : _offlineSince.TryGetValue(account.Name, out var offlineAt) && now - offlineAt < ReconnectGraceMs
                    ? AccountPresence.Reconnecting
                    : AccountPresence.Offline;

            if (account.SetPresence(presence))
            {
                membershipChanged = true;
                needSync = true;
            }

            account.RefreshFrom(i < statuses.Count ? statuses[i] : null, monitoring);
        }

        // 上下线时立即重算监控范围；Sync 后状态被清理，需要再刷一遍
        if (needSync)
        {
            _services.Monitor.Sync();
            statuses = _services.Status.Snapshot(names);

            for (var i = 0; i < Accounts.Count; i++)
            {
                Accounts[i].RefreshFrom(i < statuses.Count ? statuses[i] : null, monitoring);
            }
        }

        if (membershipChanged)
        {
            AccountsView.Refresh();
            EnsureSelection();
        }

        SampleCpu();

        RaiseMany(
            nameof(IsMonitoring), nameof(MonitorButtonText),
            nameof(IsMuted), nameof(MuteButtonText),
            nameof(AccountCount), nameof(HasVisibleAccounts),
            nameof(EnabledCount), nameof(HostileCount), nameof(HasHostile), nameof(MonitorSummary),
            nameof(ChannelText), nameof(TickText), nameof(CpuText), nameof(MemoryText), nameof(DropText),
            nameof(ConfigPathText));
    }

    /// <summary>窗口枚举刷新后调用。</summary>
    public void OnAccountsDiscovered()
    {
        var config = _services.Config.Current;
        var names = _services.Directory.Windows.Select(w => w.AccountName).ToList();

        if (config.SyncAccounts(names))
        {
            _services.Config.Save();
            Rebuild();
        }

        // 窗口增删、句柄变化（掉线重登 / 客户端重启）都要重算监控范围，
        // 不能只在发现新账号时调用
        _services.Monitor.Sync();
        RefreshStatus();
    }

    /// <summary>选中项被隐藏后自动切换到下一个可见账号。</summary>
    private void EnsureSelection()
    {
        if (_selectedAccount is { IsVisible: true })
        {
            return;
        }

        SelectedAccount = Accounts.FirstOrDefault(a => a.IsVisible);
    }

    // ---------------------------------------------------------------- 动作

    public void ToggleMonitoring()
    {
        if (_services.Monitor.Running)
        {
            _services.Monitor.Stop();
        }
        else
        {
            _services.Monitor.Start();
        }

        RefreshStatus();
    }

    public void ToggleMute()
    {
        var config = _services.Config.Current;
        config.GlobalMute = !config.GlobalMute;

        if (config.GlobalMute)
        {
            _services.Alarm.StopAll();
        }

        _services.Config.Save();
        RaiseMany(nameof(IsMuted), nameof(MuteButtonText));
    }

    /// <summary>批量操作只作用于当前可见账号，避免改动隐藏账号的设置。</summary>
    public void SetAllAccounts(bool enabled)
    {
        foreach (var account in Accounts.Where(a => a.IsVisible))
        {
            account.Enabled = enabled;
        }

        Commit();
    }

    public void InvertAccounts()
    {
        foreach (var account in Accounts.Where(a => a.IsVisible))
        {
            account.Enabled = !account.Enabled;
        }

        Commit();
    }

    /// <summary>拖动排序后写回配置顺序。</summary>
    public void ApplyAccountOrder(IEnumerable<string> namesInOrder)
    {
        _services.UiState.Current.CaptureOrder(namesInOrder);

        var order = _services.UiState.Current.AccountOrder;
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < order.Count; i++)
        {
            rank[order[i]] = i;
        }

        _services.Config.Current.Accounts.Sort((left, right) =>
            rank.GetValueOrDefault(left.Name, int.MaxValue).CompareTo(rank.GetValueOrDefault(right.Name, int.MaxValue)));

        _services.UiState.Save();
    }

    /// <summary>配置被改动：延迟落盘并重算监控范围。</summary>
    public void NotifyConfigChanged()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Commit()
    {
        _services.Config.Save();
        _services.UiState.Save();
        _services.Monitor.Sync();
        RefreshStatus();
    }

    /// <summary>颜色库变化后重建所有色块。</summary>
    public void NotifyPaletteChanged()
    {
        foreach (var account in Accounts)
        {
            account.RebuildColors(Palette);
        }

        NotifyConfigChanged();
    }

    /// <summary>账号配置被克隆或重标后，重建受影响账号的 ROI 列表。</summary>
    public void ReloadAccounts()
    {
        foreach (var account in Accounts)
        {
            account.ReloadFromSetting(Palette);
        }

        NotifyConfigChanged();
    }

    private void OnEventAppended(AlarmEvent item)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(new Action(() =>
        {
            Events.Insert(0, new EventItemViewModel(item));
            while (Events.Count > MaxEventRows)
            {
                Events.RemoveAt(Events.Count - 1);
            }
        }));
    }

    private void SampleCpu()
    {
        _process.Refresh();

        var now = Environment.TickCount64;
        var cpu = _process.TotalProcessorTime;

        if (_lastCpuSampleAt != 0)
        {
            var wallMs = now - _lastCpuSampleAt;
            if (wallMs > 0)
            {
                var cpuMs = (cpu - _lastCpuSample).TotalMilliseconds;
                _cpuPercent = cpuMs / wallMs * 100 / Environment.ProcessorCount;
            }
        }

        _lastCpuSample = cpu;
        _lastCpuSampleAt = now;
    }
}
