using GuIO.Contracts;

namespace GuIO.Kernel;

/// <summary>
/// 重试与退避策略。控制论中的「稳定性」：以阻尼换收敛，禁止忙等与振荡。
/// </summary>
public sealed record RetryPolicy
{
    public int MaxAttempts { get; init; } = 3;
    public int InitialDelayMs { get; init; } = 120;
    public double BackoffFactor { get; init; } = 1.8;
    public int MaxDelayMs { get; init; } = 1500;
    public bool Jitter { get; init; } = true;

    public static RetryPolicy Default { get; } = new();
    public static RetryPolicy None { get; } = new() { MaxAttempts = 1, InitialDelayMs = 0 };

    /// <summary>第 attempt 次（从 1 起）失败后应等待的毫秒数。</summary>
    public int DelayFor(int attempt)
    {
        double d = InitialDelayMs * Math.Pow(BackoffFactor, Math.Max(0, attempt - 1));
        d = Math.Min(d, MaxDelayMs);
        if (Jitter) d *= 0.85 + Random.Shared.NextDouble() * 0.3;
        return (int)d;
    }

    /// <summary>带退避的执行包装。</summary>
    public async Task<GuioResult<T>> RunAsync<T>(
        Func<int, CancellationToken, Task<GuioResult<T>>> op,
        IClock clock, CancellationToken ct)
    {
        GuioResult<T> last = GuioResult<T>.Fail(ErrorCode.Internal, "未执行");
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            last = await op(attempt, ct).ConfigureAwait(false);
            if (last.Ok) return last;
            // 不可恢复的错误不重试
            if (last.Error is ErrorCode.Unsupported or ErrorCode.InvalidArgument or ErrorCode.PermissionDenied)
                return last;
            if (attempt < MaxAttempts) await clock.DelayAsync(DelayFor(attempt), ct).ConfigureAwait(false);
        }
        return last;
    }
}
