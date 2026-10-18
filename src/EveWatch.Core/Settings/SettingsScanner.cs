using System.IO;
using System.Text.RegularExpressions;

namespace EveWatch.Core.Settings;

/// <summary>设置文件种类。</summary>
public enum SettingsFileKind
{
    /// <summary>角色级 <c>core_char_&lt;角色ID&gt;.dat</c>：UI 布局、总览、快捷键等。</summary>
    Character,

    /// <summary>账号级 <c>core_user_&lt;账号ID&gt;.dat</c>：窗口位置/尺寸等。</summary>
    User,
}

/// <summary>单个设置文件（core_char / core_user）。</summary>
public sealed class SettingsFile
{
    public required SettingsFileKind Kind { get; init; }

    /// <summary>文件名中的数字：角色 ID 或账号 ID。</summary>
    public required long Id { get; init; }

    public required string Path { get; init; }

    public required long Length { get; init; }

    public required DateTime LastWriteTimeUtc { get; init; }

    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>扫描某个 Profile 下的 core_char / core_user 设置文件。</summary>
public static class SettingsScanner
{
    // 只接受纯数字 ID，跳过 core_char__.dat、core_char_('char', None, 'dat').dat 等畸形残留。
    private static readonly Regex FilePattern = new(
        @"^core_(char|user)_(\d+)\.dat$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>扫描 Profile 目录下的所有合法 core 文件。</summary>
    public static IReadOnlyList<SettingsFile> Scan(string profileDirectory)
    {
        var result = new List<SettingsFile>();

        if (!Directory.Exists(profileDirectory))
        {
            return result;
        }

        foreach (string path in Directory.EnumerateFiles(profileDirectory, "core_*.dat"))
        {
            Match match = FilePattern.Match(System.IO.Path.GetFileName(path));
            if (!match.Success)
            {
                continue;
            }

            // 畸形残留可能含超长数字，解析失败则跳过该文件，避免整次扫描失败。
            if (!long.TryParse(match.Groups[2].Value, out long id))
            {
                continue;
            }

            var info = new FileInfo(path);
            result.Add(new SettingsFile
            {
                Kind = match.Groups[1].Value.Equals("char", StringComparison.OrdinalIgnoreCase)
                    ? SettingsFileKind.Character
                    : SettingsFileKind.User,
                Id = id,
                Path = path,
                Length = info.Length,
                LastWriteTimeUtc = info.LastWriteTimeUtc,
            });
        }

        return result;
    }

    /// <summary>扫描出所有角色级文件，按角色 ID 排序。</summary>
    public static IReadOnlyList<SettingsFile> ScanCharacters(string profileDirectory) =>
        Scan(profileDirectory)
            .Where(f => f.Kind == SettingsFileKind.Character)
            .OrderBy(f => f.Id)
            .ToList();

    /// <summary>扫描出所有账号级文件，按账号 ID 排序。</summary>
    public static IReadOnlyList<SettingsFile> ScanUsers(string profileDirectory) =>
        Scan(profileDirectory)
            .Where(f => f.Kind == SettingsFileKind.User)
            .OrderBy(f => f.Id)
            .ToList();
}
