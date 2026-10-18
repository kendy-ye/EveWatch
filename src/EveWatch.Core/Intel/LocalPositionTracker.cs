using System.Text.RegularExpressions;
using EveWatch.Core.Map;

namespace EveWatch.Core.Intel;

/// <summary>
/// 从角色最新的本地频道日志中维护当前星系。
/// 文件轮转时从头解析新文件；没有换星系行时保留上一次已知位置。
/// </summary>
public sealed class LocalPositionTracker
{
    private static readonly Regex LinePattern = new(
        @"^\[[^\]]+\]\s*[^>]+>\s*(?<message>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex SystemPattern = new(
        @"(?:频道更换为本地|Channel changed to Local)\s*[:：]\s*(?<name>.+?)\*?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private ChatLogTail? _tail;
    private string? _filePath;

    public LocalPositionTracker(long characterId, string characterName)
    {
        CharacterId = characterId;
        CharacterName = characterName;
    }

    public long CharacterId { get; }

    public string CharacterName { get; }

    public SolarSystem? CurrentSystem { get; private set; }

    public string? LocalFile => _filePath;

    public bool PositionKnown => CurrentSystem is not null;

    /// <summary>更新到最新日志；返回当前星系是否发生变化。</summary>
    public bool Update(IReadOnlyList<ChatLogFile> files, GalaxyMap map)
    {
        var latest = ChatLogCatalog.FindLatestLocal(files, CharacterId);
        if (latest is null)
        {
            return false;
        }

        if (!string.Equals(_filePath, latest.Path, StringComparison.OrdinalIgnoreCase))
        {
            _filePath = latest.Path;
            _tail = new ChatLogTail(latest.Path, startAtEnd: false);

            // 新会话文件：从头读一遍，找到最后一条换星系行
            var rotated = false;
            foreach (var line in _tail.ReadNewLines())
            {
                if (ParseLine(line, map))
                {
                    rotated = true;
                }
            }

            return rotated;
        }

        var changed = false;
        foreach (var line in _tail?.ReadNewLines() ?? [])
        {
            if (ParseLine(line, map))
            {
                changed = true;
            }
        }

        return changed;
    }

    private bool ParseLine(string line, GalaxyMap map)
    {
        var lineMatch = LinePattern.Match(line.TrimStart('\uFEFF'));
        if (!lineMatch.Success)
        {
            return false;
        }

        var message = lineMatch.Groups["message"].Value;
        var systemMatch = SystemPattern.Match(message);
        if (!systemMatch.Success)
        {
            return false;
        }

        var name = systemMatch.Groups["name"].Value.Trim().TrimEnd('*').Trim();
        var system = map.Find(name);
        if (system is null)
        {
            return false;
        }

        if (CurrentSystem?.Id == system.Id)
        {
            return false;
        }

        CurrentSystem = system;
        return true;
    }
}
