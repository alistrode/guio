using System.Runtime.CompilerServices;
using GuIO.Contracts;
using GuIO.Vision;

namespace GuIO.Driver.Windows;

/// <summary>
/// Windows 桌面驱动：唯一接触 Windows 平台 API 的地方。
/// 统一实现感知（截屏/语义树/OCR/画面流）、动作（SendInput）、媒体（NAudio）。
/// </summary>
public sealed class WindowsDriver : IDeviceDriver, ISelectorAware, IExtendedActuator, IWindowEnumerable, IAudioDiagnosable
{
    private readonly IntPtr _hwnd;              // IntPtr.Zero 表示整个屏幕
    private readonly InputInjector _injector = new();
    private readonly WindowsMedia _media = new();
    private UiaSemanticReader? _uia;
    private IOcrEngine? _ocr;
    private DeviceDescriptor _descriptor;

    public WindowsDriver(DeviceId id)
    {
        ParseTarget(id, out _hwnd);
        _descriptor = BuildDescriptor();
    }

    private static void ParseTarget(DeviceId id, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        // win://window/<hwnd>  或 win://app/<title|proc>  或 win://screen/0
        var path = id.Path;
        if (path.StartsWith("window/", StringComparison.OrdinalIgnoreCase))
        {
            var s = path["window/".Length..];
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hwnd = new IntPtr(Convert.ToInt64(s[2..], 16));
            else hwnd = new IntPtr(long.Parse(s));
        }
        else if (path.StartsWith("app/", StringComparison.OrdinalIgnoreCase))
        {
            var q = Uri.UnescapeDataString(path["app/".Length..]);
            var w = WindowManager.Find(q);
            if (w is not null) hwnd = w.Hwnd;
        }
        // win://screen/N => hwnd = Zero
    }

    private DeviceDescriptor BuildDescriptor()
    {
        var caps =
            CapabilityFlags.CaptureFrame | CapabilityFlags.Ocr | CapabilityFlags.TemplateMatch | CapabilityFlags.FrameDiff |
            CapabilityFlags.SemanticTree | CapabilityFlags.CaptureStream |
            CapabilityFlags.AllPointer | CapabilityFlags.AllInput |
            CapabilityFlags.WindowEnum | CapabilityFlags.WindowManage | CapabilityFlags.WindowActivate |
            CapabilityFlags.ProcessLaunch | CapabilityFlags.Shell | CapabilityFlags.SystemKeys |
            CapabilityFlags.AppLaunch | CapabilityFlags.AppList |
            CapabilityFlags.AllMedia;

        PixelRect bounds;
        bool fg = false;
        string name;
        string? proc = null;

        if (_hwnd != IntPtr.Zero && Win32.IsWindow(_hwnd))
        {
            Win32.GetWindowRect(_hwnd, out var r);
            bounds = new PixelRect(r.Left, r.Top, r.Right, r.Bottom);
            fg = Win32.GetForegroundWindow() == _hwnd;
            name = WindowTitle(_hwnd);
            uint pid = 0; Win32.GetWindowThreadProcessId(_hwnd, out pid);
            proc = ProcessName(pid);
        }
        else
        {
            int w = Win32.GetSystemMetrics(0), h = Win32.GetSystemMetrics(1);
            bounds = new PixelRect(0, 0, w, h);
            name = $"屏幕 ({w}x{h})";
        }

        return new DeviceDescriptor
        {
            Id = _hwnd != IntPtr.Zero ? DeviceId.Windows(_hwnd.ToInt64()) : DeviceId.WindowsScreen(),
            Kind = DeviceKind.WindowsDesktop,
            Name = name,
            Capabilities = caps,
            Bounds = bounds,
            DpiScale = GetDpiScale(),
            IsForeground = fg,
            OwnerProcess = proc,
        };
    }

    private static double GetDpiScale()
    {
        try
        {
            using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
            return g.DpiX / 96.0;
        }
        catch { return 1.0; }
    }

    private static string WindowTitle(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(512);
        Win32.GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string? ProcessName(uint pid)
    {
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch { return null; }
    }

    /// <summary>枚举本机所有可见窗口（按面积降序），供用户挑选准确的目标。</summary>
    public IReadOnlyList<WindowSummary> ListWindows()
    {
        var list = new List<WindowSummary>();
        foreach (var w in WindowManager.Enumerate())
        {
            if (!w.Visible) continue;
            string proc;
            try { proc = System.Diagnostics.Process.GetProcessById((int)w.Pid).ProcessName; }
            catch { proc = ""; }
            list.Add(new WindowSummary
            {
                Hwnd = w.Hwnd.ToInt64(),
                Pid = (int)w.Pid,
                ProcessName = proc,
                Title = w.Title,
                ClassName = w.ClassName,
                Bounds = w.Rect,
                Visible = w.Visible,
                Minimized = Win32.IsIconic(w.Hwnd),
            });
        }
        return list.OrderByDescending(x => (long)x.Bounds.Width * x.Bounds.Height).ToArray();
    }

    /// <summary>音频诊断（只读）。</summary>
    public object DiagnoseAudio() => _media.Diagnose();

    /// <summary>切换默认播放设备。</summary>
    public GuioResult<bool> SetDefaultOutput(string deviceId) => _media.SetDefaultOutput(deviceId);

    public DeviceDescriptor Descriptor => _descriptor;
    public IPerception Perception { get; private set; } = null!;
    public IActuator Actuator { get; private set; } = null!;
    public IMediaDevice Media => _media;

    public Task<GuioResult<bool>> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            Win32.SetProcessDPIAware();
            _uia = new UiaSemanticReader();
            _ocr = new WindowsOcrEngine();
            Perception = new WinPerception(this, _ocr);
            Actuator = new WinActuator(_injector, this);
            _descriptor = BuildDescriptor();
            return Task.FromResult(GuioResult<bool>.Success(true));
        }
        catch (Exception ex) { return Task.FromResult(GuioResult<bool>.Fail(ErrorCode.Internal, ex.Message)); }
    }

    public Task<DeviceDescriptor> RefreshAsync(CancellationToken ct = default)
    {
        _descriptor = BuildDescriptor();
        return Task.FromResult(_descriptor);
    }

    internal IntPtr Hwnd => _hwnd;
    internal UiaSemanticReader? Uia => _uia;
    internal void StopLoopback() => _media.StopLoopback();

    /// <summary>供选择器解算：把一个选择器解成目标（含偏移换算到屏幕坐标）。</summary>
    public async Task<GuioResult<ResolvedTarget>> ResolveAsync(Selector selector, PixelPoint? offset, CancellationToken ct = default)
    {
        var frame = selector.Kind == SelectorKind.Image || selector.Kind == SelectorKind.ScreenText
            ? await Perception.CaptureAsync(selector.Region, ct).ConfigureAwait(false) : null;
        IReadOnlyList<UiNode> nodes = Array.Empty<UiNode>();
        if (selector.Kind is SelectorKind.Node or SelectorKind.Text && _uia is not null)
        {
            var (hwnd, _) = ResolveTarget();
            nodes = _uia.ReadTree(hwnd);
        }
        IReadOnlyList<TextBlock> text = Array.Empty<TextBlock>();
        if (frame?.Ok == true && frame.Value is not null && _ocr is not null)
            text = await _ocr.RecognizeAsync(frame.Value, ct).ConfigureAwait(false);

        var obs = new Observation
        {
            Device = _descriptor.Id,
            Frame = frame?.Value,
            Nodes = nodes,
            Text = text,
            Screen = _descriptor.Bounds,
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        var resolver = new SelectorResolver(new TemplateMatcher(), new ImageSharpFrameCodec());
        var chain = SelectorResolver.AutoChain(selector, _descriptor.Capabilities);
        var r = resolver.ResolveChain(chain, obs);
        if (r.Ok && r.Value is { } hit && offset is { } off)
            r = GuioResult<ResolvedTarget>.Success(hit with { Point = hit.Point + off });
        return r;
    }

    /// <summary>扩展动作：应用/Shell 类。</summary>
    public async Task<GuioResult<bool>> ExecuteExtendedAsync(GuioAction action, CancellationToken ct = default)
    {
        switch (action)
        {
            case AppAction app:
                return await RunAppAsync(app, ct);
            case ShellAction sh:
                return RunShell(sh);
            case ActivateAction:
            {
                var (hwnd, scoped) = ResolveTarget();
                if (!scoped) return GuioResult<bool>.Fail(ErrorCode.InvalidArgument, "activate 需要目标窗口");
                bool ok = WindowManager.EnsureForeground(hwnd);
                return ok ? GuioResult<bool>.Success(true) : GuioResult<bool>.Fail(ErrorCode.Internal, "激活窗口失败");
            }
            default:
                return GuioResult<bool>.Fail(ErrorCode.Unsupported, $"不支持的扩展动作 {action.Name}");
        }
    }

    private async Task<GuioResult<bool>> RunAppAsync(AppAction app, CancellationToken ct)
    {
        try
        {
            switch (app.Op)
            {
                case AppOp.Launch or AppOp.Restart:
                {
                    if (app.Op == AppOp.Restart) await KillByNameAsync(app.Target);
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = app.Target!,
                        Arguments = app.Arguments ?? "",
                        UseShellExecute = true,
                    };
                    System.Diagnostics.Process.Start(psi);
                    return GuioResult<bool>.Success(true);
                }
                case AppOp.Kill:
                    await KillByNameAsync(app.Target);
                    return GuioResult<bool>.Success(true);
                case AppOp.List:
                {
                    var names = System.Diagnostics.Process.GetProcesses()
                        .Where(p => !string.IsNullOrEmpty(p.MainWindowTitle))
                        .Select(p => $"{p.ProcessName}|{p.Id}|{p.MainWindowTitle}").OrderBy(s => s).ToArray();
                    return GuioResult<bool>.Success(true, names.Length);
                }
                default:
                    return GuioResult<bool>.Fail(ErrorCode.Unsupported, "Windows 驱动暂不支持该应用操作");
            }
        }
        catch (Exception ex) { return GuioResult<bool>.Fail(ErrorCode.Internal, ex.Message); }
    }

    private static Task KillByNameAsync(string? name)
    {
        if (!string.IsNullOrEmpty(name))
        {
            var q = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(q))
            {
                try { p.Kill(true); } catch { }
            }
        }
        return Task.CompletedTask;
    }

    private static GuioResult<bool> RunShell(ShellAction sh)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + sh.Command,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit(60000);
            return GuioResult<bool>.Success(true, p.ExitCode);
        }
        catch (Exception ex) { return GuioResult<bool>.Fail(ErrorCode.Internal, ex.Message); }
    }

    /// <summary>确定感知/动作的作用目标：指定窗口时用其客户区中心坐标系，否则整屏。</summary>
    internal (IntPtr hwnd, bool scoped) ResolveTarget()
    {
        if (_hwnd != IntPtr.Zero && Win32.IsWindow(_hwnd)) return (_hwnd, true);
        return (IntPtr.Zero, false);
    }

    /// <summary>
    /// 注入前确保目标窗口在前台。承接 winio.ps1 的工程经验：
    /// 独立进程（如 CLI 调用）的控制台会抢占前台，导致 SendInput 落到错误窗口。
    /// </summary>
    internal void EnsureTargetForeground()
    {
        var (hwnd, scoped) = ResolveTarget();
        if (scoped) WindowManager.EnsureForeground(hwnd);
    }

    public async ValueTask DisposeAsync()
    {
        _uia?.Dispose();
        _media.Dispose();
        await Task.CompletedTask;
    }
}

/// <summary>Windows 感知实现。</summary>
internal sealed class WinPerception : IPerception
{
    private readonly WindowsDriver _driver;
    private readonly IOcrEngine _ocr;
    private readonly UiaSemanticReader? _uia;

    public WinPerception(WindowsDriver driver, IOcrEngine? ocr)
    {
        _driver = driver;
        _ocr = ocr ?? new WindowsOcrEngine();
        _uia = driver.Uia;
    }

    public Task<GuioResult<Frame>> CaptureAsync(PixelRect? region, CancellationToken ct = default)
    {
        try
        {
            var (hwnd, scoped) = _driver.ResolveTarget();
            Frame? f = scoped ? ScreenCapture.CaptureWindow(hwnd) : ScreenCapture.CaptureScreen(region ?? _driver.Descriptor.Bounds);
            if (f is null) return Task.FromResult(GuioResult<Frame>.Fail(ErrorCode.Internal, "截屏失败"));
            return Task.FromResult(GuioResult<Frame>.Success(f));
        }
        catch (Exception ex) { return Task.FromResult(GuioResult<Frame>.Fail(ErrorCode.Internal, ex.Message)); }
    }

    public Task<GuioResult<IReadOnlyList<UiNode>>> ReadTreeAsync(CancellationToken ct = default)
    {
        try
        {
            if (_uia is null) return Task.FromResult(GuioResult<IReadOnlyList<UiNode>>.Fail(ErrorCode.Unsupported, "UIA 未就绪"));
            var (hwnd, scoped) = _driver.ResolveTarget();
            var tree = scoped ? _uia.ReadTree(hwnd) : _uia.ReadTree(GetForeground());
            return Task.FromResult(GuioResult<IReadOnlyList<UiNode>>.Success(tree));
        }
        catch (Exception ex) { return Task.FromResult(GuioResult<IReadOnlyList<UiNode>>.Fail(ErrorCode.Internal, ex.Message)); }
    }

    private static IntPtr GetForeground() => Win32.GetForegroundWindow();

    public async Task<GuioResult<IReadOnlyList<TextBlock>>> OcrAsync(Frame frame, CancellationToken ct = default)
    {
        try
        {
            var r = await _ocr.RecognizeAsync(frame, ct).ConfigureAwait(false);
            return GuioResult<IReadOnlyList<TextBlock>>.Success(r);
        }
        catch (Exception ex) { return GuioResult<IReadOnlyList<TextBlock>>.Fail(ErrorCode.Internal, ex.Message); }
    }

    public async IAsyncEnumerable<Frame> StreamFramesAsync(StreamOptions options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        int interval = Math.Max(16, 1000 / Math.Max(1, options.TargetFps));
        int count = 0;
        while (!ct.IsCancellationRequested)
        {
            var r = await CaptureAsync(options.Region, ct).ConfigureAwait(false);
            if (r.Ok && r.Value is not null) yield return r.Value;
            count++;
            if (options.MaxFrames > 0 && count >= options.MaxFrames) yield break;
            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>Windows 动作实现。</summary>
internal sealed class WinActuator : IActuator
{
    private readonly InputInjector _injector;
    private readonly WindowsDriver _driver;

    public WinActuator(InputInjector injector, WindowsDriver driver)
    {
        _injector = injector;
        _driver = driver;
    }

    /// <summary>注入前夺回前台，避免独立进程的控制台抢焦点。</summary>
    private void Fore() => _driver.EnsureTargetForeground();

    /// <summary>把「屏幕绝对坐标」换算为实际注入坐标。命中语义节点时，坐标已是屏幕坐标。</summary>
    private (int x, int y) ToScreen(PixelPoint p) => (p.X, p.Y);

    public Task<GuioResult<bool>> ClickAsync(PixelPoint at, PointerButton button, int count, double holdMs, CancellationToken ct = default)
    {
        try { Fore(); var (x, y) = ToScreen(at); _injector.Click(x, y, button, count, holdMs); return Ok(); }
        catch (Exception ex) { return Fail(ex); }
    }

    public Task<GuioResult<bool>> MoveAsync(PixelPoint to, CancellationToken ct = default)
    {
        try { Fore(); var (x, y) = ToScreen(to); _injector.Move(x, y); return Ok(); }
        catch (Exception ex) { return Fail(ex); }
    }

    public Task<GuioResult<bool>> DragAsync(PixelPoint from, PixelPoint to, int durationMs, int steps, CancellationToken ct = default)
    {
        try
        {
            Fore();
            var (x1, y1) = ToScreen(from); var (x2, y2) = ToScreen(to);
            _injector.Drag(x1, y1, x2, y2, durationMs, steps);
            return Ok();
        }
        catch (Exception ex) { return Fail(ex); }
    }

    public Task<GuioResult<bool>> SwipeAsync(PixelPoint from, PixelPoint to, int durationMs, CancellationToken ct = default)
    {
        // 桌面把滑动映射为「按下-移动-抬起」拖拽
        try
        {
            Fore();
            var (x1, y1) = ToScreen(from); var (x2, y2) = ToScreen(to);
            _injector.Drag(x1, y1, x2, y2, durationMs, 24);
            return Ok();
        }
        catch (Exception ex) { return Fail(ex); }
    }

    public Task<GuioResult<bool>> PinchAsync(PixelPoint center, double scale, double spanPx, int durationMs, CancellationToken ct = default)
    {
        // 桌面无捏合；映射为 Ctrl+滚轮 缩放
        try
        {
            var (x, y) = ToScreen(center);
            _injector.Move(x, y);
            Thread.Sleep(40);
            int notches = scale >= 1 ? 3 : -3;
            _injector.Key(0x11, Array.Empty<string>(), 1); // 先按一次 Ctrl 满足惯用缩放
            _injector.Scroll(x, y, 0, notches, ScrollUnit.Wheel);
            return Ok();
        }
        catch (Exception ex) { return Fail(ex); }
    }

    public Task<GuioResult<bool>> ScrollAsync(PixelPoint? at, int dx, int dy, ScrollUnit unit, CancellationToken ct = default)
    {
        try { Fore(); _injector.Scroll(at?.X, at?.Y, dx, dy, unit); return Ok(); }
        catch (Exception ex) { return Fail(ex); }
    }

    public Task<GuioResult<bool>> TypeAsync(string text, bool unicode, bool clearFirst, CancellationToken ct = default)
    {
        try
        {
            Fore();
            if (clearFirst) _injector.Key(0x41, new[] { "Ctrl" }, 1); // 全选
            _injector.Type(text, unicode);
            return Ok();
        }
        catch (Exception ex) { return Fail(ex); }
    }

    public Task<GuioResult<bool>> KeyAsync(int virtualKey, IReadOnlyList<string> modifiers, int repeat, CancellationToken ct = default)
    {
        try { Fore(); _injector.Key(virtualKey, modifiers, repeat); return Ok(); }
        catch (Exception ex) { return Fail(ex); }
    }

    public Task<GuioResult<bool>> SystemKeyAsync(SystemKey key, CancellationToken ct = default)
    {
        try
        {
            // 桌面映射：Back=Alt+Left, Home=Win, Recents=Alt+Tab
            Fore();
            switch (key)
            {
                case SystemKey.Back: _injector.Key(0x25, new[] { "Alt" }, 1); break;
                case SystemKey.Home: _injector.Key(0x5B, Array.Empty<string>(), 1); break;
                case SystemKey.Recents: _injector.Key(0x09, new[] { "Alt" }, 1); break;
                case SystemKey.VolumeUp: _injector.Key(0xAF, Array.Empty<string>(), 1); break;
                case SystemKey.VolumeDown: _injector.Key(0xAE, Array.Empty<string>(), 1); break;
                case SystemKey.Mute: _injector.Key(0xAD, Array.Empty<string>(), 1); break;
                default: return Task.FromResult(GuioResult<bool>.Fail(ErrorCode.Unsupported, $"桌面无系统键 {key}"));
            }
            return Ok();
        }
        catch (Exception ex) { return Fail(ex); }
    }

    public Task<GuioResult<bool>> InvokeNodeAsync(UiNode node, string action, CancellationToken ct = default)
    {
        try
        {
            var (hwnd, scoped) = _driver.ResolveTarget();
            var uia = _driver.Uia;
            if (uia is null || !scoped) return Task.FromResult(GuioResult<bool>.Fail(ErrorCode.Unsupported, "无 UIA 上下文"));

            // 采用「按包围盒点击」作为语义动作的通用落地（跨控件类型最稳）
            if (!node.Bounds.IsEmpty)
            {
                var c = node.Bounds.Center;
                _injector.Click(c.X, c.Y, PointerButton.Left, 1, 0);
                return Ok();
            }
            return Task.FromResult(GuioResult<bool>.Fail(ErrorCode.NotFound, "节点无有效区域"));
        }
        catch (Exception ex) { return Fail(ex); }
    }

    private static Task<GuioResult<bool>> Ok() => Task.FromResult(GuioResult<bool>.Success(true));
    private static Task<GuioResult<bool>> Fail(Exception ex) => Task.FromResult(GuioResult<bool>.Fail(ErrorCode.Internal, ex.Message));
}
