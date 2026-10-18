using System.IO;
using System.Text.Json;

namespace EveWatch.Core.Settings;

/// <summary>设置克隆窗口的最近使用项（按「角色 / 账号」两个维度分别记忆）。</summary>
public sealed class ClonePreferences
{
    public string? SourceInstallId { get; set; }

    public string? SourceProfileName { get; set; }

    public string? TargetInstallId { get; set; }

    public string? TargetProfileName { get; set; }

    /// <summary>当前维度：<c>char</c> 或 <c>user</c>。</summary>
    public string Dimension { get; set; } = DimensionNames.Character;

    /// <summary>各维度上次使用的模板 ID。</summary>
    public Dictionary<string, long> TemplateIds { get; set; } = new();

    /// <summary>各维度上次勾选的目标 ID。</summary>
    public Dictionary<string, List<long>> TargetIds { get; set; } = new();

    /// <summary>账号别名（账号 ID → 名称），便于辨认 core_user。</summary>
    public Dictionary<long, string> AccountAliases { get; set; } = new();
}

/// <summary>维度名称常量。</summary>
public static class DimensionNames
{
    public const string Character = "char";
    public const string User = "user";
}

/// <summary>克隆首选项读写（<c>%APPDATA%\EveSync\settings-clone.json</c>）。</summary>
public sealed class ClonePreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    public ClonePreferenceStore(string? directory = null)
    {
        Directory = directory ?? CharacterNameResolver.DefaultCacheDirectory();
        _path = Path.Combine(Directory, "evewatch.sync.json");
    }

    public string Directory { get; }

    public ClonePreferences Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new ClonePreferences();
            }

            ClonePreferences preferences = JsonSerializer
                .Deserialize<ClonePreferences>(File.ReadAllText(_path), JsonOptions) ?? new ClonePreferences();

            // 兼容损坏/手改/旧版本文件里字典为 null 的情况，避免后续 NullReferenceException。
            preferences.TemplateIds ??= new Dictionary<string, long>();
            preferences.TargetIds ??= new Dictionary<string, List<long>>();
            preferences.AccountAliases ??= new Dictionary<long, string>();

            return preferences;
        }
        catch (Exception)
        {
            return new ClonePreferences();
        }
    }

    public void Save(ClonePreferences preferences)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(_path, JsonSerializer.Serialize(preferences, JsonOptions));
        }
        catch (Exception)
        {
            // 保存失败不影响克隆。
        }
    }
}
