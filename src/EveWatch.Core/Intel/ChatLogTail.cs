using System.IO;
using System.Text;

namespace EveWatch.Core.Intel;

/// <summary>
/// 按字节偏移增量读取一个聊天日志文件。
/// 使用共享读打开，不锁住游戏正在写的文件；文件被截断或轮转时自动从头读取。
/// </summary>
public sealed class ChatLogTail
{
    private readonly string _path;
    private readonly Encoding _encoding;
    private Decoder _decoder;
    private long _offset;
    private string _partial = string.Empty;

    public ChatLogTail(string path, bool startAtEnd)
    {
        _path = path;
        _encoding = ChatLogText.DetectFromFile(path);
        _decoder = _encoding.GetDecoder();
        _offset = startAtEnd && File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    public string Path => _path;

    public long Offset => _offset;

    /// <summary>读取自上次调用以来新增的完整行；没有新增时返回空数组。</summary>
    public IReadOnlyList<string> ReadNewLines()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var length = new FileInfo(_path).Length;
            if (length < _offset)
            {
                _offset = 0;
                _partial = string.Empty;
                _decoder = _encoding.GetDecoder();
            }

            if (length == _offset)
            {
                return [];
            }

            using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            stream.Seek(_offset, SeekOrigin.Begin);

            var remaining = length - _offset;
            if (remaining <= 0 || remaining > int.MaxValue)
            {
                return [];
            }

            var buffer = new byte[remaining];
            var total = 0;

            while (total < buffer.Length)
            {
                var read = stream.Read(buffer, total, buffer.Length - total);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            var startedAtZero = _offset == 0;
            _offset += total;

            var charCount = _decoder.GetCharCount(buffer, 0, total);
            var chars = new char[charCount];
            _decoder.GetChars(buffer, 0, total, chars, 0);

            var text = new string(chars);
            if (startedAtZero)
            {
                text = text.TrimStart('\uFEFF');
            }

            text = _partial + text;
            _partial = string.Empty;

            if (!text.EndsWith('\n'))
            {
                var lastBreak = text.LastIndexOf('\n');
                if (lastBreak < 0)
                {
                    _partial = text;
                    return [];
                }

                _partial = text[(lastBreak + 1)..];
                text = text[..lastBreak];
            }

            return text
                .Split('\n')
                .Select(line => line.TrimEnd('\r').TrimStart('\uFEFF'))
                .Where(line => line.Length > 0)
                .ToList();
        }
        catch
        {
            // 文件暂时不可读时保留偏移，下一拍重试
            return [];
        }
    }
}
