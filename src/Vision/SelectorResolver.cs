using GuIO.Contracts;

namespace GuIO.Vision;

/// <summary>
/// 接地解算器：把「选择器」解算成具体可操作目标。
/// 内置四级降级链 —— 语义树 → 文字 → 图像 → 坐标（《工程控制论》的「冗余」）。
/// 这是整个产品通用性的核心：同一条动作接口因此对所有平台一致。
/// </summary>
public sealed class SelectorResolver
{
    private readonly ITemplateMatcher _matcher;
    private readonly IFrameCodec _codec;

    public SelectorResolver(ITemplateMatcher matcher, IFrameCodec codec)
    {
        _matcher = matcher;
        _codec = codec;
    }

    /// <summary>解析单个选择器。</summary>
    public GuioResult<ResolvedTarget> Resolve(Selector sel, Observation obs)
    {
        return sel.Kind switch
        {
            SelectorKind.Node => ResolveNode(sel, obs),
            SelectorKind.Text => ResolveText(sel, obs),
            SelectorKind.ScreenText => ResolveScreenText(sel, obs),
            SelectorKind.Image => ResolveImage(sel, obs),
            SelectorKind.Point => ResolvePoint(sel, obs),
            _ => GuioResult<ResolvedTarget>.Fail(ErrorCode.InvalidArgument, $"未知选择器类型 {sel.Kind}"),
        };
    }

    /// <summary>按链依次降级解析，先命中者胜。</summary>
    public GuioResult<ResolvedTarget> ResolveChain(SelectorChain chain, Observation obs)
    {
        ErrorCode last = ErrorCode.NotFound;
        string? lastMsg = null;
        foreach (var sel in chain.Chain)
        {
            var r = Resolve(sel, obs);
            if (r.Ok) return r;
            last = r.Error;
            lastMsg = r.Message;
        }
        return GuioResult<ResolvedTarget>.Fail(last, lastMsg ?? "降级链全部未命中");
    }

    /// <summary>为一个动作目标自动构造降级链：按其形态决定起始级别。</summary>
    public static SelectorChain AutoChain(Selector sel, CapabilityFlags caps)
    {
        bool hasTree = caps.HasFlag(CapabilityFlags.SemanticTree);
        bool hasOcr = caps.HasFlag(CapabilityFlags.Ocr);

        // Point 是终态，不降级
        if (sel.Kind == SelectorKind.Point) return SelectorChain.Single(sel);

        var chain = new List<Selector>();
        switch (sel.Kind)
        {
            case SelectorKind.Node:
                if (hasTree) chain.Add(sel);
                // 节点找不到时，若它带了文本，可退到文字
                if (hasOcr && !string.IsNullOrEmpty(sel.Secondary))
                    chain.Add(Selector.Text(sel.Secondary!, true));
                if (hasOcr && !string.IsNullOrEmpty(sel.Value))
                    chain.Add(Selector.Text(sel.Value!, true));
                break;
            case SelectorKind.Text:
            case SelectorKind.ScreenText:
                if (hasTree && sel.Kind == SelectorKind.Text) chain.Add(Selector.Node("", sel.Value));
                if (hasOcr) chain.Add(sel);
                break;
            case SelectorKind.Image:
                chain.Add(sel);
                break;
        }
        if (chain.Count == 0) chain.Add(sel);
        return new SelectorChain { Chain = chain };
    }

    private static GuioResult<ResolvedTarget> ResolveNode(Selector sel, Observation obs)
    {
        var candidates = obs.Nodes.Where(n => n.Visible && !n.Bounds.IsEmpty).ToList();

        if (!string.IsNullOrEmpty(sel.Value))
        {
            var byId = candidates.Where(n =>
                string.Equals(n.AutomationId, sel.Value, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byId.Count > 0) return Pick(sel, byId, SelectorKind.Node);
        }
        if (!string.IsNullOrEmpty(sel.Secondary))
        {
            var byName = candidates.Where(n =>
                Eq(n.Name, sel.Secondary!, sel.Fuzzy) || Eq(n.Text, sel.Secondary!, sel.Fuzzy)).ToList();
            if (byName.Count > 0) return Pick(sel, byName, SelectorKind.Node);
        }
        return GuioResult<ResolvedTarget>.Fail(ErrorCode.NotFound, $"语义树未找到节点 {sel.Value ?? sel.Secondary}");
    }

    private static GuioResult<ResolvedTarget> ResolveText(Selector sel, Observation obs)
    {
        // 先查语义树文本，再退 OCR
        var nodes = obs.Nodes.Where(n => n.Visible && !n.Bounds.IsEmpty &&
            (Eq(n.Text, sel.Value!, sel.Fuzzy) || Eq(n.Name, sel.Value!, sel.Fuzzy))).ToList();
        if (nodes.Count > 0) return Pick(sel, nodes, SelectorKind.Node);

        var ocr = obs.Text.Where(t => Eq(t.Text, sel.Value!, sel.Fuzzy)).ToList();
        if (ocr.Count > 0) return PickTextBlocks(sel, ocr);
        return GuioResult<ResolvedTarget>.Fail(ErrorCode.NotFound, $"未找到文本「{sel.Value}」");
    }

    private static GuioResult<ResolvedTarget> ResolveScreenText(Selector sel, Observation obs)
    {
        var ocr = obs.Text.Where(t => Eq(t.Text, sel.Value!, sel.Fuzzy)).ToList();
        if (ocr.Count > 0) return PickTextBlocks(sel, ocr);
        return GuioResult<ResolvedTarget>.Fail(ErrorCode.NotFound, $"屏幕上未找到文字「{sel.Value}」");
    }

    private GuioResult<ResolvedTarget> ResolveImage(Selector sel, Observation obs)
    {
        if (obs.Frame is null)
            return GuioResult<ResolvedTarget>.Fail(ErrorCode.Unsupported, "图像匹配需要帧，但本次观测未取帧");
        if (string.IsNullOrEmpty(sel.TemplatePath) || !File.Exists(sel.TemplatePath))
            return GuioResult<ResolvedTarget>.Fail(ErrorCode.InvalidArgument, $"模板不存在：{sel.TemplatePath}");

        var tpl = _codec.DecodeFile(sel.TemplatePath);
        var hits = _matcher.FindAll(obs.Frame, tpl, sel.Threshold, sel.Region);
        if (hits.Count == 0)
            return GuioResult<ResolvedTarget>.Fail(ErrorCode.NotFound, $"模板未匹配（阈值 {sel.Threshold:0.##}）");

        var chosen = sel.Index >= 0 && sel.Index < hits.Count ? hits[sel.Index] : hits[0];
        return GuioResult<ResolvedTarget>.Success(new ResolvedTarget
        {
            Point = chosen.Bounds.Center,
            Bounds = chosen.Bounds,
            Via = SelectorKind.Image,
            Score = chosen.Score,
        });
    }

    private static GuioResult<ResolvedTarget> ResolvePoint(Selector sel, Observation obs)
    {
        PixelPoint p;
        if (sel.Norm is { } np) p = np.ToPixels(obs.Screen);
        else if (sel.Pixel is { } px) p = px;
        else return GuioResult<ResolvedTarget>.Fail(ErrorCode.InvalidArgument, "Point 选择器缺少坐标");

        return GuioResult<ResolvedTarget>.Success(new ResolvedTarget
        {
            Point = p,
            Bounds = PixelRect.FromSize(p.X - 1, p.Y - 1, 2, 2),
            Via = SelectorKind.Point,
        });
    }

    private static GuioResult<ResolvedTarget> Pick(Selector sel, List<UiNode> list, SelectorKind via)
    {
        list = list.OrderByDescending(n => n.Clickable).ThenByDescending(n => n.Bounds.Width * n.Bounds.Height).ToList();
        var n = sel.Index >= 0 && sel.Index < list.Count ? list[sel.Index] : list[0];
        return GuioResult<ResolvedTarget>.Success(new ResolvedTarget
        {
            Point = n.Bounds.Center,
            Bounds = n.Bounds,
            Node = n,
            Via = via,
            Score = 1.0,
        });
    }

    private static GuioResult<ResolvedTarget> PickTextBlocks(Selector sel, List<TextBlock> list)
    {
        list = list.OrderByDescending(t => t.Confidence).ToList();
        var t = sel.Index >= 0 && sel.Index < list.Count ? list[sel.Index] : list[0];
        return GuioResult<ResolvedTarget>.Success(new ResolvedTarget
        {
            Point = t.Bounds.Center,
            Bounds = t.Bounds,
            Via = SelectorKind.Text,
            Score = t.Confidence,
        });
    }

    private static bool Eq(string? a, string b, bool fuzzy)
    {
        if (a is null) return false;
        if (fuzzy) return a.Contains(b, StringComparison.OrdinalIgnoreCase);
        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
