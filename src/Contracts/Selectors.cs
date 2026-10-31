namespace GuIO.Contracts;

/// <summary>选择器形态：动作如何「指认」操作对象。五形态覆盖桌面与手机。</summary>
public enum SelectorKind
{
    /// <summary>按语义树节点指认（桌面 UIA / 安卓无障碍）。最精确。</summary>
    Node = 0,
    /// <summary>按文本指认（先查语义树文本，再退 OCR）。</summary>
    Text = 1,
    /// <summary>按图像模板指认（模板匹配）。</summary>
    Image = 2,
    /// <summary>按像素/归一化坐标指认。游戏与画布最实用。</summary>
    Point = 3,
    /// <summary>仅在当前屏幕可见文字中查找（不查语义树）。</summary>
    ScreenText = 4,
}

/// <summary>
/// 选择器：一条动作「打谁」。同一条动作接口适用所有平台，
/// 差别只在这里填哪种形态，由内核按「语义树 → 文字 → 图像 → 坐标」四级降级解算。
/// </summary>
public sealed record Selector
{
    public required SelectorKind Kind { get; init; }

    /// <summary>Node：资源 id / AutomationId；Text：目标文本；ScreenText：目标文本。</summary>
    public string? Value { get; init; }

    /// <summary>Node：节点名/类名/描述等补充限定。</summary>
    public string? Secondary { get; init; }

    /// <summary>Point：像素坐标（与 Norm 二选一）。</summary>
    public PixelPoint? Pixel { get; init; }

    /// <summary>Point：归一化坐标，优先于 Pixel，换设备不重标。</summary>
    public NormPoint? Norm { get; init; }

    /// <summary>Image：模板文件路径。</summary>
    public string? TemplatePath { get; init; }

    /// <summary>Image：匹配阈值 0..1，默认 0.85。</summary>
    public double Threshold { get; init; } = 0.85;

    /// <summary>搜索区域限定（像素），为空表示整屏。</summary>
    public PixelRect? Region { get; init; }

    /// <summary>是否允许对结果做模糊/包含匹配。</summary>
    public bool Fuzzy { get; init; }

    /// <summary>命中多个时取第几个（从 0 开始）；-1 表示取最佳。</summary>
    public int Index { get; init; } = -1;

    public static Selector Node(string automationIdOrValue, string? name = null) =>
        new() { Kind = SelectorKind.Node, Value = automationIdOrValue, Secondary = name };

    public static Selector Text(string text, bool fuzzy = false) =>
        new() { Kind = SelectorKind.Text, Value = text, Fuzzy = fuzzy };

    public static Selector ScreenText(string text, bool fuzzy = false) =>
        new() { Kind = SelectorKind.ScreenText, Value = text, Fuzzy = fuzzy };

    public static Selector Image(string templatePath, double threshold = 0.85) =>
        new() { Kind = SelectorKind.Image, TemplatePath = templatePath, Threshold = threshold };

    public static Selector At(PixelPoint p) => new() { Kind = SelectorKind.Point, Pixel = p };

    public static Selector AtNorm(double x, double y) => new() { Kind = SelectorKind.Point, Norm = new NormPoint(x, y) };
}

/// <summary>选择器链：按顺序解算，先命中者胜。默认由内核按语义树→文字→图像→坐标自动编排。</summary>
public sealed record SelectorChain
{
    public required IReadOnlyList<Selector> Chain { get; init; }

    public static SelectorChain Single(Selector s) => new() { Chain = new[] { s } };
}
