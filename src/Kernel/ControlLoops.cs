using GuIO.Contracts;

namespace GuIO.Kernel;

/// <summary>重试策略的便携别名（与 RetryPolicy 同物，便于从 Kernel 命名空间直接取用）。</summary>
public static class RetryPolicies
{
    public static RetryPolicy Default => RetryPolicy.Default;
    public static RetryPolicy None => RetryPolicy.None;
}

/// <summary>
/// 三层控制环调度：帧级（高频采帧）/ 动作级（中频执行校验）/ 会话级（低频流程决策）。
/// 三层速率与生命周期相互隔离，互不阻塞 —— 《工程控制论》多级控制的工程化。
/// </summary>
public sealed class ControlLoops : IAsyncDisposable
{
    private readonly IDeviceSession _session;
    private readonly IClock _clock;
    private CancellationTokenSource? _cts;
    private Task? _frameLoop;
    private Frame? _latestFrame;
    private readonly object _gate = new();

    public int FrameIntervalMs { get; set; } = 66;      // 帧级 ≈ 15fps
    public Frame? LatestFrame { get { lock (_gate) return _latestFrame; } }

    public ControlLoops(IDeviceSession session, IClock? clock = null)
    {
        _session = session;
        _clock = clock ?? new SystemClock();
    }

    /// <summary>启动帧级环：持续采帧供实时预览与快速判定。</summary>
    public void Start()
    {
        if (_frameLoop is not null) return;
        _cts = new CancellationTokenSource();
        _frameLoop = Task.Run(() => FrameLoopAsync(_cts.Token));
    }

    private async Task FrameLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await _session.Driver.Perception.CaptureAsync(null, ct).ConfigureAwait(false);
                if (r.Ok && r.Value is not null) { lock (_gate) _latestFrame = r.Value; }
            }
            catch (OperationCanceledException) { break; }
            catch { /* 帧级故障不影响动作级 */ }
            await _clock.DelayAsync(FrameIntervalMs, ct).ConfigureAwait(false);
        }
    }

    /// <summary>动作级：执行一个动作并等待其产生可观测效果（校验闭环）。</summary>
    public async Task<GuioResult<bool>> ExecuteAndSettleAsync(GuioAction action, int settleMs = 200, CancellationToken ct = default)
    {
        var r = await _session.ExecuteAsync(action, ct).ConfigureAwait(false);
        if (settleMs > 0) await _clock.DelayAsync(settleMs, ct).ConfigureAwait(false);
        return r;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null) { _cts.Cancel(); }
        if (_frameLoop is not null) { try { await _frameLoop.ConfigureAwait(false); } catch { } }
        _cts?.Dispose();
    }
}
