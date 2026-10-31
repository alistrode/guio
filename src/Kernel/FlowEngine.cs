using GuIO.Contracts;
using GuIO.Vision;

namespace GuIO.Kernel;

/// <summary>流程步骤：一个抽象动作，或一个控制结构。</summary>
[System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "step")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(DoStep), "do")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(WaitStep), "wait")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(IfStep), "if")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(LoopStep), "loop")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(AchieveStep), "achieve")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(CallStep), "call")]
public abstract record FlowStep
{
    public string? Id { get; init; }
    public int? Repeat { get; init; }
    public int? RepeatDelayMs { get; init; }
}

/// <summary>执行一个动作。</summary>
public sealed record DoStep : FlowStep
{
    public required GuioAction Action { get; init; }
    public bool ContinueOnError { get; init; }
}

/// <summary>等待某条件成立。</summary>
public sealed record WaitStep : FlowStep
{
    public required Goal Goal { get; init; }
    public int TimeoutMs { get; init; } = 10000;
    public bool ContinueOnError { get; init; }
}

/// <summary>条件分支。</summary>
public sealed record IfStep : FlowStep
{
    public required Goal Condition { get; init; }
    public required IReadOnlyList<FlowStep> Then { get; init; }
    public IReadOnlyList<FlowStep> Else { get; init; } = Array.Empty<FlowStep>();
}

/// <summary>循环。</summary>
public sealed record LoopStep : FlowStep
{
    public required IReadOnlyList<FlowStep> Body { get; init; }
    public int Times { get; init; } = -1;                    // -1 = 直到 BreakIf 成立
    public Goal? BreakIf { get; init; }
    public int MaxIterations { get; init; } = 1000;
}

/// <summary>带目标的一个意图闭环（学习型/启发式控制律挂在步骤上）。</summary>
public sealed record AchieveStep : FlowStep
{
    public required Goal Goal { get; init; }
    public LoopOptions Options { get; init; } = new();
    /// <summary>达成手段：若为 null，则纯等待；这里可挂 IControlLaw（由宿主注入名字解析）。</summary>
    public string? LawName { get; init; }
}

/// <summary>调用子流程。</summary>
public sealed record CallStep : FlowStep
{
    public required string FlowName { get; init; }
}

/// <summary>流程定义（可参数化、可嵌套）。</summary>
public sealed record Flow
{
    public required string Name { get; init; }
    public IReadOnlyList<FlowStep> Steps { get; init; } = Array.Empty<FlowStep>();
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
}

/// <summary>步骤执行结果。</summary>
public sealed record FlowOutcome
{
    public bool Ok { get; init; }
    public string? FailedStepId { get; init; }
    public ErrorCode Error { get; init; }
    public string? Message { get; init; }
    public int StepsExecuted { get; init; }
    public long ElapsedMs { get; init; }
    public IReadOnlyList<StepRecord> Steps { get; init; } = Array.Empty<StepRecord>();
}

/// <summary>可变计数器（async 方法不能带 ref 参数）。</summary>
public sealed class Counter { public int Value; }

/// <summary>
/// 流程引擎：把步骤/分支/循环/嵌套跑起来。
/// 单一职责 —— 它只负责「按什么顺序做」，具体动作交给会话。
/// </summary>
public sealed class FlowEngine
{
    private readonly IDeviceSession _session;
    private readonly IntentLoop _loop;
    private readonly IClock _clock;
    private readonly Dictionary<string, Flow> _subflows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IControlLaw> _laws = new(StringComparer.OrdinalIgnoreCase);

    public FlowEngine(IDeviceSession session, IClock? clock = null)
    {
        _session = session;
        _clock = clock ?? new SystemClock();
        _loop = new IntentLoop(_clock);
    }

    public void RegisterSubflow(Flow flow) => _subflows[flow.Name] = flow;
    public void RegisterLaw(string name, IControlLaw law) => _laws[name] = law;

    public async Task<FlowOutcome> RunAsync(Flow flow, CancellationToken ct = default)
    {
        long t0 = _clock.UnixMs;
        var executed = new Counter();
        _session.Telemetry.Event("flow.start", new { flow = flow.Name });

        foreach (var step in flow.Steps)
        {
            var r = await RunStepAsync(step, executed, ct).ConfigureAwait(false);
            if (!r.Ok)
            {
                _session.Telemetry.Event("flow.fail", new { step = step.Id, error = r.Error.ToString() });
                return new FlowOutcome
                {
                    Ok = false,
                    FailedStepId = step.Id,
                    Error = r.Error,
                    Message = r.Message,
                    StepsExecuted = executed.Value,
                    ElapsedMs = _clock.UnixMs - t0,
                    Steps = _session.Telemetry.Steps,
                };
            }
        }

        _session.Telemetry.Event("flow.done", new { flow = flow.Name, steps = executed.Value });
        return new FlowOutcome { Ok = true, StepsExecuted = executed.Value, ElapsedMs = _clock.UnixMs - t0, Steps = _session.Telemetry.Steps };
    }

    private async Task<GuioResult<bool>> RunStepAsync(FlowStep step, Counter executed, CancellationToken ct)
    {
        switch (step)
        {
            case DoStep d:
                return await ExecuteWithRepeatAsync(step, async () =>
                {
                    executed.Value++;
                    return await _session.ExecuteAsync(d.Action, ct).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);

            case WaitStep w:
                return await ExecuteWithRepeatAsync(step, async () =>
                {
                    executed.Value++;
                    var outcome = await _loop.RunAsync(_session, w.Goal,
                        new PassiveLaw(), new LoopOptions { TimeoutMs = w.TimeoutMs }, ct).ConfigureAwait(false);
                    return outcome.Converged ? GuioResult<bool>.Success(true, outcome.ElapsedMs)
                        : GuioResult<bool>.Fail(outcome.Error, outcome.Message);
                }, ct).ConfigureAwait(false);

            case AchieveStep a:
                return await ExecuteWithRepeatAsync(step, async () =>
                {
                    executed.Value++;
                    IControlLaw? law = a.LawName is not null && _laws.TryGetValue(a.LawName, out var l) ? l : null;
                    var outcome = await _loop.RunAsync(_session, a.Goal, law, a.Options, ct).ConfigureAwait(false);
                    return outcome.Converged ? GuioResult<bool>.Success(true, outcome.ElapsedMs)
                        : GuioResult<bool>.Fail(outcome.Error, outcome.Message);
                }, ct).ConfigureAwait(false);

            case IfStep f:
            {
                var obs = _session.Last ?? (await _session.ObserveAsync(new ObserveOptions { WithFrame = true, WithOcr = true }, ct)).Value;
                bool cond = obs is not null && EvaluateCond(f.Condition, obs);
                var branch = cond ? f.Then : f.Else;
                foreach (var s in branch)
                {
                    var r = await RunStepAsync(s, executed, ct).ConfigureAwait(false);
                    if (!r.Ok) return r;
                }
                return GuioResult<bool>.Success(true);
            }

            case LoopStep lp:
            {
                int iter = 0;
                while (iter < lp.MaxIterations && (lp.Times < 0 || iter < lp.Times))
                {
                    iter++;
                    if (lp.BreakIf is not null)
                    {
                        var obs = (await _session.ObserveAsync(new ObserveOptions { WithFrame = true, WithOcr = true }, ct)).Value;
                        if (obs is not null && EvaluateCond(lp.BreakIf, obs)) break;
                    }
                    foreach (var s in lp.Body)
                    {
                        var r = await RunStepAsync(s, executed, ct).ConfigureAwait(false);
                        if (!r.Ok) return r;
                    }
                }
                return GuioResult<bool>.Success(true);
            }

            case CallStep c:
            {
                if (!_subflows.TryGetValue(c.FlowName, out var sub))
                    return GuioResult<bool>.Fail(ErrorCode.InvalidArgument, $"未注册子流程 {c.FlowName}");
                var sub2 = await RunAsync(sub, ct).ConfigureAwait(false);
                return sub2.Ok ? GuioResult<bool>.Success(true) : GuioResult<bool>.Fail(sub2.Error, sub2.Message);
            }

            default:
                return GuioResult<bool>.Fail(ErrorCode.Unsupported, $"未知步骤 {step.GetType().Name}");
        }
    }

    private async Task<GuioResult<bool>> ExecuteWithRepeatAsync(FlowStep step, Func<Task<GuioResult<bool>>> body, CancellationToken ct)
    {
        int times = step.Repeat ?? 1;
        GuioResult<bool> last = GuioResult<bool>.Success(true);
        for (int i = 0; i < times; i++)
        {
            last = await body().ConfigureAwait(false);
            if (!last.Ok) return last;
            if (i < times - 1 && (step.RepeatDelayMs ?? 0) > 0)
                await _clock.DelayAsync(step.RepeatDelayMs!.Value, ct).ConfigureAwait(false);
        }
        return last;
    }

    private bool EvaluateCond(Goal goal, Observation obs)
    {
        var chain = SelectorResolver.AutoChain(goal.Target!, _session.Descriptor.Capabilities);
        var r = _session.Resolver.ResolveChain(chain, obs);
        return goal.Kind switch
        {
            "found" => r.Ok,
            "gone" => !r.Ok,
            "predicate" => goal.Predicate?.Invoke(obs) ?? false,
            _ => r.Ok,
        };
    }
}

/// <summary>流程序列化（JSON）—— 便于存储、分享、录制转换。</summary>
public static class FlowSerializer
{
    public static string ToJson(Flow flow) => System.Text.Json.JsonSerializer.Serialize(flow, GuioJson.Options);
    public static Flow? FromJson(string json) => System.Text.Json.JsonSerializer.Deserialize<Flow>(json, GuioJson.Options);
}
