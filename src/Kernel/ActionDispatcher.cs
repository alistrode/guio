using GuIO.Contracts;

namespace GuIO.Kernel;

/// <summary>
/// 动作分发器：把抽象动作翻译成驱动的具体调用。
/// 单一职责 —— 它只负责「怎么调」，不做接地、不做重试、不做决策。
/// </summary>
public sealed class ActionDispatcher
{
    /// <summary>执行一个动作。target 为已解算的目标（坐标类动作用得到；系统/应用类可空）。</summary>
    public async Task<GuioResult<bool>> DispatchAsync(
        IDeviceDriver driver, GuioAction action, ResolvedTarget? target, CancellationToken ct)
    {
        var act = driver.Actuator;
        switch (action)
        {
            case ClickAction c:
            {
                if (target is null) return Need(nameof(ClickAction));
                // 语义优先：命中节点且节点声明了可执行动作时，走平台语义动作
                if (c.PreferSemantic && target.Node is { } node && node.Actions.Count > 0)
                {
                    var semantic = await act.InvokeNodeAsync(node, node.Actions[0], ct).ConfigureAwait(false);
                    if (semantic.Ok) return semantic;
                }
                return await act.ClickAsync(target.Point, c.Button, c.Count, c.HoldMs, ct).ConfigureAwait(false);
            }
            case ActivateAction:
                if (driver is IExtendedActuator extA)
                    return await extA.ExecuteExtendedAsync(action, ct).ConfigureAwait(false);
                return GuioResult<bool>.Fail(ErrorCode.Unsupported, "驱动不支持 activate");

            case MoveAction:
                return target is null ? Need(nameof(MoveAction))
                    : await act.MoveAsync(target.Point, ct).ConfigureAwait(false);

            case DragAction d:
            {
                if (target is null) return Need(nameof(DragAction));
                var to = await ResolveToAsync(driver, d.To, ct).ConfigureAwait(false);
                if (!to.Ok || to.Value is null) return GuioResult<bool>.Fail(to.Error, "拖拽终点未解算");
                return await act.DragAsync(target.Point, to.Value.Point, d.DurationMs, d.Steps, ct).ConfigureAwait(false);
            }
            case SwipeAction s:
            {
                if (target is null) return Need(nameof(SwipeAction));
                var to = await ResolveToAsync(driver, s.To, ct).ConfigureAwait(false);
                if (!to.Ok || to.Value is null) return GuioResult<bool>.Fail(to.Error, "滑动终点未解算");
                return await act.SwipeAsync(target.Point, to.Value.Point, s.DurationMs, ct).ConfigureAwait(false);
            }
            case PinchAction p:
                return target is null ? Need(nameof(PinchAction))
                    : await act.PinchAsync(target.Point, p.Scale, p.SpanPx, p.DurationMs, ct).ConfigureAwait(false);

            case ScrollAction sc:
                return await act.ScrollAsync(target?.Point, sc.DeltaX, sc.DeltaY, sc.Unit, ct).ConfigureAwait(false);

            case TypeTextAction t:
                return await act.TypeAsync(t.Text, t.ForceUnicode, t.ClearFirst, ct).ConfigureAwait(false);

            case KeyAction k:
            {
                int vk = k.VirtualKey ?? KeyNames.ToVirtualKey(k.Key);
                if (vk <= 0) return GuioResult<bool>.Fail(ErrorCode.InvalidArgument, $"无法识别按键「{k.Key}」");
                return await act.KeyAsync(vk, k.Modifiers, k.Repeat, ct).ConfigureAwait(false);
            }
            case SystemKeyAction sk:
                return await act.SystemKeyAsync(sk.Key, ct).ConfigureAwait(false);

            case AppAction:
            case ShellAction:
                // 系统/应用/Shell 类动作由驱动以专门通道实现，内核此处交给驱动的扩展执行口
                if (driver is IExtendedActuator ext)
                    return await ext.ExecuteExtendedAsync(action, ct).ConfigureAwait(false);
                return GuioResult<bool>.Fail(ErrorCode.Unsupported, $"驱动不支持动作「{action.Name}」");

            default:
                return GuioResult<bool>.Fail(ErrorCode.Unsupported, $"未知动作 {action.Name}");
        }
    }

    private static async Task<GuioResult<ResolvedTarget>> ResolveToAsync(IDeviceDriver driver, TargetRef r, CancellationToken ct)
    {
        if (r.Selector is null) return GuioResult<ResolvedTarget>.Fail(ErrorCode.InvalidArgument, "目标引用为空");
        if (driver is ISelectorAware aware) return await aware.ResolveAsync(r.Selector, r.Offset, ct).ConfigureAwait(false);
        return GuioResult<ResolvedTarget>.Fail(ErrorCode.Unsupported, "驱动未提供选择器解算");
    }

    private static GuioResult<bool> Need(string name) => GuioResult<bool>.Fail(ErrorCode.InvalidArgument, $"{name} 需要目标");
}
