using System.Collections.Concurrent;
using GuIO.Contracts;

namespace GuIO.Kernel;

/// <summary>
/// 遥测：每一步动作前后各留一帧 + 一条事件，全程可回放、可归因。
/// 这是《工程控制论》「可观性」的工程化 —— 任何时刻都能重建系统状态。
/// </summary>
public interface ITelemetrySink
{
    void OnStep(StepRecord record);
    void OnEvent(TimelineEvent evt);
}

public sealed class Telemetry
{
    private readonly List<TimelineEvent> _events = new();
    private readonly List<StepRecord> _steps = new();
    private readonly object _gate = new();
    private readonly List<ITelemetrySink> _sinks = new();

    public string? EvidenceDir { get; set; }        // 证据帧落盘目录；为空则不落盘
    public bool CaptureEvidenceFrames { get; set; } = true;

    public IReadOnlyList<StepRecord> Steps { get { lock (_gate) return _steps.ToArray(); } }
    public IReadOnlyList<TimelineEvent> Events { get { lock (_gate) return _events.ToArray(); } }

    public void AddSink(ITelemetrySink sink) { lock (_gate) _sinks.Add(sink); }

    public void Event(string kind, object? payload = null)
    {
        var evt = new TimelineEvent
        {
            AtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Kind = kind,
            PayloadJson = payload is null ? null : System.Text.Json.JsonSerializer.Serialize(payload, GuioJson.Options),
        };
        lock (_gate) _events.Add(evt);
        foreach (var s in _sinks) Safe(() => s.OnEvent(evt));
    }

    public void Step(StepRecord record)
    {
        lock (_gate) _steps.Add(record);
        foreach (var s in _sinks) Safe(() => s.OnStep(record));
    }

    /// <summary>把一帧写入证据目录，返回相对路径。</summary>
    public string? SaveEvidenceFrame(IFrameCodec codec, Frame frame, string tag)
    {
        if (!CaptureEvidenceFrames || string.IsNullOrEmpty(EvidenceDir)) return null;
        try
        {
            Directory.CreateDirectory(EvidenceDir);
            var name = $"{DateTime.Now:HHmmss_fff}_{tag}.png";
            var path = Path.Combine(EvidenceDir, name);
            codec.SavePng(frame, path);
            return name;
        }
        catch { return null; }
    }

    public void Reset() { lock (_gate) { _events.Clear(); _steps.Clear(); } }

    private static void Safe(Action a) { try { a(); } catch { /* 遥测故障不影响主流程 */ } }
}

/// <summary>把事件写入 JSONL 文件的接收器（回放与审计的原始数据）。</summary>
public sealed class JsonlTelemetrySink : ITelemetrySink
{
    private readonly string _path;
    private readonly object _gate = new();
    public JsonlTelemetrySink(string path) { _path = path; Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); }

    public void OnStep(StepRecord record) => Write(new { type = "step", data = record });
    public void OnEvent(TimelineEvent evt) => Write(new { type = "event", data = evt });

    private void Write(object o)
    {
        var line = System.Text.Json.JsonSerializer.Serialize(o, GuioJson.Options);
        lock (_gate) File.AppendAllText(_path, line + Environment.NewLine);
    }
}
