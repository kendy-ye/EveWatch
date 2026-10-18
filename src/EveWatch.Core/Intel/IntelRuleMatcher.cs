using EveWatch.Core.Config;
using EveWatch.Core.Map;

namespace EveWatch.Core.Intel;

public sealed record IntelMatch(IntelRule Rule, SolarSystem Target, int Jumps);

/// <summary>把报告星系换算为跳数并匹配第一条命中的规则。</summary>
public sealed class IntelRuleMatcher
{
    private const int CacheLimit = 8;

    private readonly GalaxyMap _map;
    private readonly Dictionary<int, IReadOnlyDictionary<int, int>> _distanceCache = [];

    public IntelRuleMatcher(GalaxyMap map)
    {
        _map = map;
    }

    public IntelMatch? Match(
        int currentSystemId,
        IReadOnlyList<SolarSystem> targets,
        IReadOnlyList<IntelRule> rules)
    {
        if (targets.Count == 0 || rules.Count == 0)
        {
            return null;
        }

        var enabledRules = rules.Where(rule => rule.Enabled).ToList();
        if (enabledRules.Count == 0)
        {
            return null;
        }

        var distances = GetDistances(currentSystemId);
        var candidates = new List<(SolarSystem System, int Jumps)>();

        foreach (var target in targets)
        {
            if (distances.TryGetValue(target.Id, out var jumps))
            {
                candidates.Add((target, jumps));
            }
        }

        foreach (var (system, jumps) in candidates.OrderBy(item => item.Jumps))
        {
            foreach (var rule in enabledRules)
            {
                if (jumps >= rule.MinJumps && jumps <= rule.MaxJumps)
                {
                    return new IntelMatch(rule, system, jumps);
                }
            }
        }

        return null;
    }

    private IReadOnlyDictionary<int, int> GetDistances(int systemId)
    {
        if (_distanceCache.TryGetValue(systemId, out var cached))
        {
            return cached;
        }

        if (_distanceCache.Count >= CacheLimit)
        {
            _distanceCache.Clear();
        }

        var distances = _map.DistancesFrom(systemId);
        _distanceCache[systemId] = distances;
        return distances;
    }
}
