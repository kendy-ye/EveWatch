using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

namespace EveWatch.Core.Map;

/// <summary>一个恒星系。坐标按 <c>CoordinateScale</c> 缩放，仅用于相对布局。</summary>
public sealed record SolarSystem(
    int Id,
    string Name,
    double Security,
    int RegionId,
    string RegionName,
    int ConstellationId,
    string ConstellationName,
    double X,
    double Y,
    double Z);

/// <summary>星系图查询与跳数计算。数据来自嵌入式 SDE 快照。</summary>
public sealed class GalaxyMap
{
    private readonly Dictionary<int, SolarSystem> _byId;
    private readonly Dictionary<string, SolarSystem> _byName;
    private readonly Dictionary<int, List<int>> _adjacency;

    private GalaxyMap(
        IReadOnlyList<SolarSystem> systems,
        Dictionary<int, SolarSystem> byId,
        Dictionary<string, SolarSystem> byName,
        Dictionary<int, List<int>> adjacency,
        double coordinateScale)
    {
        Systems = systems;
        _byId = byId;
        _byName = byName;
        _adjacency = adjacency;
        CoordinateScale = coordinateScale;
    }

    public IReadOnlyList<SolarSystem> Systems { get; }

    public double CoordinateScale { get; }

    /// <summary>从程序集资源加载默认星系图。</summary>
    public static GalaxyMap LoadDefault()
    {
        var assembly = typeof(GalaxyMap).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(resource => resource.EndsWith("galaxy.json.gz", StringComparison.OrdinalIgnoreCase));

        if (name is null)
        {
            throw new InvalidOperationException("未找到嵌入的星系图资源 galaxy.json.gz。");
        }

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("无法读取星系图资源。");

        return Load(stream);
    }

    public static GalaxyMap Load(Stream stream)
    {
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var document = JsonDocument.Parse(gzip);
        var root = document.RootElement;

        var coordinateScale = root.TryGetProperty("coordinateScale", out var scaleElement)
            ? scaleElement.GetDouble()
            : 1d;

        var regionNames = new Dictionary<int, string>();
        if (root.TryGetProperty("regions", out var regions))
        {
            foreach (var item in regions.EnumerateArray())
            {
                regionNames[item[0].GetInt32()] = item[1].GetString() ?? string.Empty;
            }
        }

        var constellationNames = new Dictionary<int, string>();
        if (root.TryGetProperty("constellations", out var constellations))
        {
            foreach (var item in constellations.EnumerateArray())
            {
                constellationNames[item[0].GetInt32()] = item[1].GetString() ?? string.Empty;
            }
        }

        var systems = new List<SolarSystem>();
        var byId = new Dictionary<int, SolarSystem>();
        var byName = new Dictionary<string, SolarSystem>(StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("systems", out var systemArray))
        {
            foreach (var item in systemArray.EnumerateArray())
            {
                var regionId = item[3].GetInt32();
                var constellationId = item[4].GetInt32();

                var system = new SolarSystem(
                    item[0].GetInt32(),
                    item[1].GetString() ?? string.Empty,
                    item[2].GetDouble(),
                    regionId,
                    regionNames.GetValueOrDefault(regionId, string.Empty),
                    constellationId,
                    constellationNames.GetValueOrDefault(constellationId, string.Empty),
                    item[5].GetDouble(),
                    item[6].GetDouble(),
                    item[7].GetDouble());

                systems.Add(system);
                byId[system.Id] = system;
                byName[system.Name] = system;
            }
        }

        var adjacency = new Dictionary<int, List<int>>();
        if (root.TryGetProperty("edges", out var edges))
        {
            foreach (var item in edges.EnumerateArray())
            {
                var a = item[0].GetInt32();
                var b = item[1].GetInt32();

                if (!byId.ContainsKey(a) || !byId.ContainsKey(b))
                {
                    continue;
                }

                GetOrAdd(adjacency, a).Add(b);
                GetOrAdd(adjacency, b).Add(a);
            }
        }

        return new GalaxyMap(systems, byId, byName, adjacency, coordinateScale);
    }

    public SolarSystem? Find(string name) =>
        string.IsNullOrWhiteSpace(name) ? null : _byName.GetValueOrDefault(name.Trim());

    public SolarSystem? Find(int systemId) => _byId.GetValueOrDefault(systemId);

    /// <summary>返回直接星门邻居，用于邻域图绘制。</summary>
    public IReadOnlyList<SolarSystem> Neighbors(int systemId)
    {
        var ids = _adjacency.GetValueOrDefault(systemId);
        if (ids is null)
        {
            return [];
        }

        var result = new List<SolarSystem>(ids.Count);
        foreach (var id in ids)
        {
            if (_byId.TryGetValue(id, out var system))
            {
                result.Add(system);
            }
        }

        return result;
    }

    /// <summary>从起点做 BFS，返回所有可达星系的跳数。</summary>
    public IReadOnlyDictionary<int, int> DistancesFrom(int systemId, int maxDepth = 50)
    {
        var result = new Dictionary<int, int>();
        if (!_byId.ContainsKey(systemId))
        {
            return result;
        }

        var queue = new Queue<int>();
        result[systemId] = 0;
        queue.Enqueue(systemId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var depth = result[current];

            if (depth >= maxDepth)
            {
                continue;
            }

            foreach (var next in _adjacency.GetValueOrDefault(current) ?? [])
            {
                if (result.ContainsKey(next))
                {
                    continue;
                }

                result[next] = depth + 1;
                queue.Enqueue(next);
            }
        }

        return result;
    }

    public int? Distance(int fromSystemId, int toSystemId, int maxDepth = 50)
    {
        if (fromSystemId == toSystemId)
        {
            return _byId.ContainsKey(fromSystemId) ? 0 : null;
        }

        var distances = DistancesFrom(fromSystemId, maxDepth);
        return distances.TryGetValue(toSystemId, out var distance) ? distance : null;
    }

    /// <summary>返回一条最短星门路径（含起点与终点）；不可达时返回空数组。</summary>
    public IReadOnlyList<int> ShortestPath(int fromSystemId, int toSystemId)
    {
        if (fromSystemId == toSystemId)
        {
            return _byId.ContainsKey(fromSystemId) ? [fromSystemId] : [];
        }

        var parents = new Dictionary<int, int> { [fromSystemId] = 0 };
        var queue = new Queue<int>();
        queue.Enqueue(fromSystemId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var next in _adjacency.GetValueOrDefault(current) ?? [])
            {
                if (parents.ContainsKey(next))
                {
                    continue;
                }

                parents[next] = current;

                if (next == toSystemId)
                {
                    return Reconstruct(parents, fromSystemId, toSystemId);
                }

                queue.Enqueue(next);
            }
        }

        return [];
    }

    /// <summary>
    /// 在最短路径之外再找一条备选路径：逐条移除最短路径上的边，取绕行最短的结果。
    /// 用于弹窗里的「另有 N 条备选路线」展示。
    /// </summary>
    public IReadOnlyList<int> FindAlternativePath(int fromSystemId, int toSystemId, IReadOnlyList<int> shortest)
    {
        if (shortest.Count < 2)
        {
            return [];
        }

        var best = Array.Empty<int>();

        for (var i = 0; i < shortest.Count - 1; i++)
        {
            var blockedA = shortest[i];
            var blockedB = shortest[i + 1];
            var candidate = FindPathAvoidingEdge(fromSystemId, toSystemId, blockedA, blockedB);

            if (candidate.Count == 0)
            {
                continue;
            }

            if (best.Length == 0 || candidate.Count < best.Length)
            {
                best = [.. candidate];
            }
        }

        return best;
    }

    public IReadOnlyList<SolarSystem> MapPath(IReadOnlyList<int> path)
    {
        var result = new List<SolarSystem>(path.Count);
        foreach (var id in path)
        {
            if (_byId.TryGetValue(id, out var system))
            {
                result.Add(system);
            }
        }

        return result;
    }

    private IReadOnlyList<int> FindPathAvoidingEdge(int fromSystemId, int toSystemId, int blockedA, int blockedB)
    {
        var parents = new Dictionary<int, int> { [fromSystemId] = 0 };
        var queue = new Queue<int>();
        queue.Enqueue(fromSystemId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var next in _adjacency.GetValueOrDefault(current) ?? [])
            {
                if ((current == blockedA && next == blockedB) || (current == blockedB && next == blockedA))
                {
                    continue;
                }

                if (parents.ContainsKey(next))
                {
                    continue;
                }

                parents[next] = current;

                if (next == toSystemId)
                {
                    return Reconstruct(parents, fromSystemId, toSystemId);
                }

                queue.Enqueue(next);
            }
        }

        return [];
    }

    private static IReadOnlyList<int> Reconstruct(Dictionary<int, int> parents, int fromSystemId, int toSystemId)
    {
        var path = new List<int>();
        var current = toSystemId;

        while (current != fromSystemId)
        {
            path.Add(current);
            if (!parents.TryGetValue(current, out current))
            {
                return [];
            }
        }

        path.Add(fromSystemId);
        path.Reverse();
        return path;
    }

    private static List<int> GetOrAdd(Dictionary<int, List<int>> map, int key)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }

        return list;
    }
}
