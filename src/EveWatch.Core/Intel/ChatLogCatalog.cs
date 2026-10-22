using System.IO;
using System.Text;
using Microsoft.Win32;

namespace EveWatch.Core.Intel;

public enum ChatChannelKind
{
    Local,
    Player,
    Fleet,
    Unknown,
}

/// <summary>一个已识别的聊天日志文件。</summary>
public sealed record ChatLogFile(
    string Path,
    long CharacterId,
    string CharacterName,
    string ChannelId,
    string ChannelName,
    ChatChannelKind Kind,
    DateTime LastWriteTime,
    long Length);

/// <summary>频道探测结果中的一项。</summary>
public sealed record ChatChannelInfo(
    long CharacterId,
    string CharacterName,
    string ChannelId,
    string ChannelName,
    ChatChannelKind Kind,
    DateTime LastActive,
    string FilePath);

/// <summary>
/// 扫描 EVE 聊天日志目录，读取文件头建立角色与频道索引。
/// 只读取每个文件的头部，不加载消息正文。
/// </summary>
public sealed class ChatLogCatalog
{
    private const int MaxHeaderBytes = 4096;

    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    private sealed record CacheEntry(DateTime LastWriteTimeUtc, long Length, ChatLogFile? File);

    private static readonly object CandidateSync = new();
    private static IReadOnlyList<string>? _candidateCache;
    private static DateTime _candidateCacheAt;

    /// <summary>常见日志目录候选，按优先级排列。结果缓存 30 秒，避免反复扫描磁盘。</summary>
    public static IReadOnlyList<string> CandidateDirectories()
    {
        lock (CandidateSync)
        {
            if (_candidateCache is not null
                && (DateTime.UtcNow - _candidateCacheAt).TotalSeconds < 30)
            {
                return _candidateCache;
            }
        }

        var candidates = BuildCandidates();

        lock (CandidateSync)
        {
            _candidateCache = candidates;
            _candidateCacheAt = DateTime.UtcNow;
        }

        return candidates;
    }

    private static IReadOnlyList<string> BuildCandidates()
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? root, params string[] parts)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            var path = System.IO.Path.Combine([root, .. parts]);
            if (seen.Add(path))
            {
                candidates.Add(path);
            }
        }

        void AddDocuments(string? documents)
        {
            Add(documents, "EVE", "logs", "Chatlogs");
        }

        // 1. 系统「文档」已知文件夹：会跟随 OneDrive 或手动重定向，优先级最高。
        AddDocuments(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

        // 2. 注册表里的文档位置：部分重定向只更新这里。
        AddDocuments(ReadDocumentsFolder("User Shell Folders"));
        AddDocuments(ReadDocumentsFolder("Shell Folders"));

        // 3. 文档未重定向时仍可能在用户目录下。
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add(profile, "Documents", "EVE", "logs", "Chatlogs");
        Add(profile, "OneDrive", "Documents", "EVE", "logs", "Chatlogs");
        Add(profile, "OneDrive", "文档", "EVE", "logs", "Chatlogs");

        // 4. 覆盖 OneDrive 重定向或企业版 OneDrive 的情况。
        foreach (var name in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var root = Environment.GetEnvironmentVariable(name);
            Add(root, "Documents", "EVE", "logs", "Chatlogs");
            Add(root, "文档", "EVE", "logs", "Chatlogs");
            Add(root, "EVE", "logs", "Chatlogs");
        }

        // 5. 固定磁盘兜底：个人文档被移到 D: 等非系统盘时仍能找到。
        var user = Environment.UserName;
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady)
                {
                    continue;
                }

                var root = drive.RootDirectory.FullName;
                Add(root, "Documents", "EVE", "logs", "Chatlogs");
                Add(root, "文档", "EVE", "logs", "Chatlogs");
                Add(root, "我的文档", "EVE", "logs", "Chatlogs");
                Add(root, "Users", user, "Documents", "EVE", "logs", "Chatlogs");
                Add(root, "EVE", "logs", "Chatlogs");

                foreach (var oneDrive in Directory.EnumerateDirectories(root, "OneDrive*"))
                {
                    Add(oneDrive, "Documents", "EVE", "logs", "Chatlogs");
                    Add(oneDrive, "文档", "EVE", "logs", "Chatlogs");
                    Add(oneDrive, "我的文档", "EVE", "logs", "Chatlogs");
                }
            }
            catch
            {
                // 无权访问的磁盘跳过
            }
        }

        return candidates;
    }

    private static string? ReadDocumentsFolder(string keyName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Explorer\{keyName}");

            var value = key?.GetValue("Personal") as string;
            return string.IsNullOrWhiteSpace(value)
                ? null
                : Environment.ExpandEnvironmentVariables(value);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>取第一个包含日志文件的目录；都找不到时返回 null。</summary>
    public static string? FindDirectory(string? overrideDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideDirectory) && Directory.Exists(overrideDirectory))
        {
            return overrideDirectory;
        }

        foreach (var candidate in CandidateDirectories())
        {
            if (HasLogFiles(candidate))
            {
                return candidate;
            }
        }

        // 已知位置都没有时，再到各磁盘的一级目录下找 EVE\logs\Chatlogs，
        // 覆盖“个人文档被手动移到 D:\xxx”这类没有更新系统已知文件夹的情况。
        foreach (var candidate in DiscoverFromDriveRoots())
        {
            if (HasLogFiles(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool HasLogFiles(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                && Directory.EnumerateFiles(directory, "*.txt").Any();
        }
        catch
        {
            // 无权访问时继续下一个候选
            return false;
        }
    }

    private static IEnumerable<string> DiscoverFromDriveRoots()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady)
            {
                continue;
            }

            var root = drive.RootDirectory.FullName;

            string[] folders;
            try
            {
                folders = Directory.GetDirectories(root);
            }
            catch
            {
                continue;
            }

            foreach (var folder in folders)
            {
                yield return System.IO.Path.Combine(folder, "EVE", "logs", "Chatlogs");
            }
        }
    }

    /// <summary>扫描最近若干天的日志文件。缓存只解析新增或变化的文件头。</summary>
    public IReadOnlyList<ChatLogFile> Scan(string directory, int maxAgeDays = 30)
    {
        var result = new List<ChatLogFile>();
        if (!Directory.Exists(directory))
        {
            return result;
        }

        var cutoff = DateTime.Now.AddDays(-Math.Max(1, maxAgeDays));

        IEnumerable<string> paths;
        try
        {
            paths = Directory.EnumerateFiles(directory, "*.txt", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return result;
        }

        lock (_sync)
        {
            foreach (var path in paths)
            {
                FileInfo info;
                try
                {
                    info = new FileInfo(path);
                    if (!info.Exists || info.LastWriteTime < cutoff || info.Length == 0)
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                var cache = _cache.GetValueOrDefault(path);
                if (cache is null
                    || cache.LastWriteTimeUtc != info.LastWriteTimeUtc
                    || cache.Length != info.Length)
                {
                    var parsed = ParseFile(info);

                    // 游戏正在写的文件可能短暂无法打开；不要把失败结果缓存成永久 null，
                    // 下一轮扫描会重试。只有解析成功才记录缓存。
                    if (parsed is null)
                    {
                        continue;
                    }

                    cache = new CacheEntry(info.LastWriteTimeUtc, info.Length, parsed);
                    _cache[path] = cache;
                }

                if (cache.File is not null)
                {
                    result.Add(cache.File with
                    {
                        LastWriteTime = info.LastWriteTime,
                        Length = info.Length,
                    });
                }
            }
        }

        return result;
    }

    /// <summary>按角色列出最近日志中发现的频道，每个频道取最新一条。</summary>
    public static IReadOnlyList<ChatChannelInfo> GetChannels(IEnumerable<ChatLogFile> files)
    {
        return files
            .GroupBy(file => (file.CharacterId, Key: ChannelKey(file)))
            .Select(group => group.OrderByDescending(file => file.LastWriteTime).First())
            .Select(file => new ChatChannelInfo(
                file.CharacterId,
                file.CharacterName,
                file.ChannelId,
                file.ChannelName,
                file.Kind,
                file.LastWriteTime,
                file.Path))
            .OrderBy(info => info.CharacterName, StringComparer.Ordinal)
            .ThenBy(info => info.ChannelName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>取该角色最新的本地频道日志。</summary>
    public static ChatLogFile? FindLatestLocal(IEnumerable<ChatLogFile> files, long characterId) =>
        files
            .Where(file => file.CharacterId == characterId && file.Kind == ChatChannelKind.Local)
            .OrderByDescending(file => file.LastWriteTime)
            .FirstOrDefault();

    /// <summary>
    /// 为监听项定位频道文件。稳定频道优先按 Channel ID；舰队频道按名称跟随最新日志。
    /// </summary>
    public static ChatLogFile? ResolveListenerFile(IEnumerable<ChatLogFile> files, Config.IntelListener listener)
    {
        var candidates = files
            .Where(file => file.CharacterId == listener.CharacterId && file.Kind != ChatChannelKind.Local)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        var channelId = listener.ChannelId?.Trim() ?? string.Empty;
        var channelName = listener.ChannelName?.Trim() ?? string.Empty;
        var fleet = channelId.StartsWith("fleet_", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(channelId) && !fleet)
        {
            var byId = candidates
                .Where(file => string.Equals(file.ChannelId, channelId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastWriteTime)
                .FirstOrDefault();

            if (byId is not null)
            {
                return byId;
            }
        }

        if (!string.IsNullOrEmpty(channelName))
        {
            var byName = candidates
                .Where(file => string.Equals(file.ChannelName, channelName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastWriteTime)
                .FirstOrDefault();

            if (byName is not null)
            {
                return byName;
            }
        }

        if (!string.IsNullOrEmpty(channelId))
        {
            return candidates
                .Where(file => string.Equals(file.ChannelId, channelId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastWriteTime)
                .FirstOrDefault();
        }

        return null;
    }

    public static ChatChannelKind Classify(string channelId)
    {
        if (string.Equals(channelId, "local", StringComparison.OrdinalIgnoreCase))
        {
            return ChatChannelKind.Local;
        }

        if (channelId.StartsWith("fleet_", StringComparison.OrdinalIgnoreCase))
        {
            return ChatChannelKind.Fleet;
        }

        if (channelId.StartsWith("player_", StringComparison.OrdinalIgnoreCase))
        {
            return ChatChannelKind.Player;
        }

        return ChatChannelKind.Unknown;
    }

    private static string ChannelKey(ChatLogFile file)
    {
        // 舰队频道的 fleet_<id> 每次组建都不同，按显示名归并，探测列表只显示一条「舰队」。
        if (file.Kind == ChatChannelKind.Fleet)
        {
            return $"fleet:{file.ChannelName.ToLowerInvariant()}";
        }

        return string.IsNullOrWhiteSpace(file.ChannelId)
            ? file.ChannelName.ToLowerInvariant()
            : file.ChannelId;
    }

    private static ChatLogFile? ParseFile(FileInfo info)
    {
        try
        {
            using var stream = new FileStream(
                info.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var length = (int)Math.Min(MaxHeaderBytes, info.Length);
            var buffer = new byte[length];
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                return null;
            }

            var encoding = ChatLogText.Detect(buffer.AsSpan(0, Math.Min(3, read)));
            var text = encoding.GetString(buffer, 0, read).TrimStart('\uFEFF');
            string? channelId = null;
            string? channelName = null;
            string? listener = null;

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("Channel ID:", StringComparison.OrdinalIgnoreCase))
                {
                    channelId = line["Channel ID:".Length..].Trim();
                }
                else if (line.StartsWith("Channel Name:", StringComparison.OrdinalIgnoreCase))
                {
                    channelName = line["Channel Name:".Length..].Trim();
                }
                else if (line.StartsWith("Listener:", StringComparison.OrdinalIgnoreCase))
                {
                    listener = line["Listener:".Length..].Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(channelName))
            {
                return null;
            }

            var characterId = ParseCharacterId(info.Name);
            if (characterId <= 0)
            {
                return null;
            }

            return new ChatLogFile(
                info.FullName,
                characterId,
                string.IsNullOrWhiteSpace(listener) ? $"#{characterId}" : listener,
                channelId.Trim(),
                channelName.Trim(),
                Classify(channelId.Trim()),
                info.LastWriteTime,
                info.Length);
        }
        catch
        {
            return null;
        }
    }

    private static long ParseCharacterId(string fileName)
    {
        var baseName = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var parts = baseName.Split('_');
        return parts.Length >= 4 && long.TryParse(parts[^1], out var id) ? id : 0;
    }
}
