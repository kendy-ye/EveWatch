using System.Text.Json;
using EveWatch.Core.Alarm;
using EveWatch.Core.Config;
using EveWatch.Core.Events;
using EveWatch.Core.Map;

namespace EveWatch.Core.Intel;

public sealed record IntelAlertItem(
    long CharacterId,
    string CharacterName,
    string ChannelId,
    string ChannelName,
    string Message,
    SolarSystem Target,
    int Jumps,
    IntelRule Rule,
    DateTime Time,
    IReadOnlyList<SolarSystem> Path,
    IReadOnlyList<SolarSystem> AlternativePath);

public sealed record IntelAlertBatch(IReadOnlyList<IntelAlertItem> Items, DateTime Time);

public sealed record IntelListenerStatus(
    long CharacterId,
    string CharacterName,
    string ChannelName,
    string? CurrentSystem,
    bool PositionKnown,
    DateTime? LastMessageAt,
    string State);

public sealed record IntelStatus(
    bool Running,
    string? ChatLogDirectory,
    string? Error,
    IReadOnlyList<IntelListenerStatus> Listeners);

/// <summary>
/// 日志预警编排：扫描日志、跟踪当前位置、解析预警消息、批次合并并播放报警。
/// 整个服务在独立后台线程运行，界面只读取状态快照。
/// </summary>
public sealed class IntelAlertService : IDisposable
{
    private sealed class ListenerRuntime
    {
        public required IntelListener Listener { get; init; }

        public ChatLogTail? Tail { get; set; }

        public string? FilePath { get; set; }

        public DateTime? LastMessageAt { get; set; }

        public string State { get; set; } = "等待日志";
    }

    private readonly ConfigStore _config;
    private readonly AlarmPlayer _alarm;
    private readonly EventJournal _events;
    private readonly object _sync = new();
    private readonly object _lifecycleSync = new();
    private readonly object _pendingSync = new();

    private ManualResetEventSlim? _stop;
    private Thread? _thread;
    private GalaxyMap? _map;
    private ChatLogCatalog? _catalog;
    private IntelRuleMatcher? _matcher;
    private IntelOptions _options = new();
    private IReadOnlyList<IntelRule> _rules = [];
    private List<ChatLogFile> _files = [];
    private List<ListenerRuntime> _listeners = [];
    private Dictionary<long, LocalPositionTracker> _positions = [];
    private readonly List<IntelAlertItem> _pending = [];
    private readonly Dictionary<string, long> _cooldowns = new(StringComparer.Ordinal);
    private long _batchDeadline;
    private DateTime _startedAt;
    private string? _directory;
    private string? _error;
    private string? _signature;
    private bool _running;
    private bool _disposed;

    public IntelAlertService(ConfigStore config, AlarmPlayer alarm, EventJournal events)
    {
        _config = config;
        _alarm = alarm;
        _events = events;
    }

    /// <summary>一个批次完成时触发，可能来自后台线程。</summary>
    public event Action<IntelAlertBatch>? AlertRaised;

    public bool Running => _running;

    /// <summary>已加载的星系图；未启用或加载失败时为 null。</summary>
    public GalaxyMap? Map => _map;

    /// <summary>按当前配置重载。配置未变化时不重启线程。</summary>
    public void ApplyConfig()
    {
        if (_disposed)
        {
            return;
        }

        var intel = _config.Current.Intel ?? new IntelOptions();
        var signature = JsonSerializer.Serialize(intel);

        lock (_lifecycleSync)
        {
            if (string.Equals(signature, _signature, StringComparison.Ordinal))
            {
                return;
            }

            _signature = signature;
            Stop();
            _options = Clone(intel);

            if (_options.Enabled)
            {
                Start();
            }
        }
    }

    public IntelStatus GetStatus()
    {
        lock (_sync)
        {
            var listeners = _listeners.Select(runtime =>
            {
                _positions.TryGetValue(runtime.Listener.CharacterId, out var position);
                var state = runtime.Listener.Enabled
                    ? runtime.Tail is null
                        ? "等待日志"
                        : position is null || !position.PositionKnown
                            ? "位置未知"
                            : "监听中"
                    : "已停用";

                return new IntelListenerStatus(
                    runtime.Listener.CharacterId,
                    runtime.Listener.CharacterName,
                    string.IsNullOrWhiteSpace(runtime.Listener.ChannelName)
                        ? runtime.Listener.ChannelId
                        : runtime.Listener.ChannelName,
                    position?.CurrentSystem?.Name,
                    position?.PositionKnown ?? false,
                    runtime.LastMessageAt,
                    state);
            }).ToList();

            return new IntelStatus(_running, _directory, _error, listeners);
        }
    }

    private void Start()
    {
        _startedAt = DateTime.Now;
        _error = null;
        _pending.Clear();
        _cooldowns.Clear();
        _files = [];
        _listeners = [];
        _positions = [];

        try
        {
            _map = GalaxyMap.LoadDefault();
        }
        catch (Exception ex)
        {
            _map = null;
            _error = $"星系图加载失败：{ex.Message}";
            _events.Warn("日志预警", "星系图加载失败", _error);
            return;
        }

        _catalog = new ChatLogCatalog();
        _matcher = new IntelRuleMatcher(_map);
        _rules = _options.Rules.ToList();
        _directory = ResolveDirectory(_options.ChatLogDirectory);

        var characterNames = new Dictionary<long, string>();
        foreach (var listener in _options.Listeners)
        {
            if (listener.CharacterId <= 0)
            {
                continue;
            }

            characterNames.TryAdd(listener.CharacterId, listener.CharacterName);
            _listeners.Add(new ListenerRuntime
            {
                Listener = listener,
                State = listener.Enabled ? "等待日志" : "已停用",
            });
        }

        foreach (var (characterId, name) in characterNames)
        {
            _positions[characterId] = new LocalPositionTracker(characterId, name);
        }

        // 首次目录扫描放在后台线程，避免启用预警时阻塞设置界面。
        var stop = new ManualResetEventSlim(false);
        _stop = stop;
        _running = true;
        _thread = new Thread(() => Loop(stop))
        {
            IsBackground = true,
            Name = "EveWatch-Intel",
        };
        _thread.Start();

        _events.Info("日志预警", "已启动", _directory ?? "未找到聊天日志目录");
    }

    private void Stop()
    {
        var stop = _stop;
        var thread = _thread;

        _stop = null;
        _thread = null;
        _running = false;

        if (stop is null || thread is null)
        {
            return;
        }

        stop.Set();

        if (thread.IsAlive)
        {
            _ = thread.Join(TimeSpan.FromSeconds(2));
        }

        // 超时未退出时不要释放：后台线程可能仍停在 Wait 上，释放会让它抛异常。
        if (!thread.IsAlive)
        {
            stop.Dispose();
        }

        _events.Info("日志预警", "已停止");
    }

    private void Loop(ManualResetEventSlim stop)
    {
        var lastScan = 0L;
        var lastTail = 0L;

        while (!stop.IsSet)
        {
            try
            {
                var now = Environment.TickCount64;

                if (now - lastScan >= 5_000)
                {
                    lastScan = now;
                    RefreshCatalog(initial: false);
                }

                if (now - lastTail >= 1_000)
                {
                    lastTail = now;
                    TailOnce();
                }

                FlushBatchIfDue();
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    _error = ex.Message;
                }
            }

            if (stop.Wait(250))
            {
                break;
            }
        }
    }

    private void RefreshCatalog(bool initial)
    {
        if (_map is null || _catalog is null)
        {
            return;
        }

        if (_directory is null)
        {
            _directory = ResolveDirectory(_options.ChatLogDirectory);
            if (_directory is null)
            {
                lock (_sync)
                {
                    _error = "未找到聊天日志目录";
                }

                return;
            }

            lock (_sync)
            {
                _error = null;
            }
        }

        var files = _catalog.Scan(_directory);
        _files = [.. files];

        lock (_sync)
        {
            foreach (var position in _positions.Values)
            {
                position.Update(_files, _map);
            }

            foreach (var runtime in _listeners)
            {
                if (!runtime.Listener.Enabled)
                {
                    runtime.State = "已停用";
                    runtime.Tail = null;
                    runtime.FilePath = null;
                    continue;
                }

                var file = ChatLogCatalog.ResolveListenerFile(_files, runtime.Listener);
                if (file is null)
                {
                    runtime.Tail = null;
                    runtime.FilePath = null;
                    runtime.State = "等待日志";
                    continue;
                }

                if (runtime.Tail is null
                    || !string.Equals(runtime.FilePath, file.Path, StringComparison.OrdinalIgnoreCase))
                {
                    var startAtEnd = initial || file.LastWriteTime <= _startedAt;
                    runtime.Tail = new ChatLogTail(file.Path, startAtEnd);
                    runtime.FilePath = file.Path;
                }

                runtime.State = "监听中";
            }
        }
    }

    private void TailOnce()
    {
        if (_map is null || _matcher is null)
        {
            return;
        }

        lock (_sync)
        {
            foreach (var position in _positions.Values)
            {
                position.Update(_files, _map);
            }

            foreach (var runtime in _listeners)
            {
                if (!runtime.Listener.Enabled || runtime.Tail is null)
                {
                    continue;
                }

                foreach (var line in runtime.Tail.ReadNewLines())
                {
                    ProcessLine(runtime, line);
                }
            }
        }
    }

    private void ProcessLine(ListenerRuntime runtime, string line)
    {
        if (_map is null || _matcher is null)
        {
            return;
        }

        if (!IntelMessageParser.TryParseLine(line, out var speaker, out var message))
        {
            return;
        }

        runtime.LastMessageAt = DateTime.Now;

        if (_options.IgnoreSelf
            && string.Equals(speaker, runtime.Listener.CharacterName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (IntelMessageParser.IsIgnored(speaker, message))
        {
            return;
        }

        var targets = IntelMessageParser.ExtractSystems(message, _map);
        if (targets.Count == 0)
        {
            return;
        }

        if (!_positions.TryGetValue(runtime.Listener.CharacterId, out var position)
            || position.CurrentSystem is null)
        {
            return;
        }

        var match = _matcher.Match(position.CurrentSystem.Id, targets, _rules);
        if (match is null)
        {
            return;
        }

        var now = Environment.TickCount64;
        var cooldownKey = $"{runtime.Listener.CharacterId}\u001f{match.Target.Id}";
        if (_cooldowns.TryGetValue(cooldownKey, out var last) && now - last < _options.CooldownMs)
        {
            return;
        }

        _cooldowns[cooldownKey] = now;

        var path = _map.ShortestPath(position.CurrentSystem.Id, match.Target.Id);
        var alternative = _map.FindAlternativePath(position.CurrentSystem.Id, match.Target.Id, path);

        var item = new IntelAlertItem(
            runtime.Listener.CharacterId,
            runtime.Listener.CharacterName,
            runtime.Listener.ChannelId,
            runtime.Listener.ChannelName,
            message,
            match.Target,
            match.Jumps,
            match.Rule,
            DateTime.Now,
            _map.MapPath(path),
            _map.MapPath(alternative));

        lock (_pendingSync)
        {
            _pending.Add(item);
            if (_pending.Count == 1)
            {
                _batchDeadline = now + Math.Max(500, _options.BatchWindowMs);
            }
        }
    }

    private void FlushBatchIfDue()
    {
        List<IntelAlertItem> items;

        lock (_pendingSync)
        {
            if (_pending.Count == 0 || Environment.TickCount64 < _batchDeadline)
            {
                return;
            }

            items = [.. _pending];
            _pending.Clear();
            _batchDeadline = 0;
        }

        items.Sort((left, right) =>
        {
            var jumps = left.Jumps.CompareTo(right.Jumps);
            return jumps != 0 ? jumps : left.Time.CompareTo(right.Time);
        });

        var first = items[0];
        var result = _alarm.Play($"intel:batch:{Guid.NewGuid():N}", first.Rule.Alarm, first.Rule.Volume);
        var playText = DescribePlay(result);

        foreach (var item in items)
        {
            _events.Add(
                AlarmEventKind.Alarm,
                item.CharacterName,
                $"{item.ChannelName} · {item.Target.Name} {item.Jumps} 跳",
                $"{item.Rule.MinJumps}-{item.Rule.MaxJumps} 跳 · {playText}");
        }

        try
        {
            AlertRaised?.Invoke(new IntelAlertBatch(items, DateTime.Now));
        }
        catch
        {
            // 界面消费失败不影响后续监听
        }
    }

    private string? ResolveDirectory(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return configured;
        }

        return ChatLogCatalog.FindDirectory();
    }

    private static string DescribePlay(AlarmPlayResult result) => result switch
    {
        AlarmPlayResult.Played => "已播放",
        AlarmPlayResult.Busy => "ROI 报警占用，本次未出声",
        AlarmPlayResult.Muted => "全局静音，仅记录",
        _ => "音频不可用，仅记录",
    };

    private static IntelOptions Clone(IntelOptions source) => new()
    {
        Enabled = source.Enabled,
        ChatLogDirectory = source.ChatLogDirectory,
        CooldownMs = source.CooldownMs,
        BatchWindowMs = source.BatchWindowMs,
        IgnoreSelf = source.IgnoreSelf,
        Listeners = source.Listeners.Select(listener => new IntelListener
        {
            CharacterId = listener.CharacterId,
            CharacterName = listener.CharacterName,
            ChannelId = listener.ChannelId,
            ChannelName = listener.ChannelName,
            Enabled = listener.Enabled,
        }).ToList(),
        Rules = source.Rules.Select(rule => new IntelRule
        {
            MinJumps = rule.MinJumps,
            MaxJumps = rule.MaxJumps,
            Enabled = rule.Enabled,
            Volume = rule.Volume,
            Alarm = new AlarmSetting
            {
                Kind = rule.Alarm.Kind,
                Id = rule.Alarm.Id,
                Path = rule.Alarm.Path,
            },
        }).ToList(),
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_lifecycleSync)
        {
            Stop();
        }
    }
}
