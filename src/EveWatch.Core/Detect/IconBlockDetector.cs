using EveWatch.Core.Palette;

namespace EveWatch.Core.Detect;

/// <summary>
/// 图标块判定。
/// <para>
/// 管线：查表分类 → 纵向命中区间 → 区间内横向范围 → 过滤。
/// 全程在预分配缓冲上进行，运行期不产生托管分配。
/// </para>
/// <para>
/// <b>计数单位是「纵向命中区间」，不是「连通区域」。</b>
/// 这条 ROI 沿本地成员列表的图标列竖向框选，成员按行等距排列、一行一个图标，
/// 所以「竖直方向上有几段命中区间」就等于「有几行成员被命中」。
/// </para>
/// <para>
/// 为什么不用连通区域：EVE 的站位图标不是实心方块，而是若干互不相连的同色形状
/// （准星式边框加内部符号，实测一个图标会被切成 4 段）；而且不同 standing 的符号形状、
/// 不同 UI 缩放下的尺寸都不一样。连通区域这类做法一旦引入"块该多大、长宽比该多少"的判据，
/// 就必然绑定到某种具体形状上，换个形状就失效。纵向区间只依赖"这一行有没有命中像素"，与形状无关。
/// </para>
/// <para>本类型不是线程安全的，每个轮询线程各持一个实例。</para>
/// </summary>
public sealed class IconBlockDetector
{
    private byte[] _label = [];

    /// <summary>判定一帧。</summary>
    /// <param name="bgra">ROI 像素，行紧排，步长 = <paramref name="width"/> × 4，顺序 B、G、R、A。</param>
    /// <param name="width">ROI 宽。</param>
    /// <param name="height">ROI 高。</param>
    /// <param name="lut">当前颜色查找表。</param>
    /// <param name="options">判定参数。</param>
    /// <param name="result">结果对象，由调用方复用。</param>
    public void Detect(
        byte[] bgra,
        int width,
        int height,
        ColorLut lut,
        DetectOptions options,
        DetectResult result)
    {
        result.Reset(width, height, Environment.TickCount64);

        if (width <= 0 || height <= 0)
        {
            return;
        }

        EnsureBuffers(width * height);
        Classify(bgra, width, height, lut);

        for (var label = 1; label <= lut.ColorCount; label++)
        {
            ScanLabel(label, width, height, options, result);
        }
    }

    // ---------------------------------------------------------------- 步骤 1：查表分类

    private void Classify(byte[] bgra, int width, int height, ColorLut lut)
    {
        var map = lut.Map;
        var label = _label;
        var count = width * height;

        for (int i = 0, p = 0; i < count; i++, p += 4)
        {
            var index = ((bgra[p] >> 4) << 8) | ((bgra[p + 1] >> 4) << 4) | (bgra[p + 2] >> 4);
            label[i] = map[index];
        }
    }

    // ---------------------------------------------------------------- 步骤 2：纵向命中区间

    private void ScanLabel(int label, int width, int height, DetectOptions options, DetectResult result)
    {
        var y = 0;

        while (y < height)
        {
            if (!RowHasLabel(label, y, width))
            {
                y++;
                continue;
            }

            var y0 = y;
            var y1 = y;
            var gap = 0;

            // 区间在连续命中行上延伸，容许 RowGap 个空洞行（图标内部的空白）
            while (y < height)
            {
                if (RowHasLabel(label, y, width))
                {
                    y1 = y;
                    gap = 0;
                }
                else if (++gap > options.RowGap)
                {
                    break;
                }

                y++;
            }

            result.SegmentsSeen++;
            EmitSegment(label, y0, y1, width, options, result);
        }
    }

    private bool RowHasLabel(int label, int y, int width) =>
        _label.AsSpan(y * width, width).IndexOf((byte)label) >= 0;

    // ---------------------------------------------------------------- 步骤 3：区间内的横向范围与过滤

    private void EmitSegment(int label, int y0, int y1, int width, DetectOptions options, DetectResult result)
    {
        var pixels = 0;
        var minX = width;
        var maxX = -1;

        for (var y = y0; y <= y1; y++)
        {
            var rowStart = y * width;

            for (var x = 0; x < width; x++)
            {
                if (_label[rowStart + x] != label)
                {
                    continue;
                }

                pixels++;

                if (x < minX)
                {
                    minX = x;
                }

                if (x > maxX)
                {
                    maxX = x;
                }
            }
        }

        if (maxX < 0)
        {
            return;
        }

        var w = maxX - minX + 1;
        var h = y1 - y0 + 1;

        if (w < options.MinWidth || h < options.MinHeight)
        {
            result.RejectedSmall++;
            return;
        }

        if (pixels < options.MinPixels)
        {
            result.RejectedPixels++;
            return;
        }

        result.Add(label, minX, y0, w, h, pixels);
    }

    // ---------------------------------------------------------------- 缓冲管理

    private void EnsureBuffers(int pixels)
    {
        if (_label.Length < pixels)
        {
            _label = new byte[pixels];
        }
    }
}
