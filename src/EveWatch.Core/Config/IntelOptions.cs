namespace EveWatch.Core.Config;

/// <summary>日志预警配置。随 <c>evewatch.json</c> 持久化。</summary>
public sealed class IntelOptions
{
    /// <summary>是否启用日志预警。</summary>
    public bool Enabled { get; set; }

    /// <summary>聊天日志目录；空表示自动检测。</summary>
    public string ChatLogDirectory { get; set; } = string.Empty;

    /// <summary>同一角色 + 同一星系的报警冷却（毫秒）。</summary>
    public int CooldownMs { get; set; } = 30_000;

    /// <summary>多条报警合并为一批的时间窗口（毫秒）。</summary>
    public int BatchWindowMs { get; set; } = 1_200;

    /// <summary>忽略当前监听角色自己发送的消息。</summary>
    public bool IgnoreSelf { get; set; } = true;

    public List<IntelListener> Listeners { get; set; } = [];

    public List<IntelRule> Rules { get; set; } = [];
}

/// <summary>一个「角色 → 频道」监听项。</summary>
public sealed class IntelListener
{
    public long CharacterId { get; set; }

    public string CharacterName { get; set; } = string.Empty;

    /// <summary>稳定频道的 Channel ID；舰队频道或手动项可能为空。</summary>
    public string ChannelId { get; set; } = string.Empty;

    public string ChannelName { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}

/// <summary>一条跳数区间规则。</summary>
public sealed class IntelRule
{
    public int MinJumps { get; set; }

    public int MaxJumps { get; set; }

    public bool Enabled { get; set; } = true;

    public AlarmSetting Alarm { get; set; } = new();

    public int Volume { get; set; } = 80;
}
