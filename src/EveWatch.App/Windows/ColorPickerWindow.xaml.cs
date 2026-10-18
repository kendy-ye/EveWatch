using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EveWatch.Core.Palette;

namespace EveWatch.App.Windows;

/// <summary>
/// 颜色提取器：从 ROI 截图中按 16×16 采样取色，或手动输入 HEX / RGB，加入颜色库。
/// <para>屏幕取色需要 Windows 的 EyeDropper，WPF 无对应 API，故不实现，仅保留像素网格与手动输入两条路径。</para>
/// </summary>
public partial class ColorPickerWindow : Window
{
    private static readonly System.Windows.Media.Brush InvalidBrush = CreateInvalidBrush();

    private readonly List<PaletteColor> _palette;
    private readonly ObservableCollection<PaletteColor> _items;

    private byte[]? _pixels;
    private int _pixelWidth;
    private int _pixelHeight;
    private double _zoom = 1;
    private byte _r = 0xAD;
    private byte _g = 0x06;
    private byte _b = 0x06;
    private bool _syncing;
    private bool _changed;

    public ColorPickerWindow(List<PaletteColor> palette, byte[]? roiBgra, int roiWidth, int roiHeight, string contextLabel)
    {
        _palette = palette;

        InitializeComponent();

        _items = new ObservableCollection<PaletteColor>(palette);
        PaletteHost.ItemsSource = _items;
        CtxText.Text = contextLabel;

        BuildGrid(roiBgra, roiWidth, roiHeight);

        var initial = palette.Count > 0 ? palette[0].Hex : "#AD0606";
        if (!ColorLut.TryParseHex(initial, out var r, out var g, out var b))
        {
            r = 0xAD;
            g = 0x06;
            b = 0x06;
        }

        SetColor(r, g, b, null);
        SwatchSource.Text = "来源：颜色库初始值，未取色";

        if (_pixels is null)
        {
            SwatchSource.Text = "来源：颜色库初始值（未取到 ROI 画面）";
        }
    }

    /// <summary>新增的颜色；调用方可自动勾选到当前 ROI。</summary>
    public PaletteColor? Added { get; private set; }

    /// <summary>颜色库是否被改动（新增或删除过自定义色）。</summary>
    public bool Changed => _changed;

    // ---------------------------------------------------------------- ROI 画面

    /// <summary>
    /// 把 ROI 画面按像素放大显示，供逐像素取色。
    /// <para>
    /// 不能把整条 ROI 压缩成固定网格：本地列表的 ROI 常见 27×945，压成 16×16 后
    /// 一个格子纵向跨 59 像素，缩略图完全认不出图标（实测显示成几条重复的蓝白横条）。
    /// 这里保留原始像素，按宽度自适应缩放后放进滚动区，点哪一格就是哪一个真实像素。
    /// </para>
    /// </summary>
    private void BuildGrid(byte[]? bgra, int width, int height)
    {
        var valid = bgra is not null && width > 0 && height > 0 && bgra.Length >= width * height * 4;

        _pixels = valid ? bgra : null;
        _pixelWidth = valid ? width : 0;
        _pixelHeight = valid ? height : 0;

        if (!valid)
        {
            GridImage.Source = null;
            ZoomText.Text = "无画面";
            HintText.Text = "未取到 ROI 画面（窗口可能已最小化），请手动输入 HEX / RGB。";
            return;
        }

        // 以「宽度放得进可视区」为基准定缩放，最小 1×、最大 8×；高度交给滚动条
        _zoom = Math.Clamp(252.0 / width, 1.0, 8.0);

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra!, width * 4, 0);

        GridImage.Source = bitmap;
        GridImage.Width = width * _zoom;
        GridImage.Height = height * _zoom;
        ZoomText.Text = $"{width} × {height} · 缩放 {_zoom:0.#}×";
        HintText.Text = "点击画面取该像素颜色，可用滚动条查看整条 ROI。";
    }

    private void OnGridClick(object sender, MouseButtonEventArgs e)
    {
        if (_pixels is null || _zoom <= 0)
        {
            return;
        }

        var p = e.GetPosition(GridImage);
        var sx = (int)Math.Floor(p.X / _zoom);
        var sy = (int)Math.Floor(p.Y / _zoom);

        if (sx < 0 || sx >= _pixelWidth || sy < 0 || sy >= _pixelHeight)
        {
            return;
        }

        var i = (sy * _pixelWidth + sx) * 4;
        SetColor(_pixels[i + 2], _pixels[i + 1], _pixels[i], null);
        SwatchSource.Text = $"来源：ROI 像素 ({sx}, {sy})";
    }

    // ---------------------------------------------------------------- 联动输入

    private void OnHexChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        if (ColorLut.TryParseHex(HexBox.Text, out var r, out var g, out var b))
        {
            SetColor(r, g, b, HexBox);
            SwatchSource.Text = "来源：手动输入";
        }
        else
        {
            HexBox.BorderBrush = InvalidBrush;
        }
    }

    private void OnRgbChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        if (byte.TryParse(RBox.Text, out var r) && byte.TryParse(GBox.Text, out var g) && byte.TryParse(BBox.Text, out var b))
        {
            SetColor(r, g, b, sender as System.Windows.Controls.TextBox);
            SwatchSource.Text = "来源：手动输入";
        }
    }

    /// <summary>同步色值到各控件；<paramref name="skip"/> 指向正在被用户输入的框，不覆盖其文本。</summary>
    private void SetColor(byte r, byte g, byte b, System.Windows.Controls.TextBox? skip)
    {
        _syncing = true;
        _r = r;
        _g = g;
        _b = b;

        var hex = ColorLut.ToHex(r, g, b);
        Swatch.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        SwatchHex.Text = hex.ToUpperInvariant();

        if (!ReferenceEquals(skip, HexBox))
        {
            HexBox.Text = hex.ToUpperInvariant();
        }

        if (!ReferenceEquals(skip, RBox))
        {
            RBox.Text = r.ToString();
        }

        if (!ReferenceEquals(skip, GBox))
        {
            GBox.Text = g.ToString();
        }

        if (!ReferenceEquals(skip, BBox))
        {
            BBox.Text = b.ToString();
        }

        HexBox.ClearValue(BorderBrushProperty);
        _syncing = false;
    }

    // ---------------------------------------------------------------- 颜色库

    private void OnDeleteColorClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PaletteColor color || color.Builtin)
        {
            return;
        }

        _items.Remove(color);
        _palette.Remove(color);

        var services = AppServices.Current;
        if (services is not null)
        {
            foreach (var account in services.Config.Current.Accounts)
            {
                foreach (var roi in account.Rois)
                {
                    roi.Colors.Remove(color.Id);
                }
            }
        }

        _changed = true;
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var hex = ColorLut.ToHex(_r, _g, _b);
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            name = $"自定义 {hex}";
        }

        var color = new PaletteColor
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Hex = hex,
            Builtin = false,
        };

        _palette.Add(color);
        _items.Add(color);

        Added = color;
        _changed = true;
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

    private static System.Windows.Media.Brush CreateInvalidBrush()
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x4D, 0x4D));
        brush.Freeze();
        return brush;
    }
}
