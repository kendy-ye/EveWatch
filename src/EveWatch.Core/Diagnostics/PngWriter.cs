using System.IO.Compression;
using System.Text;

namespace EveWatch.Core.Diagnostics;

/// <summary>
/// 极简 PNG 编码器：把 BGRA 像素写成 24 位真彩 PNG。
/// <para>
/// 自实现是为了让自检工具在没有任何图像库依赖的情况下也能输出可查看的截图。
/// 只支持无损、无隔行、过滤器全 0 的最简形式，够用即可。
/// </para>
/// </summary>
public static class PngWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>把 BGRA 像素（行紧排，步长 = width × 4）写成 PNG 文件。</summary>
    public static void WriteBgra(string path, byte[] bgra, int width, int height)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "尺寸必须为正数");
        }

        if (bgra.Length < width * height * 4)
        {
            throw new ArgumentException("像素缓冲小于声明的尺寸", nameof(bgra));
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var file = File.Create(path);
        file.Write(Signature, 0, Signature.Length);

        var header = new byte[13];
        WriteInt32BigEndian(header, 0, width);
        WriteInt32BigEndian(header, 4, height);
        header[8] = 8;    // 位深
        header[9] = 2;    // 颜色类型：真彩 RGB
        header[10] = 0;   // 压缩方法
        header[11] = 0;   // 过滤方法
        header[12] = 0;   // 非隔行
        WriteChunk(file, "IHDR", header);

        WriteChunk(file, "IDAT", BuildImageData(bgra, width, height));
        WriteChunk(file, "IEND", []);
    }

    private static byte[] BuildImageData(byte[] bgra, int width, int height)
    {
        var stride = width * 3;
        var raw = new byte[(stride + 1) * height];
        var offset = 0;

        for (var y = 0; y < height; y++)
        {
            raw[offset++] = 0;   // 过滤器：none
            var source = y * width * 4;

            for (var x = 0; x < width; x++)
            {
                var pixel = source + (x * 4);
                raw[offset++] = bgra[pixel + 2];   // R
                raw[offset++] = bgra[pixel + 1];   // G
                raw[offset++] = bgra[pixel];       // B
            }
        }

        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw, 0, raw.Length);
        }

        // zlib 包装：2 字节头 + deflate 数据 + 4 字节 Adler-32
        var zlib = new byte[compressed.Length + 6];
        zlib[0] = 0x78;
        zlib[1] = 0x9C;

        compressed.Position = 0;
        compressed.ReadExactly(zlib.AsSpan(2, (int)compressed.Length));

        WriteInt32BigEndian(zlib, zlib.Length - 4, (int)Adler32(raw));
        return zlib;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        WriteInt32BigEndian(length, 0, data.Length);
        stream.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes, 0, typeBytes.Length);
        stream.Write(data, 0, data.Length);

        var crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        WriteInt32BigEndian(crcBytes, 0, (int)crc);
        stream.Write(crcBytes);
    }

    private static void WriteInt32BigEndian(Span<byte> buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint modulus = 65521;
        uint a = 1;
        uint b = 0;

        foreach (var value in data)
        {
            a = (a + value) % modulus;
            b = (b + a) % modulus;
        }

        return (b << 16) | a;
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;

        foreach (var value in type)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];

        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
