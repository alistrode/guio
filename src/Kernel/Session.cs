using GuIO.Contracts;
using GuIO.Vision;

namespace GuIO.Kernel;

/// <summary>会话契约：内核与前端之间的唯一交互面。</summary>
public interface IDeviceSession : IAsyncDisposable
{
    DeviceDescriptor Descriptor { get; }
    IDeviceDriver Driver { get; }
    Telemetry Telemetry { get; }
    Observation? Last { get; }
    IFrameCodec Codec { get; }
    SelectorResolver Resolver { get; }
    Recorder Recorder { get; }

    Task<GuioResult<Observation>> ObserveAsync(ObserveOptions? options = null, CancellationToken ct = default);
    Task<GuioResult<ResolvedTarget>> ResolveAsync(Selector selector, CancellationToken ct = default);
    Task<GuioResult<bool>> ExecuteAsync(GuioAction action, CancellationToken ct = default);
}

public sealed record ObserveOptions
{
    public bool WithFrame { get; init; } = true;
    public bool WithTree { get; init; } = true;
    public bool WithOcr { get; init; } = false;
    public PixelRect? Region { get; init; }
    public bool CacheOcr { get; init; } = true;
}

/// <summary>
/// 设备会话：绑定一个设备，统一「观测 → 解算 → 执行」。
/// 单一职责 —— 它不决定做什么，只提供做事的原语；决策在闭环/流程层。
/// </summary>
public sealed class DeviceSession : IDeviceSession
{
    private readonly IDeviceDriver _driver;
    private readonly SelectorResolver _resolver;
    private readonly ActionDispatcher _dispatcher;
    private readonly IClock _clock;

    public DeviceDescriptor Descriptor { get; private set; }
    public IDeviceDriver Driver => _driver;
    public Telemetry Telemetry { get; } = new();
    private readonly Recorder _recorder = new();
    public Recorder Recorder => _recorder;
    public Observation? Last { get; private set; }
    public IFrameCodec Codec { get; }

    public DeviceSession(IDeviceDriver driver, IFrameCodec codec, SelectorResolver resolver,
        ActionDispatcher dispatcher, IClock? clock = null)
    {
        _driver = driver;
        Codec = codec;
        _resolver = resolver;
        _dispatcher = dispatcher;
        _clock = clock ?? new SystemClock();
        Descriptor = driver.Descriptor;
    }

    public SelectorResolver Resolver => _resolver;

    public async Task<GuioResult<Observation>> ObserveAsync(ObserveOptions? o = null, CancellationToken ct = default)
    {
        o ??= new ObserveOptions();
        long t0 = _clock.UnixMs;

        Frame? frame = null;
        if (o.WithFrame)
        {
            var fr = await _driver.Perception.CaptureAsync(o.Region, ct).ConfigureAwait(false);
            if (fr.Ok) frame = fr.Value;
        }

        IReadOnlyList<UiNode> nodes = Array.Empty<UiNode>();
        if (o.WithTree && Descriptor.Capabilities.HasFlag(CapabilityFlags.SemanticTree))
        {
            var tr = await _driver.Perception.ReadTreeAsync(ct).ConfigureAwait(false);
            if (tr.Ok && tr.Value is not null) nodes = tr.Value;
        }

        IReadOnlyList<TextBlock> text = Array.Empty<TextBlock>();
        if (o.WithOcr && frame is not null)
        {
            var orc = await _driver.Perception.OcrAsync(frame, ct).ConfigureAwait(false);
            if (orc.Ok && orc.Value is not null) text = orc.Value;
        }

        var obs = new Observation
        {
            Device = Descriptor.Id,
            Frame = frame,
            Nodes = nodes,
            Text = text,
            Screen = Descriptor.Bounds,
            DpiScale = Descriptor.DpiScale,
            TimestampUnixMs = _clock.UnixMs,
        };
        Last = obs;
        Telemetry.Event("observation", new { frame = frame is not null, nodes = nodes.Count, text = text.Count });
        return GuioResult<Observation>.Success(obs, _clock.UnixMs - t0);
    }

    public async Task<GuioResult<ResolvedTarget>> ResolveAsync(Selector selector, CancellationToken ct = default)
    {
        // 保证有可用的观测
        if (Last is null || selector.Kind == SelectorKind.Image || selector.Kind == SelectorKind.ScreenText)
        {
            var need = new ObserveOptions
            {
                WithFrame = selector.Kind == SelectorKind.Image,
                WithTree = selector.Kind is SelectorKind.Node or SelectorKind.Text,
                WithOcr = selector.Kind is SelectorKind.Text or SelectorKind.ScreenText,
                Region = selector.Region,
            };
            var obsr = await ObserveAsync(need, ct).ConfigureAwait(false);
            if (!obsr.Ok || obsr.Value is null) return GuioResult<ResolvedTarget>.Fail(obsr.Error, obsr.Message);
        }

        var chain = SelectorResolver.AutoChain(selector, Descriptor.Capabilities);
        var r = _resolver.ResolveChain(chain, Last!);
        Telemetry.Event("resolve", new { kind = selector.Kind.ToString(), ok = r.Ok, via = r.Value?.Via.ToString() });
        return r;
    }

    public async Task<GuioResult<bool>> ExecuteAsync(GuioAction action, CancellationToken ct = default)
    {
        // 能力协商：不支持的动作在编排层即拒绝（可控性）
        if (!CapabilityRegistry.Supports(Descriptor.Capabilities, action))
        {
            var need = CapabilityRegistry.RequiredCapability(action);
            Telemetry.Event("reject", new { action = action.Name, need = need.ToString() });
            return GuioResult<bool>.Fail(ErrorCode.Unsupported,
                $"设备「{Descriptor.Name}」不支持动作 {action.Name}（缺少能力 {need}）");
        }

        long t0 = _clock.UnixMs;
        ResolvedTarget? target = null;
        Selector? sel = ExtractSelector(action);

        if (sel is not null)
        {
            var rr = await ResolveAsync(sel, ct).ConfigureAwait(false);
            if (!rr.Ok || rr.Value is null)
            {
                RecordStep(action, t0, rr.Error, rr.Message, null, 1);
                _recorder.Capture(action, false, _clock.UnixMs - t0, null);
                return GuioResult<bool>.Fail(rr.Error, rr.Message);
            }
            target = rr.Value;
        }

        string? before = Last?.Frame is { } f0 ? Telemetry.SaveEvidenceFrame(Codec, f0, "before") : null;
        var exec = await _dispatcher.DispatchAsync(_driver, action, target, ct).ConfigureAwait(false);

        Observation? after = null;
        string? afterPath = null;
        if (exec.Ok)
        {
            var o2 = await ObserveAsync(new ObserveOptions { WithFrame = true, WithTree = false }, ct).ConfigureAwait(false);
            if (o2.Ok) after = o2.Value;
            if (after?.Frame is { } f1) afterPath = Telemetry.SaveEvidenceFrame(Codec, f1, "after");
        }

        RecordStep(action, t0, exec.Ok ? ErrorCode.None : exec.Error, exec.Message, target, 1, before, afterPath);
        _recorder.Capture(action, exec.Ok, _clock.UnixMs - t0, target);
        return exec;
    }

    private void RecordStep(GuioAction action, long t0, ErrorCode err, string? msg, ResolvedTarget? target,
        int attempt, string? before = null, string? after = null)
    {
        Telemetry.Step(new StepRecord
        {
            StepId = Guid.NewGuid().ToString("N")[..8],
            ActionName = action.Name,
            StartedUnixMs = t0,
            DurationMs = _clock.UnixMs - t0,
            Error = err,
            Ok = err == ErrorCode.None,
            Target = target,
            FrameBefore = before,
            FrameAfter = after,
            Note = msg,
            Attempt = attempt,
        });
    }

    public static Selector? ExtractSelector(GuioAction action) => action switch
    {
        ClickAction c => c.Target,
        MoveAction m => m.Target,
        DragAction d => d.Target,
        SwipeAction s => s.Target,
        PinchAction p => p.Center,
        ScrollAction sc => sc.At,
        TypeTextAction t => t.Target,
        _ => null,
    };

    public async ValueTask DisposeAsync() => await _driver.DisposeAsync().ConfigureAwait(false);
}
