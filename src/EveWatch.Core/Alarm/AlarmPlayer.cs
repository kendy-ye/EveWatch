using EveWatch.Core.Config;
using NAudio.Wave;

namespace EveWatch.Core.Alarm;

/// <summary>一次播放请求的结果。</summary>
public enum AlarmPlayResult
{
    /// <summary>已开始播放。</summary>
    Played,

    /// <summary>全局静音，未出声。</summary>
    Muted,

    /// <summary>播放通道被别的账号占用，本次让位。</summary>
    Busy,

    /// <summary>音频设备或音频文件不可用。</summary>
    Failed,
}

/// <summary>
/// 报警播放。
/// <para>
/// <b>全局只有一个播放通道</b>：谁先触发谁占用，其余账号在该声音播完前一律让位。
/// 多开场景下如果每个账号各占一路，十个客户端同时命中就是十路声音叠加，
/// 既刺耳又听不出是哪个号出事，所以这里按"单通道、先到先得、不排队"处理。
/// 通道在换下一路声音的交接期间也会被预留，任何时刻最多只有一路在响。
/// </para>
/// <para>
/// 不做排队是有意的：排队会让声音连绵不断，同样听不清。
/// 让位的账号也不会丢报警——它仍会照常写入事件栏，且它的冷却计时从本次开始，
/// 仍在命中就会在下个冷却周期再抢一次。
/// </para>
/// <para>播放不阻塞调用线程，清理在音频回调线程上完成。</para>
/// </summary>
public sealed class AlarmPlayer : IDisposable
{
    /// <summary>试听通道的标识。与账号名不冲突，用它在单通道里做"自己人"判定。</summary>
    private const string PreviewKey = "__preview__";

    private readonly object _sync = new();
    private readonly Func<AppConfig> _configAccessor;
    private Playing? _current;
    private string? _currentKey;

    /// <summary>
    /// 通道切换中：旧的一路已让出、新的一路还没占位。此期间通道视为被占用。
    /// </summary>
    private bool _swapping;

    private bool _disposed;

    public AlarmPlayer(Func<AppConfig> configAccessor)
    {
        _configAccessor = configAccessor;
    }

    /// <summary>播放失败时触发，参数为失败原因。</summary>
    public event Action<string>? Failed;

    /// <summary>全局静音。静音时 <see cref="Play"/> 直接返回，不产生任何声音。</summary>
    public bool Muted => _configAccessor().GlobalMute;

    /// <summary>当前占用播放通道的账号；没有在播时为 null。</summary>
    public string? ActiveKey
    {
        get
        {
            lock (_sync)
            {
                return _currentKey;
            }
        }
    }

    /// <summary>
    /// 播放某个账号的报警音。通道被别的账号占用时让位。
    /// </summary>
    public AlarmPlayResult Play(string accountKey, AlarmSetting alarm, int volume)
    {
        if (_disposed)
        {
            return AlarmPlayResult.Failed;
        }

        if (Muted)
        {
            return AlarmPlayResult.Muted;
        }

        return PlayCore(accountKey, alarm, volume, preempt: false);
    }

    /// <summary>
    /// 试听内置预设。
    /// <para>
    /// 人工操作，抢占当前正在播的报警音：报警音每两秒就会响一次，
    /// 如果让位给报警，用户点了试听却听不到，会以为功能坏了。
    /// </para>
    /// </summary>
    public AlarmPlayResult Preview(AudioPreset preset, int volume)
    {
        if (_disposed)
        {
            return AlarmPlayResult.Failed;
        }

        if (Muted)
        {
            return AlarmPlayResult.Muted;
        }

        return PlayCore(
            PreviewKey,
            new AlarmSetting { Kind = AlarmSetting.KindPreset, Id = preset.Id },
            volume,
            preempt: true);
    }

    /// <summary>
    /// 试听本机音频文件。与 <see cref="Preview"/> 同样是人工操作，抢占当前正在播的报警音。
    /// </summary>
    public AlarmPlayResult PreviewFile(string path, int volume)
    {
        if (_disposed)
        {
            return AlarmPlayResult.Failed;
        }

        if (Muted)
        {
            return AlarmPlayResult.Muted;
        }

        return PlayCore(
            PreviewKey,
            new AlarmSetting { Kind = AlarmSetting.KindFile, Id = string.Empty, Path = path },
            volume,
            preempt: true);
    }

    private AlarmPlayResult PlayCore(string key, AlarmSetting alarm, int volume, bool preempt)
    {
        IWaveProvider provider;
        try
        {
            provider = CreateProvider(alarm);
        }
        catch (Exception ex)
        {
            Failed?.Invoke($"音频不可用：{ex.Message}");
            return AlarmPlayResult.Failed;
        }

        Playing? previous;
        lock (_sync)
        {
            // 占用者判定与接管必须在同一把锁里完成：多账号线程同时命中时，
            // 分开写会出现两个账号都通过了检查、都认为自己该播的窗口。
            // 同一账号的重复报警视为它自己续播，允许打断自己。
            // _swapping 期间一律让位：那时通道虽然空着，但属于"正在交接"，
            // 放进来就会出现两路声音交叠，正好是要避免的情况。
            if (_swapping || (!preempt && _current is not null && !string.Equals(_currentKey, key, StringComparison.Ordinal)))
            {
                (provider as IDisposable)?.Dispose();
                return AlarmPlayResult.Busy;
            }

            previous = _current;
            _current = null;
            _currentKey = null;
            _swapping = true;
        }

        WaveOutEvent? output = null;
        Playing? playing = null;
        try
        {
            // 先彻底停掉上一路再起新的，保证任一时刻都只有一路在响。
            // 放进 try：即便释放抛异常也要走 catch 解除通道预留，否则报警会永久失效。
            previous?.Dispose();
            output = new WaveOutEvent
            {
                Volume = Math.Clamp(volume, 0, 100) / 100f,
                DesiredLatency = 120,
            };

            playing = new Playing(output, provider);
            output.PlaybackStopped += (_, _) => OnStopped(playing);

            // 先占位再开播：极短的音频可能在 Play() 返回前就触发 PlaybackStopped，
            // 那时若还没占位，回调清不掉，通道会被一个已播完的对象永久占住
            lock (_sync)
            {
                _current = playing;
                _currentKey = key;
                _swapping = false;
            }

            output.Init(provider);
            output.Play();
            return AlarmPlayResult.Played;
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_current, playing))
                {
                    _current = null;
                    _currentKey = null;
                }

                _swapping = false;
            }

            // Playing 已持有 output 与 provider，构造成功后由它统一释放
            if (playing is not null)
            {
                playing.Dispose();
            }
            else
            {
                output?.Dispose();
                (provider as IDisposable)?.Dispose();
            }

            Failed?.Invoke($"播放失败：{ex.Message}");
            return AlarmPlayResult.Failed;
        }
    }

    /// <summary>停止当前播放（退出或急停时调用）。</summary>
    public void StopAll()
    {
        Playing? current;
        lock (_sync)
        {
            current = _current;
            _current = null;
            _currentKey = null;
        }

        current?.Dispose();
    }

    private void OnStopped(Playing playing)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_current, playing))
            {
                _current = null;
                _currentKey = null;
            }
        }

        playing.Dispose();
    }

    private static IWaveProvider CreateProvider(AlarmSetting alarm)
    {
        if (!alarm.IsPreset && !string.IsNullOrWhiteSpace(alarm.Path))
        {
            if (!File.Exists(alarm.Path))
            {
                throw new FileNotFoundException("音频文件不存在", alarm.Path);
            }

            return new AudioFileReader(alarm.Path);
        }

        var preset = AudioPresets.Find(alarm.Id) ?? AudioPresets.Default;
        var pcm = AudioPresets.PcmOf(preset);
        return new RawSourceWaveStream(new MemoryStream(pcm, writable: false), AudioPresets.Format);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopAll();
    }

    /// <summary>一路正在播放的输出。</summary>
    private sealed class Playing(WaveOutEvent output, IWaveProvider provider) : IDisposable
    {
        public WaveOutEvent Output { get; } = output;

        public IWaveProvider Provider { get; } = provider;

        public void Dispose()
        {
            try
            {
                Output.Dispose();
            }
            catch
            {
                // 忽略
            }

            (Provider as IDisposable)?.Dispose();
        }
    }
}
