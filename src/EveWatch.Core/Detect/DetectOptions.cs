using EveWatch.Core.Palette;

namespace EveWatch.Core.Detect;

/// <summary>判定管线的参数。</summary>
public sealed class DetectOptions
{
    /// <summary>命中块的最小宽度（像素），用于滤掉噪点与竖直细线。</summary>
    public int MinWidth { get; set; } = 5;

    /// <summary>命中块的最小高度（像素）。</summary>
    public int MinHeight { get; set; } = 5;

    /// <summary>
    /// 命中块内命中颜色的像素数下限。
    /// <para>
    /// 取代了原先的长宽比与占比阈值。那两条都依赖图标的形状：准星式图标是细线框加内部小符号，
    /// 外接矩形填充率天然很低（实测 0.36，贴着 0.35 的阈值）；实心方块图标则接近 1。
    /// 同一个阈值不可能同时服务这两种形状，只会让小图标被误杀、或让噪点被放进来。
    /// </para>
    /// </summary>
    public int MinPixels { get; set; } = 24;

    /// <summary>
    /// 纵向区间容许的连续空洞行数。
    /// <para>
    /// 取值有上下两个约束：上界必须明显小于「列表行距 − 图标高」，否则相邻两个成员的图标会被并成一段；
    /// 下界要能包容图标内部的空白行——准星式图标的上边框与下边框之间可能有连续几行没有命中像素。
    /// </para>
    /// <para>实测本地列表行距 30 px、图标高 22 px，也就是行间空白 8 行，本值取 3 有足够余量。</para>
    /// </summary>
    public int RowGap { get; set; } = 3;

    /// <summary>
    /// 色相容差（度）。像素色相与颜色库某项相差在此范围内即算匹配。
    /// <para>
    /// 取 12° 而不是更宽：警惕橙色相约 28°，20° 容差会一路吃到 8°~48°，
    /// 把棕色（实测误报源色相 26°~33°）也划进橙色；12° 能把橙与棕分开。
    /// </para>
    /// </summary>
    public double HueToleranceDegrees { get; set; } = 12;

    /// <summary>
    /// 彩色匹配的最低饱和度。低于此值的像素不参与色相匹配（哪怕色相对得上）。
    /// <para>
    /// 真实的 EVE 声望图标饱和度在 0.9 以上；界面里发灰的红/橙/蓝底纹落在 0.2~0.5。
    /// 这一条把「有点颜色但不够鲜艳」的像素挡在外面，避免整片底纹被算成同色。
    /// </para>
    /// </summary>
    public double MinSaturation { get; set; } = 0.5;

    /// <summary>
    /// 明度下限。低于此值的像素视为背景，避免把黑底当成深灰图标。
    /// <para>
    /// 这个值同时决定了「多暗还算同一个颜色」：4 bit 查表下 0.18 约等于「色值 ≥ 48」，
    /// 实测会把本地列表里**暗红色的人字形 UI 图标**（R≈49–61、明度 0.18–0.31）判成敌对红，
    /// 而真实的 -10 声望图标明度在 0.44 以上。取 0.35（≈色值 90）可把两者分开。
    /// </para>
    /// </summary>
    public double MinValue { get; set; } = 0.35;

    /// <summary>饱和度上限。低于此值的颜色归为中性色（灰白系），按明度匹配而不是色相。</summary>
    public double NeutralSaturation { get; set; } = 0.18;

    /// <summary>
    /// 中性色的明度容差。
    /// <para>
    /// 中性色匹配不看色相，容差直接决定「多大的灰度范围算同一档灰」。0.28 会覆盖明度
    /// 0.15~0.71 的整片灰色——文字、边框、分隔线全在里面；收到 0.10 后只认接近
    /// #6E6E6E 的那一档灰。
    /// </para>
    /// </summary>
    public double NeutralValueTolerance { get; set; } = 0.10;

    /// <summary>把本组参数转成查找表的构建参数。</summary>
    public LutTuning ToLutTuning() => new(
        HueToleranceDegrees,
        MinSaturation,
        MinValue,
        NeutralSaturation,
        NeutralValueTolerance);
}
