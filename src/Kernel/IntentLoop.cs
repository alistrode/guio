using GuIO.Contracts;
using GuIO.Vision;

namespace GuIO.Kernel;

/// <summary>意图的达成条件（给定值 + 容差）。</summary>
public sealed record Goal
{
    public required string Kind { get; init; }            // found / gone / changed / stable / predicate
    public Selector? Target { get; init; }
    public double Tolerance { get; init; } = 0.0;         // 帧差阈值（changed/stable 用）
    public PixelRect? Region { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<Observation, bool>? Predicate { get; init; }

    public static Goal Found(Selector s) => new() { Kind = "found", Target = s };
    public static Goal Gone(Selector s) => new() { Kind = "gone", Target = s };
    public static Goal Changed(double tol = 6.0, PixelRect? region = null) => new() { Kind = "changed", Tolerance = tol, Region = region };
    public static Goal Stable(double tol = 2.0, PixelRect? region = null) => new() { Kind = "stable", Tolerance = tol, Region = region };
    public static Goal Where(Func<Observation, bool> p) => new() { Kind = "predicate", Predicate = p };
}

/// <summary>控制律：给定误差与状态，决定下一个动作。脚本/启发式/学习型三选一，共用同一闭环。</summary>
public interface IControlLaw
{
    Task<GuioAction?> NextAsync(ObservabilitySnapshot snapshot, CancellationToken ct = default);
}

/// <summary>闭环运行时可读的状态快照。</summary>
public sealed record ObservabilitySnapshot
{
    public required Observation Observation { get; init; }
    public required int Attempt { get; init; }
    public required bool GoalMet { get; init; }
    public Observation? Previous { get; init; }
    public System.Collections.Concurrent.ConcurrentBag<GuioAction> History { get; init; } = new();
}

/// <summary>闭环执行结果。</summary>
public sealed record LoopOutcome
{
    public bool Converged { get; init; }
    public int Attempts { get; init; }
    public long ElapsedMs { get; init; }
    public ErrorCode Error { get; init; }
    public string? Message { get; init; }
    public Observation? Final { get; init; }
}

public sealed record LoopOptions
{
    public int TimeoutMs { get; init; } = 10000;
    public int PollMs { get; init; } = 150;
    public RetryPolicy Retry { get; init; } = RetryPolicy.Default;
    public bool DampenOnStableFailure { get; init; } = true;   // 防振荡：连续失败则加大等待
}

/// <summary>
/// 闭环原语：一个意图 = 一个小闭环。
/// 感知 → 误差 → 控制律 → 执行 → 再感知，直到收敛或超时。
/// 这是《工程控制论》在本产品的核心落点，也是控制论的「控制器」。
/// </summary>
public sealed class IntentLoop
{
    private readonly IClock _clock;

    public IntentLoop(IClock clock) { _clock = clock; }

    public async Task<LoopOutcome> RunAsync(
        IDeviceSession session, Goal goal, IControlLaw? law, LoopOptions options, CancellationToken ct)
    {
        long t0 = _clock.UnixMs;
        int attempt = 0;
        Observation? prev = session.Last;
        Observation cur;
        int consecutiveFail = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;

            var obsr = await session.ObserveAsync(new ObserveOptions
            {
                WithFrame = goal.Kind is "changed" or "stable",
                WithTree = true,
                WithOcr = goal.Kind is "found" or "gone",
                Region = goal.Region,
            }, ct).ConfigureAwait(false);

            if (!obsr.Ok || obsr.Value is null)
                return new LoopOutcome { Converged = false, Attempts = attempt, ElapsedMs = _clock.UnixMs - t0, Error = obsr.Error, Message = obsr.Message };

            cur = obsr.Value;
            bool met = EvaluateGoal(goal, cur, prev, session);
            session.Telemetry.Event("loop.tick", new { attempt, met, goal = goal.Kind });

            if (met)
                return new LoopOutcome { Converged = true, Attempts = attempt, ElapsedMs = _clock.UnixMs - t0, Final = cur };

            if (_clock.UnixMs - t0 >= options.TimeoutMs)
                return new LoopOutcome { Converged = false, Attempts = attempt, ElapsedMs = _clock.UnixMs - t0, Error = ErrorCode.NotConverged, Message = $"超过时限 {options.TimeoutMs}ms 未收敛", Final = cur };

            // 决定下一步动作
            if (law is not null)
            {
                var snap = new ObservabilitySnapshot { Observation = cur, Attempt = attempt, GoalMet = false, Previous = prev };
                var act = await law.NextAsync(snap, ct).ConfigureAwait(false);
                if (act is not null)
                {
                    var er = await session.ExecuteAsync(act, ct).ConfigureAwait(false);
                    snap.History.Add(act);
                    consecutiveFail = er.Ok ? 0 : consecutiveFail + 1;
                }
            }
            prev = cur;

            // 阻尼：防振荡。连续失败则指数加大等待。
            int wait = options.PollMs;
            if (options.DampenOnStableFailure && consecutiveFail > 0)
                wait = Math.Min(options.PollMs * (int)Math.Pow(2, Math.Min(consecutiveFail, 4)), 2000);
            await _clock.DelayAsync(wait, ct).ConfigureAwait(false);
        }
    }

    private bool EvaluateGoal(Goal goal, Observation cur, Observation? prev, IDeviceSession session)
    {
        switch (goal.Kind)
        {
            case "found":
            case "gone":
            {
                var chain = SelectorResolver.AutoChain(goal.Target!, session.Descriptor.Capabilities);
                var r = session.Resolver.ResolveChain(chain, cur);
                bool present = r.Ok;
                return goal.Kind == "found" ? present : !present;
            }
            case "changed":
                if (cur.Frame is null || prev?.Frame is null) return false;
                return GuIO.Vision.FrameDiff.MeanDifference(prev.Frame, cur.Frame, goal.Region) >= goal.Tolerance;
            case "stable":
                if (cur.Frame is null || prev?.Frame is null) return false;
                return GuIO.Vision.FrameDiff.MeanDifference(prev.Frame, cur.Frame, goal.Region) <= goal.Tolerance;
            case "predicate":
                return goal.Predicate?.Invoke(cur) ?? false;
            default:
                return false;
        }
    }
}

/// <summary>一个「等待」控制律：不主动动作，只等条件自然成立（用于加载、转场）。</summary>
public sealed class PassiveLaw : IControlLaw
{
    public Task<GuioAction?> NextAsync(ObservabilitySnapshot snapshot, CancellationToken ct = default)
        => Task.FromResult<GuioAction?>(null);
}
