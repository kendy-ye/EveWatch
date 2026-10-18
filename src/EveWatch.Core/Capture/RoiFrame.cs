namespace EveWatch.Core.Capture;

/// <summary>窗口内容区坐标系下的矩形（左上角为原点）。</summary>
public readonly record struct RoiRect(int X, int Y, int W, int H)
{
    public bool IsValid => W > 0 && H > 0;

    public override string ToString() => $"{X},{Y},{W},{H}";

    /// <summary>解析 <c>x,y,w,h</c> 形式的字符串；失败返回 false。</summary>
    public static bool TryParse(string? text, out RoiRect roi)
    {
        roi = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
        {
            return false;
        }

        var v = new int[4];
        for (var i = 0; i < 4; i++)
        {
            if (!int.TryParse(parts[i], out v[i]))
            {
                return false;
            }
        }

        roi = new RoiRect(v[0], v[1], v[2], v[3]);
        return roi.IsValid;
    }
}

/// <summary>
/// 一次 ROI 采集的产物。
/// <para>
/// <see cref="Bgra"/> 由采集会话持有并复用，**下一次采集即被覆盖**，调用方须立即消费。
/// 行紧排，步长 = <c>Width * 4</c>，像素顺序为 B、G、R、A。
/// </para>
/// </summary>
public sealed class RoiFrame
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required byte[] Bgra { get; init; }

    public required long TimestampMs { get; init; }
}
