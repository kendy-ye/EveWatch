using System.IO;
using System.Text;

namespace EveWatch.Core.Intel;

/// <summary>EVE 聊天日志可能是 UTF-8，也可能是带 BOM 的 UTF-16 LE。按 BOM 自动识别。</summary>
internal static class ChatLogText
{
    public static Encoding Detect(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length >= 2 && prefix[0] == 0xFF && prefix[1] == 0xFE)
        {
            return Encoding.Unicode;
        }

        if (prefix.Length >= 2 && prefix[0] == 0xFE && prefix[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode;
        }

        return Encoding.UTF8;
    }

    public static Encoding DetectFromFile(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            Span<byte> prefix = stackalloc byte[3];
            var read = stream.Read(prefix);
            return Detect(prefix[..read]);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }
}
