namespace EveWatch.Core.Detect;

/// <summary>一个命中块（对应本地列表里的一行成员）。</summary>
public struct HitBlock
{
    /// <summary>颜色标签，1 起，对应颜色库中的序号 + 1。</summary>
    public int Label;

    public int X;

    public int Y;

    public int W;

    public int H;

    /// <summary>块内命中颜色的像素数。</summary>
    public int Pixels;
}

/// <summary>
/// 一次判定的结果。
/// <para>对象由调用方持有并反复复用，每次 <c>Detect</c> 会先清空再填充，运行期不再分配。</para>
/// </summary>
public sealed class DetectResult
{
    private const int MaxHitRects = 64;

    /// <summary>命中块的外接矩形，最多 <see cref="MaxHitRects"/> 个。</summary>
    public readonly HitBlock[] Hits = new HitBlock[MaxHitRects];

    /// <summary>已写入 <see cref="Hits"/> 的数量。</summary>
    public int HitCount;

    /// <summary>各颜色标签的命中块数，下标即标签值。</summary>
    public readonly int[] LabelCounts = new int[256];

    public int Width;

    public int Height;

    public long TimestampMs;

    /// <summary>纵向命中区间总数（过滤前）。用于分辨漏检发生在分段还是过滤。</summary>
    public int SegmentsSeen;

    /// <summary>因尺寸低于下限被丢弃的区间数。</summary>
    public int RejectedSmall;

    /// <summary>因命中像素数不足被丢弃的区间数。</summary>
    public int RejectedPixels;

    public bool Any => HitCount > 0;

    /// <summary>清空上一轮结果。</summary>
    public void Reset(int width, int height, long timestampMs)
    {
        Width = width;
        Height = height;
        TimestampMs = timestampMs;
        HitCount = 0;
        SegmentsSeen = 0;
        RejectedSmall = 0;
        RejectedPixels = 0;

        Array.Clear(Hits, 0, Hits.Length);
        Array.Clear(LabelCounts, 0, LabelCounts.Length);
    }

    public void Add(int label, int x, int y, int w, int h, int pixels)
    {
        if ((uint)label < (uint)LabelCounts.Length)
        {
            LabelCounts[label]++;
        }

        if (HitCount >= MaxHitRects)
        {
            return;
        }

        Hits[HitCount++] = new HitBlock
        {
            Label = label,
            X = x,
            Y = y,
            W = w,
            H = h,
            Pixels = pixels,
        };
    }

    /// <summary>命中块总数（含超出 <see cref="MaxHitRects"/> 未记录矩形的部分）。</summary>
    public int TotalHits
    {
        get
        {
            var total = 0;
            for (var i = 0; i < LabelCounts.Length; i++)
            {
                total += LabelCounts[i];
            }

            return total;
        }
    }
}
