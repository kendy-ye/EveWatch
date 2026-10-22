using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EveWatch.App.Hotkeys;
using EveWatch.App.Preview;
using EveWatch.Core.Config;
using EveWatch.Core.Palette;

namespace EveWatch.App.Windows;

/// <summary>客户端预览设置：全局开关、透明度、描边、逐角色尺寸 / 位置，以及客户端分组与分组快捷键。</summary>
public partial class ClientPreviewSettingsWindow : Window
{
    private const string DefaultGroupHint = "点击「绑定」后按下组合键；Esc 取消，Delete 清除当前绑定。";

    /// <summary>逐角色透明度下拉的取值；<c>null</c> 表示跟随全局。</summary>
    private static readonly double?[] OpacityLevels = [null, 0.9, 0.8, 0.7, 0.6];

    private readonly AppServices _services;
    private readonly ClientPreviewManager _manager;
    private readonly HotkeyService _hotkeys;
    private readonly ObservableCollection<PreviewAccountRow> _rows = [];
    private readonly ObservableCollection<GroupMemberRow> _groupMembers = [];
    private readonly ObservableCollection<HotkeyRow> _groupHotkeyRows = [];
    private readonly ObservableCollection<GroupOption> _groupOptions = [];
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _groupTimer;

    private HotkeyRow? _capturing;
    private string? _selectedGroupId;
    private bool _suppressGroupEvents;

    // XAML 初始化时 Slider 会把 Value 从 0 夹到 Minimum，事件可能早于字段就绪，
    // 因此默认处于「载入中」状态，LoadSettings 结束时才解除。
    private bool _loading = true;

    public ClientPreviewSettingsWindow(AppServices services, ClientPreviewManager manager, HotkeyService hotkeys)
    {
        _services = services;
        _manager = manager;
        _hotkeys = hotkeys;

        InitializeComponent();

        AccountList.ItemsSource = _rows;
        GroupMemberList.ItemsSource = _groupMembers;
        GroupHotkeyList.ItemsSource = _groupHotkeyRows;
        GroupCombo.ItemsSource = _groupOptions;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            ReloadRows(false);
        };

        _groupTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _groupTimer.Tick += (_, _) => RefreshGroupRuntimes();

        LoadSettings();
        ReloadGroupOptions();
        SetGroupHint(DefaultGroupHint, false);

        Loaded += (_, _) =>
        {
            ReloadRows(true);
            _manager.LayoutChanged += OnLayoutChanged;
            _manager.StateChanged += OnManagerStateChanged;
            _groupTimer.Start();
        };

        Deactivated += (_, _) => EndCapture();

        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _groupTimer.Stop();
            EndCapture();
            _manager.LayoutChanged -= OnLayoutChanged;
            _manager.StateChanged -= OnManagerStateChanged;
            _services.UiState.Save();
        };
    }

    private ClientPreviewState Config => _services.UiState.Current.ClientPreview;

    // ---------------------------------------------------------------- 载入

    private void LoadSettings()
    {
        var config = Config;

        _loading = true;

        SessionCheck.IsChecked = _manager.Enabled;
        TopmostCheck.IsChecked = config.Topmost;
        ShowLabelCheck.IsChecked = config.ShowLabel;
        HideMinCheck.IsChecked = config.HideWhenMinimized;

        DefaultWidthBox.Text = config.DefaultWidth.ToString("0");
        DefaultHeightBox.Text = config.DefaultHeight.ToString("0");

        OpacitySlider.Value = Math.Round(config.Opacity * 100);
        OpacityValueText.Text = $"{OpacitySlider.Value:0}%";

        BorderWidthSlider.Value = config.ActiveBorderThickness;
        BorderWidthValueText.Text = $"{BorderWidthSlider.Value:0}px";

        ColorHexBox.Text = config.ActiveBorderColor;
        UpdateColorSwatch(config.ActiveBorderColor);

        LabelColorHexBox.Text = config.LabelColor;
        UpdateLabelSwatch(config.LabelColor);

        _loading = false;
    }

    private void OnManagerStateChanged()
    {
        SessionCheck.IsChecked = _manager.Enabled;
    }

    private void OnLayoutChanged()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    // ---------------------------------------------------------------- 逐角色列表

    private void ReloadRows(bool rebuild)
    {
        var names = OrderedAccountNames();

        if (rebuild || !names.SequenceEqual(_rows.Select(row => row.Name), StringComparer.Ordinal))
        {
            _rows.Clear();

            foreach (var name in names)
            {
                _rows.Add(CreateRow(name));
            }
        }
        else
        {
            foreach (var row in _rows)
            {
                UpdateSummary(row);
            }
        }

        var running = names.Count(name => _services.Directory.Find(name) is not null);
        RowHint.Text = names.Count == 0
            ? "当前没有发现运行中的客户端；客户端启动后对应角色会自动出现在这里。"
            : $"共 {names.Count} 个角色，运行中 {running} 个。";
    }

    private List<string> OrderedAccountNames()
    {
        var names = new List<string>();

        foreach (var account in _services.Config.Current.Accounts)
        {
            if (!names.Contains(account.Name))
            {
                names.Add(account.Name);
            }
        }

        foreach (var window in _services.Directory.Windows)
        {
            if (!names.Contains(window.AccountName))
            {
                names.Add(window.AccountName);
            }
        }

        foreach (var name in Config.Accounts.Keys)
        {
            if (!names.Contains(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private PreviewAccountRow CreateRow(string name)
    {
        // 只为运行中的角色建配置项；离线角色打开设置面板时只显示默认值，不改写配置文件。
        var state = Config.Accounts.TryGetValue(name, out var existing) ? existing : null;

        return new PreviewAccountRow(name)
        {
            Visible = state?.Visible ?? true,
            Width = state?.Width ?? Config.DefaultWidth,
            Height = state?.Height ?? Config.DefaultHeight,
            OpacityIndex = OpacityIndexFor(state?.Opacity),
            RunningText = _services.Directory.Find(name) is null ? "未运行" : "运行中",
            PositionText = state is null ? "—" : PositionText(state),
        };
    }

    private void UpdateSummary(PreviewAccountRow row)
    {
        row.RunningText = _services.Directory.Find(row.Name) is null ? "未运行" : "运行中";
        row.PositionText = Config.Accounts.TryGetValue(row.Name, out var state)
            ? PositionText(state)
            : "—";
    }

    private static string PositionText(ClientPreviewAccountState state) =>
        state.Left is { } left && state.Top is { } top
            ? $"{left:0},{top:0}"
            : "—";

    private static int OpacityIndexFor(double? opacity)
    {
        if (opacity is not { } value)
        {
            return 0;
        }

        var index = 1;
        var best = double.MaxValue;

        for (var i = 1; i < OpacityLevels.Length; i++)
        {
            var distance = Math.Abs(OpacityLevels[i]!.Value - value);
            if (distance < best)
            {
                best = distance;
                index = i;
            }
        }

        return index;
    }

    // ---------------------------------------------------------------- 全局设置

    private void OnSessionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _manager.SetEnabled(SessionCheck.IsChecked == true);
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var config = Config;
        config.Topmost = TopmostCheck.IsChecked == true;
        config.ShowLabel = ShowLabelCheck.IsChecked == true;
        config.HideWhenMinimized = HideMinCheck.IsChecked == true;

        _manager.ApplySettings();
    }

    private void OnDefaultSizeLostFocus(object sender, RoutedEventArgs e) => CommitDefaultSize();

    private void CommitDefaultSize()
    {
        if (_loading)
        {
            return;
        }

        var config = Config;

        config.DefaultWidth = Math.Clamp(
            ParseNumber(DefaultWidthBox.Text, config.DefaultWidth),
            ClientPreviewState.MinWidth,
            ClientPreviewState.MaxWidth);

        config.DefaultHeight = Math.Clamp(
            ParseNumber(DefaultHeightBox.Text, config.DefaultHeight),
            ClientPreviewState.MinHeight,
            ClientPreviewState.MaxHeight);

        DefaultWidthBox.Text = config.DefaultWidth.ToString("0");
        DefaultHeightBox.Text = config.DefaultHeight.ToString("0");

        // 直接应用到所有预览窗（覆盖逐角色尺寸），位置保持不变
        _manager.ApplyDefaultSize();

        // 逐角色列表同步显示新的宽高，避免全局改了而表格还停在旧值
        foreach (var row in _rows)
        {
            row.Width = config.DefaultWidth;
            row.Height = config.DefaultHeight;
        }
    }

    private void OnSizeBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box)
        {
            return;
        }

        // 逐角色宽高是 LostFocus 提交的，回车时先把输入推回绑定再提交
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

        if (box.Tag is PreviewAccountRow row)
        {
            CommitRowSize(row);
        }
        else
        {
            CommitDefaultSize();
        }

        e.Handled = true;
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        OpacityValueText.Text = $"{e.NewValue:0}%";
        Config.Opacity = Math.Clamp(e.NewValue / 100.0, 0.3, 1.0);
        _manager.ApplySettings();
    }

    private void OnBorderWidthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        BorderWidthValueText.Text = $"{e.NewValue:0}px";
        Config.ActiveBorderThickness = Math.Clamp(e.NewValue, 0, 6);
        _manager.ApplySettings();
    }

    private void OnColorHexLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var hex = ColorHexBox.Text?.Trim() ?? string.Empty;

        if (!ColorLut.TryParseHex(hex, out var r, out var g, out var b))
        {
            ColorHexBox.Text = Config.ActiveBorderColor;
            return;
        }

        ApplyActiveColor($"#{r:X2}{g:X2}{b:X2}");
    }

    private void OnColorPresetClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string hex })
        {
            ApplyActiveColor(hex);
        }
    }

    private void ApplyActiveColor(string hex)
    {
        Config.ActiveBorderColor = hex;
        ColorHexBox.Text = hex;
        UpdateColorSwatch(hex);
        _manager.ApplySettings();
    }

    private void UpdateColorSwatch(string hex)
    {
        if (TryCreateBrush(hex) is { } brush)
        {
            ColorSwatch.Background = brush;
        }
    }

    private void OnLabelColorHexLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var hex = LabelColorHexBox.Text?.Trim() ?? string.Empty;

        if (!ColorLut.TryParseHex(hex, out var r, out var g, out var b))
        {
            LabelColorHexBox.Text = Config.LabelColor;
            return;
        }

        ApplyLabelColor($"#{r:X2}{g:X2}{b:X2}");
    }

    private void OnLabelColorPresetClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string hex })
        {
            ApplyLabelColor(hex);
        }
    }

    private void ApplyLabelColor(string hex)
    {
        Config.LabelColor = hex;
        LabelColorHexBox.Text = hex;
        UpdateLabelSwatch(hex);
        _manager.ApplySettings();
    }

    private void UpdateLabelSwatch(string hex)
    {
        if (TryCreateBrush(hex) is { } brush)
        {
            LabelColorSwatch.Background = brush;
        }
    }

    private static SolidColorBrush? TryCreateBrush(string hex)
    {
        if (!ColorLut.TryParseHex(hex, out var r, out var g, out var b))
        {
            return null;
        }

        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void OnAutoArrangeClick(object sender, RoutedEventArgs e)
    {
        _manager.AutoArrange();
        ReloadRows(false);
    }

    private void OnResetPositionsClick(object sender, RoutedEventArgs e)
    {
        _manager.ResetPositions();
        ReloadRows(false);
    }

    // ---------------------------------------------------------------- 逐角色操作

    private void OnRowVisibleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PreviewAccountRow row })
        {
            return;
        }

        Config.For(row.Name).Visible = row.Visible;
        _manager.ApplySettings();
        UpdateSummary(row);
    }

    private void OnRowSizeLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PreviewAccountRow row })
        {
            CommitRowSize(row);
        }
    }

    private void CommitRowSize(PreviewAccountRow row)
    {
        row.Width = Math.Clamp(row.Width, ClientPreviewState.MinWidth, ClientPreviewState.MaxWidth);
        row.Height = Math.Clamp(row.Height, ClientPreviewState.MinHeight, ClientPreviewState.MaxHeight);

        var state = Config.For(row.Name);
        state.Width = row.Width;
        state.Height = row.Height;

        _manager.ApplyAccountBounds(row.Name);
        UpdateSummary(row);
    }

    private void OnRowOpacityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || sender is not FrameworkElement { Tag: PreviewAccountRow row } element)
        {
            return;
        }

        var index = (element as ComboBox)?.SelectedIndex ?? 0;
        if (index < 0 || index >= OpacityLevels.Length)
        {
            index = 0;
        }

        Config.For(row.Name).Opacity = OpacityLevels[index];
        _manager.ApplySettings();
    }

    private void OnRowResetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PreviewAccountRow row })
        {
            return;
        }

        _manager.ResetPosition(row.Name);
        UpdateSummary(row);
    }

    // ---------------------------------------------------------------- 客户端分组

    private ClientGroup? SelectedGroup => (GroupCombo.SelectedItem as GroupOption)?.Group;

    private void ReloadGroupOptions(string? selectId = null)
    {
        _suppressGroupEvents = true;

        var target = selectId ?? _selectedGroupId;
        _groupOptions.Clear();
        foreach (var group in _services.Config.Current.ClientGroups)
        {
            _groupOptions.Add(new GroupOption(group));
        }

        GroupCombo.SelectedItem = _groupOptions.FirstOrDefault(option => option.Group.Id == target)
            ?? _groupOptions.FirstOrDefault();

        GroupEmptyHint.Visibility = _groupOptions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _suppressGroupEvents = false;
        LoadGroupEditor();
    }

    private void OnGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressGroupEvents)
        {
            return;
        }

        LoadGroupEditor();
    }

    private void LoadGroupEditor()
    {
        var group = SelectedGroup;
        _selectedGroupId = group?.Id;

        _groupMembers.Clear();
        _groupHotkeyRows.Clear();

        GroupEditor.Visibility = group is null ? Visibility.Collapsed : Visibility.Visible;

        if (group is null)
        {
            return;
        }

        _suppressGroupEvents = true;
        GroupNameBox.Text = group.Name;
        GroupEditorTitle.Text = $"分组设置 · {group.Name}";
        _suppressGroupEvents = false;

        foreach (var name in group.Accounts)
        {
            _groupMembers.Add(new GroupMemberRow(group.Id, name));
        }

        RefreshGroupRuntimes();

        GroupEmptyText.Visibility = group.Accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        RebuildGroupAccountCombo(group);

        var next = new HotkeyActionInfo(
            HotkeyActions.GroupNext, "下一个客户端", $"在「{group.Name}」内循环切换", group.Id);
        var previous = new HotkeyActionInfo(
            HotkeyActions.GroupPrevious, "上一个客户端", $"在「{group.Name}」内循环切换", group.Id);

        _groupHotkeyRows.Add(new HotkeyRow(next, FindGroupBinding(HotkeyActions.GroupNext, group.Id), group.Id));
        _groupHotkeyRows.Add(new HotkeyRow(previous, FindGroupBinding(HotkeyActions.GroupPrevious, group.Id), group.Id));
    }

    private bool IsAccountRunning(string name) =>
        _services.Directory.Windows.Any(window =>
            string.Equals(window.AccountName, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>刷新组内客户端的运行状态角标。</summary>
    private void RefreshGroupRuntimes()
    {
        foreach (var row in _groupMembers)
        {
            row.SetRunning(IsAccountRunning(row.Name));
        }
    }

    private HotkeyBinding? FindGroupBinding(string action, string groupId) =>
        _services.Config.Current.Hotkeys.FirstOrDefault(binding =>
            binding.Action == action && string.Equals(binding.GroupId, groupId, StringComparison.Ordinal));

    private void RebuildGroupAccountCombo(ClientGroup group)
    {
        var names = _services.Config.Current.Accounts
            .Select(account => account.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Where(name => !group.Accounts.Any(existing =>
                string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        GroupAccountCombo.ItemsSource = names;
        GroupAccountCombo.SelectedIndex = names.Count > 0 ? 0 : -1;
    }

    private void OnAddGroupClick(object sender, RoutedEventArgs e)
    {
        var groups = _services.Config.Current.ClientGroups;
        var group = new ClientGroup
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = $"分组 {groups.Count + 1}",
        };

        groups.Add(group);
        _services.Config.Save();
        ReloadGroupOptions(group.Id);
        SetGroupHint($"已创建「{group.Name}」。", false);
    }

    private void OnDeleteGroupClick(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup is not { } group)
        {
            return;
        }

        if (MessageBox.Show(this, $"删除分组「{group.Name}」及其快捷键？", "客户端分组",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        _services.Config.Current.ClientGroups.Remove(group);
        _services.Config.Current.Hotkeys.RemoveAll(binding =>
            binding.Action is HotkeyActions.GroupNext or HotkeyActions.GroupPrevious
            && string.Equals(binding.GroupId, group.Id, StringComparison.Ordinal));

        _selectedGroupId = null;
        SaveAndApplyGroupHotkeys();
        ReloadGroupOptions();
    }

    private void OnGroupNameLostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressGroupEvents || SelectedGroup is not { } group)
        {
            return;
        }

        var name = GroupNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, group.Name, StringComparison.Ordinal))
        {
            GroupNameBox.Text = group.Name;
            return;
        }

        group.Name = name;
        _services.Config.Save();
        ReloadGroupOptions(group.Id);
    }

    private void OnAddGroupMemberClick(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup is not { } group || GroupAccountCombo.SelectedItem is not string name)
        {
            return;
        }

        if (group.Accounts.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        group.Accounts.Add(name);
        _services.Config.Save();
        LoadGroupEditor();
        SetGroupHint($"已把 {name} 加入「{group.Name}」。", false);
    }

    private void OnGroupMemberUpClick(object sender, RoutedEventArgs e) => MoveGroupMember(sender, -1);

    private void OnGroupMemberDownClick(object sender, RoutedEventArgs e) => MoveGroupMember(sender, 1);

    private void MoveGroupMember(object sender, int offset)
    {
        if (SelectedGroup is not { } group || (sender as FrameworkElement)?.Tag is not GroupMemberRow row)
        {
            return;
        }

        var index = group.Accounts.FindIndex(name => string.Equals(name, row.Name, StringComparison.OrdinalIgnoreCase));
        var target = index + offset;

        if (index < 0 || target < 0 || target >= group.Accounts.Count)
        {
            return;
        }

        (group.Accounts[index], group.Accounts[target]) = (group.Accounts[target], group.Accounts[index]);
        _services.Config.Save();
        LoadGroupEditor();
    }

    private void OnGroupMemberRemoveClick(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup is not { } group || (sender as FrameworkElement)?.Tag is not GroupMemberRow row)
        {
            return;
        }

        group.Accounts.RemoveAll(name => string.Equals(name, row.Name, StringComparison.OrdinalIgnoreCase));
        _services.Config.Save();
        LoadGroupEditor();
    }

    // ---------------------------------------------------------------- 分组快捷键录制

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

        SetGroupHint("请按下要绑定的按键…（Esc 取消，Delete 清除）", false);
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
            SetGroupHint(DefaultGroupHint, false);
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
            SetGroupHint("继续按下一个非修饰键…（Esc 取消）", false);
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
            SetGroupHint($"该组合已绑定到「{other?.Name ?? conflict.Action}」，未保存。", true);
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

    private void OnDeactivated(object? sender, EventArgs e) => EndCapture();

    private void EndCapture()
    {
        if (_capturing is not null)
        {
            _capturing.IsCapturing = false;
            _capturing = null;
        }

        _hotkeys.IsSuspended = false;
    }

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

        SaveAndApplyGroupHotkeys();
        SetGroupHint($"已绑定为 {HotkeyFormatter.Format(binding)}。", false);
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

        SaveAndApplyGroupHotkeys();
        SetGroupHint("已清除该快捷键。", false);
    }

    private void SaveAndApplyGroupHotkeys()
    {
        _services.Config.Save();
        ShowGroupErrors(_hotkeys.Apply());
    }

    private void ShowGroupErrors(IReadOnlyList<string> errors)
    {
        if (errors.Count == 0)
        {
            GroupErrorBox.Visibility = Visibility.Collapsed;
            GroupErrorText.Text = string.Empty;
            return;
        }

        GroupErrorText.Text = string.Join(Environment.NewLine, errors);
        GroupErrorBox.Visibility = Visibility.Visible;
    }

    private void SetGroupHint(string text, bool isError)
    {
        GroupHintText.Text = text;
        GroupHintText.Foreground = isError
            ? (Brush)FindResource("DangerBrush")
            : (Brush)FindResource("TextDimBrush");
    }

    // ---------------------------------------------------------------- 窗口

    private static double ParseNumber(string? text, double fallback) =>
        double.TryParse(text, out var value) ? value : fallback;

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) is not null))
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已松开，忽略
        }
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
}

/// <summary>设置窗口里的一行角色预览。</summary>
public sealed class PreviewAccountRow : INotifyPropertyChanged
{
    private bool _visible;
    private double _width;
    private double _height;
    private int _opacityIndex;
    private string _runningText = string.Empty;
    private string _positionText = "—";

    public PreviewAccountRow(string name) => Name = name;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

    public bool Visible
    {
        get => _visible;
        set => Set(ref _visible, value);
    }

    public double Width
    {
        get => _width;
        set => Set(ref _width, value);
    }

    public double Height
    {
        get => _height;
        set => Set(ref _height, value);
    }

    public int OpacityIndex
    {
        get => _opacityIndex;
        set => Set(ref _opacityIndex, value);
    }

    public string RunningText
    {
        get => _runningText;
        set => Set(ref _runningText, value);
    }

    public string PositionText
    {
        get => _positionText;
        set => Set(ref _positionText, value);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>客户端分组下拉项。</summary>
public sealed class GroupOption
{
    public GroupOption(ClientGroup group)
    {
        Group = group;
    }

    public ClientGroup Group { get; }

    public string Display => string.IsNullOrWhiteSpace(Group.Name) ? "未命名分组" : Group.Name;

    public override string ToString() => Display;
}

/// <summary>分组内的一个客户端。</summary>
public sealed class GroupMemberRow : INotifyPropertyChanged
{
    private bool _isRunning = true;

    public GroupMemberRow(string groupId, string name)
    {
        GroupId = groupId;
        Name = name;
    }

    public string GroupId { get; }

    public string Name { get; }

    public bool IsRunning => _isRunning;

    public bool HasBadge => !_isRunning;

    public string BadgeText => "未运行";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetRunning(bool value)
    {
        if (_isRunning == value)
        {
            return;
        }

        _isRunning = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasBadge)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BadgeText)));
    }
}
