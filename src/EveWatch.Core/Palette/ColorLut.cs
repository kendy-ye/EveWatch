namespace EveWatch.Core.Palette;

/// <summary>查找表的构建参数。</summary>
/// <param name="HueToleranceDegrees">色相容差（度）。像素色相与颜色库某项的差值在此范围内即算匹配。</param>
/// <param name="MinSaturation">彩色匹配的最低饱和度。低于此值的像素不参与色相匹配。</param>
/// <param name="MinValue">明度下限。低于此值的像素视为背景，不参与分类。</param>
/// <param name="NeutralSaturation">饱和度上限。低于此值的颜色归为中性色（灰白系），按明度匹配而不是色相。</param>
/// <param name="NeutralValueTolerance">中性色的明度容差。避免把白色高光误判成浅灰。</param>
public readonly record struct LutTuning(
    double HueToleranceDegrees,
    double MinSaturation,
    double MinValue,
    double NeutralSaturation,
    double NeutralValueTolerance);

/// <summary>
/// 像素到颜色标签的查找表。
/// <para>
/// 索引 = <c>(B&gt;&gt;4)&lt;&lt;8 | (G&gt;&gt;4)&lt;&lt;4 | (R&gt;&gt;4)</c>，共 4096 项、每项 1 字节，合计 4 KB。
/// 值为 0 表示"其它颜色"，1..N 表示颜色库中第 1..N 项。
/// </para>
/// <para>
/// 判定依据是 HSV 而不是原始 RGB。EVE 的站位图标带透明度，叠在不同背景上的亮度差别很大，
/// 但色相基本不变；用 RGB 欧氏距离会把同一颜色的图标分成"命中"和"漏报"两种结果。
/// 例如实测的友军蓝图标是 <c>#032076</c>，而表示该 standing 的界面蓝通常是 <c>#3C78DC</c>，
/// 两者 RGB 距离 0.30，色相只差 7°。
/// </para>
/// <para>表在颜色库变更时一次性重建；判定时每个像素只做一次整数查表。</para>
/// </summary>
public sealed class ColorLut
{
    /// <summary>查找表项数（每通道量化到 4 bit）。</summary>
    public const int Size = 4096;

    private readonly byte[] _map = new byte[Size];

    private PaletteColor[] _colors = [];
    private double[] _hue = [];
    private double[] _sat = [];
    private double[] _val = [];
    private bool[] _neutral = [];

    /// <summary>查找表本体，索引方式见类型说明。</summary>
    public byte[] Map => _map;

    public int ColorCount => _colors.Length;

    public PaletteColor? ColorAt(int index) =>
        index >= 0 && index < _colors.Length ? _colors[index] : null;

    /// <summary>标签（1 起）对应的颜色。</summary>
    public PaletteColor? ColorOfLabel(int label) => ColorAt(label - 1);

    /// <summary>标签（1 起）对应的颜色名；未知标签返回 <c>null</c>。</summary>
    public string? NameOfLabel(int label) =>
        label >= 1 && label <= _colors.Length ? _colors[label - 1].Name : null;

    /// <summary>按颜色库重建查找表。</summary>
    /// <remarks>
    /// 每个格子要对整格做穷举采样后再定标签，不能只看格子中心。
    /// 只看中心会出系统性错误：在暗色、低饱和的区域，R/G/B 差几个数色相就会翻好几个色区。
    /// 实测的例子——像素 <c>#3A2F25</c> 的实际色相是 28.6°（暗褐），而它所在格的中心是 (56,40,40)，
    /// 算出来的色相是 0°（纯红）；于是一整格暗褐像素都被贴上"敌对红"，
    /// 人眼看不到红色却触发报警。要求整格一致后，这类格子判定为不匹配。
    /// </remarks>
    public void Rebuild(IReadOnlyList<PaletteColor> colors, LutTuning tuning)
    {
        Array.Clear(_map, 0, _map.Length);

        _colors = [.. colors];
        var count = _colors.Length;

        _hue = new double[count];
        _sat = new double[count];
        _val = new double[count];
        _neutral = new bool[count];

        for (var i = 0; i < count; i++)
        {
            if (!TryParseHex(_colors[i].Hex, out var r, out var g, out var b))
            {
                r = g = b = 0;
            }

            ToHsv(r, g, b, out _hue[i], out _sat[i], out _val[i]);
            _neutral[i] = _sat[i] < tuning.NeutralSaturation;
        }

        if (count == 0)
        {
            return;
        }

        for (var qb = 0; qb < 16; qb++)
        {
            for (var qg = 0; qg < 16; qg++)
            {
                for (var qr = 0; qr < 16; qr++)
                {
                    var label = ResolveCell(qr, qg, qb, tuning, count);
                    if (label > 0)
                    {
                        _map[(qb << 8) | (qg << 4) | qr] = (byte)label;
                    }
                }
            }
        }
    }

    /// <summary>采样点落在背景档（明度低于下限）时的标记。</summary>
    private const int BackgroundLabel = -2;

    /// <summary>
    /// 判定一个量化格属于哪个颜色，返回标签（1 起）；0 表示整格无法一致判定，按不匹配处理。
    /// <para>
    /// 穷举格内 27 个格点：只要有任意一点落在背景档，就让整格作废——说明这一格里
    /// 同时存在黑底与命中色，置信度不足。若各点一致指向同一颜色，才采用该标签。
    /// </para>
    /// </summary>
    private int ResolveCell(int qr, int qg, int qb, LutTuning tuning, int count)
    {
        Span<int> rs = stackalloc int[3];
        Span<int> gs = stackalloc int[3];
        Span<int> bs = stackalloc int[3];

        rs[0] = qr << 4;
        rs[1] = (qr << 4) | 0x08;
        rs[2] = (qr << 4) | 0x0F;

        gs[0] = qg << 4;
        gs[1] = (qg << 4) | 0x08;
        gs[2] = (qg << 4) | 0x0F;

        bs[0] = qb << 4;
        bs[1] = (qb << 4) | 0x08;
        bs[2] = (qb << 4) | 0x0F;

        var agreed = -1;
        var started = false;

        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                for (var k = 0; k < 3; k++)
                {
                    var resolved = ResolvePoint(rs[i], gs[j], bs[k], tuning, count);

                    if (resolved == BackgroundLabel)
                    {
                        return 0;
                    }

                    if (!started)
                    {
                        agreed = resolved;
                        started = true;
                    }
                    else if (resolved != agreed)
                    {
                        return 0;
                    }
                }
            }
        }

        return agreed + 1;
    }

    /// <summary>把一个具体 RGB 解析成颜色库下标；-1 表示不匹配任何颜色，<see cref="BackgroundLabel"/> 表示背景。</summary>
    private int ResolvePoint(int r, int g, int b, LutTuning tuning, int count)
    {
        ToHsv((byte)r, (byte)g, (byte)b, out var hue, out var sat, out var val);

        if (val < tuning.MinValue)
        {
            return BackgroundLabel;
        }

        var isNeutral = sat < tuning.NeutralSaturation;
        var best = -1;
        var bestScore = double.MaxValue;

        for (var i = 0; i < count; i++)
        {
            double score;

            if (isNeutral || _neutral[i])
            {
                // 中性色只在同为中性的项里按明度接近度匹配
                if (!isNeutral || !_neutral[i])
                {
                    continue;
                }

                score = Math.Abs(val - _val[i]);
                if (score > tuning.NeutralValueTolerance)
                {
                    continue;
                }
            }
            else
            {
                // 有一点颜色但不够鲜艳：不参与任何匹配。
                // 界面底纹大多是这种"发灰的红/橙/蓝"，真实声望图标的饱和度在 0.9 以上。
                if (sat < tuning.MinSaturation)
                {
                    continue;
                }

                var delta = Math.Abs(hue - _hue[i]);
                if (delta > 180)
                {
                    delta = 360 - delta;
                }

                if (delta > tuning.HueToleranceDegrees)
                {
                    continue;
                }

                score = delta;
            }

            if (score < bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        return best;
    }

    /// <summary>RGB 转 HSV；<paramref name="hue"/> 单位为度（0..360），其余为 0..1。</summary>
    public static void ToHsv(byte r, byte g, byte b, out double hue, out double sat, out double val)
    {
        double rd = r / 255.0;
        double gd = g / 255.0;
        double bd = b / 255.0;

        var max = Math.Max(rd, Math.Max(gd, bd));
        var min = Math.Min(rd, Math.Min(gd, bd));
        var delta = max - min;

        val = max;
        sat = max <= 0 ? 0 : delta / max;

        if (delta <= 0)
        {
            hue = 0;
            return;
        }

        if (max == rd)
        {
            hue = 60 * (((gd - bd) / delta) % 6);
        }
        else if (max == gd)
        {
            hue = 60 * ((((bd - rd) / delta) + 2));
        }
        else
        {
            hue = 60 * ((((rd - gd) / delta) + 4));
        }

        if (hue < 0)
        {
            hue += 360;
        }
    }

    /// <summary>解析 <c>#RGB</c> / <c>#RRGGBB</c> / <c>RRGGBB</c>。</summary>
    public static bool TryParseHex(string? hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        var s = hex.Trim();
        if (s.StartsWith('#'))
        {
            s = s[1..];
        }

        if (s.Length == 3)
        {
            s = $"{s[0]}{s[0]}{s[1]}{s[1]}{s[2]}{s[2]}";
        }

        if (s.Length != 6 || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var value))
        {
            return false;
        }

        r = (byte)((value >> 16) & 0xFF);
        g = (byte)((value >> 8) & 0xFF);
        b = (byte)(value & 0xFF);
        return true;
    }

    /// <summary>把 RGB 三元组格式化为 <c>#RRGGBB</c>。</summary>
    public static string ToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";
}
