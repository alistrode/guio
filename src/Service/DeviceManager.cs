using GuIO.Contracts;
using GuIO.Kernel;
using GuIO.Vision;

namespace GuIO.Service;

/// <summary>
/// 设备管理：把「发现设备」与「接入会话」封装成服务。
/// 单一职责 —— 只负责设备的生命周期，不参与动作与感知。
/// </summary>
public sealed class DeviceManager : IAsyncDisposable
{
    private readonly CapabilityRegistry _registry;
    private readonly DeviceScheduler _scheduler;

    public DeviceManager(CapabilityRegistry registry, int maxConcurrency = 4)
    {
        _registry = registry;
        _scheduler = new DeviceScheduler(registry, maxConcurrency);
    }

    public CapabilityRegistry Registry => _registry;
    public DeviceScheduler Scheduler => _scheduler;

    /// <summary>枚举当前可见设备。由各驱动的「发现器」补充；这里给出已接入设备与默认目标。</summary>
    public IReadOnlyList<DeviceDescriptor> List()
        => _scheduler.Devices.Select(d => d.Session.Descriptor).ToArray();

    public async Task<GuioResult<DeviceEntry>> ConnectAsync(string deviceUri, string? alias = null, CancellationToken ct = default)
    {
        DeviceId id;
        try { id = DeviceId.Parse(deviceUri); }
        catch (Exception ex) { return GuioResult<DeviceEntry>.Fail(ErrorCode.InvalidArgument, ex.Message); }
        return await _scheduler.AttachAsync(id, alias, ct).ConfigureAwait(false);
    }

    /// <summary>自动挑选一个可用设备：优先已接入，其次注册表中的首个支持计算机 scheme。</summary>
    public async Task<GuioResult<DeviceEntry>> ConnectDefaultAsync(CancellationToken ct = default)
    {
        var existing = _scheduler.Devices.FirstOrDefault();
        if (existing is not null) return GuioResult<DeviceEntry>.Success(existing);

        var scheme = _registry.All.FirstOrDefault();
        if (scheme is null) return GuioResult<DeviceEntry>.Fail(ErrorCode.DeviceUnavailable, "没有任何已注册的驱动");

        DeviceId id = scheme.Scheme.ToLowerInvariant() switch
        {
            "win" => DeviceId.WindowsScreen(),
            "android" => DeviceId.AndroidLocal(),
            _ => new DeviceId(scheme.Scheme, "default"),
        };
        return await _scheduler.AttachAsync(id, ct: ct).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(string deviceUri)
    {
        if (DeviceId.TryParse(deviceUri, out var id)) await _scheduler.DetachAsync(id).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await _scheduler.DisposeAsync().ConfigureAwait(false);
}
