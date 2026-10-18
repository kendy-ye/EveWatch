using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EveWatch.App.ViewModels;
using EveWatch.Core.Cloning;
using EveWatch.Core.Config;

namespace EveWatch.App.Windows;

/// <summary>
/// 账号配置克隆：把基准账号的 ROI / 颜色 / 音频 / 启用状态复制到选定的目标账号。
/// <para>基准账号与目标账号都在窗口内选择，目标可多选。<c>ShowDialog()</c> 返回 true 表示已应用。</para>
/// </summary>
public partial class CloneWindow : Window
{
    private readonly AppServices _services;
    private readonly List<AccountSetting> _accounts;
    private readonly ObservableCollection<CloneTargetItem> _targets = [];
    private bool _loading;
    private bool _firstBuild = true;

    public CloneWindow(AppServices services, AccountSetting? defaultSource)
    {
        _services = services;
        _accounts = [.. services.Config.Current.Accounts];

        InitializeComponent();

        TargetList.ItemsSource = _targets;
        SourceCombo.ItemsSource = _accounts;

        // 赋值会触发 SelectionChanged，此时目标列表尚未建立，先挡住
        _loading = true;
        SourceCombo.SelectedItem = defaultSource is not null && _accounts.Contains(defaultSource)
            ? defaultSource
            : _accounts.FirstOrDefault();
        _loading = false;

        RebuildTargets();
    }

    private AccountSetting? Source => SourceCombo.SelectedItem as AccountSetting;

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        RebuildTargets();
    }

    /// <summary>
    /// 按当前基准账号重建目标列表。
    /// <para>首次建立时默认全选；之后切换基准账号只保留原有勾选，不去动用户的选择。</para>
    /// </summary>
    private void RebuildTargets()
    {
        var source = Source;
        var keep = new HashSet<string>(
            _targets.Where(t => t.Selected).Select(t => t.Name),
            StringComparer.Ordinal);

        var selectAll = _firstBuild;
        _firstBuild = false;

        _targets.Clear();

        foreach (var account in _accounts)
        {
            if (source is not null && ReferenceEquals(account, source))
            {
                continue;
            }

            _targets.Add(new CloneTargetItem(account)
            {
                Selected = selectAll || keep.Contains(account.Name),
            });
        }

        UpdateSummary();
    }

    private CloneOptions BuildOptions() => new()
    {
        // ROI 默认不克隆：坐标是相对窗口内容区的，目标账号界面布局不同就会落到别处
        Rois = ChkRois.IsChecked == true,
        Colors = ChkColors.IsChecked == true,
        Audio = ChkAudio.IsChecked == true,
        Enabled = ChkEnabled.IsChecked == true,
    };

    private void OnTargetToggled(object sender, RoutedEventArgs e) => UpdateSummary();

    private void OnOptionChanged(object sender, RoutedEventArgs e) => UpdateSummary();

    private void OnSelectAllTargetsClick(object sender, RoutedEventArgs e) => SetAllTargets(true);

    private void OnClearTargetsClick(object sender, RoutedEventArgs e) => SetAllTargets(false);

    private void OnInvertTargetsClick(object sender, RoutedEventArgs e)
    {
        foreach (var target in _targets)
        {
            target.Selected = !target.Selected;
        }

        UpdateSummary();
    }

    private void SetAllTargets(bool selected)
    {
        foreach (var target in _targets)
        {
            target.Selected = selected;
        }

        UpdateSummary();
    }

    private void UpdateSummary()
    {
        // 构造期间 CheckBox 先于按钮创建，需容错
        if (BtnOk is null || TargetCountText is null)
        {
            return;
        }

        var selected = _targets.Count(t => t.Selected);

        TargetCountText.Text = $"已选 {selected} / {_targets.Count}";
        BtnOk.IsEnabled = selected > 0 && BuildOptions().Any;
        BtnOk.Content = selected > 0 ? $"克隆到 {selected} 个账号" : "克隆";
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var source = Source;
        var targets = _targets.Where(t => t.Selected).Select(t => t.Setting).ToList();
        var options = BuildOptions();

        if (source is null || targets.Count == 0 || !options.Any)
        {
            return;
        }

        var changed = AccountCloner.Apply(source, targets, options);

        _services.Events.Info("EveWatch", "账号配置已克隆", $"{source.Name} → {changed} 个账号");
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // 非按下状态调用会抛异常，忽略
        }
    }
}

/// <summary>克隆窗口里的一个目标账号。</summary>
internal sealed class CloneTargetItem : ObservableObject
{
    private bool _selected;

    public CloneTargetItem(AccountSetting setting)
    {
        Setting = setting;
    }

    public AccountSetting Setting { get; }

    public string Name => Setting.Name;

    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }
}
