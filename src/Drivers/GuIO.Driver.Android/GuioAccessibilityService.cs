using Android.AccessibilityServices;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views.Accessibility;
using GuIO.Contracts;
using ErrorCode = GuIO.Contracts.ErrorCode;

namespace GuIO.Driver.Android;

/// <summary>
/// GuIO 无障碍服务。这是 Android 端「读界面结构 + 执行手势 + 输入」的底座，
/// 相当于 Windows 端的 UIA。服务常驻，驱动通过静态实例访问它。
/// </summary>
[Service(
    Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE",
    Exported = true,
    Label = "GuIO 自动化服务")]
[IntentFilter(new[] { "android.accessibilityservice.AccessibilityService" })]
[MetaData("android.accessibilityservice", Resource = "@xml/guio_accessibility_config")]
public sealed class GuioAccessibilityService : AccessibilityService
{
    public static GuioAccessibilityService? Instance { get; private set; }

    protected override void OnServiceConnected()
    {
        base.OnServiceConnected();
        Instance = this;
    }

    public override void OnAccessibilityEvent(AccessibilityEvent? e) { /* 事件流可按需订阅；这里保持无状态 */ }

    public override void OnInterrupt() { }

    public override bool OnUnbind(Intent? intent)
    {
        Instance = null;
        return base.OnUnbind(intent);
    }

    /// <summary>当前活动窗口的根节点。</summary>
    public AccessibilityNodeInfo? RootNode() => RootInActiveWindow;

    /// <summary>读取整棵语义树（统一投影为 UiNode）。</summary>
    public IReadOnlyList<UiNode> ReadTree(int maxNodes = 3000, int maxDepth = 40)
    {
        var result = new List<UiNode>();
        var root = RootInActiveWindow;
        if (root is null) return result;

        var stack = new Stack<(AccessibilityNodeInfo Node, int Depth)>();
        stack.Push((root, 0));
        int visited = 0;
        while (stack.Count > 0 && visited < maxNodes)
        {
            var (node, depth) = stack.Pop();
            visited++;
            result.Add(Project(node));
            if (depth >= maxDepth) continue;
            int n = node.ChildCount;
            for (int i = 0; i < n; i++)
            {
                var child = node.GetChild(i);
                if (child is not null) stack.Push((child, depth + 1));
            }
        }
        return result;
    }

    private static UiNode Project(AccessibilityNodeInfo n)
    {
        var rect = new Rect();
        n.GetBoundsInScreen(rect);
        var actions = new List<string>();
        if (n.Clickable) actions.Add("invoke");
        if (n.Scrollable) actions.Add("scroll");
        if (n.LongClickable) actions.Add("longClick");
        if (n.Editable) actions.Add("setValue");

        return new UiNode
        {
            Kind = MapClass(n),
            Name = n.ContentDescription ?? n.Text ?? n.HintText,
            Text = n.Text,
            AutomationId = n.ViewIdResourceName,
            ClassName = n.ClassName,
            Bounds = new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
            Enabled = n.Enabled,
            Visible = n.VisibleToUser,
            Clickable = n.Clickable,
            Editable = n.Editable,
            Scrollable = n.Scrollable,
            Actions = actions,
        };
    }

    private static string MapClass(AccessibilityNodeInfo n)
    {
        var c = n.ClassName ?? "";
        if (n.Editable) return "Edit";
        if (c.Contains("Button")) return "Button";
        if (c.Contains("TextView")) return "Text";
        if (c.Contains("EditText")) return "Edit";
        if (c.Contains("ImageView")) return "Image";
        if (c.Contains("CheckBox")) return "CheckBox";
        if (c.Contains("Switch")) return "Switch";
        if (c.Contains("ListView") || c.Contains("RecyclerView")) return "List";
        if (c.Contains("ScrollView")) return "ScrollView";
        return c;
    }

    /// <summary>按识别到的节点做点击/长按（语义动作优先）。</summary>
    public bool InvokeNodeByBounds(PixelRect bounds, bool longClick)
    {
        var root = RootInActiveWindow;
        if (root is null) return false;
        var target = FindByBounds(root, bounds, 0);
        if (target is null) return false;
        if (longClick && target.LongClickable) return target.PerformAction(global::Android.Views.Accessibility.Action.LongClick);
        if (target.Clickable) return target.PerformAction(global::Android.Views.Accessibility.Action.Click);
        // 不可点的容器：向上寻找可点祖先
        var p = target;
        while (p is not null)
        {
            if (p.Clickable) return p.PerformAction(global::Android.Views.Accessibility.Action.Click);
            p = p.Parent;
        }
        return false;
    }

    private static AccessibilityNodeInfo? FindByBounds(AccessibilityNodeInfo node, PixelRect target, int depth)
    {
        if (depth > 60) return null;
        var r = new Rect();
        node.GetBoundsInScreen(r);
        if (r.Left == target.Left && r.Top == target.Top && r.Width() == target.Width && r.Height() == target.Height)
            return node;
        int n = node.ChildCount;
        for (int i = 0; i < n; i++)
        {
            var c = node.GetChild(i);
            if (c is null) continue;
            var found = FindByBounds(c, target, depth + 1);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>给可编辑节点设置文本（无障碍通道，支持中文与 emoji）。</summary>
    public bool SetTextAtBounds(PixelRect bounds, string text)
    {
        var root = RootInActiveWindow;
        if (root is null) return false;
        var target = FindEditable(root, bounds, 0) ?? FindFocusedEditable(root, 0);
        if (target is null) return false;
        var args = new Bundle();
        args.PutCharSequence(AccessibilityNodeInfo.ActionArgumentSetTextCharsequence!, text);
        return target.PerformAction(global::Android.Views.Accessibility.Action.SetText, args);
    }

    private static AccessibilityNodeInfo? FindEditable(AccessibilityNodeInfo node, PixelRect target, int depth)
    {
        if (depth > 60) return null;
        var r = new Rect();
        node.GetBoundsInScreen(r);
        if (node.Editable && RectContains(r, target)) return node;
        int n = node.ChildCount;
        for (int i = 0; i < n; i++)
        {
            var c = node.GetChild(i);
            if (c is null) continue;
            var f = FindEditable(c, target, depth + 1);
            if (f is not null) return f;
        }
        return null;
    }

    private static AccessibilityNodeInfo? FindFocusedEditable(AccessibilityNodeInfo node, int depth)
    {
        if (depth > 60) return null;
        if (node.Editable && node.Focused) return node;
        int n = node.ChildCount;
        for (int i = 0; i < n; i++)
        {
            var c = node.GetChild(i);
            if (c is null) continue;
            var f = FindFocusedEditable(c, depth + 1);
            if (f is not null) return f;
        }
        return null;
    }

    private static bool RectContains(Rect outer, PixelRect inner)
    {
        // 目标中心点落在节点内即认为匹配（更宽容）
        int cx = inner.Left + inner.Width / 2, cy = inner.Top + inner.Height / 2;
        return outer.Contains(cx, cy);
    }
}
