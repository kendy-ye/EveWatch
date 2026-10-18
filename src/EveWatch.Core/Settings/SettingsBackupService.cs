using System.Globalization;
using System.IO;
using System.Text;

namespace EveWatch.Core.Settings;

/// <summary>备份目录中的一个文件。</summary>
public sealed record BackupFile(string FileName, long Length, DateTime LastWriteTimeUtc);

/// <summary>一个备份点（某 Profile 在某时刻的一批文件）。</summary>
public sealed record BackupEntry(
    string Directory,
    SettingsProfile Profile,
    DateTime Timestamp,
    IReadOnlyList<BackupFile> Files);

/// <summary>
/// 设置备份的创建 / 浏览 / 还原。
/// 目录结构：<c>&lt;root&gt;\&lt;install&gt;_&lt;profile&gt;\&lt;yyyyMMdd_HHmmss&gt;\core_*.dat</c>。
/// </summary>
public sealed class SettingsBackupService
{
    // 到毫秒：避免同一秒内二次备份命中同一目录、静默覆盖上一个备份点。
    private const string TimestampFormat = "yyyyMMdd_HHmmss_fff";

    public SettingsBackupService(string? root = null) => Root = root ?? DefaultRoot;

    /// <summary>默认根目录：<c>%APPDATA%\EveSync\SettingsBackups</c>。</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EveWatch",
        "SettingsBackups");

    public string Root { get; }

    /// <summary>把一组文件备份到新的时间戳目录，返回备份目录。</summary>
    public string CreateBackup(SettingsProfile profile, IEnumerable<SettingsFile> files, DateTime? timestamp = null)
    {
        string directory = Path.Combine(
            Root,
            ProfileFolderName(profile),
            (timestamp ?? DateTime.Now).ToString(TimestampFormat));

        Directory.CreateDirectory(directory);

        foreach (SettingsFile file in files)
        {
            File.Copy(file.Path, Path.Combine(directory, file.FileName), overwrite: true);
        }

        return directory;
    }

    /// <summary>列出某 Profile 的所有备份点（按时间倒序）。</summary>
    public IReadOnlyList<BackupEntry> ListBackups(SettingsProfile profile)
    {
        var result = new List<BackupEntry>();
        string profileRoot = Path.Combine(Root, ProfileFolderName(profile));

        if (!Directory.Exists(profileRoot))
        {
            return result;
        }

        foreach (string directory in Directory.EnumerateDirectories(profileRoot))
        {
            string name = Path.GetFileName(directory);
            DateTime timestamp;
            if (!DateTime.TryParseExact(name, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp)
                && !DateTime.TryParseExact(name, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp))
            {
                timestamp = Directory.GetLastWriteTime(directory);
            }

            var files = new List<BackupFile>();
            foreach (string file in Directory.EnumerateFiles(directory, "core_*.dat"))
            {
                var info = new FileInfo(file);
                files.Add(new BackupFile(info.Name, info.Length, info.LastWriteTimeUtc));
            }

            if (files.Count > 0)
            {
                result.Add(new BackupEntry(directory, profile, timestamp, files));
            }
        }

        return result.OrderByDescending(e => e.Timestamp).ToList();
    }

    /// <summary>把备份点里的所有文件还原回 Profile 目录，返回还原数量。</summary>
    public int Restore(BackupEntry entry, string profileDirectory)
    {
        int restored = 0;

        foreach (BackupFile file in entry.Files)
        {
            try
            {
                File.Copy(
                    Path.Combine(entry.Directory, file.FileName),
                    Path.Combine(profileDirectory, file.FileName),
                    overwrite: true);
                restored++;
            }
            catch (Exception)
            {
                // 单项还原失败不中断其余项。
            }
        }

        return restored;
    }

    /// <summary>与创建时一致的 Profile 目录名。</summary>
    public static string ProfileFolderName(SettingsProfile profile) => SafeName($"{profile.InstallId}_{profile.Name}");

    private static string SafeName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            builder.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        }

        return builder.ToString();
    }
}
