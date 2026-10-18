namespace EveWatch.Core.Palette;

/// <summary>
/// 颜色库中的一项。
/// <para>内置四项为 EVE standing 色，不可删除；其余为用户自定义。</para>
/// </summary>
public sealed class PaletteColor
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary><c>#RRGGBB</c> 形式的十六进制色值。</summary>
    public string Hex { get; set; } = "#000000";

    public bool Builtin { get; set; }
}
