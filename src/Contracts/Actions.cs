namespace GuIO.Contracts;

/// <summary>指针按键。</summary>
public enum PointerButton { Left = 0, Right = 1, Middle = 2 }

/// <summary>滑动/拖拽的方向辅助（仅用于语义化动作，坐标仍是权威）。</summary>
public enum SwipeDirection { Up, Down, Left, Right }

/// <summary>系统级按键（端侧自足时手机用，桌面映射为其等价键）。</summary>
public enum SystemKey { Back, Home, Recents, Notifications, Power, VolumeUp, VolumeDown, Mute }

/// <summary>滚动单位。</summary>
public enum ScrollUnit { Wheel, Line, Page, Pixel }

/// <summary>
/// 动作抽象。所有复杂行为都由这些有限动词组合而成。
/// 这是跨桌面与手机的唯一动作表；各驱动用能力标志申报实际支持子集。
/// </summary>
[System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "action")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ClickAction), "click")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(MoveAction), "move")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(DragAction), "drag")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(SwipeAction), "swipe")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(PinchAction), "pinch")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ScrollAction), "scroll")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(TypeTextAction), "type")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(KeyAction), "key")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(SystemKeyAction), "syskey")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(AppAction), "app")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ShellAction), "shell")]
public abstract record GuioAction
{
    /// <summary>动作名，用于协议与日志。</summary>
    public abstract string Name { get; }
}

public sealed record ClickAction : GuioAction
{
    public override string Name => "click";
    public required Selector Target { get; init; }
    public PointerButton Button { get; init; } = PointerButton.Left;
    public int Count { get; init; } = 1;                 // 2 即双击
    public double HoldMs { get; init; } = 0;             // >0 即长按
    public bool PreferSemantic { get; init; } = true;    // 优先走 UIA/无障碍节点动作
}

/// <summary>把目标窗口带到前台并聚焦。注入前的正确做法，替代手写 SetForegroundWindow。</summary>
public sealed record ActivateAction : GuioAction
{
    public override string Name => "activate";
}

public sealed record MoveAction : GuioAction
{
    public override string Name => "move";
    public required Selector Target { get; init; }
}

public sealed record DragAction : GuioAction
{
    public override string Name => "drag";
    public required Selector Target { get; init; }
    public required TargetRef To { get; init; }
    public int DurationMs { get; init; } = 400;
    public int Steps { get; init; } = 20;
}

public sealed record SwipeAction : GuioAction
{
    public override string Name => "swipe";
    public required Selector Target { get; init; }
    public required TargetRef To { get; init; }
    public int DurationMs { get; init; } = 300;
    public SwipeDirection? Direction { get; init; }
    public double? Distance { get; init; }               // 与 Direction 搭配时为归一化距离
}

public sealed record PinchAction : GuioAction
{
    public override string Name => "pinch";
    public required Selector Center { get; init; }
    public double Scale { get; init; } = 1.0;            // >1 放大，<1 缩小
    public double SpanPx { get; init; } = 300;
    public int DurationMs { get; init; } = 300;
}

public sealed record ScrollAction : GuioAction
{
    public override string Name => "scroll";
    public Selector? At { get; init; }                   // 为空表示当前指针位置/屏幕中心
    public int DeltaX { get; init; }
    public int DeltaY { get; init; }
    public ScrollUnit Unit { get; init; } = ScrollUnit.Wheel;
}

public sealed record TypeTextAction : GuioAction
{
    public override string Name => "type";
    /// <summary>要输入的文字。</summary>
    public required string Text { get; init; }
    /// <summary>先聚焦的目标；为空则直接输入到当前焦点。</summary>
    public Selector? Target { get; init; }
    /// <summary>是否强制走「先设文本/剪贴板提交」通道以支持中文。</summary>
    public bool ForceUnicode { get; init; }
    public bool ClearFirst { get; init; }
}

public sealed record KeyAction : GuioAction
{
    public override string Name => "key";
    /// <summary>主键（单键或组合键的最后一个键），使用约定键名，如 "Enter"、"A"、"F5"。</summary>
    public required string Key { get; init; }
    /// <summary>修饰键，如 Ctrl/Alt/Shift/Win。</summary>
    public IReadOnlyList<string> Modifiers { get; init; } = Array.Empty<string>();
    /// <summary>直接以虚拟键码指定（跨平台时优先用 Key 名）。</summary>
    public int? VirtualKey { get; init; }
    public int Repeat { get; init; } = 1;
}

public sealed record SystemKeyAction : GuioAction
{
    public override string Name => "syskey";
    public required SystemKey Key { get; init; }
}

public sealed record AppAction : GuioAction
{
    public override string Name => "app";
    public required AppOp Op { get; init; }
    /// <summary>包名 / 进程名 / 可执行路径。List 时可为空。</summary>
    public string? Target { get; init; }
    public string? Arguments { get; init; }
}

public enum AppOp { Launch, Kill, List, Install, Uninstall, Restart }

public sealed record ShellAction : GuioAction
{
    public override string Name => "shell";
    public required string Command { get; init; }
    public bool AsElevated { get; init; }
}

/// <summary>目标引用：动作的「到哪去」。</summary>
public sealed record TargetRef
{
    public Selector? Selector { get; init; }
    public PixelPoint? Offset { get; init; }             // 相对解析结果的偏移
    public static TargetRef Node(Selector s) => new() { Selector = s };
}
