using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using EveWatch.Core.Alarm;
using EveWatch.Core.Config;
using EveWatch.Core.Intel;

namespace EveWatch.App.Windows;

/// <summary>频道预警面板：监听账号与频道、跳数规则、报警声音与弹窗设置。</summary>
public partial class IntelSettingsWindow : Window
{
    private readonly AppServices _services;

    private readonly ObservableCollection<ListenerRow> _listenerRows = [];
    private readonly ObservableCollection<RuleRow> _ruleRows = [];
    private readonly ObservableCollection<ChannelOption> _probeChannels = [];
    private readonly ChatLogCatalog _catalog = new();
    private readonly DispatcherTimer _statusTimer;

    private IReadOnlyList<ChatChannelInfo> _allChannels = [];
    private bool _suppressPopupEvents;
    private bool _suppressRuleEvents;
    private bool _suppressProbeEvents;

    public IntelSettingsWindow(AppServices services)
    {
        _services = services;

        InitializeComponent();

        ListenerList.ItemsSource = _listenerRows;
        RuleList.ItemsSource = _ruleRows;
        ProbeChannelList.ItemsSource = _probeChannels;

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _statusTimer.Tick += (_, _) => RefreshIntelStatus();

        LoadIntelSettings();

        Loaded += (_, _) =>
        {
            RefreshIntelStatus();
            ReloadChannelCatalog();
            _statusTimer.Start();
        };
        Closed += (_, _) => _statusTimer.Stop();
    }

    // ---------------------------------------------------------------- 基础

    private void LoadIntelSettings()
    {
        var intel = _services.Config.Current.Intel;
        IntelEnabledCheck.IsChecked = intel.Enabled;
        IgnoreSelfCheck.IsChecked = intel.IgnoreSelf;

        // 配置留空时显示自动检测到的目录，避免输入框空白让人以为没有配置。
        var directory = intel.ChatLogDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = ChatLogCatalog.FindDirectory(null) ?? string.Empty;
        }

        ChatLogDirectoryBox.Text = directory;

        ReloadListenerRows();
        ReloadRuleRows();
        LoadPopupSettings();
    }

    private void SaveIntelConfig()
    {
        _services.Config.Save();
        RefreshIntelStatus();
    }

    private void RefreshIntelStatus()
    {
        var status = _services.Intel.GetStatus();

        var state = _services.Config.Current.Intel.Enabled
            ? status.Running ? "运行中" : "已启用，等待目录或星系图"
            : "未启用";

        if (status.ChatLogDirectory is { } directory)
        {
            IntelStatusText.Text = status.Error is { } error
                ? $"{state} · {directory}{Environment.NewLine}{error}"
                : $"{state} · {directory}";
        }
        else
        {
            var expected = ChatLogCatalog.CandidateDirectories().FirstOrDefault()
                ?? @"%USERPROFILE%\Documents\EVE\logs\Chatlogs";

            IntelStatusText.Text =
                $"{state} · 未找到聊天日志目录{Environment.NewLine}" +
                $"默认位置：{expected}{Environment.NewLine}" +
                "如果该目录不存在，请先在游戏里打开任意聊天频道生成日志，或点「浏览」手动选择 EVE\\logs\\Chatlogs。";
        }

        foreach (var row in _listenerRows)
        {
            var listenerStatus = status.Listeners.FirstOrDefault(item =>
                item.CharacterId == row.Model.CharacterId
                && string.Equals(item.ChannelName, row.Model.ChannelName, StringComparison.OrdinalIgnoreCase));

            row.RefreshStatus(listenerStatus);
        }
    }

    private void ReloadListenerRows()
    {
        _listenerRows.Clear();
        var statuses = _services.Intel.GetStatus().Listeners;

        foreach (var listener in _services.Config.Current.Intel.Listeners)
        {
            var status = statuses.FirstOrDefault(item =>
                item.CharacterId == listener.CharacterId
                && string.Equals(item.ChannelName, listener.ChannelName, StringComparison.OrdinalIgnoreCase));

            var row = new ListenerRow(listener);
            row.RefreshStatus(status);
            _listenerRows.Add(row);
        }

        ListenerEmptyText.Visibility = _listenerRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- 频道探测

    private async void ReloadChannelCatalog()
    {
        var configured = _services.Config.Current.Intel.ChatLogDirectory;
        var directory = ChatLogCatalog.FindDirectory(configured);

        if (directory is null)
        {
            ProbeHint.Text = "未找到日志目录，请点「浏览」手动选择";
            return;
        }

        try
        {
            var files = await Task.Run(() => _catalog.Scan(directory));
            _allChannels = ChatLogCatalog.GetChannels(files);
            PopulateProbeCharacters();
            ProbeHint.Text = $"已扫描 {files.Count} 个日志文件 · {_allChannels.Count} 个频道";
        }
        catch (Exception ex)
        {
            ProbeHint.Text = $"扫描失败：{ex.Message}";
        }
    }

    private void PopulateProbeCharacters()
    {
        _suppressProbeEvents = true;

        var previous = (ProbeCharacterCombo.SelectedItem as CharacterOption)?.Id;
        var characters = _allChannels
            .GroupBy(channel => channel.CharacterId)
            .Select(group => new CharacterOption(
                group.Key,
                group.First().CharacterName))
            .OrderBy(option => option.Name, StringComparer.Ordinal)
            .ToList();

        ProbeCharacterCombo.ItemsSource = characters;
        ProbeCharacterCombo.SelectedItem = characters.FirstOrDefault(option => option.Id == previous)
            ?? characters.FirstOrDefault();

        _suppressProbeEvents = false;
        PopulateProbeChannels();
    }

    private void PopulateProbeChannels()
    {
        _probeChannels.Clear();

        if (ProbeCharacterCombo.SelectedItem is not CharacterOption character)
        {
            return;
        }

        foreach (var channel in _allChannels
                     .Where(item => item.CharacterId == character.Id && item.Kind != ChatChannelKind.Local)
                     .OrderBy(item => item.ChannelName, StringComparer.Ordinal))
        {
            _probeChannels.Add(new ChannelOption(channel));
        }
    }

    private void OnProbeCharacterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProbeEvents)
        {
            return;
        }

        PopulateProbeChannels();
    }

    private void OnRescanIntelClick(object sender, RoutedEventArgs e)
    {
        ReloadChannelCatalog();
        RefreshIntelStatus();
    }

    private void OnIntelEnabledChanged(object sender, RoutedEventArgs e)
    {
        _services.Config.Current.Intel.Enabled = IntelEnabledCheck.IsChecked == true;
        SaveIntelConfig();
    }

    private void OnIgnoreSelfChanged(object sender, RoutedEventArgs e)
    {
        _services.Config.Current.Intel.IgnoreSelf = IgnoreSelfCheck.IsChecked == true;
        SaveIntelConfig();
    }

    private void OnChatLogDirectoryLostFocus(object sender, RoutedEventArgs e)
    {
        var value = ChatLogDirectoryBox.Text.Trim();
        if (string.Equals(value, _services.Config.Current.Intel.ChatLogDirectory, StringComparison.Ordinal))
        {
            return;
        }

        _services.Config.Current.Intel.ChatLogDirectory = value;
        SaveIntelConfig();
        ReloadChannelCatalog();
    }

    private void OnBrowseLogDirectoryClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择 EVE 聊天日志目录（Chatlogs）",
            UseDescriptionForTitle = true,
            SelectedPath = ChatLogDirectoryBox.Text,
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        ChatLogDirectoryBox.Text = dialog.SelectedPath;
        _services.Config.Current.Intel.ChatLogDirectory = dialog.SelectedPath;
        SaveIntelConfig();
        ReloadChannelCatalog();
    }

    // ---------------------------------------------------------------- 监听

    private bool AddListenerCore(long characterId, string characterName, string channelId, string channelName)
    {
        var listeners = _services.Config.Current.Intel.Listeners;

        var duplicate = listeners.Any(listener =>
            listener.CharacterId == characterId
            && ((!string.IsNullOrWhiteSpace(channelId)
                 && string.Equals(listener.ChannelId, channelId, StringComparison.OrdinalIgnoreCase))
                || string.Equals(listener.ChannelName, channelName, StringComparison.OrdinalIgnoreCase)));

        if (duplicate)
        {
            return false;
        }

        listeners.Add(new IntelListener
        {
            CharacterId = characterId,
            CharacterName = characterName,
            ChannelId = channelId,
            ChannelName = channelName,
            Enabled = true,
        });

        return true;
    }

    private void OnAddSelectedChannelsClick(object sender, RoutedEventArgs e)
    {
        if (ProbeCharacterCombo.SelectedItem is not CharacterOption character)
        {
            ProbeHint.Text = "请先选择一个角色";
            return;
        }

        var selected = ProbeChannelList.SelectedItems
            .OfType<ChannelOption>()
            .ToList();

        if (selected.Count == 0)
        {
            ProbeHint.Text = "先在频道列表中选择一个或多个频道";
            return;
        }

        var added = selected.Count(option => AddListenerCore(
            character.Id,
            character.Name,
            option.Info.ChannelId,
            option.Info.ChannelName));

        if (added > 0)
        {
            SaveIntelConfig();
            ReloadListenerRows();
        }

        ProbeHint.Text = added > 0 ? $"已添加 {added} 个频道" : "选中的频道已经全部在监听列表中";
    }

    private void OnAddManualChannelClick(object sender, RoutedEventArgs e)
    {
        if (ProbeCharacterCombo.SelectedItem is not CharacterOption character)
        {
            ProbeHint.Text = "请先选择一个角色";
            return;
        }

        var value = ManualChannelBox.Text.Trim();
        if (value.Length == 0)
        {
            ProbeHint.Text = "请输入频道名或 Channel ID";
            return;
        }

        var matched = _allChannels.FirstOrDefault(channel =>
            channel.CharacterId == character.Id
            && (string.Equals(channel.ChannelId, value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(channel.ChannelName, value, StringComparison.OrdinalIgnoreCase)));

        var channelId = matched?.ChannelId ?? (value.StartsWith("player_", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("fleet_", StringComparison.OrdinalIgnoreCase)
                ? value
                : string.Empty);
        var channelName = matched?.ChannelName ?? value;

        if (!AddListenerCore(character.Id, character.Name, channelId, channelName))
        {
            ProbeHint.Text = "该频道已经在监听列表中";
            return;
        }

        ManualChannelBox.Text = string.Empty;
        SaveIntelConfig();
        ReloadListenerRows();
        ProbeHint.Text = matched is null ? $"已添加 {value}，等待日志出现" : $"已添加 {channelName}";
    }

    private void OnListenerToggleClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ListenerRow row)
        {
            return;
        }

        row.Model.Enabled = !row.Model.Enabled;
        SaveIntelConfig();
        ReloadListenerRows();
    }

    private void OnListenerDeleteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ListenerRow row)
        {
            return;
        }

        _services.Config.Current.Intel.Listeners.Remove(row.Model);
        SaveIntelConfig();
        ReloadListenerRows();
    }

    // ---------------------------------------------------------------- 规则

    private void ReloadRuleRows()
    {
        _suppressRuleEvents = true;
        _ruleRows.Clear();

        for (var index = 0; index < _services.Config.Current.Intel.Rules.Count; index++)
        {
            _ruleRows.Add(new RuleRow(_services.Config.Current.Intel.Rules[index], index + 1));
        }

        RuleHint.Text = _ruleRows.Count == 0
            ? "还没有规则。添加后，命中区间的星系才会触发报警。"
            : "规则从上到下依次判断；区间重叠时先命中先生效。";

        _suppressRuleEvents = false;
    }

    private void OnAddRuleClick(object sender, RoutedEventArgs e)
    {
        var rules = _services.Config.Current.Intel.Rules;
        var last = rules.LastOrDefault();
        var min = last is null ? 0 : Math.Min(100, last.MaxJumps + 1);

        rules.Add(new IntelRule
        {
            MinJumps = min,
            MaxJumps = Math.Min(100, min + 3),
            Volume = 80,
            Enabled = true,
            Alarm = new AlarmSetting
            {
                Kind = AlarmSetting.KindPreset,
                Id = AudioPresets.DefaultId,
            },
        });

        SaveIntelConfig();
        ReloadRuleRows();
    }

    private void OnRuleFieldLostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressRuleEvents || (sender as FrameworkElement)?.DataContext is not RuleRow row)
        {
            return;
        }

        row.CommitRange();
        SaveIntelConfig();
    }

    private void OnRuleSoundChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRuleEvents || (sender as FrameworkElement)?.DataContext is not RuleRow row)
        {
            return;
        }

        row.SyncSound();
        SaveIntelConfig();
    }

    private void OnRuleSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressRuleEvents || (sender as FrameworkElement)?.DataContext is not RuleRow row)
        {
            return;
        }

        row.SyncVolume();
    }

    private void OnRuleSliderCommit(object sender, RoutedEventArgs e)
    {
        if (_suppressRuleEvents || (sender as FrameworkElement)?.DataContext is not RuleRow row)
        {
            return;
        }

        row.SyncVolume();
        SaveIntelConfig();
    }

    private void OnRuleEnabledClick(object sender, RoutedEventArgs e)
    {
        if (_suppressRuleEvents || (sender as FrameworkElement)?.DataContext is not RuleRow row)
        {
            return;
        }

        row.SyncEnabled();
        SaveIntelConfig();
    }

    private void OnRuleUpClick(object sender, RoutedEventArgs e) => MoveRule(sender, -1);

    private void OnRuleDownClick(object sender, RoutedEventArgs e) => MoveRule(sender, 1);

    private void MoveRule(object sender, int offset)
    {
        if ((sender as FrameworkElement)?.Tag is not RuleRow row)
        {
            return;
        }

        var rules = _services.Config.Current.Intel.Rules;
        var index = rules.IndexOf(row.Model);
        var target = index + offset;

        if (index < 0 || target < 0 || target >= rules.Count)
        {
            return;
        }

        (rules[index], rules[target]) = (rules[target], rules[index]);
        SaveIntelConfig();
        ReloadRuleRows();
    }

    private void OnRuleDeleteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RuleRow row)
        {
            return;
        }

        _services.Config.Current.Intel.Rules.Remove(row.Model);
        SaveIntelConfig();
        ReloadRuleRows();
    }

    private void OnRulePreviewClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RuleRow row)
        {
            return;
        }

        if (row.Model.Alarm.IsPreset)
        {
            var preset = AudioPresets.Find(row.Model.Alarm.Id) ?? AudioPresets.Default;
            _ = _services.Alarm.Preview(preset, row.Volume);
        }
        else if (!string.IsNullOrWhiteSpace(row.Model.Alarm.Path))
        {
            _ = _services.Alarm.PreviewFile(row.Model.Alarm.Path, row.Volume);
        }
    }

    // ---------------------------------------------------------------- 弹窗设置

    private void LoadPopupSettings()
    {
        _suppressPopupEvents = true;

        var state = _services.UiState.Current.IntelAlertWindow;
        state.Normalize();

        PositionModeCombo.SelectedValue = state.PositionMode;
        AnchorCombo.SelectedValue = state.Anchor;
        SizeCombo.SelectedValue = state.Size;
        AutoCloseCombo.SelectedValue = state.AutoCloseSeconds.ToString();
        ViewModeCombo.SelectedValue = state.ViewMode;

        OpacitySlider.Value = state.Opacity * 100;
        OpacityText.Text = $"{state.Opacity * 100:0}%";

        TopmostCheck.IsChecked = state.Topmost;
        ShowRegionCheck.IsChecked = state.ShowRegion;
        ShowSecurityCheck.IsChecked = state.ShowSecurity;
        ShowChannelCheck.IsChecked = state.ShowChannel;
        ShowCharacterCheck.IsChecked = state.ShowCharacter;
        ShowSoundCheck.IsChecked = state.ShowSound;

        var monitors = System.Windows.Forms.Screen.AllScreens
            .OrderByDescending(screen => screen.Primary)
            .Select(screen =>
            {
                var name = MonitorName.Resolve(screen.DeviceName);
                var label = string.IsNullOrWhiteSpace(name) ? screen.DeviceName : name;
                var primary = screen.Primary ? " · 主屏" : string.Empty;

                return new MonitorOption(
                    screen.DeviceName,
                    $"{label} · {screen.Bounds.Width}×{screen.Bounds.Height}{primary}");
            })
            .ToList();

        MonitorCombo.ItemsSource = monitors;
        MonitorCombo.SelectedItem = monitors.FirstOrDefault(option =>
            string.Equals(option.DeviceName, state.Monitor, StringComparison.OrdinalIgnoreCase))
            ?? monitors.FirstOrDefault(option =>
                string.Equals(option.DeviceName, System.Windows.Forms.Screen.PrimaryScreen?.DeviceName,
                    StringComparison.OrdinalIgnoreCase))
            ?? monitors.FirstOrDefault();

        PlacementXBox.Text = state.Placement is { } placement ? $"{placement.Left:0}" : string.Empty;
        PlacementYBox.Text = state.Placement is { } placementY ? $"{placementY.Top:0}" : string.Empty;

        PopupHint.Text = string.Equals(state.PositionMode, "custom", StringComparison.OrdinalIgnoreCase)
            ? "当前为自定义位置；拖动弹窗标题栏或输入坐标后保存。"
            : "当前为锚点位置；每次显示会按所选显示器的工作区重新计算。";

        _suppressPopupEvents = false;
    }

    private void OnPopupSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressPopupEvents || !IsLoaded)
        {
            return;
        }

        SavePopupSettings();
    }

    private void SavePopupSettings()
    {
        if (_suppressPopupEvents)
        {
            return;
        }

        var state = _services.UiState.Current.IntelAlertWindow;

        state.PositionMode = PositionModeCombo.SelectedValue as string ?? "anchor";
        state.Anchor = AnchorCombo.SelectedValue as string ?? "top-right";
        state.Monitor = (MonitorCombo.SelectedItem as MonitorOption)?.DeviceName ?? string.Empty;
        state.Size = SizeCombo.SelectedValue as string ?? "standard";
        state.ViewMode = ViewModeCombo.SelectedValue as string ?? "route";
        state.Opacity = Math.Clamp(OpacitySlider.Value / 100, 0.65, 1.0);

        state.AutoCloseSeconds = int.TryParse(AutoCloseCombo.SelectedValue?.ToString(), out var seconds)
            ? seconds
            : 10;

        state.Topmost = TopmostCheck.IsChecked == true;
        state.ShowRegion = ShowRegionCheck.IsChecked == true;
        state.ShowSecurity = ShowSecurityCheck.IsChecked == true;
        state.ShowChannel = ShowChannelCheck.IsChecked == true;
        state.ShowCharacter = ShowCharacterCheck.IsChecked == true;
        state.ShowSound = ShowSoundCheck.IsChecked == true;

        if (string.Equals(state.PositionMode, "custom", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(PlacementXBox.Text, out var left)
            && double.TryParse(PlacementYBox.Text, out var top))
        {
            state.Placement = new WindowPlacement { Left = left, Top = top };
        }

        _services.UiState.Save();
        IntelAlertWindow.Current?.RefreshWindowState();

        PopupHint.Text = string.Equals(state.PositionMode, "custom", StringComparison.OrdinalIgnoreCase)
            ? "自定义位置已保存；未填写坐标时可先拖动弹窗再使用「使用当前位置」。"
            : "锚点位置已保存；每次显示按目标显示器的可用区域计算。";
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // XAML 初始化时 Slider 会先于后面的 TextBlock 赋值，事件可能早于字段就绪。
        if (OpacityText is null)
        {
            return;
        }

        OpacityText.Text = $"{OpacitySlider.Value:0}%";

        if (_suppressPopupEvents || !IsLoaded)
        {
            return;
        }

        var state = _services.UiState.Current.IntelAlertWindow;
        state.Opacity = Math.Clamp(OpacitySlider.Value / 100, 0.65, 1.0);
        _services.UiState.Save();
        IntelAlertWindow.Current?.RefreshWindowState();
    }

    private void OnOpacityCommit(object sender, RoutedEventArgs e)
    {
        if (_suppressPopupEvents)
        {
            return;
        }

        _services.UiState.Save();
    }

    private void OnApplyCoordinatesClick(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(PlacementXBox.Text, out var left)
            || !double.TryParse(PlacementYBox.Text, out var top))
        {
            PopupHint.Text = "坐标必须是数字。";
            return;
        }

        var state = _services.UiState.Current.IntelAlertWindow;
        state.PositionMode = "custom";
        state.Anchor = AnchorCombo.SelectedValue as string ?? state.Anchor;
        state.Monitor = (MonitorCombo.SelectedItem as MonitorOption)?.DeviceName ?? string.Empty;
        state.Placement = new WindowPlacement { Left = left, Top = top };
        state.Opacity = Math.Clamp(OpacitySlider.Value / 100, 0.65, 1.0);
        state.AutoCloseSeconds = int.TryParse(AutoCloseCombo.SelectedValue?.ToString(), out var seconds)
            ? seconds
            : 10;
        state.Topmost = TopmostCheck.IsChecked == true;
        state.ViewMode = ViewModeCombo.SelectedValue as string ?? "route";
        state.ShowRegion = ShowRegionCheck.IsChecked == true;
        state.ShowSecurity = ShowSecurityCheck.IsChecked == true;
        state.ShowChannel = ShowChannelCheck.IsChecked == true;
        state.ShowCharacter = ShowCharacterCheck.IsChecked == true;
        state.ShowSound = ShowSoundCheck.IsChecked == true;

        PositionModeCombo.SelectedValue = "custom";
        _services.UiState.Save();
        IntelAlertWindow.Current?.RefreshWindowState();
        PopupHint.Text = $"已应用坐标 {left:0}, {top:0}。";
    }

    private void OnUseCurrentPositionClick(object sender, RoutedEventArgs e)
    {
        if (IntelAlertWindow.Current is not { IsVisible: true } window)
        {
            PopupHint.Text = "弹窗当前没有显示；先触发一次报警或拖动弹窗后再试。";
            return;
        }

        PlacementXBox.Text = $"{window.Left:0}";
        PlacementYBox.Text = $"{window.Top:0}";
        OnApplyCoordinatesClick(sender, e);
    }

    private void OnResetPopupPositionClick(object sender, RoutedEventArgs e)
    {
        var state = _services.UiState.Current.IntelAlertWindow;
        state.PositionMode = "anchor";
        state.Anchor = "top-right";
        state.Placement = null;
        _services.UiState.Save();
        LoadPopupSettings();
        IntelAlertWindow.Current?.RefreshWindowState();
        PopupHint.Text = "已恢复为右上角锚点。";
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
}

/// <summary>监听项行。</summary>
public sealed class ListenerRow : INotifyPropertyChanged
{
    private string _currentSystem = "位置未知";
    private string _state = "等待日志";

    public ListenerRow(IntelListener model)
    {
        Model = model;
    }

    public IntelListener Model { get; }

    public string CharacterName => Model.CharacterName;

    public string CharacterIdText => $"#{Model.CharacterId}";

    public string ChannelName => string.IsNullOrWhiteSpace(Model.ChannelName)
        ? Model.ChannelId
        : Model.ChannelName;

    public string ChannelIdText => string.IsNullOrWhiteSpace(Model.ChannelId)
        ? "等待日志绑定"
        : Model.ChannelId;

    public string CurrentSystem => _currentSystem;

    public string State => Model.Enabled ? _state : "已停用";

    public string EnabledText => Model.Enabled ? "停用" : "启用";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshStatus(IntelListenerStatus? status)
    {
        _currentSystem = status?.CurrentSystem ?? "位置未知";

        _state = status is null
            ? "等待日志"
            : status.PositionKnown ? status.State : "位置未知";

        Raise(nameof(CurrentSystem));
        Raise(nameof(State));
        Raise(nameof(EnabledText));
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>规则行。</summary>
public sealed class RuleRow : INotifyPropertyChanged
{
    public RuleRow(IntelRule model, int order)
    {
        Model = model;
        OrderText = order.ToString("00");
    }

    public IntelRule Model { get; }

    public IReadOnlyList<AudioPreset> SoundOptions => AudioPresets.All;

    public string OrderText { get; }

    public int MinJumps
    {
        get => Model.MinJumps;
        set
        {
            if (Model.MinJumps == value)
            {
                return;
            }

            Model.MinJumps = value;
            Raise();
        }
    }

    public int MaxJumps
    {
        get => Model.MaxJumps;
        set
        {
            if (Model.MaxJumps == value)
            {
                return;
            }

            Model.MaxJumps = value;
            Raise();
        }
    }

    public string SoundId
    {
        get => Model.Alarm.Id;
        set
        {
            if (string.Equals(Model.Alarm.Id, value, StringComparison.Ordinal))
            {
                return;
            }

            Model.Alarm.Kind = AlarmSetting.KindPreset;
            Model.Alarm.Id = value;
            Model.Alarm.Path = string.Empty;
            Raise();
        }
    }

    public int Volume
    {
        get => Model.Volume;
        set
        {
            if (Model.Volume == value)
            {
                return;
            }

            Model.Volume = value;
            Raise();
        }
    }

    public bool Enabled
    {
        get => Model.Enabled;
        set
        {
            if (Model.Enabled == value)
            {
                return;
            }

            Model.Enabled = value;
            Raise();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void CommitRange()
    {
        Model.MinJumps = Math.Clamp(Model.MinJumps, 0, 100);
        Model.MaxJumps = Math.Clamp(Math.Max(Model.MinJumps, Model.MaxJumps), 0, 100);
        Raise(nameof(MinJumps));
        Raise(nameof(MaxJumps));
    }

    public void SyncSound()
    {
        if (string.IsNullOrWhiteSpace(Model.Alarm.Id))
        {
            Model.Alarm.Id = AudioPresets.DefaultId;
        }

        Model.Alarm.Kind = AlarmSetting.KindPreset;
        Model.Alarm.Path = string.Empty;
        Raise(nameof(SoundId));
    }

    public void SyncVolume() => Raise(nameof(Volume));

    public void SyncEnabled() => Raise(nameof(Enabled));

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record CharacterOption(long Id, string Name)
{
    public string Display => $"{Name} · #{Id}";
}

public sealed record ChannelOption(ChatChannelInfo Info)
{
    public string Name => Info.ChannelName;

    public string Id => Info.ChannelId;

    public string Kind => Info.Kind switch
    {
        ChatChannelKind.Player => "玩家频道",
        ChatChannelKind.Fleet => "舰队频道",
        ChatChannelKind.Local => "本地频道",
        _ => "频道",
    };
}

public sealed record MonitorOption(string DeviceName, string Display)
{
    public override string ToString() => Display;
}

/// <summary>从 EDID 读取显示器品牌型号名称，用于目标显示器下拉框。</summary>
internal static class MonitorName
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public int StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice info, uint flags);

    public static string Resolve(string deviceName)
    {
        try
        {
            var monitor = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(deviceName, 0, ref monitor, 0))
            {
                return string.Empty;
            }

            return ReadName(monitor.DeviceId);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadName(string deviceId)
    {
        // DeviceID 形如 MONITOR\HKCB32C\{GUID}\0002，第二段是 EDID 的 PnP ID。
        var parts = deviceId.Split('\\');
        if (parts.Length < 2 || !string.Equals(parts[0], "MONITOR", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var pnpId = parts[1];
        using var root = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{pnpId}");
        if (root is null)
        {
            return pnpId;
        }

        foreach (var instance in root.GetSubKeyNames())
        {
            using var parameters = root.OpenSubKey($@"{instance}\Device Parameters");
            if (parameters?.GetValue("EDID") is not byte[] edid || edid.Length < 128)
            {
                continue;
            }

            var name = ParseEdidName(edid);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return pnpId;
    }

    private static string ParseEdidName(byte[] edid)
    {
        // EDID 描述块从第 54 字节开始，每块 18 字节；0xFC 表示显示器名称。
        for (var offset = 54; offset + 18 <= Math.Min(edid.Length, 126); offset += 18)
        {
            if (edid[offset + 3] != 0xFC)
            {
                continue;
            }

            var raw = Encoding.ASCII.GetString(edid, offset + 5, 13);
            var name = raw.Trim('\0', ' ', '\r', '\n');
            if (name.Length > 0)
            {
                return name;
            }
        }

        return string.Empty;
    }
}
