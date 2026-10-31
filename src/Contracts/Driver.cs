namespace GuIO.Contracts;

/// <summary>
/// 设备驱动总接口。这是内核唯一认识的「执行器/传感器」边界。
/// 内核永不直接引用具体驱动，只通过本接口与能力注册表交互。
/// </summary>
public interface IDeviceDriver : IAsyncDisposable
{
    DeviceDescriptor Descriptor { get; }
    IPerception Perception { get; }
    IActuator Actuator { get; }
    IMediaDevice Media { get; }

    /// <summary>建立连接 / 申请权限 / 前置校验。</summary>
    Task<GuioResult<bool>> ConnectAsync(CancellationToken ct = default);

    /// <summary>刷新设备描述（尺寸、前台、方向等可能变化）。</summary>
    Task<DeviceDescriptor> RefreshAsync(CancellationToken ct = default);
}

/// <summary>感知能力。</summary>
public interface IPerception
{
    /// <summary>截取一帧（可限定区域）。</summary>
    Task<GuioResult<Frame>> CaptureAsync(PixelRect? region = null, CancellationToken ct = default);

    /// <summary>读取语义树快照。不支持时返回 Unsupported。</summary>
    Task<GuioResult<IReadOnlyList<UiNode>>> ReadTreeAsync(CancellationToken ct = default);

    /// <summary>把一帧交给 OCR 引擎（由视觉层实现，驱动可提供优化通道）。</summary>
    Task<GuioResult<IReadOnlyList<TextBlock>>> OcrAsync(Frame frame, CancellationToken ct = default);

    /// <summary>开启连续画面流（帧级控制环使用）。不支持时返回 Unsupported。</summary>
    IAsyncEnumerable<Frame> StreamFramesAsync(StreamOptions options, CancellationToken ct = default);
}

public sealed record StreamOptions
{
    public int TargetFps { get; init; } = 15;
    public PixelRect? Region { get; init; }
    public int MaxFrames { get; init; } = 0;             // 0 = 不限
}

/// <summary>动作能力。所有方法返回统一结果，不抛异常穿越边界。</summary>
public interface IActuator
{
    Task<GuioResult<bool>> ClickAsync(PixelPoint at, PointerButton button, int count, double holdMs, CancellationToken ct = default);
    Task<GuioResult<bool>> MoveAsync(PixelPoint to, CancellationToken ct = default);
    Task<GuioResult<bool>> DragAsync(PixelPoint from, PixelPoint to, int durationMs, int steps, CancellationToken ct = default);
    Task<GuioResult<bool>> SwipeAsync(PixelPoint from, PixelPoint to, int durationMs, CancellationToken ct = default);
    Task<GuioResult<bool>> PinchAsync(PixelPoint center, double scale, double spanPx, int durationMs, CancellationToken ct = default);
    Task<GuioResult<bool>> ScrollAsync(PixelPoint? at, int dx, int dy, ScrollUnit unit, CancellationToken ct = default);
    Task<GuioResult<bool>> TypeAsync(string text, bool unicode, bool clearFirst, CancellationToken ct = default);
    Task<GuioResult<bool>> KeyAsync(int virtualKey, IReadOnlyList<string> modifiers, int repeat, CancellationToken ct = default);
    Task<GuioResult<bool>> SystemKeyAsync(SystemKey key, CancellationToken ct = default);

    /// <summary>执行语义节点动作（UIA 模式 / 无障碍动作）。不支持时返回 Unsupported。</summary>
    Task<GuioResult<bool>> InvokeNodeAsync(UiNode node, string action, CancellationToken ct = default);
}

/// <summary>媒体能力（播放 / 回环录音 / 音量）。驱动按能力标志申报。</summary>
public interface IMediaDevice
{
    Task<GuioResult<bool>> PlayAsync(string path, double volume = 1.0, CancellationToken ct = default);
    Task<GuioResult<string>> StartLoopbackRecordAsync(string outPath, int seconds, CancellationToken ct = default);
    Task<GuioResult<double>> GetVolumeAsync(CancellationToken ct = default);
    Task<GuioResult<bool>> SetVolumeAsync(double volume, CancellationToken ct = default);
    Task<GuioResult<bool>> SetMuteAsync(bool mute, CancellationToken ct = default);
}

/// <summary>可注入时钟，便于回放与测试。</summary>
public interface IClock
{
    long UnixMs { get; }
    Task DelayAsync(int ms, CancellationToken ct = default);
}

public sealed class SystemClock : IClock
{
    public long UnixMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public Task DelayAsync(int ms, CancellationToken ct = default) => Task.Delay(ms, ct);
}
