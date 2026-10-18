using System.IO;

namespace EveWatch.Core.Settings;

/// <summary>一个 EVE 设置 Profile（<c>settings_*</c> 目录）。</summary>
public sealed record SettingsProfile(string InstallId, string Name, string Directory);

/// <summary>
/// 定位 <c>%LOCALAPPDATA%\CCP\EVE</c> 下的所有设置 Profile（<c>settings_*</c> 目录）。
/// </summary>
public static class SettingsRootLocator
{
    /// <summary>EVE 设置根目录。</summary>
    public static string GetRootDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CCP",
        "EVE");

    /// <summary>枚举所有安装目录下的 <c>settings_*</c> Profile；目录不存在时返回空。</summary>
    public static IReadOnlyList<SettingsProfile> FindProfiles()
    {
        var result = new List<SettingsProfile>();
        string root = GetRootDirectory();

        if (!Directory.Exists(root))
        {
            return result;
        }

        foreach (string install in Directory.EnumerateDirectories(root))
        {
            IEnumerable<string> profiles;
            try
            {
                profiles = Directory.EnumerateDirectories(install, "settings_*");
            }
            catch (Exception)
            {
                continue;
            }

            foreach (string profile in profiles)
            {
                result.Add(new SettingsProfile(Path.GetFileName(install), Path.GetFileName(profile), profile));
            }
        }

        return result
            .OrderBy(p => p.InstallId, StringComparer.Ordinal)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
    }
}
