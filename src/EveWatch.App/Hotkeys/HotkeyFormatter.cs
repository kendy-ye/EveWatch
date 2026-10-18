using System.Windows.Input;
using EveWatch.Core.Config;

namespace EveWatch.App.Hotkeys;

/// <summary>把快捷键绑定格式化为界面展示文本。</summary>
public static class HotkeyFormatter
{
    public static string Format(HotkeyBinding binding)
    {
        var parts = new List<string>(5);

        if ((binding.Modifiers & HotkeyModifiers.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((binding.Modifiers & HotkeyModifiers.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((binding.Modifiers & HotkeyModifiers.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((binding.Modifiers & HotkeyModifiers.Win) != 0)
        {
            parts.Add("Win");
        }

        var key = KeyInterop.KeyFromVirtualKey(binding.VirtualKey);
        parts.Add(key == Key.None ? $"VK{binding.VirtualKey}" : KeyName(key));

        return string.Join(" + ", parts);
    }

    private static string KeyName(Key key) => key switch
    {
        Key.D0 => "0",
        Key.D1 => "1",
        Key.D2 => "2",
        Key.D3 => "3",
        Key.D4 => "4",
        Key.D5 => "5",
        Key.D6 => "6",
        Key.D7 => "7",
        Key.D8 => "8",
        Key.D9 => "9",
        Key.NumPad0 => "Num 0",
        Key.NumPad1 => "Num 1",
        Key.NumPad2 => "Num 2",
        Key.NumPad3 => "Num 3",
        Key.NumPad4 => "Num 4",
        Key.NumPad5 => "Num 5",
        Key.NumPad6 => "Num 6",
        Key.NumPad7 => "Num 7",
        Key.NumPad8 => "Num 8",
        Key.NumPad9 => "Num 9",
        Key.Multiply => "Num *",
        Key.Add => "Num +",
        Key.Subtract => "Num -",
        Key.Divide => "Num /",
        Key.Decimal => "Num .",
        Key.OemPlus => "+",
        Key.OemMinus => "-",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe => "\\",
        Key.OemTilde => "`",
        Key.Space => "Space",
        Key.Return => "Enter",
        Key.Escape => "Esc",
        Key.Back => "Backspace",
        Key.Delete => "Del",
        Key.Insert => "Ins",
        Key.PageUp => "PgUp",
        Key.PageDown => "PgDn",
        Key.Capital => "Caps",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.Left => "←",
        Key.Right => "→",
        Key.PrintScreen => "PrtSc",
        Key.Scroll => "Scroll",
        _ => key.ToString(),
    };
}
