using System.Text.Json;
using System.Text.Json.Nodes;
using GuIO.Contracts;
using GuIO.Driver.Windows;
using GuIO.Kernel;
using GuIO.Service;

namespace GuIO.Mcp;

/// <summary>
/// GuIO MCP Server（stdio）。把「统一动作表」原样映射为 MCP 工具，
/// 于是任意 LLM Agent 都能用同一套语义操作 Windows 桌面与 Android 手机。
/// </summary>
public static class Program
{
    private static readonly DeviceManager Devices;
    private static readonly RpcService Rpc;

    static Program()
    {
        var registry = new CapabilityRegistry();
        registry.Register("win", "Windows 桌面", id => new WindowsDriver(id));
        Devices = new DeviceManager(registry);
        Rpc = new RpcService(Devices);
    }

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        var reader = new StreamReader(stdin, System.Text.Encoding.UTF8);
        var writer = new StreamWriter(stdout, new System.Text.UTF8Encoding(false)) { AutoFlush = true };

        while (true)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonNode? req;
            try { req = JsonNode.Parse(line); } catch { continue; }
            if (req is null) continue;
            var id = req["id"];
            var method = req["method"]?.GetValue<string>();

            JsonObject? resp = null;
            switch (method)
            {
                case "initialize":
                    resp = new JsonObject
                    {
                        ["protocolVersion"] = "2024-11-05",
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "guio", ["version"] = "0.1.0" },
                    };
                    break;
                case "notifications/initialized":
                    continue; // 通知无需响应
                case "tools/list":
                    resp = new JsonObject { ["tools"] = BuildTools() };
                    break;
                case "tools/call":
                    resp = await CallToolAsync(req["params"]).ConfigureAwait(false);
                    break;
                case "ping":
                    resp = new JsonObject();
                    break;
                default:
                    Write(writer, new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = id?.DeepClone(),
                        ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"method not found: {method}" },
                    });
                    continue;
            }

            var outObj = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = resp };
            Write(writer, outObj);
        }
        return 0;
    }

    private static void Write(StreamWriter w, JsonObject o)
        => w.WriteLine(o.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));

    private static JsonArray BuildTools()
    {
        var arr = new JsonArray();
        void Add(string name, string desc, JsonObject schema) =>
            arr.Add(new JsonObject
            {
                ["name"] = name, ["description"] = desc, ["inputSchema"] = schema,
            });

        JsonObject DeviceProp() => new() { ["type"] = "string", ["description"] = "设备 URI，如 win://window/1234、win://app/notepad、android://local" };
        var coordProps = new JsonObject
        {
            ["x"] = new JsonObject { ["type"] = "integer" },
            ["y"] = new JsonObject { ["type"] = "integer" },
            ["nx"] = new JsonObject { ["type"] = "number", ["description"] = "归一化 X (0..1)，优先于 x" },
            ["ny"] = new JsonObject { ["type"] = "number", ["description"] = "归一化 Y (0..1)" },
            ["text"] = new JsonObject { ["type"] = "string", ["description"] = "按文字定位目标" },
            ["device"] = DeviceProp(),
        };

        Add("gui_observe", "观测当前设备：返回语义树节点、屏上文字（OCR）、屏幕尺寸。这是所有动作前的第一步。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["device"] = DeviceProp(),
                    ["ocr"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否做 OCR" },
                    ["tree"] = new JsonObject { ["type"] = "boolean" },
                },
            });

        Add("gui_screenshot", "截屏保存到文件。返回路径与尺寸。",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["device"] = DeviceProp(), ["out"] = new JsonObject { ["type"] = "string" } } });

        Add("gui_vision", "使用本机 Ollama 视觉模型识别图片或当前屏幕；图像只发送到本机服务，不调用云端。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["image"] = new JsonObject { ["type"] = "string", ["description"] = "本地图片路径；省略时截取 device 当前屏幕" },
                    ["prompt"] = new JsonObject { ["type"] = "string", ["description"] = "希望模型回答的问题；默认中文描述界面文字、控件和位置，位置使用 [0,1000] 归一化坐标" },
                    ["device"] = DeviceProp(),
                },
            });

        Add("gui_find", "按选择器查找元素，返回命中坐标（不点击）。", new JsonObject { ["type"] = "object", ["properties"] = coordProps });

        Add("gui_click", "点击目标。可用坐标(x/y 或 nx/ny)、文字(text)或图片(image)定位。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["x"] = new JsonObject { ["type"] = "integer" },
                    ["y"] = new JsonObject { ["type"] = "integer" },
                    ["nx"] = new JsonObject { ["type"] = "number" },
                    ["ny"] = new JsonObject { ["type"] = "number" },
                    ["text"] = new JsonObject { ["type"] = "string" },
                    ["image"] = new JsonObject { ["type"] = "string" },
                    ["count"] = new JsonObject { ["type"] = "integer", ["description"] = "2 为双击" },
                    ["device"] = DeviceProp(),
                },
            });

        Add("gui_type", "输入文本（支持中文/emoji）。", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["value"] = new JsonObject { ["type"] = "string" },
                ["unicode"] = new JsonObject { ["type"] = "boolean" },
                ["x"] = new JsonObject { ["type"] = "integer" },
                ["y"] = new JsonObject { ["type"] = "integer" },
                ["device"] = DeviceProp(),
            },
            ["required"] = new JsonArray("value"),
        });

        Add("gui_key", "按单个键或组合键。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["key"] = new JsonObject { ["type"] = "string", ["description"] = "如 Enter/ Esc / F5 / A" },
                    ["modifiers"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "如 [\"Ctrl\"]" },
                    ["device"] = DeviceProp(),
                },
                ["required"] = new JsonArray("key"),
            });

        Add("gui_scroll", "滚动。", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["dy"] = new JsonObject { ["type"] = "integer", ["description"] = "负值向上" },
                ["dx"] = new JsonObject { ["type"] = "integer" },
                ["device"] = DeviceProp(),
            },
        });

        Add("gui_syskey", "系统键：Back / Home / Recents / Notifications。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["key"] = new JsonObject { ["type"] = "string" }, ["device"] = DeviceProp() },
                ["required"] = new JsonArray("key"),
            });

        Add("gui_app", "应用操作：launch / kill / list / install / uninstall。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["op"] = new JsonObject { ["type"] = "string" },
                    ["target"] = new JsonObject { ["type"] = "string", ["description"] = "进程名/包名/路径" },
                    ["device"] = DeviceProp(),
                },
                ["required"] = new JsonArray("op"),
            });

        Add("gui_wait", "等待条件成立：found/gone/changed/stable。闭环收敛，带超时。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["goal"] = new JsonObject { ["type"] = "string", ["description"] = "found/gone/changed/stable" },
                    ["text"] = new JsonObject { ["type"] = "string" },
                    ["timeoutMs"] = new JsonObject { ["type"] = "integer" },
                    ["device"] = DeviceProp(),
                },
                ["required"] = new JsonArray("goal"),
            });

        Add("gui_volume", "读取或设置系统音量。省略 value 即读取。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["value"] = new JsonObject { ["type"] = "number", ["description"] = "0..1" },
                    ["device"] = DeviceProp(),
                },
            });

        Add("gui_play", "播放音频文件。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string" }, ["device"] = DeviceProp() },
                ["required"] = new JsonArray("path"),
            });

        Add("gui_record_loopback", "录制系统正在播放的声音（用于验证音效是否真的响了）。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["out"] = new JsonObject { ["type"] = "string" },
                    ["seconds"] = new JsonObject { ["type"] = "integer" },
                    ["device"] = DeviceProp(),
                },
            });

        Add("gui_flow", "运行一个流程（步骤序列，支持分支/循环/等待）。传入 flow 的 JSON。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["flow"] = new JsonObject { ["type"] = "string", ["description"] = "Flow 的 JSON 字符串" },
                    ["device"] = DeviceProp(),
                },
                ["required"] = new JsonArray("flow"),
            });

        return arr;
    }

    private static async Task<JsonObject> CallToolAsync(JsonNode? pars)
    {
        var name = pars?["name"]?.GetValue<string>() ?? "";
        var args = pars?["arguments"] as JsonObject ?? new JsonObject();
        JsonElement? argEl = JsonDocument.Parse(args.ToJsonString()).RootElement.Clone();

        (string method, JsonElement? merged) = MapTool(name, args, argEl);
        if (method.Length == 0)
            return ToolResult($"未知工具 {name}", isError: true);

        var resp = await Rpc.HandleAsync(new RpcRequest { Id = "m", Method = method, Params = merged }).ConfigureAwait(false);
        if (resp.Error is not null) return ToolResult(resp.Error.Message, isError: true);
        var text = resp.Result?.GetRawText() ?? "{}";
        return ToolResult(text, isError: false);
    }

    private static (string, JsonElement?) MapTool(string name, JsonObject args, JsonElement? argEl)
    {
        JsonElement? WithAction(string action)
        {
            var json = args.ToJsonString();
            var o = JsonNode.Parse(json)!.AsObject();
            o["action"] = action;
            return JsonDocument.Parse(o.ToJsonString()).RootElement.Clone();
        }

        return name switch
        {
            "gui_observe" => (ProtocolMethods.Observe, argEl),
            "gui_screenshot" => (ProtocolMethods.Screenshot, argEl),
            "gui_vision" => (ProtocolMethods.VisionAnalyze, argEl),
            "gui_find" => (ProtocolMethods.Find, argEl),
            "gui_click" => (ProtocolMethods.Act, WithAction(((int?)args["count"]?.GetValue<int>() ?? 1) >= 2 ? "double" : "click")),
            "gui_type" => (ProtocolMethods.Act, WithAction("type")),
            "gui_key" => (ProtocolMethods.Act, WithAction("key")),
            "gui_scroll" => (ProtocolMethods.Act, WithAction("scroll")),
            "gui_syskey" => (ProtocolMethods.Act, WithAction("syskey")),
            "gui_app" => (ProtocolMethods.Act, WithAction("app")),
            "gui_wait" => (ProtocolMethods.Wait, argEl),
            "gui_volume" => (ProtocolMethods.MediaVolume, argEl),
            "gui_play" => (ProtocolMethods.MediaPlay, argEl),
            "gui_record_loopback" => (ProtocolMethods.MediaRecord, argEl),
            "gui_flow" => (ProtocolMethods.RunFlow, argEl),
            _ => ("", null),
        };
    }

    private static JsonObject ToolResult(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };
}
