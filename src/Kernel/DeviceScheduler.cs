using System.Collections.Concurrent;
using GuIO.Contracts;
using GuIO.Vision;

namespace GuIO.Kernel;

/// <summary>一台已接入的设备（会话 + 元数据）。</summary>
public sealed record DeviceEntry(DeviceId Id, string Alias, IDeviceSession Session, DateTimeOffset ConnectedAt);

/// <summary>
/// 多设备调度：设备池 + 并发 + 限流。
/// 单一职责 —— 只管「哪些设备、谁能跑、跑多少」，不碰业务。
/// </summary>
public sealed class DeviceScheduler : IAsyncDisposable
{
    private readonly CapabilityRegistry _registry;
    private readonly ConcurrentDictionary<string, DeviceEntry> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _concurrency;

    public DeviceScheduler(CapabilityRegistry registry, int maxConcurrency = 4)
    {
        _registry = registry;
        _concurrency = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    public IReadOnlyCollection<DeviceEntry> Devices => _devices.Values.ToArray();

    /// <summary>接入设备：创建驱动、连接、开会话。</summary>
    public async Task<GuioResult<DeviceEntry>> AttachAsync(DeviceId id, string? alias = null, CancellationToken ct = default)
    {
        var key = id.ToString();
        if (_devices.TryGetValue(key, out var existing)) return GuioResult<DeviceEntry>.Success(existing);

        IDeviceDriver driver;
        try { driver = _registry.Create(id); }
        catch (Exception ex) { return GuioResult<DeviceEntry>.Fail(ErrorCode.Unsupported, ex.Message); }

        var conn = await driver.ConnectAsync(ct).ConfigureAwait(false);
        if (!conn.Ok)
        {
            await driver.DisposeAsync().ConfigureAwait(false);
            return GuioResult<DeviceEntry>.Fail(conn.Error, conn.Message);
        }

        var session = new DeviceSession(driver, new GuIO.Vision.ImageSharpFrameCodec(),
            new SelectorResolver(new GuIO.Vision.TemplateMatcher(), new GuIO.Vision.ImageSharpFrameCodec()),
            new ActionDispatcher());
        var entry = new DeviceEntry(id, alias ?? driver.Descriptor.Name, session, DateTimeOffset.Now);
        _devices[key] = entry;
        return GuioResult<DeviceEntry>.Success(entry);
    }

    public DeviceEntry? Get(DeviceId id) => _devices.TryGetValue(id.ToString(), out var e) ? e : null;
    public DeviceEntry? GetByAlias(string alias) => _devices.Values.FirstOrDefault(d => string.Equals(d.Alias, alias, StringComparison.OrdinalIgnoreCase));

    public async Task DetachAsync(DeviceId id)
    {
        if (_devices.TryRemove(id.ToString(), out var e))
            await e.Session.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>在并发限额内跑一个有设备会话的任务。</summary>
    public async Task<T> RunLimitedAsync<T>(Func<Task<T>> work, CancellationToken ct = default)
    {
        await _concurrency.WaitAsync(ct).ConfigureAwait(false);
        try { return await work().ConfigureAwait(false); }
        finally { _concurrency.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var e in _devices.Values) await e.Session.DisposeAsync().ConfigureAwait(false);
        _devices.Clear();
        _concurrency.Dispose();
    }
}
