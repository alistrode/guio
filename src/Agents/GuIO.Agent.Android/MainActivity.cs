using Android.App;
using Android.Content.PM;
using Android.OS;
using GuIO.Kernel;
using GuIO.Service;
using GuIO.Driver.Android;

namespace GuIO.Agent.Android;

/// <summary>GuIO 主界面：显示服务状态、设备信息、无障碍/权限引导、流程入口。</summary>
[Activity(Label = "GuIO", MainLauncher = true, Exported = true, Theme = "@android:style/Theme.Material.Light")]
public sealed class MainActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // 让引擎侧的驱动工厂指向 Android 驱动
        GuioRuntime.RegisterAndroid(this);

        var root = new global::Android.Widget.LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
        };
        int pad = (int)(16 * Resources!.DisplayMetrics!.Density);
        root.SetPadding(pad, pad, pad, pad);

        var title = new global::Android.Widget.TextView(this)
        {
            Text = "GuIO 通用自动化 · 端侧",
        };
        title.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 22);
        root.AddView(title);

        var status = new global::Android.Widget.TextView(this) { Id = 1001 };
        root.AddView(status);

        var openA11y = new global::Android.Widget.Button(this) { Text = "开启无障碍服务" };
        openA11y.Click += (_, _) =>
        {
            StartActivity(new global::Android.Content.Intent(global::Android.Provider.Settings.ActionAccessibilitySettings));
        };
        root.AddView(openA11y);

        var refresh = new global::Android.Widget.Button(this) { Text = "刷新状态" };
        refresh.Click += (_, _) => UpdateStatus(status);
        root.AddView(refresh);

        var selfTest = new global::Android.Widget.Button(this) { Text = "自检：读界面结构" };
        selfTest.Click += (_, _) => RunSelfTest(status);
        root.AddView(selfTest);

        var dump = new global::Android.Widget.TextView(this)
        {
            Text = GuioRuntime.Describe(),
        };
        dump.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12);
        root.AddView(dump);

        SetContentView(root);
        UpdateStatus(status);
    }

    protected override void OnResume()
    {
        base.OnResume();
        GuioRuntime.StartLocalServer(this);
    }

    private void UpdateStatus(global::Android.Widget.TextView tv)
    {
        bool a11y = GuioAccessibilityService.Instance is not null;
        tv.Text = a11y
            ? "状态：无障碍服务已连接 ✓"
            : "状态：无障碍服务未启用 —— 请点击下方按钮开启";
    }

    private void RunSelfTest(global::Android.Widget.TextView tv)
    {
        var svc = GuioAccessibilityService.Instance;
        if (svc is null) { tv.Text = "自检失败：无障碍服务未启用"; return; }
        var tree = svc.ReadTree(maxNodes: 200);
        int clickable = tree.Count(n => n.Clickable);
        tv.Text = $"自检：读到 {tree.Count} 个节点（{clickable} 个可点）\n"
                + string.Join("\n", tree.Take(5).Select(n => $"· {n.Kind} | {n.Name ?? n.Text ?? n.AutomationId}"));
    }
}

/// <summary>
/// 组装根：把 Android 驱动、服务、内核接起来，并提供端侧常驻与本地 RPC。
/// 与 Windows 宿主的差别仅在于「用哪个驱动」和「怎么常驻」。
/// </summary>
public static class GuioRuntime
{
    private static CapabilityRegistry? _registry;
    private static DeviceManager? _devices;
    private static RpcService? _rpc;
    private static GuIO.Transport.LocalServer? _server;

    public static void RegisterAndroid(global::Android.Content.Context ctx)
    {
        _registry ??= BuildRegistry();
        _devices ??= new DeviceManager(_registry);
        _rpc ??= new RpcService(_devices);
    }

    private static CapabilityRegistry BuildRegistry()
    {
        var reg = new CapabilityRegistry();
        reg.Register("android", "Android 设备", id => new AndroidDriver(id));
        return reg;
    }

    /// <summary>启动本地 RPC（供本机其它 App / 自身脚本调用）。</summary>
    public static void StartLocalServer(global::Android.Content.Context ctx)
    {
        if (_server is not null) return;
        RegisterAndroid(ctx);
        _server = new GuIO.Transport.LocalServer(0);
        _server.OnClient += async conn =>
        {
            while (true)
            {
                var data = await conn.ReceiveAsync();
                if (data is null) break;
                var req = GuIO.Transport.WireCodec.DecodeJson<GuIO.Contracts.RpcRequest>(data.Value);
                if (req is null) continue;
                var resp = await _rpc!.HandleAsync(req);
                await conn.SendAsync(GuIO.Transport.ChannelKind.Control, GuIO.Transport.WireCodec.EncodeJson(resp));
            }
        };
        _server.Start();
    }

    public static string Describe()
    {
        var reg = _registry ?? BuildRegistry();
        return "驱动: " + string.Join(", ", reg.All.Select(r => r.Scheme))
             + $"\n本地端口: {_server?.Port ?? 0}";
    }
}
