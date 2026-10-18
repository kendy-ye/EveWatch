using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using EveWatch.Core.Alarm;
using EveWatch.Core.Capture;
using EveWatch.Core.Cloning;
using EveWatch.Core.Config;
using EveWatch.Core.Detect;
using EveWatch.Core.Diagnostics;
using EveWatch.Core.Discovery;
using EveWatch.Core.Events;
using EveWatch.Core.Palette;
using EveWatch.Core.Settings;
using EveWatch.Core.Status;
using EveWatch.Core.Watch;

namespace EveWatch.Selftest;

/// <summary>
/// 命令行自检与基准工具。
/// <para>用于在没有界面时验证采集、判定、设置扫描是否正常，并测量实机成本。</para>
/// </summary>
internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(nint value);

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        try
        {
            // PerMonitorV2，保证 GetClientRect 与 WGC 内容区是同一套物理像素
            _ = SetProcessDpiAwarenessContext(new nint(-4));
        }
        catch
        {
            // Win10 以下忽略
        }

        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            PrintUsage();
            return 0;
        }

        var store = new ConfigStore();
        var config = store.Load();

        return args[0].ToLowerInvariant() switch
        {
            "--list" => ListWindows(config),
            "--shot" => Shot(config, args),
            "--detect" => Detect(config, args),
            "--bench" => Bench(config, args),
            "--sync-scan" => SyncScan(),
            "--stress" => Stress(config, args),
            "--detect-selftest" => DetectSelfTest(config),
            "--gen-config" => GenConfig(store, config),
            _ => Unknown(args[0]),
        };
    }

    // ---------------------------------------------------------------- 命令

    private static int ListWindows(AppConfig config)
    {
        Console.WriteLine($"目标进程      : {config.ProcessName}");
        Console.WriteLine($"WGC 支持      : {WgcCaptureService.IsSupported()}");
        Console.WriteLine($"可关捕获边框  : {WgcCaptureService.SupportsBorderToggle}");
        Console.WriteLine($"可限帧        : {WgcCaptureService.SupportsMinUpdateInterval}");
        Console.WriteLine();

        var windows = WindowFinder.Enumerate(config.ProcessName);
        Console.WriteLine($"发现 {windows.Count} 个窗口");

        for (var i = 0; i < windows.Count; i++)
        {
            var w = windows[i];
            var mark = string.Equals(w.ClassName, "trinityWindow", StringComparison.OrdinalIgnoreCase) ? "●" : "○";
            var state = w.Minimized ? "已最小化 " : string.Empty;

            Console.WriteLine(
                $"  [{i}] {mark} 0x{w.Hwnd:X}  {w.ClientWidth}×{w.ClientHeight}  " +
                $"pid={w.ProcessId}  class={w.ClassName}  {state}账号={w.AccountName}");
        }

        return 0;
    }

    private static int Detect(AppConfig config, string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法：--detect <账号名> [x,y,w,h]");
            return 1;
        }

        var accountName = args[1];
        var roi = new RoiRect(0, 0, 25, 500);

        if (args.Length >= 3 && !RoiRect.TryParse(args[2], out roi))
        {
            Console.WriteLine($"ROI 参数无法解析：{args[2]}");
            return 1;
        }

        var window = WindowFinder.FindByAccount(config.ProcessName, accountName);
        if (window is null)
        {
            Console.WriteLine($"找不到账号「{accountName}」的窗口，先用 --list 看看。");
            return 1;
        }

        var palette = PaletteFor(config, accountName);
        var lut = new ColorLut();
        lut.Rebuild(palette, config.Detect.ToLutTuning());

        using var capture = new WgcCaptureService();
        if (!capture.Attach(window.Hwnd, 0))
        {
            Console.WriteLine("采集会话建立失败。");
            return 1;
        }

        var frame = capture.CaptureRoi(window.Hwnd, roi);
        if (frame is null)
        {
            Console.WriteLine("取帧失败（窗口最小化或尚未渲染）。");
            return 1;
        }

        var detector = new IconBlockDetector();
        var result = new DetectResult();
        detector.Detect(frame.Bgra, frame.Width, frame.Height, lut, config.Detect, result);

        Console.WriteLine($"窗口      : 0x{window.Hwnd:X}  内容区 {capture.ContentWidth(window.Hwnd)}×{capture.ContentHeight(window.Hwnd)}");
        Console.WriteLine($"ROI       : {roi}  实际截到 {frame.Width}×{frame.Height}");
        Console.WriteLine($"颜色库    : {string.Join('、', palette.Select(p => p.Name))}");
        Console.WriteLine($"命中      : {DescribeHits(lut, result)}");
        Console.WriteLine($"纵向区间  : {result.SegmentsSeen}　丢弃 尺寸 {result.RejectedSmall} / 像素数 {result.RejectedPixels}");
        Console.WriteLine($"采集耗时  : {capture.LastCaptureCostMs(window.Hwnd):F2} ms");
        Console.WriteLine($"限帧      : {capture.AppliedMinIntervalMs(window.Hwnd)} ms");
        PrintColorHistogram(frame.Bgra, frame.Width, frame.Height, lut);
        PrintMatchedHsv(frame.Bgra, frame.Width, frame.Height, lut);
        PrintTopMatchedColors(frame.Bgra, frame.Width, frame.Height, lut);
        PrintBlocks(lut, result);

        var outputDirectory = Path.Combine(AppContext.BaseDirectory, "shots");
        var safeName = Sanitize(accountName);
        var outputPath = Path.Combine(outputDirectory, $"{safeName}_{roi.X}_{roi.Y}.png");
        var zoomPath = Path.Combine(outputDirectory, $"{safeName}_{roi.X}_{roi.Y}_zoom4.png");

        // ROI 只有几十像素宽，原尺寸下数不清块；放大 4 倍供肉眼核对
        WriteUpscaled(zoomPath, frame.Bgra, frame.Width, frame.Height, 4);

        var canvas = (byte[])frame.Bgra.Clone();
        for (var i = 0; i < result.HitCount; i++)
        {
            var hit = result.Hits[i];
            DrawRect(canvas, frame.Width, frame.Height, hit.X - 1, hit.Y - 1, hit.W + 2, hit.H + 2);
        }

        PngWriter.WriteBgra(outputPath, canvas, frame.Width, frame.Height);
        Console.WriteLine($"命中可视化: {outputPath}");
        Console.WriteLine($"放大 4 倍 : {zoomPath}");

        return 0;
    }

    private static int Shot(AppConfig config, string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法：--shot <账号名>");
            return 1;
        }

        var accountName = args[1];
        var window = WindowFinder.FindByAccount(config.ProcessName, accountName);
        if (window is null)
        {
            Console.WriteLine($"找不到账号「{accountName}」的窗口，先用 --list 看看。");
            return 1;
        }

        using var capture = new WgcCaptureService();
        if (!capture.Attach(window.Hwnd, 0))
        {
            Console.WriteLine("采集会话建立失败。");
            return 1;
        }

        var frame = capture.CaptureFull(window.Hwnd);
        if (frame is null)
        {
            Console.WriteLine("取帧失败（窗口最小化或尚未渲染）。");
            return 1;
        }

        var outputPath = Path.Combine(
            AppContext.BaseDirectory,
            "shots",
            $"{Sanitize(accountName)}_full.png");

        PngWriter.WriteBgra(outputPath, frame.Bgra, frame.Width, frame.Height);

        Console.WriteLine($"内容区    : {frame.Width}×{frame.Height}");
        Console.WriteLine($"客户区    : {window.ClientWidth}×{window.ClientHeight}");
        Console.WriteLine($"采集耗时  : {capture.LastCaptureCostMs(window.Hwnd):F2} ms");
        Console.WriteLine($"整窗截图  : {outputPath}");

        return 0;
    }

    private static int Bench(AppConfig config, string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("用法：--bench <账号名> <x,y,w,h> [秒数]");
            return 1;
        }

        var accountName = args[1];
        if (!RoiRect.TryParse(args[2], out var roi))
        {
            Console.WriteLine($"ROI 参数无法解析：{args[2]}");
            return 1;
        }

        var seconds = args.Length >= 4 && int.TryParse(args[3], out var parsed) ? Math.Max(1, parsed) : 10;

        var window = WindowFinder.FindByAccount(config.ProcessName, accountName);
        if (window is null)
        {
            Console.WriteLine($"找不到账号「{accountName}」的窗口。");
            return 1;
        }

        var palette = PaletteFor(config, accountName);
        var lut = new ColorLut();
        lut.Rebuild(palette, config.Detect.ToLutTuning());

        using var capture = new WgcCaptureService();
        if (!capture.Attach(window.Hwnd, config.Capture.MinUpdateIntervalMs))
        {
            Console.WriteLine("采集会话建立失败。");
            return 1;
        }

        var detector = new IconBlockDetector();
        var result = new DetectResult();

        var process = Process.GetCurrentProcess();
        var cpuAtStart = process.TotalProcessorTime;

        var frames = 0;
        var misses = 0;
        var captureTotalMs = 0.0;
        var detectTotalMs = 0.0;
        var hits = 0;
        var nextReportSecond = 1;

        Console.WriteLine($"基准测试：{accountName}  ROI {roi}  节拍 {config.TickMs} ms  持续 {seconds} 秒");
        Console.WriteLine($"限帧      {capture.AppliedMinIntervalMs(window.Hwnd)} ms　颜色库 {palette.Count} 项");
        Console.WriteLine();
        Console.WriteLine("   秒   帧数   丢帧   采集均值   判定均值   累计命中");

        var wall = Stopwatch.StartNew();

        while (wall.Elapsed.TotalSeconds < seconds)
        {
            var tickStart = Stopwatch.GetTimestamp();

            var frame = capture.CaptureRoi(window.Hwnd, roi);
            var afterCapture = Stopwatch.GetTimestamp();

            if (frame is null)
            {
                misses++;
            }
            else
            {
                detector.Detect(frame.Bgra, frame.Width, frame.Height, lut, config.Detect, result);
                var afterDetect = Stopwatch.GetTimestamp();

                captureTotalMs += TicksToMs(afterCapture - tickStart);
                detectTotalMs += TicksToMs(afterDetect - afterCapture);
                frames++;
                hits += result.HitCount;
            }

            var spentMs = TicksToMs(Stopwatch.GetTimestamp() - tickStart);
            var wait = Math.Max(5, config.TickMs - (int)spentMs);
            Thread.Sleep(wait);

            var elapsedSecond = (int)wall.Elapsed.TotalSeconds;
            if (elapsedSecond >= nextReportSecond)
            {
                nextReportSecond = elapsedSecond + 2;
                Console.WriteLine(
                    $"{elapsedSecond,5}  {frames,6}  {misses,5}  " +
                    $"{Average(captureTotalMs, frames),9:F3}  {Average(detectTotalMs, frames),9:F3}  {hits,8}");
            }
        }

        wall.Stop();
        process.Refresh();

        var cpuDelta = process.TotalProcessorTime - cpuAtStart;
        var singleCorePercent = cpuDelta.TotalMilliseconds / wall.Elapsed.TotalMilliseconds * 100;

        Console.WriteLine();
        Console.WriteLine($"有效帧      {frames}，丢帧 {misses}");
        Console.WriteLine($"采集均值    {Average(captureTotalMs, frames):F3} ms/次");
        Console.WriteLine($"判定均值    {Average(detectTotalMs, frames):F3} ms/次");
        Console.WriteLine($"端到端均值  {Average(captureTotalMs + detectTotalMs, frames):F3} ms/tick");
        Console.WriteLine($"CPU         {singleCorePercent:F2}% 单核（{wall.Elapsed.TotalSeconds:F0} 秒累计）");
        Console.WriteLine($"内存        {process.WorkingSet64 / 1024 / 1024} MB");

        return 0;
    }

    /// <summary>
    /// 用真实的 MonitorService 跑多账号监控，用于复现「开监控后进程消失」的问题。
    /// 参数：[秒数] [mute] [配置文件路径]（带 mute 时全局静音，用于把音频路径排除在外）。
    /// </summary>
    private static int Stress(AppConfig config, string[] args)
    {
        var seconds = args.Length >= 2 && int.TryParse(args[1], out var parsed) ? Math.Max(5, parsed) : 60;
        var mute = args.Length >= 3 && string.Equals(args[2], "mute", StringComparison.OrdinalIgnoreCase);

        // 指定配置文件时按它跑（只读，不回写），便于直接复现主程序的实际账号配置
        var store = new ConfigStore(args.Length >= 4 ? args[3] : null);
        var cfg = args.Length >= 4 ? store.Load() : config;
        cfg.GlobalMute = mute;
        store.Replace(cfg);

        var captureConfig = store.Current;

        using var capture = new WgcCaptureService();
        using var directory = new AccountDirectory(captureConfig.ProcessName);
        var status = new StatusStore();
        var frames = new FrameStore();
        using var alarm = new AlarmPlayer(() => store.Current);
        var events = new EventJournal();
        using var monitor = new MonitorService(store, capture, directory, status, alarm, events, frames);

        directory.Start();
        Thread.Sleep(1200);

        Console.WriteLine(
            $"压测 {seconds} 秒 · 账号 {captureConfig.Accounts.Count} · 静音 {mute} · 节拍 {captureConfig.TickMs} ms");
        Console.WriteLine("   秒  watchers  会话     ticks  增量   丢帧  敌对   fps");

        monitor.Start();

        var watch = Stopwatch.StartNew();
        var lastTicks = 0L;

        while (watch.Elapsed.TotalSeconds < seconds)
        {
            Thread.Sleep(1000);

            var all = status.All();
            var ticks = all.Sum(s => s.Ticks);

            Console.WriteLine(
                $"{watch.Elapsed.TotalSeconds,5:F0}  {monitor.ActiveWatchers,8}  {capture.SessionCount,4}  {ticks,8}  {ticks - lastTicks,5}  " +
                $"{all.Sum(s => s.DropFrames),5}  {all.Count(s => s.Hostile),5}  {all.Sum(s => s.ActualFps),5:F1}");

            lastTicks = ticks;
        }

        monitor.Stop();
        Console.WriteLine($"完成：ticks={status.All().Sum(s => s.Ticks)}，进程未崩溃");
        return 0;
    }

    private static int SyncScan()
    {
        var profiles = SettingsRootLocator.FindProfiles();
        Console.WriteLine($"设置根目录：{SettingsRootLocator.GetRootDirectory()}");
        Console.WriteLine($"发现 {profiles.Count} 个 Profile");

        foreach (var profile in profiles)
        {
            var characters = SettingsScanner.ScanCharacters(profile.Directory);
            var users = SettingsScanner.ScanUsers(profile.Directory);

            Console.WriteLine();
            Console.WriteLine($"  {profile.InstallId} / {profile.Name}");
            Console.WriteLine($"    角色级 core_char × {characters.Count}");
            foreach (var file in characters)
            {
                Console.WriteLine($"      #{file.Id}  {file.Length / 1024} KB  {file.LastWriteTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
            }

            Console.WriteLine($"    账号级 core_user × {users.Count}");
            foreach (var file in users)
            {
                Console.WriteLine($"      #{file.Id}  {file.Length / 1024} KB  {file.LastWriteTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
            }
        }

        var running = GameProcessGuard.GetRunningProcesses();
        Console.WriteLine();
        Console.WriteLine(running.Count == 0
            ? "客户端进程：未运行，可以安全写入设置"
            : $"客户端进程：{string.Join('、', running)} 正在运行，克隆会被拒绝");

        return 0;
    }

    private static int GenConfig(ConfigStore store, AppConfig config)
    {
        store.Save();
        Console.WriteLine($"已写入配置：{store.Path}");
        Console.WriteLine($"账号 {config.Accounts.Count} 个，颜色库 {config.Palette.Count} 项");
        Console.WriteLine($"克隆工具可对 {config.Accounts.Count - 1} 个账号执行配置复制");
        return 0;
    }

    /// <summary>
    /// 用合成图验证判定：既要有真图标命中，也不能把暗褐色噪声当成红。
    /// <para>
    /// 之所以要合成，是因为真实客户端里本地列表有没有敌对不由我们控制；
    /// 而这个回归点（量化格的色相失真）必须能随时复验。
    /// </para>
    /// </summary>
    private static int DetectSelfTest(AppConfig config)
    {
        const int width = 59;
        const int height = 440;

        // 只取一种颜色建表，模拟"某条 ROI 只勾了敌对红"
        var target = config.Palette.FirstOrDefault(p => p.Name.Contains("敌对", StringComparison.Ordinal))
            ?? config.Palette[0];

        var bgra = new byte[width * height * 4];
        FillRect(bgra, width, height, 0, 0, width, height, 0x0B, 0x0B, 0x0B);

        // 三个"图标"：22×22，y 间距 30（与本地列表实测行距一致）
        FillRect(bgra, width, height, 13, 40, 22, 22, 0xAD, 0x06, 0x06);
        FillRect(bgra, width, height, 13, 70, 22, 22, 0x8B, 0x10, 0x10);
        FillRect(bgra, width, height, 13, 100, 22, 22, 0x6F, 0x04, 0x04);

        // 两段暗褐色：实测误报源，色相 26~30°，落不进敌对红的 ±20°
        FillRect(bgra, width, height, 13, 180, 22, 60, 0x3A, 0x2F, 0x25);
        FillRect(bgra, width, height, 13, 260, 22, 40, 0x3E, 0x1E, 0x05);

        // 实测的另一类误报源：本地列表里暗红的人字形 UI 图标（明度 0.24），
        // 色相正好落在红的窗口内，只能靠明度下限挡掉
        FillRect(bgra, width, height, 13, 320, 22, 40, 0x3C, 0x0E, 0x0E);

        // 发灰的红底纹：色相 3.5°、明度 0.54 都合格，只有饱和度 0.49 < 0.5，
        // 用来锁住「彩色匹配必须有饱和度下限」这条规则
        FillRect(bgra, width, height, 13, 380, 22, 40, 0x8A, 0x4A, 0x46);

        var lut = new ColorLut();
        lut.Rebuild([target], config.Detect.ToLutTuning());

        var detector = new IconBlockDetector();
        var result = new DetectResult();
        detector.Detect(bgra, width, height, lut, config.Detect, result);

        Console.WriteLine($"合成 ROI   : {width}×{height}");
        Console.WriteLine($"图标       : 3 个（#AD0606 / #8B1010 / #6F0404，间距 30）");
        Console.WriteLine($"噪声       : 2 段暗褐 + 1 段暗红人字 + 1 段发灰红底纹（#8A4A46）");
        Console.WriteLine(
            $"判定参数   : 色相 ±{config.Detect.HueToleranceDegrees:F0}°　饱和度 ≥{config.Detect.MinSaturation:F2}　" +
            $"明度 ≥{config.Detect.MinValue:F2}　中性容差 {config.Detect.NeutralValueTolerance:F2}");
        Console.WriteLine($"颜色库     : {target.Name} {target.Hex}");
        Console.WriteLine($"命中       : {DescribeHits(lut, result)}   纵向区间 {result.SegmentsSeen}");

        foreach (var hit in result.Hits.Take(result.HitCount).OrderBy(h => h.Y))
        {
            Console.WriteLine($"    x={hit.X,3} y={hit.Y,3} w={hit.W,3} h={hit.H,3} 像素 {hit.Pixels}");
        }

        var passed = result.HitCount == 3;
        Console.WriteLine(passed
            ? "结论       : 通过——3 个图标全部命中，两段暗褐与暗红 UI 段均未误报"
            : $"结论       : 不符，期望 3 实际 {result.HitCount}");

        return passed ? 0 : 1;
    }

    private static void FillRect(byte[] bgra, int width, int height, int x, int y, int w, int h, byte r, byte g, byte b)
    {
        var lastY = Math.Min(y + h, height);
        var lastX = Math.Min(x + w, width);

        for (var yy = Math.Max(0, y); yy < lastY; yy++)
        {
            for (var xx = Math.Max(0, x); xx < lastX; xx++)
            {
                var p = ((yy * width) + xx) * 4;
                bgra[p] = b;
                bgra[p + 1] = g;
                bgra[p + 2] = r;
                bgra[p + 3] = 0xFF;
            }
        }
    }

    private static int Unknown(string arg)
    {
        Console.WriteLine($"未知命令：{arg}");
        PrintUsage();
        return 1;
    }

    // ---------------------------------------------------------------- 辅助

    private static List<PaletteColor> PaletteFor(AppConfig config, string accountName)
    {
        var account = config.FindAccount(accountName);
        var ids = account?.Rois.FirstOrDefault(r => r.Colors.Count > 0)?.Colors;

        if (ids is not { Count: > 0 })
        {
            return [.. config.Palette];
        }

        var picked = new List<PaletteColor>();
        foreach (var color in config.Palette)
        {
            if (ids.Contains(color.Id))
            {
                picked.Add(color);
            }
        }

        return picked.Count > 0 ? picked : [.. config.Palette];
    }

    private static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static double Average(double total, int count) => count == 0 ? 0 : total / count;

    /// <summary>打印 ROI 里出现最多的几种颜色，以及它们经查找表得到的分类结果。用于核对颜色与调参。</summary>
    private static void PrintColorHistogram(byte[] bgra, int width, int height, ColorLut lut)
    {
        var histogram = new Dictionary<int, int>();
        var total = width * height;

        for (var i = 0; i < total; i++)
        {
            var p = i * 4;
            var key = (bgra[p + 2] << 16) | (bgra[p + 1] << 8) | bgra[p];
            histogram[key] = histogram.TryGetValue(key, out var count) ? count + 1 : 1;
        }

        Console.WriteLine("颜色分布  :");

        foreach (var pair in histogram.OrderByDescending(kv => kv.Value).Take(8))
        {
            var r = (pair.Key >> 16) & 0xFF;
            var g = (pair.Key >> 8) & 0xFF;
            var b = pair.Key & 0xFF;

            ColorLut.ToHsv((byte)r, (byte)g, (byte)b, out var hue, out var sat, out var val);

            var label = lut.Map[((b >> 4) << 8) | ((g >> 4) << 4) | (r >> 4)];
            var name = label == 0 ? "其它" : lut.NameOfLabel(label) ?? $"#{label}";

            Console.WriteLine(
                $"    #{r:X2}{g:X2}{b:X2}  {pair.Value,7} 像素  {pair.Value * 100.0 / total,5:F1}%  " +
                $"色相 {hue,5:F1}°  饱和 {sat:F2}  明度 {val:F2}  →  {name}");
        }
    }

    /// <summary>
    /// 逐块打印命中矩形的坐标与高度。
    /// 用于分辨数量偏差发生在哪一步：相邻两块被并成一块（高度约两倍、间距异常），
    /// 还是一个块被拆成两块（高度很小、成对出现）。
    /// </summary>
    private static void PrintBlocks(ColorLut lut, DetectResult result)
    {
        if (result.HitCount == 0)
        {
            return;
        }

        Console.WriteLine("命中块明细（按 y 排序）:");

        var order = Enumerable.Range(0, result.HitCount).OrderBy(i => result.Hits[i].Y).ToArray();
        var previousY = int.MinValue;

        foreach (var i in order)
        {
            var hit = result.Hits[i];
            var name = lut.NameOfLabel(hit.Label) ?? $"#{hit.Label}";
            var spacing = previousY == int.MinValue ? "    -" : $"{hit.Y - previousY,5}";
            previousY = hit.Y;

            Console.WriteLine(
                $"  {name}  x={hit.X,3} y={hit.Y,3} w={hit.W,3} h={hit.H,3} " +
                $"像素 {hit.Pixels,4}  与上一块间距 {spacing}");
        }
    }

    /// <summary>最近邻放大，便于肉眼核对原始画面。</summary>
    private static void WriteUpscaled(string path, byte[] bgra, int width, int height, int factor)
    {
        var w = width * factor;
        var h = height * factor;
        var buffer = new byte[w * h * 4];

        for (var y = 0; y < h; y++)
        {
            var sourceRow = (y / factor) * width;

            for (var x = 0; x < w; x++)
            {
                var source = (sourceRow + (x / factor)) * 4;
                var target = ((y * w) + x) * 4;

                buffer[target] = bgra[source];
                buffer[target + 1] = bgra[source + 1];
                buffer[target + 2] = bgra[source + 2];
                buffer[target + 3] = bgra[source + 3];
            }
        }

        PngWriter.WriteBgra(path, buffer, w, h);
    }

    /// <summary>
    /// 统计被判定为命中的像素的饱和度与明度分布。
    /// 用于核对"匹配只看色相"是否过宽：色相接近但很灰很暗的像素（暖色渲染的暗部、褐色背景）
    /// 也会被判成命中，而人眼看到的是褐色而不是红色。
    /// </summary>
    private static void PrintMatchedHsv(byte[] bgra, int width, int height, ColorLut lut)
    {
        var count = 0;
        var dull = 0;
        var veryDull = 0;
        var minSat = 1.0;
        var maxSat = 0.0;
        var satSum = 0.0;
        var valSum = 0.0;

        var total = width * height;
        for (var i = 0; i < total; i++)
        {
            var p = i * 4;
            var b = bgra[p];
            var g = bgra[p + 1];
            var r = bgra[p + 2];

            if (lut.Map[((b >> 4) << 8) | ((g >> 4) << 4) | (r >> 4)] == 0)
            {
                continue;
            }

            ColorLut.ToHsv(r, g, b, out _, out var sat, out var val);

            count++;
            satSum += sat;
            valSum += val;

            if (sat < minSat)
            {
                minSat = sat;
            }

            if (sat > maxSat)
            {
                maxSat = sat;
            }

            if (sat < 0.50)
            {
                dull++;
            }

            if (sat < 0.30)
            {
                veryDull++;
            }
        }

        if (count == 0)
        {
            Console.WriteLine("命中像素HSV: 无");
            return;
        }

        Console.WriteLine(
            $"命中像素HSV: {count} 个  饱和 最低 {minSat:F2} / 均值 {satSum / count:F2} / 最高 {maxSat:F2}" +
            $"  明度均值 {valSum / count:F2}");
        Console.WriteLine(
            $"　　　　　　 饱和 < 0.50 的 {dull,6} 个（{dull * 100.0 / count,5:F1}%）　" +
            $"< 0.30 的 {veryDull,6} 个（{veryDull * 100.0 / count,5:F1}%）");
    }

    /// <summary>把命中像素按具体 RGB 出现次数排序输出，用于核对匹配到的到底是"红"还是"褐"。</summary>
    private static void PrintTopMatchedColors(byte[] bgra, int width, int height, ColorLut lut)
    {
        var counts = new Dictionary<int, int>();
        var total = width * height;

        for (var i = 0; i < total; i++)
        {
            var p = i * 4;
            var b = bgra[p];
            var g = bgra[p + 1];
            var r = bgra[p + 2];

            if (lut.Map[((b >> 4) << 8) | ((g >> 4) << 4) | (r >> 4)] == 0)
            {
                continue;
            }

            var key = (r << 16) | (g << 8) | b;
            counts[key] = counts.TryGetValue(key, out var existing) ? existing + 1 : 1;
        }

        if (counts.Count == 0)
        {
            Console.WriteLine("命中像素的具体颜色: 无");
            return;
        }

        Console.WriteLine($"命中像素的具体颜色（共 {counts.Count} 种，列前 12）:");

        foreach (var pair in counts.OrderByDescending(kv => kv.Value).Take(12))
        {
            var r = (pair.Key >> 16) & 0xFF;
            var g = (pair.Key >> 8) & 0xFF;
            var b = pair.Key & 0xFF;

            ColorLut.ToHsv((byte)r, (byte)g, (byte)b, out var hue, out var sat, out var val);

            Console.WriteLine(
                $"    #{r:X2}{g:X2}{b:X2}  {pair.Value,5} 个  色相 {hue,5:F1}°  饱和 {sat:F2}  明度 {val:F2}");
        }
    }

    private static string Sanitize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        }

        return builder.ToString();
    }

    private static string DescribeHits(ColorLut lut, DetectResult result)
    {
        if (result.HitCount == 0)
        {
            return "无";
        }

        var parts = new List<string>();
        for (var label = 1; label < result.LabelCounts.Length; label++)
        {
            var count = result.LabelCounts[label];
            if (count > 0)
            {
                parts.Add($"{lut.NameOfLabel(label) ?? $"#{label}"} ×{count}");
            }
        }

        return string.Join('、', parts);
    }

    /// <summary>把命中块用白框标出，便于肉眼核对。</summary>
    private static void DrawRect(byte[] canvas, int width, int height, int x, int y, int w, int h)
    {
        for (var i = 0; i < w; i++)
        {
            SetPixel(canvas, width, height, x + i, y);
            SetPixel(canvas, width, height, x + i, y + h - 1);
        }

        for (var j = 0; j < h; j++)
        {
            SetPixel(canvas, width, height, x, y + j);
            SetPixel(canvas, width, height, x + w - 1, y + j);
        }
    }

    private static void SetPixel(byte[] canvas, int width, int height, int x, int y)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height)
        {
            return;
        }

        var index = (((y * width) + x) * 4);
        canvas[index] = 0xFF;
        canvas[index + 1] = 0xFF;
        canvas[index + 2] = 0xFF;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("EveWatch 自检工具");
        Console.WriteLine();
        Console.WriteLine("  --list                            列出目标进程的窗口");
        Console.WriteLine("  --shot   <账号>                   导出该窗口的整幅内容区截图，用于挑 ROI 坐标");
        Console.WriteLine("  --detect <账号> [x,y,w,h]          对指定账号跑一次判定，输出命中可视化 PNG");
        Console.WriteLine("  --bench  <账号> <x,y,w,h> [秒]     采集与判定的持续基准测试");
        Console.WriteLine("  --stress <秒> [mute] [配置文件]    用真实 MonitorService 跑多账号监控，复现/回归采集崩溃");
        Console.WriteLine("  --sync-scan                       扫描客户端设置目录与进程占用状态");
        Console.WriteLine("  --detect-selftest                 用合成图验证判定：真图标命中、暗褐不误报");
        Console.WriteLine("  --gen-config                      生成默认配置文件");
        Console.WriteLine("  --help                            显示本帮助");
    }
}
