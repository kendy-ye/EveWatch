using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EveWatch.App.Hotkeys;
using EveWatch.Core.Config;

namespace EveWatch.App.Windows;

/// <summary>设置面板：全局快捷键绑定。客户端分组与分组快捷键在「客户端预览设置」里。</summary>
public partial class SettingsWindow : Window
{
    private const string DefaultHint = "点击「绑定」后按下组合键；Esc 取消，Delete 清除当前绑定。";

    private readonly AppServices _services;
    private readonly HotkeyService _hotkeys;
    private readonly List<HotkeyRow> _rows = [];

    private HotkeyRow? _capturing;

    public SettingsWindow(AppServices services, HotkeyService hotkeys)
    {
        _services = services;
        _hotkeys = hotkeys;

        InitializeComponent();

        BuildRows();
        HotkeyList.ItemsSource = _rows;

        SetHint(DefaultHint, false);

        Deactivated += (_, _) => EndCapture();
        Closed += (_, _) => EndCapture();
    }

    private void BuildRows()
    {
        var bindings = _services.Config.Current.Hotkeys;

        foreach (var action in HotkeyActions.All)
        {
            var binding = bindings.FirstOrDefault(b => b.Action == action.Id);
            _rows.Add(new HotkeyRow(action, binding));
        }
    }

    // ---------------------------------------------------------------- 录制

    private void OnBindClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HotkeyRow row)
        {
            return;
        }

        EndCapture();

        _capturing = row;
        row.IsCapturing = true;
        _hotkeys.IsSuspended = true;

        SetHint("请按下要绑定的按键…（Esc 取消，Delete 清除）", false);
        Focus();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturing is not { } row)
        {
            return;
        }

        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            EndCapture();
            SetHint(DefaultHint, false);
            return;
        }

        if (key is (Key.Delete or Key.Back) && Keyboard.Modifiers == ModifierKeys.None)
        {
            EndCapture();
            ClearBinding(row);
            return;
        }

        if (IsModifierKey(key))
        {
            SetHint("继续按下一个非修饰键…（Esc 取消）", false);
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey <= 0)
        {
            return;
        }

        var modifiers = ToModifiers(Keyboard.Modifiers);

        var conflict = _services.Config.Current.Hotkeys.FirstOrDefault(b =>
            !(b.Action == row.Action.Id && string.Equals(b.GroupId, row.GroupId, StringComparison.Ordinal))
            && b.Modifiers == modifiers && b.VirtualKey == virtualKey);

        if (conflict is not null)
        {
            var other = HotkeyActions.Find(conflict.Action);
            EndCapture();
            SetHint($"该组合已绑定到「{other?.Name ?? conflict.Action}」，未保存。", true);
            return;
        }

        EndCapture();
        ApplyBinding(row, modifiers, virtualKey);
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin or
        Key.None or Key.Apps;

    private static int ToModifiers(ModifierKeys keys)
    {
        var modifiers = 0;

        if ((keys & ModifierKeys.Control) != 0)
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if ((keys & ModifierKeys.Alt) != 0)
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if ((keys & ModifierKeys.Shift) != 0)
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if ((keys & ModifierKeys.Windows) != 0)
        {
            modifiers |= HotkeyModifiers.Win;
        }

        return modifiers;
    }

    private void EndCapture()
    {
        if (_capturing is not null)
        {
            _capturing.IsCapturing = false;
            _capturing = null;
        }

        _hotkeys.IsSuspended = false;
    }

    // ---------------------------------------------------------------- 快捷键维护

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HotkeyRow row)
        {
            EndCapture();
            ClearBinding(row);
        }
    }

    private void ApplyBinding(HotkeyRow row, int modifiers, int virtualKey)
    {
        var bindings = _services.Config.Current.Hotkeys;
        var binding = bindings.FirstOrDefault(b =>
            b.Action == row.Action.Id && string.Equals(b.GroupId, row.GroupId, StringComparison.Ordinal));

        if (binding is null)
        {
            binding = new HotkeyBinding { Action = row.Action.Id, GroupId = row.GroupId };
            bindings.Add(binding);
        }

        binding.Modifiers = modifiers;
        binding.VirtualKey = virtualKey;

        row.Binding = binding;
        row.Refresh();

        SaveAndApply();
        SetHint($"已绑定为 {HotkeyFormatter.Format(binding)}。", false);
    }

    private void ClearBinding(HotkeyRow row)
    {
        var binding = _services.Config.Current.Hotkeys.FirstOrDefault(b =>
            b.Action == row.Action.Id && string.Equals(b.GroupId, row.GroupId, StringComparison.Ordinal));
        if (binding is not null)
        {
            _services.Config.Current.Hotkeys.Remove(binding);
        }

        row.Binding = null;
        row.Refresh();

        SaveAndApply();
        SetHint("已清除该快捷键。", false);
    }

    private void OnClearAllClick(object sender, RoutedEventArgs e)
    {
        if (_services.Config.Current.Hotkeys.Count == 0)
        {
            return;
        }

        if (MessageBox.Show(this, "清除全部快捷键绑定（包含各分组的快捷键）？", "快捷键设置",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        EndCapture();
        _services.Config.Current.Hotkeys.Clear();

        foreach (var row in _rows)
        {
            row.Binding = null;
            row.Refresh();
        }

        SaveAndApply();
        SetHint("已清除全部快捷键。", false);
    }

    private void SaveAndApply()
    {
        _services.Config.Save();
        ShowErrors(_hotkeys.Apply());
    }

    private void ShowErrors(IReadOnlyList<string> errors)
    {
        if (errors.Count == 0)
        {
            ErrorBox.Visibility = Visibility.Collapsed;
            ErrorText.Text = string.Empty;
            return;
        }

        ErrorText.Text = string.Join(Environment.NewLine, errors);
        ErrorBox.Visibility = Visibility.Visible;
    }

    private void SetHint(string text, bool isError)
    {
        HintText.Text = text;
        HintText.Foreground = isError
            ? (Brush)FindResource("DangerBrush")
            : (Brush)FindResource("TextDimBrush");
    }

    // ---------------------------------------------------------------- 窗口

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) is not null))
        {
            return;
        }

        DragMove();
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnOpenConfigClick(object sender, RoutedEventArgs e)
    {
        var directory = Path.GetDirectoryName(_services.Config.Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
    }
}

/// <summary>设置面板里的一行快捷键绑定。</summary>
public sealed class HotkeyRow : INotifyPropertyChanged
{
    private bool _isCapturing;

    public HotkeyRow(HotkeyActionInfo action, HotkeyBinding? binding, string? groupId = null)
    {
        Action = action;
        Binding = binding;
        GroupId = groupId ?? string.Empty;
    }

    public HotkeyActionInfo Action { get; }

    public HotkeyBinding? Binding { get; set; }

    public string GroupId { get; }

    public bool IsCapturing
    {
        get => _isCapturing;
        set
        {
            if (_isCapturing == value)
            {
                return;
            }

            _isCapturing = value;
            Raise(nameof(IsCapturing));
            Raise(nameof(DisplayText));
            Raise(nameof(HasBinding));
        }
    }

    public bool HasBinding => Binding is not null;

    public string DisplayText => IsCapturing
        ? "按下按键…"
        : Binding is null ? "未设置" : HotkeyFormatter.Format(Binding);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh()
    {
        Raise(nameof(HasBinding));
        Raise(nameof(DisplayText));
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
