using System.IO;

namespace EveWatch.Core.Settings;

/// <summary>单个目标的克隆结果。</summary>
public sealed record CloneItemResult(
    SettingsFileKind Kind,
    long TargetId,
    string TargetPath,
    string TargetFileName,
    bool Success,
    bool Skipped,
    string? Error,
    string? BackupPath);

/// <summary>一次克隆的整体结果。</summary>
public sealed class CloneResult
{
    public required string BackupDirectory { get; init; }

    public required IReadOnlyList<CloneItemResult> Items { get; init; }

    public bool Success => Items.Count > 0 && Items.All(i => i.Success);

    public int SucceededCount => Items.Count(i => i.Success);

    /// <summary>实际覆盖的文件数。</summary>
    public int CopiedCount => Items.Count(i => i.Success && !i.Skipped);

    /// <summary>因内容已一致而跳过的文件数。</summary>
    public int SkippedCount => Items.Count(i => i.Skipped);
}

/// <summary>
/// 设置克隆：备份目标 → 用模板覆盖 → 哈希校验；内容已一致的自动跳过。
/// 并支持从本次备份还原。调用方需先确认客户端已关闭。
/// </summary>
public sealed class SettingsCloner
{
    public SettingsCloner(string? backupRoot = null) => Backups = new SettingsBackupService(backupRoot);

    /// <summary>备份服务（供「备份管理」浏览/还原历史备份）。</summary>
    public SettingsBackupService Backups { get; }

    /// <summary>用 <paramref name="source"/> 覆盖所有 <paramref name="targets"/>，逐个备份并校验。</summary>
    public CloneResult Clone(SettingsProfile profile, SettingsFile source, IReadOnlyList<SettingsFile> targets)
    {
        List<SettingsFile> effective = targets
            .Where(t => !string.Equals(t.Path, source.Path, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (effective.Count == 0)
        {
            return new CloneResult { BackupDirectory = string.Empty, Items = Array.Empty<CloneItemResult>() };
        }

        // 内容已一致的目标不覆盖、不备份。
        var identical = effective
            .Where(t => SettingsFileComparison.AreIdentical(source.Path, t.Path))
            .ToHashSet();

        List<SettingsFile> toCopy = effective.Where(t => !identical.Contains(t)).ToList();

        string backupDirectory = string.Empty;
        if (toCopy.Count > 0)
        {
            try
            {
                backupDirectory = Backups.CreateBackup(profile, toCopy);
            }
            catch (Exception ex)
            {
                // 备份是安全前提：备份失败则一个目标都不覆盖，整体转为失败结果。
                return new CloneResult
                {
                    BackupDirectory = string.Empty,
                    Items = effective.Select(t => new CloneItemResult(
                        t.Kind, t.Id, t.Path, t.FileName,
                        Success: false, Skipped: false, Error: $"备份失败：{ex.Message}", BackupPath: null)).ToList(),
                };
            }
        }

        var items = new List<CloneItemResult>();

        foreach (SettingsFile target in effective)
        {
            if (identical.Contains(target))
            {
                items.Add(new CloneItemResult(
                    target.Kind, target.Id, target.Path, target.FileName,
                    Success: true, Skipped: true, Error: null, BackupPath: null));
                continue;
            }

            string backupPath = Path.Combine(backupDirectory, target.FileName);

            try
            {
                File.Copy(source.Path, target.Path, overwrite: true);

                bool verified = SettingsFileComparison.AreIdentical(source.Path, target.Path);
                items.Add(new CloneItemResult(
                    target.Kind, target.Id, target.Path, target.FileName,
                    Success: verified,
                    Skipped: false,
                    Error: verified ? null : "哈希校验不一致",
                    BackupPath: backupPath));
            }
            catch (Exception ex)
            {
                items.Add(new CloneItemResult(
                    target.Kind, target.Id, target.Path, target.FileName,
                    Success: false, Skipped: false, Error: ex.Message, BackupPath: null));
            }
        }

        return new CloneResult { BackupDirectory = backupDirectory, Items = items };
    }

    /// <summary>把本次克隆的所有成功项从备份还原回去，返回还原数量。</summary>
    public int Restore(CloneResult result)
    {
        int restored = 0;

        foreach (CloneItemResult item in result.Items)
        {
            if (!item.Success || item.BackupPath is null || !File.Exists(item.BackupPath))
            {
                continue;
            }

            try
            {
                File.Copy(item.BackupPath, item.TargetPath, overwrite: true);
                restored++;
            }
            catch (Exception)
            {
                // 单项还原失败不中断其余项。
            }
        }

        return restored;
    }
}
