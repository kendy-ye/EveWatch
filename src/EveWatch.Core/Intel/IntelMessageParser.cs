using System.Text.RegularExpressions;
using EveWatch.Core.Map;

namespace EveWatch.Core.Intel;

/// <summary>从聊天日志行中提取发送者、正文与星系名。</summary>
public static class IntelMessageParser
{
    private static readonly Regex LinePattern = new(
        @"^\[[^\]]+\]\s*(?<speaker>[^>]+)>\s*(?<message>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex TokenPattern = new(
        @"[A-Za-z0-9][A-Za-z0-9\-_]*",
        RegexOptions.Compiled);

    /// <summary>预警频道用 clr 表示区域已清空/安全。</summary>
    private static readonly Regex ClearPattern = new(
        @"\bclr\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool TryParseLine(string line, out string speaker, out string message)
    {
        var match = LinePattern.Match(line.TrimStart('\uFEFF'));
        if (!match.Success)
        {
            speaker = string.Empty;
            message = string.Empty;
            return false;
        }

        speaker = match.Groups["speaker"].Value.Trim();
        message = match.Groups["message"].Value.Trim();
        return message.Length > 0;
    }

    public static bool IsIgnored(string speaker, string message)
    {
        if (message.Length == 0)
        {
            return true;
        }

        if (speaker.Contains("EVE系统", StringComparison.OrdinalIgnoreCase)
            || speaker.Contains("EVE System", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // clr 表示区域安全（clear），即使带星系名也不报警。
        if (ClearPattern.IsMatch(message))
        {
            return true;
        }

        return message.StartsWith("频道置顶信息", StringComparison.Ordinal)
            || message.StartsWith("Channel MOTD", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>按整词匹配星系名。命中多个时按出现顺序去重返回。</summary>
    public static IReadOnlyList<SolarSystem> ExtractSystems(string message, GalaxyMap map)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return [];
        }

        var result = new List<SolarSystem>();
        var seen = new HashSet<int>();

        foreach (Match match in TokenPattern.Matches(message))
        {
            var system = map.Find(match.Value);
            if (system is not null && seen.Add(system.Id))
            {
                result.Add(system);
            }
        }

        return result;
    }
}
