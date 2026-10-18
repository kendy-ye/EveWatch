using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using EveWatch.Core.Alarm;
using EveWatch.Core.Config;
using EveWatch.Core.Discovery;
using EveWatch.Core.Intel;
using EveWatch.Core.Map;
using IoPath = System.IO.Path;

namespace EveWatch.App.Windows;

/// <summary>
/// 日志预警置顶弹窗：最短路线 / 邻域关联图、批次列表、备选路线与位置持久化。
/// </summary>
public partial class IntelAlertWindow : Window
{
    private static readonly Brush PrimaryBrush = Freeze(0xF0, 0xA0, 0x2A);
    private static readonly Brush PrimaryHiBrush = Freeze(0xFF, 0xBF, 0x66);
    private static readonly Brush RedBrush = Freeze(0xFF, 0x5D, 0x5D);
    private static readonly Brush SlateBrush = Freeze(0x5A, 0x6B, 0x7A);
    private static readonly Brush DimTextBrush = Freeze(0x8A, 0x97, 0xA5);
    private static readonly Brush DimLineBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
    private static readonly Brush RedHaloBrush = new SolidColorBrush(Color.FromArgb(0x3D, 0xFF, 0x5D, 0x5D));
    private static readonly Brush RouteLineBrush = Freeze(0x3A, 0x4A, 0x5A);
    private static readonly Brush CardCurrentFill = Freeze(0x1A, 0x17, 0x10);
    private static readonly Brush CardAlertFill = Freeze(0x1E, 0x11, 0x14);
    private static readonly Brush CardFill = Freeze(0x15, 0x1B, 0x21);
    private static readonly Brush CardStroke = Freeze(0x2D, 0x39, 0x44);
    private static readonly Brush CardViaStroke = Freeze(0x8A, 0x6A, 0x32);
    private static readonly Brush CardText = Freeze(0xE3, 0xEB, 0xF2);
    private static readonly Brush CardTextDim = Freeze(0x5A, 0x66, 0x72);
    private static readonly Brush AltCardFill = Freeze(0x12, 0x10, 0x0C);
    private static readonly Brush AltCardStroke = Freeze(0x4A, 0x3A, 0x22);
    private static readonly Brush AltCardText = Freeze(0xB9, 0xA2, 0x7A);
    private static readonly Brush AltLineBrush = Freeze(0x8A, 0x6A, 0x32);

    private readonly AppServices _services;
    private readonly ObservableCollection<IntelAlertItem> _items = [];
    private readonly DispatcherTimer _timer;

    private IntelAlertItem? _selected;
    private bool _alternativeVisible;
    private bool _paused;
    private TimeSpan _pausedRemaining;
    private DateTime _deadline;
    private double _dragLeft;
    private double _dragTop;
    private bool _suppressSelection;

    private bool _rightDragging;
    private bool _rightDragMoved;
    private Point _rightDragStartScreen;
    private double _rightDragStartLeft;
    private double _rightDragStartTop;

    public IntelAlertWindow(AppServices services)
    {
        _services = services;
        Current = this;

        InitializeComponent();
        BatchList.ItemsSource = _items;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _timer.Tick += (_, _) => OnTimerTick();

        Loaded += (_, _) => ApplyWindowState();
        Closed += (_, _) =>
        {
            _timer.Stop();
            if (ReferenceEquals(Current, this))
            {
                Current = null;
            }
        };
    }

    public static IntelAlertWindow? Current { get; private set; }

    /// <summary>由预警服务在 UI 线程调用；窗口已存在时合并到当前批次。</summary>
    public void ShowBatch(IntelAlertBatch batch)
    {
        var added = new List<IntelAlertItem>();

        foreach (var item in batch.Items)
        {
            var duplicate = _items.Any(existing =>
                existing.CharacterId == item.CharacterId
                && existing.Target.Id == item.Target.Id
                && Math.Abs((existing.Time - item.Time).TotalSeconds) < 3);

            if (!duplicate)
            {
                _items.Add(item);
                added.Add(item);
            }
        }

        SortItems();

        var characters = _items.Select(item => item.CharacterId).Distinct().Count();
        AccountBadge.Visibility = characters > 1 ? Visibility.Visible : Visibility.Collapsed;
        AccountBadgeText.Text = $"{characters} 个账号";

        BatchList.Visibility = _items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        // 主图跟随最新一条报警；用户点选列表时由 OnBatchSelectionChanged 覆盖选择。
        var focus = added.Count > 0
            ? added.OrderBy(item => item.Time).Last()
            : _selected ?? _items.OrderBy(item => item.Time).LastOrDefault();
        SelectItem(focus, updateList: true);

        if (focus is not null && _items.Count > 1)
        {
            BatchList.ScrollIntoView(focus);
        }

        if (!IsVisible)
        {
            Show();
        }

        UpdateLayout();
        ApplyWindowState();
        ResetCountdown();
    }

    /// <summary>设置面板修改位置、透明度等后即时刷新。</summary>
    public void RefreshWindowState()
    {
        ApplyWindowState();
    }

    private void SortItems()
    {
        // 最新的报警排在最前，同一时间再按跳数从近到远。
        var sorted = _items
            .OrderByDescending(item => item.Time)
            .ThenBy(item => item.Jumps)
            .ToList();

        _items.Clear();
        foreach (var item in sorted)
        {
            _items.Add(item);
        }
    }

    private void SelectItem(IntelAlertItem? item, bool updateList)
    {
        _selected = item;
        if (item is null)
        {
            return;
        }

        if (updateList)
        {
            _suppressSelection = true;
            BatchList.SelectedItem = item;
            _suppressSelection = false;
        }

        TargetName.Text = item.Target.Name;
        JumpsText.Text = $"{item.Jumps} 跳";

        CharacterText.Text = item.CharacterName;
        ChannelText.Text = string.IsNullOrWhiteSpace(item.ChannelName) ? item.ChannelId : item.ChannelName;
        TimeText.Text = item.Time.ToString("HH:mm:ss");

        MessageText.Text = item.Message;
        MessageBox.ToolTip = string.IsNullOrWhiteSpace(item.Message) ? null : item.Message;
        MessageBox.Visibility = string.IsNullOrWhiteSpace(item.Message)
            ? Visibility.Collapsed
            : Visibility.Visible;

        RuleText.Text = $"{item.Rule.MinJumps}-{item.Rule.MaxJumps} 跳";
        RegionText.Text = item.Target.RegionName;
        SecurityText.Text = item.Target.Security.ToString("0.00");

        var sound = item.Rule.Alarm.IsPreset
            ? AudioPresets.Find(item.Rule.Alarm.Id)?.Name ?? item.Rule.Alarm.Id
            : IoPath.GetFileName(item.Rule.Alarm.Path);

        SoundText.Text = $"{sound} · {item.Rule.Volume}%";

        var alternativeJumps = item.AlternativePath.Count - 1;
        AlternativeButton.Visibility = alternativeJumps > 0 ? Visibility.Visible : Visibility.Collapsed;
        AlternativeButton.Content = _alternativeVisible ? "隐藏备选" : "备选路线";
        AlternativeButton.ToolTip = alternativeJumps > 0 ? $"另有 {alternativeJumps} 跳备选路线" : null;

        var state = _services.UiState.Current.IntelAlertWindow;
        VizTitle.Text = string.Equals(state.ViewMode, "neighborhood", StringComparison.OrdinalIgnoreCase)
            ? "邻域关联图"
            : "最短星门路径";
        VizHint.Text = string.Equals(state.ViewMode, "neighborhood", StringComparison.OrdinalIgnoreCase)
            ? $"{Math.Max(5, item.Jumps + 1)} 跳邻域"
            : "每段 = 1 跳";

        ApplyMetaVisibility();
        DrawGraph();
    }

    private void ApplyMetaVisibility()
    {
        var state = _services.UiState.Current.IntelAlertWindow;

        CharacterText.Visibility = state.ShowCharacter ? Visibility.Visible : Visibility.Collapsed;
        CharacterSeparator.Visibility = state.ShowCharacter ? Visibility.Visible : Visibility.Collapsed;
        ChannelText.Visibility = state.ShowChannel ? Visibility.Visible : Visibility.Collapsed;
        ChannelSeparator.Visibility = state.ShowChannel ? Visibility.Visible : Visibility.Collapsed;

        MetaSound.Visibility = state.ShowSound ? Visibility.Visible : Visibility.Collapsed;
        MetaRegion.Visibility = state.ShowRegion ? Visibility.Visible : Visibility.Collapsed;
        MetaSecurity.Visibility = state.ShowSecurity ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyWindowState()
    {
        var state = _services.UiState.Current.IntelAlertWindow;
        state.Normalize();

        // 与 HTML 原型保持一致的紧凑 / 标准 / 大三档宽度
        Width = state.Size switch
        {
            "compact" => 480,
            "large" => 720,
            _ => 600,
        };

        TargetName.FontSize = state.Size == "compact" ? 24 : 26;
        Opacity = state.Opacity;
        Topmost = state.Topmost;

        TopmostChipText.Text = state.Topmost ? "置顶" : "非置顶";
        PinIcon.Stroke = state.Topmost ? PrimaryBrush : DimTextBrush;
        PinButton.ToolTip = state.Topmost ? "取消置顶" : "置顶显示";
        AccentBorder.Background = state.Topmost ? RedBrush : (Brush)FindResource("BorderStrongBrush");

        if (_selected is { } item)
        {
            VizTitle.Text = string.Equals(state.ViewMode, "neighborhood", StringComparison.OrdinalIgnoreCase)
                ? "邻域关联图"
                : "最短星门路径";
            VizHint.Text = string.Equals(state.ViewMode, "neighborhood", StringComparison.OrdinalIgnoreCase)
                ? $"{Math.Max(5, item.Jumps + 1)} 跳邻域"
                : "每段 = 1 跳";
        }

        ApplyMetaVisibility();
        UpdateLayout();
        ApplyPosition(state);

        if (_selected is not null)
        {
            DrawGraph();
        }
    }

    private void ApplyPosition(IntelAlertWindowState state)
    {
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : 340;

        if (string.Equals(state.PositionMode, "custom", StringComparison.OrdinalIgnoreCase)
            && state.Placement is { } placement)
        {
            Left = ClampLeft(placement.Left, width);
            Top = ClampTop(placement.Top, height);
            return;
        }

        var screen = FindScreen(state.Monitor);
        var dpi = VisualTreeHelper.GetDpi(this);
        var area = screen.WorkingArea;

        var left = area.Left / dpi.DpiScaleX;
        var top = area.Top / dpi.DpiScaleY;
        var right = area.Right / dpi.DpiScaleX;
        var bottom = area.Bottom / dpi.DpiScaleY;

        double x;
        double y;

        switch (state.Anchor)
        {
            case "top-left":
                x = left + 12;
                y = top + 12;
                break;

            case "bottom-left":
                x = left + 12;
                y = bottom - height - 12;
                break;

            case "bottom-right":
                x = right - width - 12;
                y = bottom - height - 12;
                break;

            case "center":
                x = left + (right - left - width) / 2;
                y = top + (bottom - top - height) / 2;
                break;

            case "wall-side":
                var wall = RoiWallWindow.Current;
                if (wall is not null)
                {
                    x = wall.Left + wall.ActualWidth + 12;
                    y = wall.Top;
                    if (x + width > right)
                    {
                        x = wall.Left - width - 12;
                    }
                }
                else
                {
                    x = right - width - 12;
                    y = top + 12;
                }

                break;

            default:
                x = right - width - 12;
                y = top + 12;
                break;
        }

        Left = ClampLeft(x, width);
        Top = ClampTop(y, height);
    }

    private static double ClampLeft(double left, double width)
    {
        var min = SystemParameters.VirtualScreenLeft - width + 40;
        var max = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40;
        return Math.Clamp(left, min, Math.Max(min, max));
    }

    private static double ClampTop(double top, double height)
    {
        var min = SystemParameters.VirtualScreenTop - height + 40;
        var max = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;
        return Math.Clamp(top, min, Math.Max(min, max));
    }

    private static System.Windows.Forms.Screen FindScreen(string monitor)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;

        if (!string.IsNullOrWhiteSpace(monitor))
        {
            var match = screens.FirstOrDefault(screen =>
                string.Equals(screen.DeviceName, monitor, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                return match;
            }
        }

        return System.Windows.Forms.Screen.PrimaryScreen ?? screens[0];
    }

    private void SaveCustomPosition()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var screen = handle != nint.Zero
            ? System.Windows.Forms.Screen.FromHandle(handle)
            : System.Windows.Forms.Screen.PrimaryScreen;

        var state = _services.UiState.Current.IntelAlertWindow;
        state.PositionMode = "custom";
        state.Placement = new WindowPlacement { Left = Left, Top = Top };
        state.Monitor = screen?.DeviceName ?? string.Empty;
        _services.UiState.Save();
    }

    // ---------------------------------------------------------------- 计时

    private void ResetCountdown()
    {
        var seconds = _services.UiState.Current.IntelAlertWindow.AutoCloseSeconds;

        if (seconds <= 0)
        {
            _timer.Stop();
            CountdownText.Text = "不自动关闭";
            CountdownBar.Visibility = Visibility.Collapsed;
            return;
        }

        CountdownBar.Visibility = Visibility.Visible;
        _paused = false;
        _pausedRemaining = TimeSpan.FromSeconds(seconds);
        _deadline = DateTime.UtcNow.AddSeconds(seconds);
        _timer.Start();
        UpdateCountdown();
    }

    private void OnTimerTick()
    {
        if (_paused)
        {
            return;
        }

        if (DateTime.UtcNow >= _deadline)
        {
            HideAlert();
            return;
        }

        UpdateCountdown();
    }

    private void UpdateCountdown()
    {
        if (_paused)
        {
            CountdownText.Text = "暂停中";
            return;
        }

        var total = _services.UiState.Current.IntelAlertWindow.AutoCloseSeconds;
        if (total <= 0)
        {
            return;
        }

        var remaining = _deadline - DateTime.UtcNow;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        CountdownText.Text = $"{(int)Math.Ceiling(remaining.TotalSeconds)} 秒后关闭";
        CountdownBar.Value = Math.Clamp(remaining.TotalSeconds / total * 100, 0, 100);
    }

    private void OnGraphMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (GraphScroll.ScrollableWidth <= 0)
        {
            return;
        }

        GraphScroll.ScrollToHorizontalOffset(GraphScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (_timer.IsEnabled && !_paused)
        {
            _pausedRemaining = _deadline - DateTime.UtcNow;
            if (_pausedRemaining < TimeSpan.Zero)
            {
                _pausedRemaining = TimeSpan.Zero;
            }

            _paused = true;
            UpdateCountdown();
        }
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (_paused)
        {
            _deadline = DateTime.UtcNow + _pausedRemaining;
            _paused = false;
        }
    }

    private void HideAlert()
    {
        _timer.Stop();

        // 关闭后清空本批次，下一次报警从干净列表开始。
        _items.Clear();
        _selected = null;
        _alternativeVisible = false;
        _rightDragging = false;

        Hide();
    }

    // ---------------------------------------------------------------- 绘图

    private void DrawGraph()
    {
        GraphCanvas.Children.Clear();

        if (_selected is not { } item)
        {
            return;
        }

        var state = _services.UiState.Current.IntelAlertWindow;
        var viewport = GraphScroll.ViewportWidth > 0
            ? GraphScroll.ViewportWidth
            : GraphScroll.ActualWidth;

        if (viewport <= 0)
        {
            viewport = Math.Max(120, Width - 32);
        }

        if (string.Equals(state.ViewMode, "neighborhood", StringComparison.OrdinalIgnoreCase)
            && _services.Intel.Map is { } map)
        {
            GraphCanvas.Width = viewport;

            if (DrawNeighborhood(item, map))
            {
                GraphScroll.ScrollToLeftEnd();
                return;
            }
        }

        // 长路径不再压缩到读不清：卡片保持最小宽度，超长时出现横向滚动条。
        var pathCount = Math.Max(
            item.Path.Count,
            _alternativeVisible ? item.AlternativePath.Count : 0);
        var requiredWidth = 24 + pathCount * 64 + (pathCount + 1) * 8;

        GraphCanvas.Width = Math.Max(viewport, requiredWidth);
        VizHint.Text = GraphCanvas.Width > viewport + 1
            ? "左右滚动 · 每段 = 1 跳"
            : "每段 = 1 跳";

        DrawRoute(item.Path, alternative: false);

        if (_alternativeVisible && item.AlternativePath.Count > 1)
        {
            DrawRoute(item.AlternativePath, alternative: true);
        }

        GraphScroll.ScrollToLeftEnd();
    }

    private void DrawRoute(IReadOnlyList<SolarSystem> path, bool alternative)
    {
        if (path.Count == 0)
        {
            return;
        }

        var width = GraphCanvas.Width;
        if (double.IsNaN(width) || width <= 0)
        {
            width = GraphCanvas.ActualWidth > 0 ? GraphCanvas.ActualWidth : Width - 32;
        }

        var height = GraphCanvas.ActualHeight;

        if (path.Count == 1)
        {
            if (!alternative)
            {
                DrawSameSystem(path[0], width, height);
            }

            return;
        }

        var nodeHeight = alternative ? 30d : 38d;
        var top = alternative
            ? height - nodeHeight - 14
            : _alternativeVisible ? 38d : (height - nodeHeight) / 2 - 6;

        var nodeWidth = Math.Min(alternative ? 72 : 82, (width - 40) / path.Count - 4);
        nodeWidth = Math.Max(44, nodeWidth);

        var gap = (width - path.Count * nodeWidth) / (path.Count + 1);
        if (gap < 6)
        {
            gap = 6;
            nodeWidth = Math.Max(42, (width - (path.Count + 1) * gap) / path.Count);
        }

        if (alternative)
        {
            AddText($"备选路线 · {path.Count - 1} 跳", 6, top - 15, 8.5, AltCardText, font: "Body");
        }

        for (var index = 0; index < path.Count; index++)
        {
            var x = gap + index * (nodeWidth + gap);

            if (index > 0)
            {
                var previousX = gap + (index - 1) * (nodeWidth + gap);
                CanvasPositionLine(
                    previousX + nodeWidth,
                    top + nodeHeight / 2,
                    x,
                    top + nodeHeight / 2,
                    alternative);
            }

            var isCurrent = index == 0;
            var isTarget = index == path.Count - 1 && !alternative;
            var isVia = !isCurrent && !isTarget && !alternative;

            if (!alternative)
            {
                var roleText = isCurrent ? "当前位置" : isTarget ? "警报位置" : "途经";
                var roleColor = isCurrent ? PrimaryBrush : isTarget ? RedBrush : CardTextDim;
                AddText(roleText, x + nodeWidth / 2, top - 13, 8.5, roleColor, font: "Body", centered: true, width: nodeWidth);
            }

            var fill = alternative
                ? AltCardFill
                : isCurrent ? CardCurrentFill : isTarget ? CardAlertFill : CardFill;
            var stroke = alternative
                ? AltCardStroke
                : isCurrent ? PrimaryBrush : isTarget ? RedBrush : isVia ? CardViaStroke : CardStroke;
            var nameBrush = alternative ? AltCardText : CardText;
            var nameSize = alternative ? 10 : 11;

            if (isTarget)
            {
                AddTargetHalo(x + nodeWidth / 2, top + nodeHeight / 2, nodeWidth + 18);
            }

            AddCard(path[index], x, top, nodeWidth, nodeHeight, fill, stroke, nameBrush, nameSize);
        }
    }

    private void CanvasPositionLine(double x1, double y1, double x2, double y2, bool alternative)
    {
        GraphCanvas.Children.Add(new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = alternative ? AltLineBrush : RouteLineBrush,
            StrokeThickness = alternative ? 1.2 : 1.4,
            StrokeDashArray = alternative ? new DoubleCollection { 4, 3 } : null,
        });

        GraphCanvas.Children.Add(new Ellipse
        {
            Width = 4,
            Height = 4,
            Fill = alternative ? AltLineBrush : SlateBrush,
            IsHitTestVisible = false,
        });

        var dot = (Ellipse)GraphCanvas.Children[^1];
        Canvas.SetLeft(dot, x1 - 2);
        Canvas.SetTop(dot, y1 - 2);
    }

    private void DrawSameSystem(SolarSystem system, double width, double height)
    {
        var cardWidth = Math.Min(120, width - 40);
        var cardHeight = 38;
        var x = (width - cardWidth) / 2;
        var y = Math.Max(38, (height - cardHeight) / 2 - 10);

        AddRing(width / 2, y + cardHeight / 2, cardWidth + 30, 0x24);
        AddRing(width / 2, y + cardHeight / 2, cardWidth + 8, 0x3D);

        AddCard(system, x, y, cardWidth, cardHeight,
            CardAlertFill, RedBrush, CardText, 12);

        AddText("当前位置 = 警报位置", width / 2, y + cardHeight + 12, 9.5, RedBrush,
            font: "Body", centered: true, width: 220);
    }

    private void AddRing(double centerX, double centerY, double size, byte alpha)
    {
        var ring = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = Brushes.Transparent,
            Stroke = new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0x4D, 0x4D)),
            StrokeThickness = 1.2,
            IsHitTestVisible = false,
        };

        Canvas.SetLeft(ring, centerX - size / 2);
        Canvas.SetTop(ring, centerY - size / 2);
        GraphCanvas.Children.Add(ring);
    }

    private void AddText(
        string text,
        double x,
        double y,
        double fontSize,
        Brush brush,
        string font = "Mono",
        bool centered = false,
        double width = 0)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            Foreground = brush,
            FontFamily = (FontFamily)FindResource(font == "Body" ? "BodyFont" : "MonoFont"),
            IsHitTestVisible = false,
        };

        if (centered)
        {
            label.Width = width;
            label.TextAlignment = TextAlignment.Center;
            Canvas.SetLeft(label, x - width / 2);
        }
        else
        {
            Canvas.SetLeft(label, x);
        }

        Canvas.SetTop(label, y);
        GraphCanvas.Children.Add(label);
    }

    private void AddCard(
        SolarSystem system,
        double x,
        double y,
        double width,
        double height,
        Brush fill,
        Brush stroke,
        Brush nameBrush,
        double nameSize)
    {
        var label = new TextBlock
        {
            Text = system.Name,
            FontFamily = (FontFamily)FindResource("MonoFont"),
            FontSize = nameSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = nameBrush,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 0, 4, 0),
        };

        var card = new Border
        {
            Width = width,
            Height = height,
            Background = fill,
            BorderBrush = stroke,
            BorderThickness = new Thickness(1.3),
            CornerRadius = new CornerRadius(7),
            Child = label,
            ToolTip = $"{system.Name} · 安全 {system.Security:0.00} · {system.RegionName}",
        };

        Canvas.SetLeft(card, x);
        Canvas.SetTop(card, y);
        GraphCanvas.Children.Add(card);
    }

    private bool DrawNeighborhood(IntelAlertItem item, GalaxyMap map)
    {
        var current = item.Path.FirstOrDefault();
        if (current is null)
        {
            return false;
        }

        var depthLimit = Math.Min(6, Math.Max(5, item.Jumps + 1));
        var distances = map.DistancesFrom(current.Id, depthLimit);

        if (!distances.ContainsKey(item.Target.Id))
        {
            return false;
        }

        var maxDepth = distances.Values.DefaultIfEmpty(0).Max();
        var pathIds = item.Path.Select(system => system.Id).ToHashSet();
        var pathIndex = item.Path
            .Select((system, index) => (system.Id, index))
            .ToDictionary(pair => pair.Id, pair => pair.index);

        // 每一列按“路径节点优先，其余按安全等级/名称”取前 5 个，避免真实宇宙里一列塞满几十个节点。
        var selected = new Dictionary<int, SolarSystem>();

        for (var depth = 0; depth <= maxDepth; depth++)
        {
            var column = distances
                .Where(pair => pair.Value == depth)
                .Select(pair => map.Find(pair.Key))
                .Where(system => system is not null)
                .Cast<SolarSystem>()
                .OrderBy(system => pathIndex.GetValueOrDefault(system.Id, int.MaxValue))
                .ThenBy(system => system.Id == item.Target.Id ? 0 : 1)
                .ThenByDescending(system => system.Security)
                .ThenBy(system => system.Name, StringComparer.Ordinal)
                .ToList();

            var count = 0;
            foreach (var system in column)
            {
                if (count >= 5 || !selected.TryAdd(system.Id, system))
                {
                    continue;
                }

                count++;
            }

            foreach (var system in item.Path)
            {
                if (distances.TryGetValue(system.Id, out var systemDepth) && systemDepth == depth)
                {
                    selected[system.Id] = system;
                }
            }
        }

        selected[current.Id] = current;
        selected[item.Target.Id] = item.Target;

        var width = GraphCanvas.Width;
        if (double.IsNaN(width) || width <= 0)
        {
            width = GraphCanvas.ActualWidth > 0 ? GraphCanvas.ActualWidth : Width - 32;
        }

        var height = GraphCanvas.ActualHeight;

        var nodeWidth = Math.Min(72, (width - 32) / (maxDepth + 1) - 4);
        nodeWidth = Math.Max(48, nodeWidth);
        var columnStep = maxDepth > 0
            ? Math.Min(100, (width - 32 - nodeWidth) / maxDepth)
            : 0;

        var positions = new Dictionary<int, (double X, double Y, double W, double H)>();
        var pathEdges = BuildEdgeSet(item.Path);
        var alternativeEdges = _alternativeVisible
            ? BuildEdgeSet(item.AlternativePath)
            : new HashSet<string>(StringComparer.Ordinal);

        for (var depth = 0; depth <= maxDepth; depth++)
        {
            var nodes = selected.Values
                .Where(system => distances.TryGetValue(system.Id, out var d) && d == depth)
                .OrderBy(system => pathIndex.GetValueOrDefault(system.Id, int.MaxValue))
                .ThenBy(system => system.Id == item.Target.Id ? 0 : 1)
                .ThenBy(system => system.Name, StringComparer.Ordinal)
                .ToList();

            if (nodes.Count == 0)
            {
                continue;
            }

            var total = nodes.Count * 24 + (nodes.Count - 1) * 4;
            var start = 24 + Math.Max(0, (height - 24 - total) / 2);
            var x = maxDepth == 0 ? (width - nodeWidth) / 2 : 16 + depth * columnStep;

            AddText($"{depth} 跳", x + nodeWidth / 2, 8, 8.5, CardTextDim, "Body", true, nodeWidth);

            for (var index = 0; index < nodes.Count; index++)
            {
                positions[nodes[index].Id] = (x, start + index * 28, nodeWidth, 24);
            }
        }

        // 先画边：普通边细而暗，路径边用主色，备选路线用虚线。
        foreach (var system in selected.Values)
        {
            foreach (var neighbor in map.Neighbors(system.Id))
            {
                if (!selected.ContainsKey(neighbor.Id) || neighbor.Id < system.Id)
                {
                    continue;
                }

                var from = positions[system.Id];
                var to = positions[neighbor.Id];
                var key = EdgeKey(system.Id, neighbor.Id);
                var onPath = pathEdges.Contains(key);
                var onAlternative = alternativeEdges.Contains(key) && !onPath;

                var stroke = onPath ? PrimaryBrush : onAlternative ? AltLineBrush : CardStroke;
                var thickness = onPath ? 1.8 : onAlternative ? 1.3 : 1.0;
                var dash = onAlternative ? new DoubleCollection { 4, 3 } : null;

                if (Math.Abs(from.X - to.X) < 1)
                {
                    var elbowX = from.X + from.W + 7;
                    var y1 = from.Y + from.H / 2;
                    var y2 = to.Y + to.H / 2;

                    GraphCanvas.Children.Add(new Polyline
                    {
                        Points = new PointCollection
                        {
                            new(from.X + from.W, y1),
                            new(elbowX, y1),
                            new(elbowX, y2),
                            new(to.X + to.W, y2),
                        },
                        Stroke = stroke,
                        StrokeThickness = thickness,
                        StrokeDashArray = dash,
                        Opacity = onPath || onAlternative ? 0.9 : 0.55,
                    });
                }
                else
                {
                    var left = from.X < to.X ? from : to;
                    var right = from.X < to.X ? to : from;
                    GraphCanvas.Children.Add(new Line
                    {
                        X1 = left.X + left.W,
                        Y1 = left.Y + left.H / 2,
                        X2 = right.X,
                        Y2 = right.Y + right.H / 2,
                        Stroke = stroke,
                        StrokeThickness = thickness,
                        StrokeDashArray = dash,
                        Opacity = onPath || onAlternative ? 0.9 : 0.55,
                    });
                }
            }
        }

        foreach (var system in selected.Values)
        {
            var position = positions[system.Id];
            var isCurrent = system.Id == current.Id;
            var isTarget = system.Id == item.Target.Id;
            var isVia = pathIds.Contains(system.Id) && !isCurrent && !isTarget;

            var fill = isTarget ? CardAlertFill : isCurrent ? CardCurrentFill : CardFill;
            var stroke = isTarget ? RedBrush : isCurrent ? PrimaryBrush : isVia ? CardViaStroke : CardStroke;
            var nameBrush = isCurrent || isTarget || isVia ? CardText : CardTextDim;

            if (isTarget)
            {
                AddTargetHalo(position.X + position.W / 2, position.Y + position.H / 2, position.W + 16);
            }

            AddCard(system, position.X, position.Y, position.W, position.H,
                fill, stroke, nameBrush, 9);
        }

        return true;
    }

    private void AddTargetHalo(double x, double y, double size)
    {
        var halo = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = RedHaloBrush,
            IsHitTestVisible = false,
        };

        Canvas.SetLeft(halo, x - size / 2);
        Canvas.SetTop(halo, y - size / 2);
        GraphCanvas.Children.Add(halo);

        var animation = new DoubleAnimation(0.15, 0.55, TimeSpan.FromSeconds(1.1))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };

        halo.BeginAnimation(OpacityProperty, animation);
    }

    private static HashSet<string> BuildEdgeSet(IReadOnlyList<SolarSystem> path)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < path.Count - 1; index++)
        {
            result.Add(EdgeKey(path[index].Id, path[index + 1].Id));
        }

        return result;
    }

    private static string EdgeKey(int a, int b) => a < b ? $"{a}-{b}" : $"{b}-{a}";

    private static Brush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // ---------------------------------------------------------------- 交互

    /// <summary>按住右键拖动整个弹窗；松开后保存为自定义位置。</summary>
    private void OnShellMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractive(e.OriginalSource as DependencyObject))
        {
            return;
        }

        _rightDragging = true;
        _rightDragMoved = false;
        _rightDragStartScreen = PointToScreen(e.GetPosition(this));
        _rightDragStartLeft = Left;
        _rightDragStartTop = Top;

        Shell.CaptureMouse();
        e.Handled = true;
    }

    private void OnShellMouseMove(object sender, MouseEventArgs e)
    {
        if (!_rightDragging)
        {
            return;
        }

        var current = PointToScreen(e.GetPosition(this));
        var dpi = VisualTreeHelper.GetDpi(this);
        var deltaX = (current.X - _rightDragStartScreen.X) / dpi.DpiScaleX;
        var deltaY = (current.Y - _rightDragStartScreen.Y) / dpi.DpiScaleY;

        if (!_rightDragMoved && (Math.Abs(deltaX) > 1 || Math.Abs(deltaY) > 1))
        {
            _rightDragMoved = true;
        }

        Left = _rightDragStartLeft + deltaX;
        Top = _rightDragStartTop + deltaY;
        e.Handled = true;
    }

    private void OnShellMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_rightDragging)
        {
            return;
        }

        EndRightDrag();
        e.Handled = true;
    }

    private void OnShellLostMouseCapture(object sender, MouseEventArgs e) => _rightDragging = false;

    private void EndRightDrag()
    {
        _rightDragging = false;

        if (Shell.IsMouseCaptured)
        {
            Shell.ReleaseMouseCapture();
        }

        // 只移动过才切换为自定义位置，避免单纯右键点击改变已保存的锚点。
        if (_rightDragMoved)
        {
            SaveCustomPosition();
        }
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || IsInteractive(e.OriginalSource as DependencyObject))
        {
            return;
        }

        _dragLeft = Left;
        _dragTop = Top;

        try
        {
            DragMove();
        }
        catch
        {
            return;
        }

        if (Math.Abs(Left - _dragLeft) > 1 || Math.Abs(Top - _dragTop) > 1)
        {
            SaveCustomPosition();
        }
    }

    private static bool IsInteractive(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button or TextBox or ComboBox or ListBox or ScrollBar)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void OnBatchSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
        {
            return;
        }

        if (BatchList.SelectedItem is IntelAlertItem item)
        {
            SelectItem(item, updateList: false);
        }
    }

    private void OnToggleAlternativeClick(object sender, RoutedEventArgs e)
    {
        _alternativeVisible = !_alternativeVisible;
        AlternativeButton.Content = _alternativeVisible ? "隐藏备选" : "备选路线";
        DrawGraph();
    }

    private void OnTogglePinClick(object sender, RoutedEventArgs e)
    {
        var state = _services.UiState.Current.IntelAlertWindow;
        state.Topmost = !state.Topmost;
        Topmost = state.Topmost;
        TopmostChipText.Text = state.Topmost ? "置顶" : "非置顶";
        PinIcon.Stroke = state.Topmost ? PrimaryBrush : DimTextBrush;
        PinButton.ToolTip = state.Topmost ? "取消置顶" : "置顶显示";
        AccentBorder.Background = state.Topmost ? RedBrush : (Brush)FindResource("BorderStrongBrush");
        _services.UiState.Save();
    }

    private void OnCopySystemClick(object sender, RoutedEventArgs e)
    {
        if (_selected is { } item)
        {
            try
            {
                Clipboard.SetText(item.Target.Name);
            }
            catch
            {
                // 剪贴板被占用时忽略
            }
        }
    }

    private void OnFocusCharacterClick(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } item)
        {
            return;
        }

        var window = _services.Directory.Find(item.CharacterName);
        if (window is not null)
        {
            WindowFinder.Activate(window.Hwnd);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => HideAlert();
}
