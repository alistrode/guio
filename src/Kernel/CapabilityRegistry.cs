using System.Collections.Concurrent;
using GuIO.Contracts;

namespace GuIO.Kernel;

/// <summary>
/// 能力注册表：驱动在此登记自己的能力与工厂。内核只通过它获取驱动，
/// 永不直接 new 具体驱动 —— 这是《工程控制论》「控制器不依赖执行器细节」的落点，
/// 也是「可控性」：不支持的动作在编排层即被拒绝。
/// </summary>
public sealed class CapabilityRegistry
{
    private readonly ConcurrentDictionary<string, DriverRegistration> _byScheme = new(StringComparer.OrdinalIgnoreCase);

    public sealed record DriverRegistration(string Scheme, string DisplayName, Func<DeviceId, IDeviceDriver> Factory);

    public void Register(DriverRegistration reg) => _byScheme[reg.Scheme] = reg;

    public void Register(string scheme, string displayName, Func<DeviceId, IDeviceDriver> factory)
        => Register(new DriverRegistration(scheme, displayName, factory));

    public bool IsKnown(string scheme) => _byScheme.ContainsKey(scheme);

    public IReadOnlyCollection<DriverRegistration> All => _byScheme.Values.ToArray();

    public IDeviceDriver Create(DeviceId id)
    {
        if (!_byScheme.TryGetValue(id.Scheme, out var reg))
            throw new InvalidOperationException($"没有为 scheme「{id.Scheme}」注册驱动。已注册：{string.Join(", ", _byScheme.Keys)}");
        return reg.Factory(id);
    }

    /// <summary>能力协商：给定动作所需能力，判断某设备是否支持。</summary>
    public static bool Supports(CapabilityFlags deviceCaps, GuioAction action)
    {
        var need = RequiredCapability(action);
        return need == CapabilityFlags.None || (deviceCaps & need) == need;
    }

    /// <summary>动作 → 所需能力标志的映射。</summary>
    public static CapabilityFlags RequiredCapability(GuioAction action) => action switch
    {
        ClickAction { Count: >= 2 } => CapabilityFlags.PointerDouble,
        ClickAction { HoldMs: > 0 } => CapabilityFlags.PointerLongPress,
        ClickAction { Button: PointerButton.Right } => CapabilityFlags.PointerRightClick,
        ClickAction => CapabilityFlags.PointerClick,
        ActivateAction => CapabilityFlags.WindowActivate,
        MoveAction => CapabilityFlags.PointerMove,
        DragAction => CapabilityFlags.PointerDrag,
        SwipeAction => CapabilityFlags.PointerSwipe,
        PinchAction => CapabilityFlags.PointerPinch,
        ScrollAction => CapabilityFlags.PointerScroll,
        TypeTextAction { ForceUnicode: true } => CapabilityFlags.TypeUnicode,
        TypeTextAction => CapabilityFlags.TypeText,
        KeyAction { Modifiers.Count: > 0 } => CapabilityFlags.KeyChord,
        KeyAction => CapabilityFlags.KeyPress,
        SystemKeyAction => CapabilityFlags.SystemKeys,
        AppAction { Op: AppOp.Launch or AppOp.Restart } => CapabilityFlags.AppLaunch,
        AppAction { Op: AppOp.Kill } => CapabilityFlags.AppLaunch,
        AppAction { Op: AppOp.List } => CapabilityFlags.AppList,
        AppAction { Op: AppOp.Install or AppOp.Uninstall } => CapabilityFlags.AppInstall,
        ShellAction => CapabilityFlags.Shell,
        _ => CapabilityFlags.None,
    };
}
