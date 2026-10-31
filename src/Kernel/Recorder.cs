using System.Text.Json;
using GuIO.Contracts;

namespace GuIO.Kernel;

/// <summary>录制下来的一步：动作 + 相对时间戳 + 备注。</summary>
public sealed record RecordedStep
{
    public required long AtOffsetMs { get; init; }
    public required string ActionJson { get; init; }
    public long ElapsedMs { get; init; }
    public bool Ok { get; init; }
    public string? Target { get; init; }
}

/// <summary>一次录制会话的产物。</summary>
public sealed record Recording
{
    public required string Name { get; init; }
    public required string Device { get; init; }
    public long StartedUnixMs { get; init; }
    public long DurationMs { get; init; }
    public IReadOnlyList<RecordedStep> Steps { get; init; } = Array.Empty<RecordedStep>();
    public string? VideoPath { get; init; }
    public string? EvidenceDir { get; init; }
}

/// <summary>
/// 录制器：把会话里每一次动作按时间线录下来（事件 + 可选证据帧）。
/// 与遥测共用数据源，因此「可回放」是天然能力，而非额外负担。
/// </summary>
public sealed class Recorder
{
    private readonly IClock _clock;
    private readonly List<RecordedStep> _steps = new();
    private long _t0;
    private bool _recording;
    private string _name = "recording";

    public Recorder(IClock? clock = null) => _clock = clock ?? new SystemClock();

    public bool IsRecording => _recording;
    public string Device { get; private set; } = "";

    public void Start(string name, string device)
    {
        _name = name;
        Device = device;
        _steps.Clear();
        _t0 = _clock.UnixMs;
        _recording = true;
    }

    /// <summary>记录一次动作执行。</summary>
    public void Capture(GuioAction action, bool ok, long elapsedMs, ResolvedTarget? target)
    {
        if (!_recording) return;
        _steps.Add(new RecordedStep
        {
            AtOffsetMs = _clock.UnixMs - _t0,
            ActionJson = JsonSerializer.Serialize<object>(action, GuioJson.Options),
            ElapsedMs = elapsedMs,
            Ok = ok,
            Target = target is null ? null : $"{target.Point.X},{target.Point.Y}",
        });
    }

    public Recording Stop(string? videoPath = null, string? evidenceDir = null)
    {
        _recording = false;
        return new Recording
        {
            Name = _name,
            Device = Device,
            StartedUnixMs = _t0,
            DurationMs = _clock.UnixMs - _t0,
            Steps = _steps.ToArray(),
            VideoPath = videoPath,
            EvidenceDir = evidenceDir,
        };
    }

    public static string ToJson(Recording r) => JsonSerializer.Serialize(r, GuioJson.Options);
    public static Recording? FromJson(string json) => JsonSerializer.Deserialize<Recording>(json, GuioJson.Options);
}

/// <summary>
/// 回放器：按录制的时间线（或压缩节奏）重放动作，并做「动作级校验」——
/// 某一步失败可按策略重试或中止，保证回放的可归因性。
/// </summary>
public sealed class Replayer
{
    private readonly IDeviceSession _session;
    private readonly IClock _clock;

    public Replayer(IDeviceSession session, IClock? clock = null)
    {
        _session = session;
        _clock = clock ?? new SystemClock();
    }

    public sealed record ReplayOutcome(bool Ok, int Executed, int Failed, long ElapsedMs, string? Message);

    /// <summary>回放。speed 为倍速（>1 更快）；rescale 为 true 时忽略原始间隔只按动作间隙 120ms 跑。</summary>
    public async Task<ReplayOutcome> ReplayAsync(Recording rec, double speed = 1.0, bool rescale = false,
        RetryPolicy? retry = null, CancellationToken ct = default)
    {
        long t0 = _clock.UnixMs;
        int executed = 0, failed = 0;
        long prevOffset = 0;
        var policy = retry ?? RetryPolicy.Default;

        foreach (var step in rec.Steps)
        {
            ct.ThrowIfCancellationRequested();

            if (!rescale)
            {
                long wait = (long)((step.AtOffsetMs - prevOffset) / Math.Max(0.01, speed));
                if (wait > 0) await _clock.DelayAsync((int)Math.Min(wait, 60000), ct).ConfigureAwait(false);
            }
            else
            {
                await _clock.DelayAsync(120, ct).ConfigureAwait(false);
            }
            prevOffset = step.AtOffsetMs;

            var action = DeserializeAction(step.ActionJson);
            if (action is null) { failed++; continue; }

            var r = await policy.RunAsync<bool>(async (_, c) =>
            {
                var res = await _session.ExecuteAsync(action, c).ConfigureAwait(false);
                return res;
            }, _clock, ct).ConfigureAwait(false);

            executed++;
            if (!r.Ok)
            {
                failed++;
                _session.Telemetry.Event("replay.step.fail", new { step.AtOffsetMs, error = r.Error.ToString() });
            }
        }

        return new ReplayOutcome(failed == 0, executed, failed, _clock.UnixMs - t0,
            failed == 0 ? null : $"{failed} 步失败");
    }

    /// <summary>把录制转成可编辑流程（便于人工修正后再跑）。</summary>
    public static Flow ToFlow(Recording rec)
    {
        var steps = new List<FlowStep>();
        foreach (var s in rec.Steps)
        {
            var a = DeserializeAction(s.ActionJson);
            if (a is not null) steps.Add(new DoStep { Action = a, ContinueOnError = false });
        }
        return new Flow { Name = rec.Name + "_flow", Steps = steps };
    }

    private static GuioAction? DeserializeAction(string json)
    {
        try { return JsonSerializer.Deserialize<GuioAction>(json, GuioJson.Options); }
        catch { return null; }
    }
}
