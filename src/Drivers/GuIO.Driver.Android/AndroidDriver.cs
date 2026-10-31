using Android.AccessibilityServices;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.OS;
using Android.Provider;
using Android.Views.Accessibility;
using GuIO.Contracts;
using ErrorCode = GuIO.Contracts.ErrorCode;

namespace GuIO.Driver.Android;

/// <summary>
/// Android 驱动：端侧自足。感知（无障碍树/截屏）、动作（无障碍手势/输入/系统键）、
/// 应用与音量。与 Windows 驱动实现同一个 IDeviceDriver 契约，上层内核零改动。
/// </summary>
public sealed class AndroidDriver : IDeviceDriver, ISelectorAware, IExtendedActuator
{
    private readonly Context _ctx;
    private DeviceDescriptor _descriptor = null!;
    private AndroidMedia _media = null!;

    /// <summary>可选的屏幕截图提供者（由宿主用 MediaProjection 注入）。</summary>
    public Func<PixelRect?, Frame?>? ScreenshotProvider { get; set; }

    /// <summary>可选的提权执行者（Shizuku 注入后提供 shell 能力）。</summary>
    public Func<string, (int code, string output)>? ShellExecutor { get; set; }

    public AndroidDriver(DeviceId id)
    {
        _ctx = global::Android.App.Application.Context;
        _descriptor = BuildDescriptor();
    }

    private DeviceDescriptor BuildDescriptor()
    {
        var metrics = _ctx.Resources?.DisplayMetrics;
        int w = metrics?.WidthPixels ?? 1080;
        int h = metrics?.HeightPixels ?? 2400;
        double dens = metrics?.Density ?? 2.75;

        var caps = CapabilityFlags.CaptureFrame | CapabilityFlags.SemanticTree | CapabilityFlags.Ocr |
                   CapabilityFlags.TemplateMatch | CapabilityFlags.FrameDiff |
                   CapabilityFlags.PointerClick | CapabilityFlags.PointerLongPress |
                   CapabilityFlags.PointerSwipe | CapabilityFlags.PointerPinch |
                   CapabilityFlags.PointerScroll |
                   CapabilityFlags.KeyPress | CapabilityFlags.SystemKeys |
                   CapabilityFlags.TypeText | CapabilityFlags.TypeUnicode |
                   CapabilityFlags.AppLaunch | CapabilityFlags.AppList |
                   CapabilityFlags.AudioPlay | CapabilityFlags.AudioVolume;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q) caps |= CapabilityFlags.CaptureStream;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q) caps |= CapabilityFlags.AudioLoopbackRec;
        if (ShellExecutor is not null) caps |= CapabilityFlags.Shell;

        return new DeviceDescriptor
        {
            Id = new DeviceId("android", "local"),
            Kind = DeviceKind.AndroidPhone,
            Name = $"{Build.Manufacturer} {Build.Model}",
            Capabilities = caps,
            Bounds = new PixelRect(0, 0, w, h),
            DpiScale = dens,
            Rotation = 0,
            Extra = new Dictionary<string, string>
            {
                ["sdk"] = Build.VERSION.SdkInt.ToString(),
                ["release"] = Build.VERSION.Release ?? "",
                ["model"] = Build.Model ?? "",
            },
        };
    }

    public DeviceDescriptor Descriptor => _descriptor;
    public IPerception Perception { get; private set; } = null!;
    public IActuator Actuator { get; private set; } = null!;
    public IMediaDevice Media => _media;

    private GuioAccessibilityService? Service => GuioAccessibilityService.Instance;

    public Task<GuioResult<bool>> ConnectAsync(CancellationToken ct = default)
    {
        _media = new AndroidMedia(_ctx);
        Perception = new AndroidPerception(this);
        Actuator = new AndroidActuator(this);
        _descriptor = BuildDescriptor();
        if (Service is null)
        {
            return Task.FromResult(GuioResult<bool>.Fail(ErrorCode.PermissionDenied,
                "无障碍服务未启用：请在系统设置中为 GuIO 开启无障碍权限"));
        }
        return Task.FromResult(GuioResult<bool>.Success(true));
    }

    public Task<DeviceDescriptor> RefreshAsync(CancellationToken ct = default)
    {
        _descriptor = BuildDescriptor();
        return Task.FromResult(_descriptor);
    }

    internal GuioAccessibilityService RequireService() =>
        Service ?? throw new InvalidOperationException("无障碍服务未启用");

    internal Frame? TakeScreenshot(PixelRect? region) => ScreenshotProvider?.Invoke(region);

    public async Task<GuioResult<ResolvedTarget>> ResolveAsync(Selector selector, PixelPoint? offset, CancellationToken ct = default)
    {
        var frame = selector.Kind is SelectorKind.Image or SelectorKind.ScreenText ? TakeScreenshot(selector.Region) : null;
        var nodes = selector.Kind is SelectorKind.Node or SelectorKind.Text && Service is not null
            ? Service.ReadTree() : Array.Empty<UiNode>();

        var obs = new Observation
        {
            Device = _descriptor.Id,
            Frame = frame,
            Nodes = nodes,
            Text = Array.Empty<TextBlock>(),
            Screen = _descriptor.Bounds,
            DpiScale = _descriptor.DpiScale,
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        var resolver = new GuIO.Vision.SelectorResolver(new GuIO.Vision.TemplateMatcher(), new GuIO.Vision.ImageSharpFrameCodec());
        var chain = GuIO.Vision.SelectorResolver.AutoChain(selector, _descriptor.Capabilities);
        var r = resolver.ResolveChain(chain, obs);
        if (r.Ok && r.Value is { } hit && offset is { } off)
            r = GuioResult<ResolvedTarget>.Success(hit with { Point = hit.Point + off });
        await Task.CompletedTask;
        return r;
    }

    public Task<GuioResult<bool>> ExecuteExtendedAsync(GuioAction action, CancellationToken ct = default)
    {
        switch (action)
        {
            case AppAction app: return Task.FromResult(RunApp(app));
            case ShellAction sh:
                if (ShellExecutor is null) return Task.FromResult(GuioResult<bool>.Fail(ErrorCode.Unsupported, "未接入 Shizuku 提权，无法执行 shell"));
                var (code, _) = ShellExecutor(sh.Command);
                return Task.FromResult(code == 0 ? GuioResult<bool>.Success(true) : GuioResult<bool>.Fail(ErrorCode.Internal, $"exit={code}"));
            default: return Task.FromResult(GuioResult<bool>.Fail(ErrorCode.Unsupported, action.Name));
        }
    }

    private GuioResult<bool> RunApp(AppAction app)
    {
        try
        {
            var pm = _ctx.PackageManager!;
            switch (app.Op)
            {
                case AppOp.Launch or AppOp.Restart:
                {
                    var intent = pm.GetLaunchIntentForPackage(app.Target!);
                    if (intent is null) return GuioResult<bool>.Fail(ErrorCode.NotFound, $"未找到应用 {app.Target}");
                    intent.AddFlags(ActivityFlags.NewTask);
                    _ctx.StartActivity(intent);
                    return GuioResult<bool>.Success(true);
                }
                case AppOp.List:
                {
                    var pkgs = pm.GetInstalledApplications(global::Android.Content.PM.PackageInfoFlags.MatchAll);
                    return GuioResult<bool>.Success(true, pkgs?.Count ?? 0);
                }
                default:
                    return GuioResult<bool>.Fail(ErrorCode.Unsupported, "端侧不支持该应用操作（需 Shizuku 提权）");
            }
        }
        catch (Exception ex) { return GuioResult<bool>.Fail(ErrorCode.Internal, ex.Message); }
    }

    public ValueTask DisposeAsync()
    {
        _media?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Android 感知。</summary>
internal sealed class AndroidPerception : IPerception
{
    private readonly AndroidDriver _driver;
    public AndroidPerception(AndroidDriver d) => _driver = d;

    public Task<GuioResult<Frame>> CaptureAsync(PixelRect? region, CancellationToken ct = default)
    {
        var f = _driver.TakeScreenshot(region);
        return Task.FromResult(f is null
            ? GuioResult<Frame>.Fail(ErrorCode.PermissionDenied, "截屏未就绪（需授予屏幕捕获权限）")
            : GuioResult<Frame>.Success(f));
    }

    public Task<GuioResult<IReadOnlyList<UiNode>>> ReadTreeAsync(CancellationToken ct = default)
    {
        var svc = GuioAccessibilityService.Instance;
        if (svc is null) return Task.FromResult(GuioResult<IReadOnlyList<UiNode>>.Fail(ErrorCode.PermissionDenied, "无障碍服务未启用"));
        return Task.FromResult(GuioResult<IReadOnlyList<UiNode>>.Success(svc.ReadTree()));
    }

    public Task<GuioResult<IReadOnlyList<TextBlock>>> OcrAsync(Frame frame, CancellationToken ct = default)
        => Task.FromResult(GuioResult<IReadOnlyList<TextBlock>>.Fail(ErrorCode.Unsupported, "端侧 OCR 由视觉层提供"));

    public async IAsyncEnumerable<Frame> StreamFramesAsync(StreamOptions options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        int interval = Math.Max(16, 1000 / Math.Max(1, options.TargetFps));
        int count = 0;
        while (!ct.IsCancellationRequested)
        {
            var f = _driver.TakeScreenshot(options.Region);
            if (f is not null) yield return f;
            count++;
            if (options.MaxFrames > 0 && count >= options.MaxFrames) yield break;
            await Task.Delay(interval, ct);
        }
    }
}

/// <summary>Android 动作：无障碍手势 + 输入 + 系统键。</summary>
internal sealed class AndroidActuator : IActuator
{
    private readonly AndroidDriver _driver;
    public AndroidActuator(AndroidDriver d) => _driver = d;

    public Task<GuioResult<bool>> ClickAsync(PixelPoint at, PointerButton button, int count, double holdMs, CancellationToken ct = default)
    {
        try
        {
            var svc = _driver.RequireService();
            var rect = PixelRect.FromSize(at.X - 2, at.Y - 2, 4, 4);
            bool ok = svc.InvokeNodeByBounds(rect, holdMs > 0);
            if (!ok)
            {
                // 无障碍节点不可点：退化为手势点击
                var path = new global::Android.Graphics.Path();
                path.MoveTo(at.X, at.Y);
                int duration = (int)Math.Max(holdMs > 0 ? holdMs : 50, 50);
                var stroke = new GestureDescription.StrokeDescription(path, 0, duration);
                var b = new GestureDescription.Builder();
                b.AddStroke(stroke);
                ok = svc.DispatchGesture(b.Build(), null, null);
            }
            return Task.FromResult(ok ? GuioResult<bool>.Success(true) : GuioResult<bool>.Fail(ErrorCode.Internal, "点击失败"));
        }
        catch (Exception ex) { return Task.FromResult(GuioResult<bool>.Fail(ErrorCode.Internal, ex.Message)); }
    }

    public Task<GuioResult<bool>> MoveAsync(PixelPoint to, CancellationToken ct = default) =>
        Task.FromResult(GuioResult<bool>.Fail(ErrorCode.Unsupported, "Android 无鼠标悬停"));

    public Task<GuioResult<bool>> DragAsync(PixelPoint from, PixelPoint to, int durationMs, int steps, CancellationToken ct = default)
        => SwipeAsync(from, to, durationMs, ct);

    public Task<GuioResult<bool>> SwipeAsync(PixelPoint from, PixelPoint to, int durationMs, CancellationToken ct = default)
    {
        try
        {
            var svc = _driver.RequireService();
            var path = new global::Android.Graphics.Path();
            path.MoveTo(from.X, from.Y);
            path.LineTo(to.X, to.Y);
            var stroke = new GestureDescription.StrokeDescription(path, 0, Math.Max(60, durationMs));
            var b = new GestureDescription.Builder();
            b.AddStroke(stroke);
            bool ok = svc.DispatchGesture(b.Build(), null, null);
            return Task.FromResult(ok ? GuioResult<bool>.Success(true) : GuioResult<bool>.Fail(ErrorCode.Internal, "滑动失败"));
        }
        catch (Exception ex) { return Task.FromResult(GuioResult<bool>.Fail(ErrorCode.Internal, ex.Message)); }
    }

    public Task<GuioResult<bool>> PinchAsync(PixelPoint center, double scale, double spanPx, int durationMs, CancellationToken ct = default)
    {
        try
        {
            var svc = _driver.RequireService();
            var b = new GestureDescription.Builder();

            // 从中心向两侧（放大）或从两侧向中心（缩小）的双指轨迹
            double half = spanPx / 2;
            double startHalf = scale >= 1 ? half * 0.4 : half;
            double endHalf = scale >= 1 ? half : half * 0.4;

            var p1 = new global::Android.Graphics.Path(); p1.MoveTo((float)(center.X - startHalf), center.Y); p1.LineTo((float)(center.X - endHalf), center.Y);
            var p2 = new global::Android.Graphics.Path(); p2.MoveTo((float)(center.X + startHalf), center.Y); p2.LineTo((float)(center.X + endHalf), center.Y);
            int dur = Math.Max(80, durationMs);
            b.AddStroke(new GestureDescription.StrokeDescription(p1, 0, dur));
            b.AddStroke(new GestureDescription.StrokeDescription(p2, 0, dur));
            bool ok = svc.DispatchGesture(b.Build(), null, null);
            return Task.FromResult(ok ? GuioResult<bool>.Success(true) : GuioResult<bool>.Fail(ErrorCode.Internal, "捏合失败"));
        }
        catch (Exception ex) { return Task.FromResult(GuioResult<bool>.Fail(ErrorCode.Internal, ex.Message)); }
    }

    public Task<GuioResult<bool>> ScrollAsync(PixelPoint? at, int dx, int dy, ScrollUnit unit, CancellationToken ct = default)
    {
        try
        {
            var svc = _driver.RequireService();
            PixelPoint c = at ?? new PixelPoint(_driver.Descriptor.Bounds.Width / 2, _driver.Descriptor.Bounds.Height / 2);

            // 优先对可滚动节点做语义滚动
            var root = svc.RootNode();
            if (root is not null)
            {
                var target = FindScrollable(root, c, 0);
                if (target is not null && target.Scrollable)
                {
                    if (dy != 0) target.PerformAction(dy < 0
                        ? global::Android.Views.Accessibility.Action.ScrollForward
                        : global::Android.Views.Accessibility.Action.ScrollBackward);
                    return Task.FromResult(GuioResult<bool>.Success(true));
                }
            }

            // 退化为手指滑动
            int dist = (unit == ScrollUnit.Pixel ? Math.Abs(dy) : Math.Abs(dy) * 250);
            var from = new PixelPoint(c.X, dy < 0 ? c.Y + dist / 2 : c.Y - dist / 2);
            var to = new PixelPoint(c.X, dy < 0 ? c.Y - dist / 2 : c.Y + dist / 2);
            return SwipeAsync(from, to, 250, ct);
        }
        catch (Exception ex) { return Task.FromResult(GuioResult<bool>.Fail(ErrorCode.Internal, ex.Message)); }
    }

    private static AccessibilityNodeInfo? FindScrollable(AccessibilityNodeInfo node, PixelPoint p, int depth)
    {
        if (depth > 60) return null;
        var r = new Rect();
        node.GetBoundsInScreen(r);
        if (node.Scrollable && r.Contains(p.X, p.Y)) return node;
        for (int i = 0; i < node.ChildCount; i++)
        {
            var c = node.GetChild(i);
            if (c is null) continue;
            var f = FindScrollable(c, p, depth + 1);
            if (f is not null) return f;
        }
        return null;
    }

    public Task<GuioResult<bool>> TypeAsync(string text, bool unicode, bool clearFirst, CancellationToken ct = default)
    {
        try
        {
            var svc = _driver.RequireService();
            // 无障碍 SetText 通道：支持中文/emoji
            // 注：宿主会把目标 bounds 通过 Descriptor.Extra 或最近观测提供；这里默认当前焦点
            var rect = new PixelRect(0, 0, _driver.Descriptor.Bounds.Width, _driver.Descriptor.Bounds.Height);
            bool ok = svc.SetTextAtBounds(rect, text);
            return Task.FromResult(ok ? GuioResult<bool>.Success(true) : GuioResult<bool>.Fail(ErrorCode.Internal, "无可用输入框"));
        }
        catch (Exception ex) { return Task.FromResult(GuioResult<bool>.Fail(ErrorCode.Internal, ex.Message)); }
    }

    public Task<GuioResult<bool>> KeyAsync(int virtualKey, IReadOnlyList<string> modifiers, int repeat, CancellationToken ct = default)
    {
        // Android 键盘输入通过无障碍文本通道完成；单键（如 Enter）映射为系统动作
        try
        {
            var svc = _driver.RequireService();
            if (virtualKey == 0x0D) // Enter
            {
                var root = svc.RootNode();
                var target = FindFocused(root!, 0);
                if (target is not null)
                {
                    // 优先走 IME 提交
                    var args = new Bundle();
                    target.PerformAction(global::Android.Views.Accessibility.Action.Click);
                    return Task.FromResult(GuioResult<bool>.Success(true));
                }
            }
            return Task.FromResult(GuioResult<bool>.Fail(ErrorCode.Unsupported, "端侧单键输入受限，请用 type"));
        }
        catch (Exception ex) { return Task.FromResult(GuioResult<bool>.Fail(ErrorCode.Internal, ex.Message)); }
    }

    private static AccessibilityNodeInfo? FindFocused(AccessibilityNodeInfo node, int depth)
    {
        if (depth > 60) return null;
        if (node.Focused) return node;
        for (int i = 0; i < node.ChildCount; i++)
        {
            var c = node.GetChild(i);
            if (c is null) continue;
            var f = FindFocused(c, depth + 1);
            if (f is not null) return f;
        }
        return null;
    }

    public Task<GuioResult<bool>> SystemKeyAsync(SystemKey key, CancellationToken ct = default)
    {
        try
        {
            var svc = _driver.RequireService();
            bool ok = key switch
            {
                SystemKey.Back => svc.PerformGlobalAction(GlobalAction.Back),
                SystemKey.Home => svc.PerformGlobalAction(GlobalAction.Home),
                SystemKey.Recents => svc.PerformGlobalAction(GlobalAction.Recents),
                SystemKey.Notifications => svc.PerformGlobalAction(GlobalAction.Notifications),
                _ => false,
            };
            return Task.FromResult(ok ? GuioResult<bool>.Success(true) : GuioResult<bool>.Fail(ErrorCode.Unsupported, $"Android 无系统键 {key}（音量请用媒体接口）"));
        }
        catch (Exception ex) { return Task.FromResult(GuioResult<bool>.Fail(ErrorCode.Internal, ex.Message)); }
    }

    public Task<GuioResult<bool>> InvokeNodeAsync(UiNode node, string action, CancellationToken ct = default)
    {
        try
        {
            var svc = _driver.RequireService();
            bool ok = svc.InvokeNodeByBounds(node.Bounds, action.Contains("long", StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(ok ? GuioResult<bool>.Success(true) : GuioResult<bool>.Fail(ErrorCode.Internal, "节点动作失败"));
        }
        catch (Exception ex) { return Task.FromResult(GuioResult<bool>.Fail(ErrorCode.Internal, ex.Message)); }
    }
}
