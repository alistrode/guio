using System.Text.Json;
using GuIO.Contracts;
using GuIO.Driver.Windows;
using GuIO.Kernel;
using GuIO.Service;
using GuIO.Transport;

namespace GuIO.Agent.Windows;

/// <summary>
/// Windows 宿主：组装根。负责注册驱动、装配服务、监听控制通道。
/// 「前后端分离」中的后端 —— 前端（CLI/MCP/GUI/遥控）只通过 JSON-RPC 与它对话。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "serve")
            return await ServeAsync(args).ConfigureAwait(false);

        // 无参数：作为一次性 CLI 转发到本地或直接内嵌执行（由 GuIO.Cli 负责；这里打印用法）
        Console.WriteLine("guio-agent — GuIO Windows 宿主");
        Console.WriteLine("用法:");
        Console.WriteLine("  guio-agent serve [--port 0] [--pipe name]   启动常驻服务并打印端口");
        return 0;
    }

    /// <summary>启动常驻服务：注册驱动、构建设备管理、监听 TCP loopback，处理 JSON-RPC。</summary>
    public static async Task<int> ServeAsync(string[] args)
    {
        int port = ArgInt(args, "--port", 0);

        var registry = new CapabilityRegistry();
        registry.Register("win", "Windows 桌面", id => new WindowsDriver(id));

        var devices = new DeviceManager(registry);
        var rpc = new RpcService(devices);

        var server = new LocalServer(port);
        server.OnClient += async conn =>
        {
            conn.OnMessage += (ch, payload) => { /* 事件/视频通道由订阅者消费 */ };
            await HandleConnectionAsync(conn, rpc).ConfigureAwait(false);
        };
        server.Start();

        Console.WriteLine($"GUIO_AGENT_READY port={server.Port}");
        Console.Out.Flush();

        // 常驻直到收到退出信号
        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; tcs.TrySetResult(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => tcs.TrySetResult();
        await tcs.Task.ConfigureAwait(false);

        await server.DisposeAsync().ConfigureAwait(false);
        await devices.DisposeAsync().ConfigureAwait(false);
        return 0;
    }

    private static async Task HandleConnectionAsync(TcpConnection conn, RpcService rpc)
    {
        try
        {
            while (true)
            {
                var req = await conn.ReceiveAsync().ConfigureAwait(false);
                if (req is null) break;
                RpcRequest? r = null;
                try { r = WireCodec.DecodeJson<RpcRequest>(req.Value); } catch { }
                if (r is null) continue;

                var resp = await rpc.HandleAsync(r).ConfigureAwait(false);
                await conn.SendAsync(ChannelKind.Control, WireCodec.EncodeJson(resp)).ConfigureAwait(false);
            }
        }
        catch { }
        finally { await conn.DisposeAsync().ConfigureAwait(false); }
    }

    private static int ArgInt(string[] args, string name, int def)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name && int.TryParse(args[i + 1], out var v)) return v;
        return def;
    }
}
