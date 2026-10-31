using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using GuIO.Contracts;
using GuIO.Driver.Windows;
using GuIO.Kernel;
using GuIO.Service;
using TextBlock = Avalonia.Controls.TextBlock;

namespace GuIO.App;

/// <summary>
/// GuIO 主界面。设计原则：焦点明确（连接 → 观察 → 操作）、层级清晰、删繁就简。
/// 配色取自参考图：砚蓝 #404D62 为基调，胭雾 #DDBECA 为唯一强调色。
/// 界面只做展示与指令下发，语义全在引擎侧（前后端分离）。
/// </summary>
public sealed class MainWindow : Window
{
    // ===== 配色：砚蓝 x 胭雾 =====
    private static readonly Color InkDeep = Color.Parse("#2B3442");
    private static readonly Color InkBlue = Color.Parse("#404D62");
    private static readonly Color InkSoft = Color.Parse("#4E5C73");
    private static readonly Color RougeMist = Color.Parse("#DDBECA");
    private static readonly Color TextHi = Color.Parse("#F4EAEF");
    private static readonly Color TextMid = Color.Parse("#B4BFD1");
    private static readonly Color TextLo = Color.Parse("#7C8AA3");

    private static IBrush B(Color c) => new SolidColorBrush(c);
    private static readonly IBrush CardBg = new SolidColorBrush(Color.Parse("#1AFFFFFF"));
    private static readonly IBrush CardLine = new SolidColorBrush(Color.Parse("#1FFFFFFF"));

    private readonly DeviceManager _devices;
    private readonly RpcService _rpc;

    private readonly TextBox _deviceBox;
    private readonly TextBlock _statusText;
    private readonly Avalonia.Controls.Shapes.Ellipse _statusDot;
    private readonly TextBlock _deviceInfo;
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly Control _previewHint;
    private readonly TextBox _clickText;
    private readonly TextBox _coordBox;
    private readonly TextBox _typeBox;
    private readonly ListBox _nodesList;
    private readonly TextBlock _nodeSummary;
    private readonly TextBox _log;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(900) };

    private bool _autoRefresh;
    private string? _lastRecPath;

    public MainWindow()
    {
        var registry = new CapabilityRegistry();
        registry.Register("win", "Windows 桌面", id => new WindowsDriver(id));
        _devices = new DeviceManager(registry);
        _rpc = new RpcService(_devices);

        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI, sans-serif");
        Title = "GuIO";
        try { Icon = LoadIcon(); } catch { }
        Width = 1320;
        Height = 860;
        MinWidth = 1080;
        MinHeight = 680;
        Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(InkDeep, 0),
                new GradientStop(InkBlue, 0.62),
                new GradientStop(InkSoft, 1),
            },
        };

        _deviceBox = new TextBox { Text = "win://app/notepad", Watermark = "设备地址，如 win://app/notepad" };
        StyleInput(_deviceBox);

        _statusDot = new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, Fill = B(TextLo), VerticalAlignment = VerticalAlignment.Center };
        _statusText = new TextBlock { Text = "未连接", Foreground = B(RougeMist), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        _deviceInfo = new TextBlock { Text = "", Foreground = B(TextLo), FontSize = 12, TextWrapping = TextWrapping.Wrap };

        _previewHint = BuildPreviewHint();
        _nodesList = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        _nodeSummary = new TextBlock { Text = "尚未观测", Foreground = B(TextLo), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        _nodesList.SelectionChanged += (_, _) =>
        {
            if (_nodesList.SelectedItem is NodeItem ni) _coordBox.Text = $"{ni.CenterX},{ni.CenterY}";
        };

        _clickText = new TextBox { Watermark = "要点的文字，如：确定" };
        StyleInput(_clickText);
        _coordBox = new TextBox { Watermark = "坐标 300,420 或归一化 0.5,0.5" };
        StyleInput(_coordBox);
        _typeBox = new TextBox { Watermark = "要输入的文字（支持中文）" };
        StyleInput(_typeBox);
        _log = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 11.5,
            Background = new SolidColorBrush(Color.Parse("#14000000")),
            Foreground = B(TextMid),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
        };

        Content = BuildLayout();
        _timer.Tick += async (_, _) => await RefreshPreviewAsync();
        Closing += (_, _) => _timer.Stop();
        Log("欢迎使用 GuIO。");
    }

    /// <summary>从嵌入资源或安装目录加载窗口图标。</summary>
    private static Avalonia.Controls.WindowIcon? LoadIcon()
    {
        // 优先同目录 icon.ico（发布时随包），其次工程 assets
        foreach (var p in new[] { "icon.ico", Path.Combine(AppContext.BaseDirectory, "icon.ico") })
            if (File.Exists(p)) return new Avalonia.Controls.WindowIcon(p);
        return null;
    }

    // ================= 布局 =================
    private Control BuildLayout()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };

        var header = BuildHeader();
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("330,*,372"),
            Margin = new Avalonia.Thickness(24, 4, 24, 24),
        };

        var left = BuildLeft();
        Grid.SetColumn(left, 0);
        body.Children.Add(left);

        var center = BuildCenter();
        Grid.SetColumn(center, 1);
        center.Margin = new Avalonia.Thickness(16, 0, 16, 0);
        body.Children.Add(center);

        var right = BuildRight();
        Grid.SetColumn(right, 2);
        body.Children.Add(right);

        Grid.SetRow(body, 1);
        root.Children.Add(body);
        return root;
    }

    private Control BuildHeader()
    {
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(9),
            Background = B(RougeMist),
            Child = new TextBlock
            {
                Text = "G", FontWeight = FontWeight.Bold, FontSize = 17,
                Foreground = B(InkDeep),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "GuIO", FontSize = 19, FontWeight = FontWeight.SemiBold, Foreground = B(TextHi) });
        titles.Children.Add(new TextBlock { Text = "桌面 · 安卓   统一自动化", FontSize = 11.5, Foreground = B(TextLo) });
        brand.Children.Add(titles);

        var badge = new Border
        {
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(Color.Parse("#26DDBECA")),
            BorderBrush = new SolidColorBrush(Color.Parse("#44DDBECA")),
            BorderThickness = new Thickness(1),
            Padding = new Avalonia.Thickness(14, 7),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _statusDot, _statusText } },
        };

        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        g.Children.Add(brand);
        Grid.SetColumn(badge, 1);
        g.Children.Add(badge);
        g.Margin = new Avalonia.Thickness(28, 22, 28, 12);
        return g;
    }

    private Control BuildLeft()
    {
        var col = new StackPanel { Spacing = 14 };

        var devBody = new StackPanel { Spacing = 10 };
        devBody.Children.Add(_deviceBox);
        devBody.Children.Add(Row(Primary("连接设备", async () => await ConnectAsync()), Ghost("观测", async () => await ObserveAsync())));
        devBody.Children.Add(_deviceInfo);
        col.Children.Add(Card("设备", devBody));

        var click = new StackPanel { Spacing = 10 };
        click.Children.Add(_clickText);
        click.Children.Add(Row(Primary("按文字点击", async () => await ClickTextAsync(1)), Ghost("双击", async () => await ClickTextAsync(2))));
        click.Children.Add(Divider());
        click.Children.Add(_coordBox);
        click.Children.Add(Row(Ghost("按坐标点击", async () => await ClickCoordAsync()), Ghost("查找", async () => await FindAsync())));
        col.Children.Add(Card("定位与点击", click));

        var type = new StackPanel { Spacing = 10 };
        type.Children.Add(_typeBox);
        type.Children.Add(Row(Primary("输入文本", async () => await TypeAsync()), Ghost("回车", async () => await KeyAsync("Enter")), Ghost("返回", async () => await KeyAsync("Back"))));
        col.Children.Add(Card("文本输入", type));

        var media = new StackPanel { Spacing = 10 };
        media.Children.Add(Row(Ghost("上滚", async () => await ScrollAsync(-3)), Ghost("下滚", async () => await ScrollAsync(3)), Ghost("截屏", async () => await ScreenshotAsync())));
        media.Children.Add(Row(Primary("AI 识图", async () => await VisionAsync()), Ghost("音量 50%", async () => await VolumeAsync(0.5)), Ghost("静音", async () => await VolumeAsync(0))));
        col.Children.Add(Card("视图与声音", media));

        var rec = new StackPanel { Spacing = 10 };
        rec.Children.Add(Row(Primary("开始录制", async () => await RecStartAsync()), Ghost("停止", async () => await RecStopAsync())));
        rec.Children.Add(Ghost("回放上次录制", async () => await ReplayAsync()));
        col.Children.Add(Card("录制回放", rec));

        return new ScrollViewer { Content = col, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private Control BuildCenter()
    {
        var g = new Grid();
        g.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#12000000")),
            BorderBrush = CardLine,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Avalonia.Thickness(10),
            Child = new Grid { Children = { _preview, _previewHint } },
        });

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Avalonia.Thickness(0, 0, 0, 18),
        };
        bar.Children.Add(Ghost("刷新一帧", async () => await RefreshPreviewAsync()));
        var auto = Ghost("自动刷新：关");
        auto.Click += (_, _) =>
        {
            _autoRefresh = !_autoRefresh;
            if (_autoRefresh) { _timer.Start(); auto.Content = "自动刷新：开"; }
            else { _timer.Stop(); auto.Content = "自动刷新：关"; }
        };
        bar.Children.Add(auto);
        g.Children.Add(bar);
        return g;
    }

    private Control BuildRight()
    {
        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,3*,Auto,2*") };

        var nodesHead = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Avalonia.Thickness(0, 0, 0, 8) };
        nodesHead.Children.Add(SectionLabel("界面元素"));
        Grid.SetColumn(_nodeSummary, 1);
        nodesHead.Children.Add(_nodeSummary);
        Grid.SetRow(nodesHead, 0);
        g.Children.Add(nodesHead);

        var nodesCard = new Border
        {
            Background = CardBg, BorderBrush = CardLine, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Avalonia.Thickness(6), Child = _nodesList,
        };
        Grid.SetRow(nodesCard, 1);
        g.Children.Add(nodesCard);

        var logHead = SectionLabel("日志与遥测");
        logHead.Margin = new Avalonia.Thickness(0, 16, 0, 8);
        Grid.SetRow(logHead, 2);
        g.Children.Add(logHead);

        var logCard = new Border
        {
            Background = CardBg, BorderBrush = CardLine, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Avalonia.Thickness(10),
            Child = new ScrollViewer { Content = _log },
        };
        Grid.SetRow(logCard, 3);
        g.Children.Add(logCard);
        return g;
    }

    // ================= 组件工厂 =================
    private static Control Card(string title, Control body)
    {
        var sp = new StackPanel { Spacing = 12 };
        sp.Children.Add(SectionLabel(title));
        sp.Children.Add(body);
        return new Border
        {
            Background = CardBg, BorderBrush = CardLine, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(13), Padding = new Avalonia.Thickness(16), Child = sp,
        };
    }

    private static TextBlock SectionLabel(string s) => new()
    {
        Text = s, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = B(TextLo),
    };

    private static Control Divider() => new Border
    {
        Height = 1, Background = new SolidColorBrush(Color.Parse("#1AFFFFFF")), Margin = new Avalonia.Thickness(0, 2),
    };

    private static Control Row(params Control[] items)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", items.Length))) };
        for (int i = 0; i < items.Length; i++)
        {
            items[i].Margin = new Avalonia.Thickness(i == 0 ? 0 : 8, 0, 0, 0);
            items[i].HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(items[i], i);
            g.Children.Add(items[i]);
        }
        return g;
    }

    private static Button Primary(string text, Func<Task> onClick)
    {
        var b = new Button
        {
            Content = text,
            Background = B(RougeMist),
            Foreground = B(InkDeep),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Padding = new Avalonia.Thickness(14, 9),
            FontWeight = FontWeight.SemiBold,
            FontSize = 12.5,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        b.Click += async (_, _) => await onClick();
        return b;
    }

    private static Button Ghost(string text, Func<Task>? onClick = null)
    {
        var b = new Button
        {
            Content = text,
            Background = new SolidColorBrush(Color.Parse("#1FFFFFFF")),
            Foreground = B(RougeMist),
            BorderBrush = new SolidColorBrush(Color.Parse("#40DDBECA")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Avalonia.Thickness(12, 8),
            FontSize = 12.5,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        if (onClick is not null) b.Click += async (_, _) => await onClick();
        return b;
    }

    private static void StyleInput(TextBox t)
    {
        t.Background = new SolidColorBrush(Color.Parse("#1F000000"));
        t.Foreground = B(TextHi);
        t.BorderBrush = new SolidColorBrush(Color.Parse("#33DDBECA"));
        t.BorderThickness = new Avalonia.Thickness(1);
        t.CornerRadius = new CornerRadius(8);
        t.Padding = new Avalonia.Thickness(11, 9);
        t.FontSize = 12.5;
    }

    private static Control BuildPreviewHint()
    {
        var sp = new StackPanel
        {
            Spacing = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        sp.Children.Add(new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 46, Height = 46,
            Stroke = new SolidColorBrush(Color.Parse("#4DDDBECA")),
            StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        sp.Children.Add(new TextBlock
        {
            Text = "尚未连接设备", FontSize = 15, Foreground = B(TextMid),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        sp.Children.Add(new TextBlock
        {
            Text = "在左侧填写设备地址并点击「连接设备」，\n即可在此看到画面。",
            FontSize = 12, Foreground = B(TextLo), TextAlignment = TextAlignment.Center,
        });
        return sp;
    }

    // ================= 引擎调用（复用统一 RPC） =================
    private string Dev => _deviceBox.Text ?? "";

    private async Task JsonAsync(string method, object? pars)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(pars ?? new { }, GuioJson.Options));
        var resp = await _rpc.HandleAsync(new RpcRequest { Id = "g", Method = method, Params = doc.RootElement.Clone() });
        var text = resp.Error is not null ? $"错误: {resp.Error.Message}" : (resp.Result?.GetRawText() ?? "{}");
        Log($"{method}  ->  {Clip(text, 220)}");
    }

    private async Task ConnectAsync()
    {
        await JsonAsync(ProtocolMethods.Connect, new { device = Dev });
        var entry = _devices.Scheduler.Devices.FirstOrDefault();
        if (entry is null) { SetStatus(false, "连接失败"); return; }
        var d = entry.Session.Descriptor;
        SetStatus(true, "已连接");
        _deviceInfo.Text = $"{d.Name}\n{d.Bounds.Width} × {d.Bounds.Height} · {CapCount(d.Capabilities)} 项能力";
        await RefreshPreviewAsync();
    }

    private void SetStatus(bool ok, string text)
    {
        _statusText.Text = text;
        _statusDot.Fill = B(ok ? RougeMist : TextLo);
    }

    private static int CapCount(CapabilityFlags f) => System.Enum.GetValues<CapabilityFlags>()
        .Count(v => v != CapabilityFlags.None && !v.ToString().StartsWith("All") && f.HasFlag(v));

    private async Task ObserveAsync()
    {
        await JsonAsync(ProtocolMethods.Observe, new { device = Dev, frame = true, tree = true, ocr = false });
        await RefreshPreviewAsync();
        var entry = _devices.Scheduler.Devices.FirstOrDefault();
        var nodes = entry?.Session.Last?.Nodes ?? Array.Empty<UiNode>();
        var visible = nodes.Where(n => n.Visible && !n.Bounds.IsEmpty).ToArray();
        _nodesList.ItemsSource = visible
            .Select(n => new NodeItem($"{n.Kind}   {Clip(n.Name ?? n.Text ?? n.AutomationId, 22)}   {n.Bounds.Width}×{n.Bounds.Height}",
                n.Bounds.Center.X, n.Bounds.Center.Y))
            .ToArray();
        _nodeSummary.Text = $"{visible.Length} 个";
    }

    private async Task RefreshPreviewAsync()
    {
        try
        {
            var entry = _devices.Scheduler.Devices.FirstOrDefault();
            if (entry is null) return;
            var r = await entry.Session.Driver.Perception.CaptureAsync(null);
            if (!r.Ok || r.Value is null) return;
            var png = entry.Session.Codec.EncodePng(r.Value);
            using var ms = new MemoryStream(png);
            _preview.Source = new Bitmap(ms);
            _previewHint.IsVisible = false;
        }
        catch (Exception ex) { Log("预览失败: " + ex.Message); }
    }

    private async Task ScreenshotAsync()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), $"guio_{DateTime.Now:HHmmss}.png");
        await JsonAsync(ProtocolMethods.Screenshot, new { device = Dev, @out = path });
        await RefreshPreviewAsync();
    }

    private async Task VisionAsync()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { device = Dev }, GuioJson.Options));
        var resp = await _rpc.HandleAsync(new RpcRequest
        {
            Id = "g",
            Method = ProtocolMethods.VisionAnalyze,
            Params = doc.RootElement.Clone(),
        });
        if (resp.Error is not null)
        {
            Log("AI 识图错误: " + resp.Error.Message);
            return;
        }
        var result = resp.Result;
        if (result is { } r && r.TryGetProperty("analysis", out var analysis) && analysis.ValueKind == JsonValueKind.String)
            Log("本地 AI 识图结果：" + analysis.GetString());
        else
            Log("AI 识图失败: " + (result?.GetRawText() ?? "无结果"));
    }

    private async Task ClickTextAsync(int count)
        => await JsonAsync(ProtocolMethods.Act, new { device = Dev, action = count >= 2 ? "double" : "click", text = _clickText.Text });

    private async Task ClickCoordAsync()
    {
        var (x, y, norm) = ParseCoord();
        object point = BuildPoint(x, y, norm);
        await JsonAsync(ProtocolMethods.Act, new { device = Dev, action = "click", point });
    }

    private async Task FindAsync()
    {
        var (x, y, norm) = ParseCoord();
        if (string.IsNullOrWhiteSpace(_coordBox.Text))
            await JsonAsync(ProtocolMethods.Find, new { device = Dev, text = _clickText.Text });
        else
            await JsonAsync(ProtocolMethods.Find, new { device = Dev, point = BuildPoint(x, y, norm) });
    }

    private async Task TypeAsync()
        => await JsonAsync(ProtocolMethods.Act, new { device = Dev, action = "type", value = _typeBox.Text, unicode = true });

    private async Task KeyAsync(string key)
        => await JsonAsync(ProtocolMethods.Act, new { device = Dev, action = "key", key });

    private async Task ScrollAsync(int dy)
        => await JsonAsync(ProtocolMethods.Act, new { device = Dev, action = "scroll", dy });

    private async Task VolumeAsync(double v)
        => await JsonAsync(ProtocolMethods.MediaVolume, new { device = Dev, value = v });

    private async Task RecStartAsync()
        => await JsonAsync(ProtocolMethods.RecordStart, new { device = Dev, name = "gui_rec" });

    private async Task RecStopAsync()
    {
        _lastRecPath = Path.Combine(Path.GetTempPath(), "guio_gui_rec.json");
        await JsonAsync(ProtocolMethods.RecordStop, new { device = Dev, @out = _lastRecPath });
    }

    private async Task ReplayAsync()
    {
        if (_lastRecPath is null) { Log("尚无录制，请先「开始录制」。"); return; }
        await JsonAsync(ProtocolMethods.Replay, new { device = Dev, file = _lastRecPath, rescale = true });
    }

    private static object BuildPoint(double x, double y, bool norm)
        => norm ? new { norm = new[] { x, y } } : (object)new { x = (int)x, y = (int)y };

    private (double x, double y, bool norm) ParseCoord()
    {
        var parts = (_coordBox.Text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && double.TryParse(parts[0], out var x) && double.TryParse(parts[1], out var y))
            return (x, y, x <= 1 && y <= 1);
        return (0, 0, true);
    }

    private void Log(string s)
    {
        var line = DateTime.Now.ToString("HH:mm:ss") + "   " + s;
        _log.Text = string.IsNullOrEmpty(_log.Text) ? line : _log.Text + Environment.NewLine + line;
        _log.CaretIndex = _log.Text!.Length;
    }

    private static string Clip(string? s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= n ? s : s[..n] + "…";
    }

    private sealed record NodeItem(string Label, int CenterX, int CenterY)
    {
        public override string ToString() => Label;
    }
}
