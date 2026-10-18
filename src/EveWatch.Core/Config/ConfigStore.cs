using System.Text.Encodings.Web;
using System.Text.Json;

namespace EveWatch.Core.Config;

/// <summary>功能配置的读写。</summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _sync = new();

    public ConfigStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(AppContext.BaseDirectory, "evewatch.json");
        Current = AppConfig.CreateDefault();
    }

    /// <summary>配置文件路径。</summary>
    public string Path { get; }

    /// <summary>当前生效的配置。</summary>
    public AppConfig Current { get; private set; }

    /// <summary>最近一次加载的错误说明；成功时为 <c>null</c>。</summary>
    public string? LastError { get; private set; }

    /// <summary>配置被替换或保存后触发（可能来自任意线程）。</summary>
    public event Action? Changed;

    /// <summary>
    /// 从磁盘加载。文件不存在时生成默认配置并落盘；
    /// 内容非法或含 BOM 时按容错策略处理，不抛异常。
    /// </summary>
    public AppConfig Load()
    {
        lock (_sync)
        {
            LastError = null;

            try
            {
                if (!File.Exists(Path))
                {
                    Current = AppConfig.CreateDefault();
                    SaveCore();
                    return Current;
                }

                // File.ReadAllText 会识别并剥掉 UTF-8 BOM
                var text = File.ReadAllText(Path);
                var loaded = JsonSerializer.Deserialize<AppConfig>(text, JsonOptions);

                if (loaded is null)
                {
                    LastError = "配置内容为空，已回退默认值。";
                    Current = AppConfig.CreateDefault();
                    return Current;
                }

                loaded.Normalize();
                Current = loaded;
                return Current;
            }
            catch (Exception ex)
            {
                LastError = $"配置读取失败，已回退默认值：{ex.Message}";
                Current = AppConfig.CreateDefault();
                return Current;
            }
        }
    }

    /// <summary>把当前配置写回磁盘。</summary>
    public void Save()
    {
        lock (_sync)
        {
            try
            {
                SaveCore();
            }
            catch (Exception ex)
            {
                LastError = $"配置保存失败：{ex.Message}";
            }
        }

        Changed?.Invoke();
    }

    /// <summary>替换当前配置对象（不落盘），并通知订阅者。</summary>
    public void Replace(AppConfig config)
    {
        config.Normalize();
        lock (_sync)
        {
            Current = config;
        }

        Changed?.Invoke();
    }

    private void SaveCore()
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(Path, JsonSerializer.Serialize(Current, JsonOptions));
    }
}
