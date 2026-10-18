namespace EveWatch.Core.Events;

/// <summary>事件类型。</summary>
public enum AlarmEventKind
{
    /// <summary>命中但未升级为报警（去抖未通过或处于冷却期）。</summary>
    Hit,

    /// <summary>已触发报警。</summary>
    Alarm,

    Info,

    Warn,

    Error,
}

/// <summary>一条事件记录。</summary>
public sealed record AlarmEvent(
    long TimestampMs,
    AlarmEventKind Kind,
    string Account,
    string Text,
    string Note = "")
{
    /// <summary>本地时间，形如 <c>14:03:07</c>。</summary>
    public string TimeText => DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs).ToLocalTime().ToString("HH:mm:ss");
}

/// <summary>事件流：内存里保留最近若干条，并按需落盘。</summary>
public sealed class EventJournal
{
    private readonly int _capacity;
    private readonly LinkedList<AlarmEvent> _items = new();
    private readonly object _sync = new();

    public EventJournal(int capacity = 500) => _capacity = Math.Max(32, capacity);

    /// <summary>新事件到达（可能来自任意线程）。</summary>
    public event Action<AlarmEvent>? Appended;

    /// <summary>取最近的若干条，按时间倒序。</summary>
    public IReadOnlyList<AlarmEvent> Recent(int count = 200)
    {
        lock (_sync)
        {
            return [.. _items.Take(Math.Max(1, count))];
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _items.Clear();
        }
    }

    public void Add(AlarmEventKind kind, string account, string text, string note = "")
    {
        var item = new AlarmEvent(DateTimeOffset.Now.ToUnixTimeMilliseconds(), kind, account, text, note);

        lock (_sync)
        {
            _items.AddFirst(item);
            while (_items.Count > _capacity)
            {
                _items.RemoveLast();
            }
        }

        Appended?.Invoke(item);
    }

    public void Info(string account, string text, string note = "") => Add(AlarmEventKind.Info, account, text, note);

    public void Warn(string account, string text, string note = "") => Add(AlarmEventKind.Warn, account, text, note);

    public void Error(string account, string text, string note = "") => Add(AlarmEventKind.Error, account, text, note);
}
