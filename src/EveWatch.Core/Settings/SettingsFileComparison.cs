using System.IO;
using System.Security.Cryptography;

namespace EveWatch.Core.Settings;

/// <summary>设置文件内容比较（用于预览差异与「跳过已一致」）。</summary>
public static class SettingsFileComparison
{
    /// <summary>两个文件内容是否完全相同（MD5）。任一不存在或读取失败时返回 false。</summary>
    public static bool AreIdentical(string left, string right)
    {
        try
        {
            if (!File.Exists(left) || !File.Exists(right))
            {
                return false;
            }

            using FileStream a = File.OpenRead(left);
            using FileStream b = File.OpenRead(right);
            using MD5 md5 = MD5.Create();
            return md5.ComputeHash(a).AsSpan().SequenceEqual(md5.ComputeHash(b));
        }
        catch (Exception)
        {
            return false;
        }
    }
}
