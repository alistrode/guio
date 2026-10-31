namespace GuIO.Contracts;

/// <summary>统一结果包装。所有动作与感知调用返回此类型，避免抛异常穿越层边界。</summary>
public sealed record GuioResult<T>
{
    public bool Ok { get; init; }
    public T? Value { get; init; }
    public ErrorCode Error { get; init; } = ErrorCode.None;
    public string? Message { get; init; }
    public long ElapsedMs { get; init; }

    public static GuioResult<T> Success(T value, long elapsedMs = 0) =>
        new() { Ok = true, Value = value, ElapsedMs = elapsedMs };

    public static GuioResult<T> Fail(ErrorCode code, string? message = null, long elapsedMs = 0) =>
        new() { Ok = false, Error = code, Message = message, ElapsedMs = elapsedMs };
}

/// <summary>错误码。收敛失败与不可控要区分清楚，便于编排层决策。</summary>
public enum ErrorCode
{
    None = 0,
    NotFound = 1,          // 选择器未命中
    Timeout = 2,           // 未在时限内收敛
    NotConverged = 3,      // 超时后目标仍未达成
    Unsupported = 4,       // 当前驱动无此能力（可控性拒绝）
    DeviceUnavailable = 5,
    PermissionDenied = 6,
    Ambiguous = 7,         // 命中多个且无法判定
    InvalidArgument = 8,
    Transport = 9,
    Internal = 99,
}

/// <summary>单步执行记录（遥测最小单元）。</summary>
public sealed record StepRecord
{
    public required string StepId { get; init; }
    public required string ActionName { get; init; }
    public long StartedUnixMs { get; init; }
    public long DurationMs { get; init; }
    public ErrorCode Error { get; init; }
    public bool Ok { get; init; }
    public ResolvedTarget? Target { get; init; }
    public string? FrameBefore { get; init; }            // 证据帧路径
    public string? FrameAfter { get; init; }
    public string? Note { get; init; }
    public int Attempt { get; init; } = 1;
}

/// <summary>会话级事件流中的一条（用于回放与审计）。</summary>
public sealed record TimelineEvent
{
    public required long AtUnixMs { get; init; }
    public required string Kind { get; init; }           // action / observation / note / transition
    public string? PayloadJson { get; init; }
}
