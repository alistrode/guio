namespace GuIO.Contracts;

/// <summary>设备大类。</summary>
public enum DeviceKind
{
    Unknown = 0,
    WindowsDesktop,
    MacOSDesktop,
    LinuxDesktop,
    AndroidPhone,
    AndroidTablet,
    Browser,
    IoTRelay,
}

/// <summary>
/// 能力标志。驱动在启动时申报自己支持哪些能力，
/// 内核据此进行能力协商：不支持的动作在编排层即被拒绝，而非运行到一半失败。
/// 这是《工程控制论》中「可控性」的工程化落点。
/// </summary>
[Flags]
public enum CapabilityFlags : long
{
    None = 0,

    // 感知
    CaptureFrame      = 1L << 0,
    CaptureStream     = 1L << 1,
    SemanticTree      = 1L << 2,
    Ocr               = 1L << 3,
    TemplateMatch     = 1L << 4,
    FrameDiff         = 1L << 5,

    // 指针
    PointerClick      = 1L << 8,
    PointerDouble     = 1L << 9,
    PointerRightClick = 1L << 10,
    PointerLongPress  = 1L << 11,
    PointerMove       = 1L << 12,
    PointerDrag       = 1L << 13,
    PointerSwipe      = 1L << 14,
    PointerPinch      = 1L << 15,
    PointerScroll     = 1L << 16,

    // 输入
    KeyPress          = 1L << 20,
    KeyChord          = 1L << 21,
    TypeText          = 1L << 22,
    TypeUnicode       = 1L << 23,   // 支持中文/emoji 等非 ASCII 直接输入

    // 系统
    SystemKeys        = 1L << 28,   // 返回/Home/最近任务
    AppLaunch         = 1L << 29,
    AppList           = 1L << 30,
    AppInstall        = 1L << 31,
    Shell             = 1L << 32,

    // 媒体
    AudioPlay         = 1L << 40,
    AudioLoopbackRec  = 1L << 41,   // 录制「正在播放的声音」
    AudioVolume       = 1L << 42,

    // 桌面专有
    WindowEnum        = 1L << 48,
    WindowManage      = 1L << 49,
    WindowActivate    = 1L << 52,
    ProcessLaunch     = 1L << 50,
    Elevation         = 1L << 51,   // 提权能力（桌面 UAC / 安卓 Shizuku）

    AllPointer = PointerClick | PointerDouble | PointerRightClick | PointerLongPress
               | PointerMove | PointerDrag | PointerSwipe | PointerPinch | PointerScroll,
    AllInput   = KeyPress | KeyChord | TypeText | TypeUnicode,
    AllPercept = CaptureFrame | CaptureStream | SemanticTree | Ocr | TemplateMatch | FrameDiff,
    AllMedia   = AudioPlay | AudioLoopbackRec | AudioVolume,
}

/// <summary>设备寻址 URI，例如 win://window/1234、android://local、android://tcp/192.168.1.5:5555。</summary>
public sealed record DeviceId(string Scheme, string Path)
{
    public static DeviceId Windows(long hwnd) => new("win", $"window/{hwnd}");
    public static DeviceId WindowsScreen(int index = 0) => new("win", $"screen/{index}");
    public static DeviceId AndroidLocal() => new("android", "local");
    public static DeviceId AndroidAdb(string serial) => new("android", $"adb/{serial}");
    public override string ToString() => $"{Scheme}://{Path}";

    public static DeviceId Parse(string text)
    {
        int i = text.IndexOf("://", StringComparison.Ordinal);
        if (i <= 0) throw new FormatException($"非法设备标识：{text}");
        return new DeviceId(text[..i], text[(i + 3)..]);
    }

    public static bool TryParse(string? text, out DeviceId id)
    {
        id = new DeviceId("", "");
        if (string.IsNullOrWhiteSpace(text)) return false;
        int i = text.IndexOf("://", StringComparison.Ordinal);
        if (i <= 0) return false;
        id = new DeviceId(text[..i], text[(i + 3)..]);
        return true;
    }
}

/// <summary>驱动的自我介绍：是谁、能干什么、屏幕多大、是不是当前前台。</summary>
public sealed record DeviceDescriptor
{
    public required DeviceId Id { get; init; }
    public required DeviceKind Kind { get; init; }
    public required string Name { get; init; }
    public CapabilityFlags Capabilities { get; init; } = CapabilityFlags.None;
    public PixelRect Bounds { get; init; }
    public double DpiScale { get; init; } = 1.0;
    public int Rotation { get; init; }               // 0/90/180/270
    public bool IsForeground { get; init; }
    public string? OwnerProcess { get; init; }
    public IReadOnlyDictionary<string, string> Extra { get; init; } = new Dictionary<string, string>();
}
