using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EveWatch.App.ViewModels;
using EveWatch.Core.Config;
using EveWatch.Core.Settings;

namespace EveWatch.App.Windows;

/// <summary>
/// 游戏布局同步：把某个 Profile 下的 core_char / core_user 设置文件从一个模板覆盖到多个目标。
/// <para>执行前必须完全关闭 EVE 客户端与启动器，否则退出时会回写覆盖；由 <see cref="GameProcessGuard"/> 守卫。</para>
/// </summary>
public partial class SettingsSyncWindow : Window
{
    private readonly AppServices _services;
    private readonly SettingsCloner _cloner = new();
    private readonly CharacterNameResolver _resolver = new();
    private readonly ClonePreferenceStore _prefStore = new();
    private readonly ObservableCollection<SyncRow> _templateRows = [];
    private readonly ObservableCollection<SyncRow> _targetRows = [];
    private readonly ObservableCollection<LogLine> _log = [];

    private List<SettingsProfile> _profiles = [];
    private SettingsProfile? _source;
    private SettingsProfile? _target;

    private ClonePreferences _prefs = new();
    private bool _resolving;

    private SettingsFileKind _scope = SettingsFileKind.Character;
    private List<SettingsFile> _sourceFiles = [];
    private List<SettingsFile> _targetFiles = [];
    private SettingsFile? _template;
    private readonly HashSet<string> _selectedPaths = new(StringComparer.OrdinalIgnoreCase);

    private CloneResult? _lastResult;
    private bool _blocked;
    private bool _loading;

    public SettingsSyncWindow(AppServices services)
    {
        _services = services;
        _prefs = _prefStore.Load();

        InitializeComponent();

        TemplateHost.ItemsSource = _templateRows;
        TargetHost.ItemsSource = _targetRows;
        LogHost.ItemsSource = _log;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    // ---------------------------------------------------------------- 生命周期

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateLayout();

        var saved = _services.UiState.Current.SyncWindow;
        if (saved is not null)
        {
            Left = saved.Left;
            Top = saved.Top;
        }
        else
        {
            var area = SystemParameters.WorkArea;
            Left = Math.Max(12, area.Left + (area.Width - ActualWidth) / 2);
            Top = Math.Max(12, area.Top + (area.Height - ActualHeight) / 2 - 24);
        }

        UpdateScopeButtons();
        ReloadScan();

        Append("info", $"定位设置根目录 {SettingsRootLocator.GetRootDirectory()}");
        Append("info", $"发现 {_profiles.Count} 个 Profile（settings_*）");
        Append("info", $"扫描到 {_sourceFiles.Count} 个模板文件、{_targetFiles.Count} 个目标文件");

        CheckGuard();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _services.UiState.Current.SyncWindow = new WindowPlacement { Left = Left, Top = Top };
        _services.UiState.Save();
        _resolver.Dispose();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

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

    // ---------------------------------------------------------------- 扫描

    private string ScopeText => _scope == SettingsFileKind.Character
        ? "角色 core_char_*.dat"
        : "账号 core_user_*.dat";

    private void ReloadScan()
    {
        _loading = true;
        _profiles = SettingsRootLocator.FindProfiles().ToList();
        SourceCombo.ItemsSource = _profiles;
        TargetCombo.ItemsSource = _profiles;

        if (_profiles.Count == 0)
        {
            _source = null;
            _target = null;
            SourceCombo.SelectedItem = null;
            TargetCombo.SelectedItem = null;
            _sourceFiles = [];
            _targetFiles = [];
            _template = null;
            _selectedPaths.Clear();
            _loading = false;
            BuildRows();
            Append("warn", "未发现任何 settings_* Profile，无法同步。");
            return;
        }

        if (_source is null || !_profiles.Contains(_source))
        {
            _source = _profiles[0];
        }

        if (_target is null || !_profiles.Contains(_target))
        {
            _target = _profiles[0];
        }

        SourceCombo.SelectedItem = _source;
        TargetCombo.SelectedItem = _target;
        _loading = false;

        LoadFiles();
    }

    private List<SettingsFile> ScanDir(SettingsProfile? profile)
    {
        if (profile is null)
        {
            return [];
        }

        return (_scope == SettingsFileKind.Character
            ? SettingsScanner.ScanCharacters(profile.Directory)
            : SettingsScanner.ScanUsers(profile.Directory)).ToList();
    }

    /// <summary>按当前 Profile 与范围重新扫描，模板取第一个文件，目标默认全选（模板除外）。</summary>
    private void LoadFiles()
    {
        _sourceFiles = ScanDir(_source);
        _targetFiles = ScanDir(_target);
        _template = _sourceFiles.Count > 0 ? _sourceFiles[0] : null;

        _selectedPaths.Clear();
        foreach (var file in _targetFiles)
        {
            if (_template is null || !SamePath(file.Path, _template.Path))
            {
                _selectedPaths.Add(file.Path);
            }
        }

        BuildRows();
        Append("info", $"扫描 {ScopeText}：模板 {_sourceFiles.Count} 个 · 目标 {_targetFiles.Count} 个");
        _ = ResolveNamesAsync();
    }

    /// <summary>重新扫描但保留当前模板与选择。</summary>
    private void RescanPreserving()
    {
        var templatePath = _template?.Path;
        var previous = new HashSet<string>(_selectedPaths, StringComparer.OrdinalIgnoreCase);

        _sourceFiles = ScanDir(_source);
        _targetFiles = ScanDir(_target);
        _template = _sourceFiles.FirstOrDefault(f => templatePath is not null && SamePath(f.Path, templatePath))
            ?? _sourceFiles.FirstOrDefault();

        _selectedPaths.Clear();
        foreach (var file in _targetFiles)
        {
            if (_template is not null && SamePath(file.Path, _template.Path))
            {
                continue;
            }

            if (previous.Contains(file.Path))
            {
                _selectedPaths.Add(file.Path);
            }
        }

        BuildRows();
        _ = ResolveNamesAsync();
    }

    private void BuildRows()
    {
        _templateRows.Clear();
        foreach (var file in _sourceFiles)
        {
            var row = MakeRow(file);
            row.IsActive = _template is not null && SamePath(file.Path, _template.Path);
            _templateRows.Add(row);
        }

        _targetRows.Clear();
        foreach (var file in _targetFiles)
        {
            if (_template is not null && SamePath(file.Path, _template.Path))
            {
                continue;
            }

            var row = MakeRow(file);
            row.IsSelected = _selectedPaths.Contains(file.Path);
            row.IsSame = _template is not null && SettingsFileComparison.AreIdentical(_template.Path, file.Path);
            _targetRows.Add(row);
        }

        UpdateStatus();
        UpdateButtons();
    }

    /// <summary>
    /// 造一行列表项。名称随维度取：角色维度是 ESI 解析出的角色名，
    /// 账号维度是本地别名；都没拿到时回退为 <c>#ID</c>。
    /// </summary>
    private SyncRow MakeRow(SettingsFile file)
    {
        var row = new SyncRow(file)
        {
            Name = NameOf(file.Id),
            IsNameEditable = _scope == SettingsFileKind.User,
        };

        // 订阅在赋值之后：初值不该被当成用户改名写回别名表
        row.PropertyChanged += OnRowPropertyChanged;
        return row;
    }

    private string NameOf(long id) =>
        _scope == SettingsFileKind.Character
            ? _resolver.GetName(id)
            : _prefs.AccountAliases.TryGetValue(id, out string? alias) && !string.IsNullOrWhiteSpace(alias)
                ? alias
                : $"账号 #{id}";

    /// <summary>账号维度的名称即别名：改动即时落盘，改回默认文本视为清除。</summary>
    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_scope != SettingsFileKind.User || sender is not SyncRow row || e.PropertyName != nameof(SyncRow.Name))
        {
            return;
        }

        var alias = row.Name?.Trim();
        if (string.IsNullOrEmpty(alias) || alias.StartsWith("账号 #", StringComparison.Ordinal))
        {
            _prefs.AccountAliases.Remove(row.File.Id);
        }
        else
        {
            _prefs.AccountAliases[row.File.Id] = alias;
        }

        _prefStore.Save(_prefs);
    }

    /// <summary>
    /// 解析角色名（ESI <c>universe/names</c>）。只在角色维度做，结果进本地缓存，
    /// 已缓存的不再请求；离线或限流时保持 <c>#ID</c>，不影响克隆本身。
    /// </summary>
    private async Task ResolveNamesAsync()
    {
        if (_scope != SettingsFileKind.Character || _resolving)
        {
            return;
        }

        var ids = _sourceFiles.Concat(_targetFiles).Select(f => f.Id).Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        _resolving = true;
        try
        {
            await _resolver.ResolveAsync(ids);

            foreach (var row in _templateRows.Concat(_targetRows))
            {
                row.Name = _resolver.GetName(row.File.Id);
            }

            var unknown = ids.Count(id => _resolver.GetName(id).StartsWith('#'));
            if (unknown == 0)
            {
                Append("ok", $"角色名解析完成：{ids.Count} 个角色");
            }
            else
            {
                Append("warn", $"角色名解析 {ids.Count - unknown}/{ids.Count} 个，其余保持 #ID（离线、限流或接口未收录）");
            }
        }
        finally
        {
            _resolving = false;
        }
    }

    private void UpdateStatus()
    {
        var selected = _targetRows.Where(r => r.IsSelected).ToList();
        var same = selected.Count(r => r.IsSame);

        StatusText.Text = $"已选 {selected.Count} 个 · 将覆盖 {selected.Count - same} 个 · 其中 {same} 个内容已一致（跳过）";
    }

    private void UpdateButtons()
    {
        BtnPreview.IsEnabled = !_blocked;
        BtnRun.IsEnabled = !_blocked;
        BtnRestore.IsEnabled = !_blocked && _lastResult is not null;
    }

    // ---------------------------------------------------------------- 守卫

    /// <summary>检测客户端进程；返回 true 表示可以安全写入。</summary>
    private bool CheckGuard()
    {
        var running = GameProcessGuard.GetRunningProcesses();
        _blocked = running.Count > 0;
        UpdateGuardVisual();

        if (_blocked)
        {
            Append("err", $"GameProcessGuard：检测到 {string.Join("、", running)} 正在运行，克隆已锁定。");
        }

        return !_blocked;
    }

    private void UpdateGuardVisual()
    {
        if (_blocked)
        {
            GuardPill.Background = Brush("DangerDimBrush");
            GuardPill.BorderBrush = Brush("DangerBrush");
            GuardText.Foreground = Brush("DangerBrush");
            GuardText.Text = "检测到 EVE 客户端运行中";
            GuardDot.Fill = Brush("DangerBrush");

            WarnBar.Background = Brush("DangerDimBrush");
            WarnBar.BorderBrush = Brush("DangerBrush");
            WarnIcon.Foreground = Brush("DangerBrush");
            WarnText.Foreground = Brush("DangerBrush");
            WarnText.Text = "检测到 EVE 客户端正在运行，克隆已锁定。请先退出全部客户端与启动器，再点上方状态条重新检测。";
        }
        else
        {
            GuardPill.Background = Brush("PrimaryDimBrush");
            GuardPill.BorderBrush = Brush("PrimaryBrush");
            GuardText.Foreground = Brush("PrimaryHiBrush");
            GuardText.Text = "EVE 客户端未运行";
            GuardDot.Fill = Brush("PrimaryBrush");

            WarnBar.Background = Brush("PrimaryDimBrush");
            WarnBar.BorderBrush = Brush("PrimaryBrush");
            WarnIcon.Foreground = Brush("PrimaryHiBrush");
            WarnText.Foreground = Brush("TextSubBrush");
            WarnText.Text =
                "执行前必须完全关闭 EVE 客户端与启动器，否则客户端退出时会回写设置、覆盖本次改动。" + Environment.NewLine +
                "克隆会连带聊天频道设置，存在 alt 暴露风险。" + Environment.NewLine +
                "建议目标与模板使用相同分辨率，否则窗口位置可能出现偏移。";
        }

        UpdateButtons();
    }

    private void OnGuardClick(object sender, MouseButtonEventArgs e) => CheckGuard();

    private System.Windows.Media.Brush Brush(string key) => (System.Windows.Media.Brush)FindResource(key);

    // ---------------------------------------------------------------- 选择

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SourceCombo.SelectedItem is not SettingsProfile profile)
        {
            return;
        }

        _source = profile;
        LoadFiles();
        Append("info", $"源 Profile → {profile.Name}（{profile.Directory}）");
    }

    private void OnTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TargetCombo.SelectedItem is not SettingsProfile profile)
        {
            return;
        }

        _target = profile;
        LoadFiles();
        Append("info", $"目标 Profile → {profile.Name}（{profile.Directory}）");
    }

    private void OnScopeCharClick(object sender, RoutedEventArgs e) => SetScope(SettingsFileKind.Character);

    private void OnScopeUserClick(object sender, RoutedEventArgs e) => SetScope(SettingsFileKind.User);

    private void SetScope(SettingsFileKind scope)
    {
        if (_scope == scope)
        {
            return;
        }

        _scope = scope;
        UpdateScopeButtons();
        LoadFiles();
        Append("info", $"范围切换为 {ScopeText}");
    }

    private void UpdateScopeButtons()
    {
        ApplyScope(ScopeChar, _scope == SettingsFileKind.Character);
        ApplyScope(ScopeUser, _scope == SettingsFileKind.User);

        static void ApplyScope(System.Windows.Controls.Button button, bool active)
        {
            button.Background = active
                ? (System.Windows.Media.Brush)Application.Current.FindResource("Surface4Brush")
                : System.Windows.Media.Brushes.Transparent;
            button.Foreground = active
                ? (System.Windows.Media.Brush)Application.Current.FindResource("TextBrush")
                : (System.Windows.Media.Brush)Application.Current.FindResource("TextSubBrush");
        }
    }

    private void OnRescanClick(object sender, RoutedEventArgs e)
    {
        _profiles = SettingsRootLocator.FindProfiles().ToList();
        SourceCombo.ItemsSource = _profiles;
        TargetCombo.ItemsSource = _profiles;

        RescanPreserving();
        CheckGuard();
        Append("info", $"重新扫描：发现 {_profiles.Count} 个 Profile，模板 {_sourceFiles.Count} 个、目标 {_targetFiles.Count} 个");
    }

    private void OnTemplateRowClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SyncRow row)
        {
            return;
        }

        _template = row.File;
        _selectedPaths.Remove(row.File.Path);
        BuildRows();
        Append("info", $"模板改为 {row.File.FileName}");
    }

    private void OnTargetRowClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SyncRow row)
        {
            return;
        }

        if (!_selectedPaths.Remove(row.File.Path))
        {
            _selectedPaths.Add(row.File.Path);
        }

        BuildRows();
    }

    private void OnSelectAllTargetsClick(object sender, RoutedEventArgs e)
    {
        foreach (var file in _targetFiles)
        {
            if (_template is not null && SamePath(file.Path, _template.Path))
            {
                continue;
            }

            _selectedPaths.Add(file.Path);
        }

        BuildRows();
    }

    private void OnInvertTargetsClick(object sender, RoutedEventArgs e)
    {
        foreach (var file in _targetFiles)
        {
            if (_template is not null && SamePath(file.Path, _template.Path))
            {
                continue;
            }

            if (!_selectedPaths.Remove(file.Path))
            {
                _selectedPaths.Add(file.Path);
            }
        }

        BuildRows();
    }

    private void OnClearTargetsClick(object sender, RoutedEventArgs e)
    {
        _selectedPaths.Clear();
        BuildRows();
    }

    private List<SettingsFile> SelectedTargets()
    {
        var result = new List<SettingsFile>();
        foreach (var file in _targetFiles)
        {
            if (_template is not null && SamePath(file.Path, _template.Path))
            {
                continue;
            }

            if (_selectedPaths.Contains(file.Path))
            {
                result.Add(file);
            }
        }

        return result;
    }

    // ---------------------------------------------------------------- 动作

    private void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        if (!CheckGuard())
        {
            Append("err", "拒绝预览：检测到 EVE 客户端正在运行。");
            return;
        }

        if (_template is null)
        {
            Append("warn", "未选择模板文件。");
            return;
        }

        var targets = SelectedTargets();
        if (targets.Count == 0)
        {
            Append("warn", "未选择任何目标。");
            return;
        }

        Append("info", "──────── 预览（Dry-run）────────");
        Append("info", $"模板 {_template.FileName}  →  {targets.Count} 个目标");

        var copy = new List<SettingsFile>();
        var same = new List<SettingsFile>();
        foreach (var target in targets)
        {
            (SettingsFileComparison.AreIdentical(_template.Path, target.Path) ? same : copy).Add(target);
        }

        if (copy.Count > 0)
        {
            Append("ok", $"将覆盖 {copy.Count} 个：{string.Join("、", copy.Select(t => t.FileName))}");
        }

        if (same.Count > 0)
        {
            Append("info", $"跳过 {same.Count} 个（内容已一致）：{string.Join("、", same.Select(t => t.FileName))}");
        }

        Append("info", $"备份目录 {_cloner.Backups.Root}");
    }

    private void OnRunClick(object sender, RoutedEventArgs e)
    {
        if (!CheckGuard())
        {
            Append("err", "拒绝执行：检测到 EVE 客户端正在运行，退出时会把设置回写覆盖。");
            return;
        }

        if (_template is null || _target is null)
        {
            Append("warn", "未选择模板或目标 Profile。");
            return;
        }

        var targets = SelectedTargets();
        if (targets.Count == 0)
        {
            Append("warn", "未选择任何目标。");
            return;
        }

        Append("info", "──────── 执行克隆 ────────");

        CloneResult result;
        try
        {
            result = _cloner.Clone(_target, _template, targets);
        }
        catch (Exception ex)
        {
            Append("err", $"克隆失败：{ex.Message}");
            return;
        }

        _lastResult = result;

        if (!string.IsNullOrEmpty(result.BackupDirectory))
        {
            Append("ok", $"备份目录 {result.BackupDirectory}");
        }

        var failed = result.Items.Count(i => !i.Success);
        Append("ok", $"完成：覆盖 {result.CopiedCount} · 跳过 {result.SkippedCount} · 失败 {failed}");

        foreach (var item in result.Items.Where(i => !i.Success))
        {
            Append("err", $"失败 {item.TargetFileName}：{item.Error}");
        }

        _services.Events.Info("EveWatch", "客户端设置已同步", $"{_template.FileName} → 覆盖 {result.CopiedCount} 个");

        RescanPreserving();
        UpdateButtons();
    }

    private void OnBackupClick(object sender, RoutedEventArgs e)
    {
        var profile = _target ?? _source;
        if (profile is null)
        {
            Append("warn", "尚未选择 Profile。");
            return;
        }

        Append("info", $"备份根目录  {_cloner.Backups.Root}");

        var entries = _cloner.Backups.ListBackups(profile);
        if (entries.Count == 0)
        {
            Append("info", "  （该 Profile 暂无备份点）");
            return;
        }

        foreach (var entry in entries.Take(30))
        {
            Append("info", $"  {entry.Timestamp:yyyyMMdd_HHmmss}   {entry.Files.Count} 个文件");
        }
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (!CheckGuard())
        {
            Append("err", "拒绝还原：检测到 EVE 客户端正在运行。");
            return;
        }

        if (_lastResult is null)
        {
            Append("warn", "没有可还原的本次克隆记录。");
            return;
        }

        int restored;
        try
        {
            restored = _cloner.Restore(_lastResult);
        }
        catch (Exception ex)
        {
            Append("err", $"还原失败：{ex.Message}");
            return;
        }

        Append("ok", $"已从本次备份还原 {restored} 个文件，回到克隆前状态");
        _lastResult = null;

        RescanPreserving();
        UpdateButtons();
    }

    // ---------------------------------------------------------------- 日志

    private void Append(string level, string text)
    {
        var foreground = level switch
        {
            "ok" => "#FFE3EBF2",
            "warn" => "#FFE0A45C",
            "err" => "#FFFF8F8F",
            _ => "#FF5A6672",
        };

        _log.Add(new LogLine(DateTime.Now.ToString("HH:mm:ss"), text, foreground));
        while (_log.Count > 200)
        {
            _log.RemoveAt(0);
        }

        LogScroll.UpdateLayout();
        LogScroll.ScrollToEnd();
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

/// <summary>同步列表里的一行（一个设置文件）。</summary>
internal sealed class SyncRow : ObservableObject
{
    private string _name = string.Empty;
    private bool _isNameEditable;
    private bool _isActive;
    private bool _isSelected;
    private bool _isSame;

    public SyncRow(SettingsFile file) => File = file;

    public SettingsFile File { get; }

    public string IdLabel => $"#{File.Id}";

    /// <summary>角色名 / 账号别名；未解析时为 <c>#ID</c>。</summary>
    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    /// <summary>名称是否可编辑（只有账号维度允许起别名）。</summary>
    public bool IsNameEditable
    {
        get => _isNameEditable;
        set => Set(ref _isNameEditable, value);
    }

    public string MetaText =>
        $"{File.FileName} · {Math.Round(File.Length / 1024.0)} KB · {File.LastWriteTimeUtc.ToLocalTime():MM-dd HH:mm}";

    /// <summary>是否为当前模板。</summary>
    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    /// <summary>是否被勾选为目标。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>与模板内容是否一致。</summary>
    public bool IsSame
    {
        get => _isSame;
        set
        {
            if (Set(ref _isSame, value))
            {
                Raise(nameof(StatusText));
            }
        }
    }

    public string StatusText => IsSame ? "一致" : "待覆盖";
}

/// <summary>日志区的一行。</summary>
internal sealed class LogLine(string time, string text, string foreground)
{
    public string Time { get; } = time;

    public string Text { get; } = text;

    public string Foreground { get; } = foreground;

    public string Display => $"{Time}  {Text}";
}
