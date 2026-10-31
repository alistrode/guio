namespace GuIO.Contracts;

/// <summary>一帧图像。像素以 BGRA32 紧凑排列，供跨端零拷贝传递与序列化。</summary>
public sealed record Frame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Stride { get; init; }             // 每行字节数
    public required byte[] Pixels { get; init; }          // BGRA32，长度 >= Stride*Height
    public PixelRect SourceRect { get; init; }            // 该帧对应的屏幕区域
    public long TimestampUnixMs { get; init; }
    public int Format { get; init; } = 0;                 // 0 = BGRA32

    /// <summary>取某像素 (BGRA)。</summary>
    public (byte B, byte G, byte R, byte A) GetPixel(int x, int y)
    {
        int i = y * Stride + x * 4;
        return (Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
    }
}

/// <summary>把帧编码成 PNG（用于存证与回传）。实现由内核/视觉层提供。</summary>
public interface IFrameCodec
{
    byte[] EncodePng(Frame frame);
    Frame DecodePng(byte[] png, PixelRect sourceRect);
    Frame DecodeFile(string path);
    void SavePng(Frame frame, string path);
}

/// <summary>语义树节点（UIA / 无障碍的统一投影）。</summary>
public sealed record UiNode
{
    public required string Kind { get; init; }            // 控件类型，如 Button/Edit
    public string? Name { get; init; }                   // 显示名/内容描述
    public string? Text { get; init; }                   // 文本值
    public string? AutomationId { get; init; }           // 资源 id / AutomationId
    public string? ClassName { get; init; }
    public PixelRect Bounds { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Visible { get; init; } = true;
    public bool Clickable { get; init; }
    public bool Editable { get; init; }
    public bool Scrollable { get; init; }
    /// <summary>平台侧原始句柄/引用，仅驱动内部用于执行动作，不外传。</summary>
    public string? NativeRef { get; init; }
    /// <summary>此节点支持的语义动作名。</summary>
    public IReadOnlyList<string> Actions { get; init; } = Array.Empty<string>();
}

/// <summary>OCR 文本块。</summary>
public sealed record TextBlock
{
    public required string Text { get; init; }
    public PixelRect Bounds { get; init; }
    public double Confidence { get; init; } = 1.0;
}

/// <summary>一次观测的完整结果：帧 + 语义树 + 文字 + 候选元素。这是「感知」的输出。</summary>
public sealed record Observation
{
    public required DeviceId Device { get; init; }
    public Frame? Frame { get; init; }
    public IReadOnlyList<UiNode> Nodes { get; init; } = Array.Empty<UiNode>();
    public IReadOnlyList<TextBlock> Text { get; init; } = Array.Empty<TextBlock>();
    public PixelRect Screen { get; init; }
    public double DpiScale { get; init; } = 1.0;
    public long TimestampUnixMs { get; init; }
}

/// <summary>解算后的具体目标：坐标 + 命中的节点（若有）+ 命中方式。</summary>
public sealed record ResolvedTarget
{
    public required PixelPoint Point { get; init; }
    public PixelRect Bounds { get; init; }
    public UiNode? Node { get; init; }
    public SelectorKind Via { get; init; }
    public double Score { get; init; } = 1.0;
}
