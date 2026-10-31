using GuIO.Contracts;
using GuIO.Kernel;
using GuIO.Vision;
using Xunit;

namespace GuIO.Tests;

/// <summary>契约层：几何换算、选择器构造、设备标识、能力协商。</summary>
public class ContractTests
{
    [Fact]
    public void NormalizedPoint_MapsToPixels()
    {
        var area = new PixelRect(100, 50, 300, 250); // 200x200
        var p = new NormPoint(0.5, 0.5).ToPixels(area);
        Assert.Equal(200, p.X);
        Assert.Equal(150, p.Y);
    }

    [Fact]
    public void PixelRect_Intersect_Works()
    {
        var a = new PixelRect(0, 0, 100, 100);
        var b = new PixelRect(50, 50, 150, 150);
        var i = a.Intersect(b);
        Assert.Equal(50, i.Left);
        Assert.Equal(50, i.Width);
    }

    [Fact]
    public void DeviceId_RoundTrips()
    {
        var id = DeviceId.Windows(1234);
        Assert.Equal("win://window/1234", id.ToString());
        Assert.True(DeviceId.TryParse("android://local", out var a));
        Assert.Equal("android", a.Scheme);
        Assert.False(DeviceId.TryParse("garbage", out _));
    }

    [Fact]
    public void CapabilityNegotiation_RejectsUnsupported()
    {
        var caps = CapabilityFlags.PointerClick | CapabilityFlags.TypeText;
        Assert.True(CapabilityRegistry.Supports(caps, new ClickAction { Target = Selector.AtNorm(0.5, 0.5) }));
        Assert.False(CapabilityRegistry.Supports(caps, new PinchAction { Center = Selector.AtNorm(0.5, 0.5) }));
        Assert.False(CapabilityRegistry.Supports(caps, new KeyAction { Key = "A", Modifiers = new[] { "Ctrl" } }));
    }

    [Fact]
    public void ProtocolMethods_AreStable()
    {
        Assert.Equal("action.perform", ProtocolMethods.Act);
        Assert.Equal("perception.observe", ProtocolMethods.Observe);
    }
}

/// <summary>转发：能力缺失时给出 Unsupported，而不是崩溃。</summary>
public class CapabilityRejectTests
{
    [Fact]
    public void CapsOnly_NoTree_MeansNodeSelectorFails()
    {
        var caps = CapabilityFlags.None;
        Assert.Equal(CapabilityFlags.PointerClick, CapabilityRegistry.RequiredCapability(new ClickAction { Target = Selector.AtNorm(0, 0) }));
        Assert.False(CapabilityRegistry.Supports(caps, new ClickAction { Target = Selector.AtNorm(0, 0) }));
    }
}

/// <summary>降级链：语义树优先，找不到退文字。</summary>
public class ResolverTests
{
    private static Observation Obs(params UiNode[] nodes) => new()
    {
        Device = DeviceId.WindowsScreen(),
        Nodes = nodes,
        Screen = new PixelRect(0, 0, 1000, 1000),
    };

    [Fact]
    public void AutoChain_NodeWithText_FallsBackToText()
    {
        var sel = Selector.Node("btn_ok", "确定");
        var chain = SelectorResolver.AutoChain(sel, CapabilityFlags.SemanticTree | CapabilityFlags.Ocr);
        Assert.Contains(chain.Chain, s => s.Kind == SelectorKind.Node);
        Assert.Contains(chain.Chain, s => s.Kind == SelectorKind.Text && s.Value == "确定");
    }

    [Fact]
    public void ResolveNode_ById_Finds()
    {
        var resolver = new SelectorResolver(new TemplateMatcher(), new ImageSharpFrameCodec());
        var obs = Obs(new UiNode
        {
            Kind = "Button", AutomationId = "ok", Name = "确定",
            Bounds = new PixelRect(10, 10, 110, 50), Clickable = true,
        });
        var r = resolver.Resolve(Selector.Node("ok"), obs);
        Assert.True(r.Ok);
        Assert.Equal(60, r.Value!.Point.X);
    }

    [Fact]
    public void ResolvePoint_FromNorm_Works()
    {
        var resolver = new SelectorResolver(new TemplateMatcher(), new ImageSharpFrameCodec());
        var r = resolver.Resolve(Selector.AtNorm(0.25, 0.5), Obs());
        Assert.True(r.Ok);
        Assert.Equal(250, r.Value!.Point.X);
        Assert.Equal(500, r.Value!.Point.Y);
    }
}

/// <summary>帧差：可识别变化。</summary>
public class VisionTests
{
    private static Frame Solid(byte v) => new()
    {
        Width = 8, Height = 8, Stride = 32, Pixels = Enumerable.Repeat(v, 8 * 32).ToArray(),
        SourceRect = new PixelRect(0, 0, 8, 8),
    };

    [Fact]
    public void FrameDiff_DetectsChange()
    {
        Assert.Equal(0, FrameDiff.MeanDifference(Solid(10), Solid(10)), 3);
        Assert.True(FrameDiff.MeanDifference(Solid(10), Solid(200)) > 100);
    }

    [Fact]
    public void TemplateMatcher_FindsExactSubImage()
    {
        // 用带纹理的模板（纯色模板方差为 0，NCC 无法定位）
        int[,] pattern = new int[4, 4]
        {
            { 30, 200, 30, 200 },
            { 200, 30, 200, 30 },
            { 30, 200, 30, 200 },
            { 200, 30, 200, 30 },
        };
        var big = Solid(0);
        var px = (byte[])big.Pixels.Clone();
        const int ox = 4, oy = 2;
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                byte v = (byte)pattern[y, x];
                int i = (oy + y) * big.Stride + (ox + x) * 4;
                px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 255;
            }
        var hay = big with { Pixels = px };

        var tplPx = new byte[4 * 16];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                byte v = (byte)pattern[y, x];
                int i = y * 16 + x * 4;
                tplPx[i] = v; tplPx[i + 1] = v; tplPx[i + 2] = v; tplPx[i + 3] = 255;
            }
        var tpl = new Frame { Width = 4, Height = 4, Stride = 16, Pixels = tplPx, SourceRect = new PixelRect(0, 0, 4, 4) };

        var matcher = new TemplateMatcher();
        var hits = matcher.FindAll(hay, tpl, 0.95, null);
        Assert.NotEmpty(hits);
        Assert.Equal(ox, hits[0].Bounds.Left);
        Assert.Equal(oy, hits[0].Bounds.Top);
    }
}

/// <summary>重试/退避：指数增长、上限约束。</summary>
public class RetryTests
{
    [Fact]
    public void Backoff_GrowsAndCaps()
    {
        var p = new RetryPolicy { InitialDelayMs = 100, BackoffFactor = 2, MaxDelayMs = 500, Jitter = false };
        Assert.Equal(100, p.DelayFor(1));
        Assert.Equal(200, p.DelayFor(2));
        Assert.Equal(400, p.DelayFor(3));
        Assert.Equal(500, p.DelayFor(4));
    }

    [Fact]
    public async Task Retry_StopsOnUnsupported()
    {
        var p = new RetryPolicy { MaxAttempts = 5, InitialDelayMs = 1, Jitter = false };
        int calls = 0;
        var r = await p.RunAsync<bool>((_, _) => { calls++; return Task.FromResult(GuioResult<bool>.Fail(ErrorCode.Unsupported)); },
            new SystemClock(), default);
        Assert.False(r.Ok);
        Assert.Equal(1, calls); // 不可恢复错误不重试
    }
}

/// <summary>流程序列化：多态步骤可往返。</summary>
public class FlowTests
{
    [Fact]
    public void Flow_JsonRoundTrip()
    {
        var flow = new Flow
        {
            Name = "demo",
            Steps = new FlowStep[]
            {
                new DoStep { Action = new ClickAction { Target = Selector.AtNorm(0.5, 0.5) } },
                new WaitStep { Goal = Goal.Found(Selector.Text("完成")), TimeoutMs = 5000 },
                new IfStep { Condition = Goal.Found(Selector.Text("是")), Then = new FlowStep[] { new CallStep { FlowName = "sub" } } },
                new LoopStep { Times = 3, Body = new FlowStep[] { new DoStep { Action = new KeyAction { Key = "Enter" } } } },
            },
        };
        var json = FlowSerializer.ToJson(flow);
        var back = FlowSerializer.FromJson(json);
        Assert.NotNull(back);
        Assert.Equal(4, back!.Steps.Count);
        Assert.IsType<WaitStep>(back.Steps[1]);
    }
}

/// <summary>录制回放：时间线与流程转换。</summary>
public class RecorderTests
{
    [Fact]
    public void Recorder_CapturesAndConverts()
    {
        var rec = new Recorder();
        rec.Start("t", "win://screen/0");
        rec.Capture(new KeyAction { Key = "A" }, true, 10, null);
        rec.Capture(new ClickAction { Target = Selector.AtNorm(0.5, 0.5) }, true, 20, null);
        var r = rec.Stop();
        Assert.Equal(2, r.Steps.Count);
        var flow = Replayer.ToFlow(r);
        Assert.Equal(2, flow.Steps.Count);
    }
}

/// <summary>窗口枚举契约：DeviceUri 可回填设备地址。</summary>
public class WindowEnumTests
{
    [Fact]
    public void WindowSummary_DeviceUri_IsUsable()
    {
        var w = new WindowSummary
        {
            Hwnd = 527504, Pid = 100, ProcessName = "notepad", Title = "test",
            ClassName = "Notepad", Bounds = new PixelRect(10, 10, 110, 60),
        };
        Assert.Equal("win://window/527504", w.DeviceUri);
        Assert.True(DeviceId.TryParse(w.DeviceUri, out var id));
        Assert.Equal("win", id.Scheme);
    }
}

/// <summary>最小化窗口会产生伪坐标，必须能被产品代码识别（回归防护）。</summary>
public class MinimizedWindowTests
{
    [Fact]
    public void LooksMinimized_DetectsRealMinimizedRect()
    {
        // Windows 对最小化窗口返回屏幕外伪坐标且尺寸极小
        var minimized = new PixelRect(-25600, -25600, -25600 + 159, -25600 + 27);
        Assert.True(GuIO.Driver.Windows.WindowManager.LooksMinimized(minimized));
    }

    [Fact]
    public void LooksMinimized_AcceptsNormalRect()
    {
        var normal = new PixelRect(123, 261, 123 + 1431, 261 + 735);
        Assert.False(GuIO.Driver.Windows.WindowManager.LooksMinimized(normal));
    }

    [Fact]
    public void LooksMinimized_AcceptsSmallButRealWindow()
    {
        // 一个真正的 300x200 小窗口在屏幕内，不应被判为最小化
        var small = new PixelRect(50, 50, 350, 250);
        Assert.False(GuIO.Driver.Windows.WindowManager.LooksMinimized(small));
    }
}
