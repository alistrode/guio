using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using GuIO.Contracts;

namespace GuIO.Driver.Windows;

/// <summary>语义树读取（UIA3 via FlaUI）。这是 Windows 端「按结构找」的底座。</summary>
internal sealed class UiaSemanticReader : IDisposable
{
    private readonly UIA3Automation _automation = new();

    public IReadOnlyList<UiNode> ReadTree(IntPtr hwnd, int maxNodes = 4000, int maxDepth = 40)
    {
        var result = new List<UiNode>();
        AutomationElement? root;
        try { root = _automation.FromHandle(hwnd); }
        catch { return result; }
        if (root is null) return result;

        var stack = new Stack<(AutomationElement El, int Depth)>();
        stack.Push((root, 0));
        var visited = 0;
        while (stack.Count > 0 && visited < maxNodes)
        {
            var (el, depth) = stack.Pop();
            visited++;
            UiNode? node = null;
            try { node = Project(el); }
            catch { }
            if (node is not null) result.Add(node);

            if (depth >= maxDepth) continue;
            try
            {
                foreach (var child in el.FindAllChildren())
                    stack.Push((child, depth + 1));
            }
            catch { }
        }
        return result;
    }

    private static UiNode Project(AutomationElement el)
    {
        var rect = el.Properties.BoundingRectangle.ValueOrDefault;
        var bounds = new PixelRect((int)rect.Left, (int)rect.Top, (int)rect.Right, (int)rect.Bottom);
        var actions = new List<string>();

        bool clickable = false, editable = false, scrollable = false;
        try
        {
            var patterns = el.Patterns;
            if (patterns.Invoke.IsSupported) { clickable = true; actions.Add("invoke"); }
            if (patterns.SelectionItem.IsSupported) { clickable = true; actions.Add("select"); }
            if (patterns.Toggle.IsSupported) { actions.Add("toggle"); }
            if (patterns.Value.IsSupported) { editable = true; actions.Add("setValue"); }
            if (patterns.TextEdit.IsSupported) { editable = true; }
            if (patterns.Scroll.IsSupported) { scrollable = true; actions.Add("scroll"); }
            if (patterns.ExpandCollapse.IsSupported) { actions.Add("expand"); }
        }
        catch { }

        return new UiNode
        {
            Kind = SafeStr(() => el.ControlType.ToString()) ?? "Unknown",
            Name = SafeStr(() => el.Name),
            Text = SafeStr(() => { try { return el.Patterns.Value.PatternOrDefault?.Value?.Value; } catch { return null; } }),
            AutomationId = SafeStr(() => el.AutomationId),
            ClassName = SafeStr(() => el.ClassName),
            Bounds = bounds,
            Enabled = SafeBool(() => el.Properties.IsEnabled.Value),
            Visible = !SafeBool(() => el.Properties.IsOffscreen.Value),
            Clickable = clickable,
            Editable = editable,
            Scrollable = scrollable,
            NativeRef = SafeStr(() => el.Properties.RuntimeId.ValueOrDefault?.ToString()),
            Actions = actions,
        };
    }

    private static string? SafeStr(Func<string?> f) { try { return f(); } catch { return null; } }
    private static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }

    public void Dispose() => _automation.Dispose();
}
