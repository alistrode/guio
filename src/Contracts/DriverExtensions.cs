namespace GuIO.Contracts;

/// <summary>窗口摘要（供枚举与选择目标）。</summary>
public sealed record WindowSummary
{
    public required long Hwnd { get; init; }
    public required int Pid { get; init; }
    public required string ProcessName { get; init; }
    public required string Title { get; init; }
    public required string ClassName { get; init; }
    public required PixelRect Bounds { get; init; }
    public bool Visible { get; init; }
    public bool Minimized { get; init; }

    /// <summary>可作为设备地址使用。</summary>
    public string DeviceUri => $"win://window/{Hwnd}";
}

/// <summary>能提供音频诊断与设备切换的驱动。</summary>
public interface IAudioDiagnosable
{
    object DiagnoseAudio();
    GuioResult<bool> SetDefaultOutput(string deviceId);
}

/// <summary>能枚举本机窗口的驱动（含窗口概念的平台）。</summary>
public interface IWindowEnumerable
{
    IReadOnlyList<WindowSummary> ListWindows();
}



/// <summary>扩展动作口（启动应用、Shell 等）。由具体驱动实现。</summary>
public interface IExtendedActuator
{
    Task<GuioResult<bool>> ExecuteExtendedAsync(GuioAction action, CancellationToken ct = default);
}

/// <summary>让分发器能按选择器解算终点目标的驱动侧接口。</summary>
public interface ISelectorAware
{
    Task<GuioResult<ResolvedTarget>> ResolveAsync(Selector selector, PixelPoint? offset, CancellationToken ct = default);
}

/// <summary>键名 → 虚拟键码（桌面用得最多；安卓侧忽略具体值，仅取语义）。</summary>
public static class KeyNames
{
    private static readonly Dictionary<string, int> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Backspace"] = 0x08, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Return"] = 0x0D,
        ["Shift"] = 0x10, ["Ctrl"] = 0x11, ["Control"] = 0x11, ["Alt"] = 0x12,
        ["Pause"] = 0x13, ["CapsLock"] = 0x14, ["Esc"] = 0x1B, ["Escape"] = 0x1B,
        ["Space"] = 0x20, ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23,
        ["Home"] = 0x24, ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["Print"] = 0x2C, ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Del"] = 0x2E,
        ["Win"] = 0x5B, ["LWin"] = 0x5B, ["RWin"] = 0x5C,
        ["NumPad0"] = 0x60, ["NumPad1"] = 0x61, ["NumPad2"] = 0x62, ["NumPad3"] = 0x63,
        ["NumPad4"] = 0x64, ["NumPad5"] = 0x65, ["NumPad6"] = 0x66, ["NumPad7"] = 0x67,
        ["NumPad8"] = 0x68, ["NumPad9"] = 0x69, ["Multiply"] = 0x6A, ["Add"] = 0x6B,
        ["Subtract"] = 0x6D, ["Decimal"] = 0x6E, ["Divide"] = 0x6F,
        ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73, ["F5"] = 0x74, ["F6"] = 0x75,
        ["F7"] = 0x76, ["F8"] = 0x77, ["F9"] = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B,
        ["VolumeUp"] = 0xAF, ["VolumeDown"] = 0xAE, ["VolumeMute"] = 0xAD,
    };

    public static int ToVirtualKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return 0;
        key = key.Trim();
        if (Map.TryGetValue(key, out int vk)) return vk;
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
        }
        return 0;
    }
}
