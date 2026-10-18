using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace EveWatch.Core.Settings;

/// <summary>
/// 把角色 ID 解析为角色名（ESI <c>/universe/names/</c> 批量查询）。
/// 带本地缓存；离线或限流时回退为 <c>#ID</c>，不影响克隆本身。
/// </summary>
public sealed class CharacterNameResolver : IDisposable
{
    private const string Endpoint = "https://esi.evetech.net/latest/universe/names/?datasource=tranquility";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly Dictionary<long, string> _cache = new();

    public CharacterNameResolver(string? cacheDirectory = null)
    {
        _cachePath = Path.Combine(cacheDirectory ?? DefaultCacheDirectory(), "character-names.json");
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("EveWatch/1.0");
        LoadCache();
    }

    /// <summary>默认缓存目录：<c>%APPDATA%\EveWatch</c>。</summary>
    public static string DefaultCacheDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EveWatch");

    /// <summary>取缓存中的名字；未解析过时回退为 <c>#ID</c>。</summary>
    public string GetName(long id) =>
        _cache.TryGetValue(id, out string? name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : $"#{id}";

    /// <summary>批量解析并写入缓存。失败时静默保留已有缓存。</summary>
    public async Task ResolveAsync(IEnumerable<long> ids, CancellationToken cancellationToken = default)
    {
        List<long> missing = ids.Distinct().Where(id => !_cache.ContainsKey(id)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        try
        {
            string json = JsonSerializer.Serialize(missing);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _http
                .PostAsync(Endpoint, content, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            await using Stream stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            List<EsiName>? names = await JsonSerializer
                .DeserializeAsync<List<EsiName>>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (names is null)
            {
                return;
            }

            foreach (EsiName item in names)
            {
                _cache[item.Id] = item.Name;
            }

            SaveCache();
        }
        catch (Exception)
        {
            // 离线 / 限流 / 超时：保留缓存，回退到 #ID。
        }
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return;
            }

            Dictionary<long, string>? loaded = JsonSerializer
                .Deserialize<Dictionary<long, string>>(File.ReadAllText(_cachePath), JsonOptions);

            if (loaded is null)
            {
                return;
            }

            foreach (KeyValuePair<long, string> pair in loaded)
            {
                _cache[pair.Key] = pair.Value;
            }
        }
        catch (Exception)
        {
            // 缓存损坏：忽略，后续重新解析。
        }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(_cache, JsonOptions));
        }
        catch (Exception)
        {
            // 写缓存失败不影响解析结果。
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed record EsiName(long Id, string Name);
}
