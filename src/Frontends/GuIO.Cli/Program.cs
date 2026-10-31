using System.Text.Json;
using GuIO.Contracts;
using GuIO.Driver.Windows;
using GuIO.Kernel;
using GuIO.Service;
using GuIO.Transport;

namespace GuIO.Cli;

/// <summary>
/// GuIO 命令行前端。命令即动词，目标即选择器。
/// 这是「统一契约」最直观的出口：同一条命令在 Windows 与手机端语义一致。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        try
        {
            // 远端模式：所有命令转为 JSON-RPC 打到常驻服务
            int agentPort = OptInt(args, "--agent", -1);
            if (agentPort > 0)
                return await RemoteAsync(args, agentPort).ConfigureAwait(false);

            return await InProcAsync(args).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Emit(new { ok = false, error = ex.Message });
            return 1;
        }
    }

    // ---------------- 内嵌（本机零配置） ----------------
    private static async Task<int> InProcAsync(string[] args)
    {
        var registry = new CapabilityRegistry();
        registry.Register("win", "Windows 桌面", id => new WindowsDriver(id));
        var devices = new DeviceManager(registry);
        var rpc = new RpcService(devices);

        var cmd = args[0].ToLowerInvariant();
        JsonElement? pars = BuildParams(args);

        var method = cmd switch
        {
            "devices" or "ls" => ProtocolMethods.ListDevices,
            "windows" or "wins" => ProtocolMethods.ListWindows,
            "connect" => ProtocolMethods.Connect,
            "observe" => ProtocolMethods.Observe,
            "screenshot" or "shot" => ProtocolMethods.Screenshot,
            "vision" => ProtocolMethods.VisionAnalyze,
            "find" => ProtocolMethods.Find,
            "click" or "double" or "right" or "longpress" or "move" or "type" or "key"
                or "scroll" or "syskey" or "app" or "shell" or "drag" or "swipe" or "activate" => ProtocolMethods.Act,
            "wait" => ProtocolMethods.Wait,
            "flow" => ProtocolMethods.RunFlow,
            "play" => ProtocolMethods.MediaPlay,
            "volume" => ProtocolMethods.MediaVolume,
            "audio" or "audiocheck" => ProtocolMethods.MediaDiag,
            "audio-default" or "setaudio" => ProtocolMethods.MediaSetDefault,
            "stt" or "transcribe" => ProtocolMethods.SpeechTranscribe,
            "record" => ProtocolMethods.MediaRecord,
            "rec-start" => ProtocolMethods.RecordStart,
            "rec-stop" => ProtocolMethods.RecordStop,
            "replay" => ProtocolMethods.Replay,
            "ping" => ProtocolMethods.Ping,
            _ => null,
        };

        if (method is null) { Emit(new { ok = false, error = $"未知命令 {cmd}" }); return 2; }

        // 非 act 命令：把命令名也带进参数，供 RpcService 的 ParseAction 识别
        if (method == ProtocolMethods.Act)
            pars = MergeAction(cmd, pars);

        var resp = await rpc.HandleAsync(new RpcRequest { Id = "c", Method = method, Params = pars }).ConfigureAwait(false);
        if (resp.Error is not null) { Emit(new { ok = false, error = resp.Error.Message }); return 1; }
        Emit(resp.Result);
        await devices.DisposeAsync().ConfigureAwait(false);
        return 0;
    }

    // ---------------- 远端（连接常驻服务） ----------------
    private static async Task<int> RemoteAsync(string[] args, int port)
    {
        await using var conn = await TcpConnection.ConnectAsync("127.0.0.1", port).ConfigureAwait(false);
        var cmd = args[0].ToLowerInvariant();
        var method = cmd switch
        {
            "devices" or "ls" => ProtocolMethods.ListDevices,
            "windows" or "wins" => ProtocolMethods.ListWindows,
            "connect" => ProtocolMethods.Connect,
            "observe" => ProtocolMethods.Observe,
            "screenshot" or "shot" => ProtocolMethods.Screenshot,
            "vision" => ProtocolMethods.VisionAnalyze,
            "find" => ProtocolMethods.Find,
            "click" or "double" or "right" or "longpress" or "move" or "type" or "key"
                or "scroll" or "syskey" or "app" or "shell" or "drag" or "swipe" or "activate" => ProtocolMethods.Act,
            "wait" => ProtocolMethods.Wait,
            "flow" => ProtocolMethods.RunFlow,
            "play" => ProtocolMethods.MediaPlay,
            "volume" => ProtocolMethods.MediaVolume,
            "audio" or "audiocheck" => ProtocolMethods.MediaDiag,
            "audio-default" or "setaudio" => ProtocolMethods.MediaSetDefault,
            "stt" or "transcribe" => ProtocolMethods.SpeechTranscribe,
            "record" => ProtocolMethods.MediaRecord,
            "rec-start" => ProtocolMethods.RecordStart,
            "rec-stop" => ProtocolMethods.RecordStop,
            "replay" => ProtocolMethods.Replay,
            "ping" => ProtocolMethods.Ping,
            _ => null,
        };
        if (method is null) { Emit(new { ok = false, error = $"未知命令 {cmd}" }); return 2; }

        var pars = BuildParams(args);
        if (method == ProtocolMethods.Act) pars = MergeAction(cmd, pars);

        var req = new RpcRequest { Id = "c", Method = method, Params = pars };
        await conn.SendAsync(ChannelKind.Control, WireCodec.EncodeJson(req)).ConfigureAwait(false);
        var data = await conn.ReceiveAsync().ConfigureAwait(false);
        if (data is null) { Emit(new { ok = false, error = "无响应" }); return 1; }
        var doc = JsonDocument.Parse(data.Value);
        Emit(doc.RootElement.TryGetProperty("result", out var r) ? r : doc.RootElement);
        return 0;
    }

    /// <summary>把动词名合入参数（RpcService.ParseAction 读取 "action" 字段）。</summary>
    private static JsonElement? MergeAction(string cmd, JsonElement? pars)
    {
        var dict = new Dictionary<string, object?> { ["action"] = cmd };
        if (pars is { } e && e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
                dict[p.Name] = p.Value.Clone();

        // type 的 --text 是「要输入的文本」，不是选择器；转成 value，避免被当作文字目标
        if (cmd == "type" && dict.TryGetValue("text", out var tv) && tv is not null)
        {
            dict["value"] = tv;
            dict.Remove("text");
        }

        var json = JsonSerializer.Serialize(dict, GuioJson.Options);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>把命令行选项解析成 JSON 参数对象。</summary>
    private static JsonElement? BuildParams(string[] args)
    {
        var dict = new Dictionary<string, object?>();
        for (int i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--")) continue;
            var key = a[2..];
            string? val = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";

            switch (key)
            {
                case "device": dict["device"] = val; break;
                case "filter": dict["filter"] = val; break;
                case "model": dict["model"] = val; break;
                case "language": dict["language"] = val; break;
                case "prompt": dict["prompt"] = val; break;
                case "python": dict["python"] = val; break;
                case "id": dict["id"] = val; break;
                case "timestamps": dict["timestamps"] = bool.Parse(val!); break;
                case "x": dict["x"] = int.Parse(val!); break;
                case "y": dict["y"] = int.Parse(val!); break;
                case "nx": dict["nx"] = double.Parse(val!); break;
                case "ny": dict["ny"] = double.Parse(val!); break;
                case "text" or "screenText": dict[key] = val; break;
                case "value": dict["value"] = val; break;
                case "image": dict["image"] = val; break;
                case "out": dict["out"] = val; break;
                case "unicode": dict["unicode"] = bool.Parse(val!); break;
                case "clear": dict["clear"] = bool.Parse(val!); break;
                case "holdMs": dict["holdMs"] = double.Parse(val!); break;
                case "count": dict["count"] = int.Parse(val!); break;
                case "dy": dict["dy"] = int.Parse(val!); break;
                case "dx": dict["dx"] = int.Parse(val!); break;
                case "key": dict["key"] = val; break;
                case "modifiers": dict["modifiers"] = val!.Split(',', StringSplitOptions.RemoveEmptyEntries); break;
                case "op": dict["op"] = val; break;
                case "target": dict["target"] = val; break;
                case "command": dict["command"] = val; break;
                case "goal": dict["goal"] = val; break;
                case "timeoutMs": dict["timeoutMs"] = int.Parse(val!); break;
                case "volume": dict["volume"] = double.Parse(val!); break;
                case "seconds": dict["seconds"] = int.Parse(val!); break;
                case "file": dict["file"] = val; break;
                case "name": dict["name"] = val; break;
                case "speed": dict["speed"] = double.Parse(val!); break;
                case "rescale": dict["rescale"] = bool.Parse(val!); break;
                case "frame": dict["frame"] = bool.Parse(val!); break;
                case "tree": dict["tree"] = bool.Parse(val!); break;
                case "ocr": dict["ocr"] = bool.Parse(val!); break;
                case "norm":
                {
                    var parts = val!.Split(',');
                    if (parts.Length == 2) dict["point"] = new { norm = new[] { double.Parse(parts[0]), double.Parse(parts[1]) } };
                    break;
                }
                case "agent": break; // 已单独处理
                default: dict[key] = val; break;
            }
        }

        // x/y 组合成 point
        if (dict.ContainsKey("x") && dict.ContainsKey("y"))
        {
            var x = dict["x"]; var y = dict["y"];
            dict.Remove("x"); dict.Remove("y");
            dict["point"] = new { x, y };
        }
        if (dict.ContainsKey("nx") && dict.ContainsKey("ny"))
        {
            var x = dict["nx"]; var y = dict["ny"];
            dict.Remove("nx"); dict.Remove("ny");
            dict["point"] = new { norm = new[] { x, y } };
        }

        if (dict.Count == 0) return null;
        var json = JsonSerializer.Serialize(dict, GuioJson.Options);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static int OptInt(string[] args, string name, int def)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name && int.TryParse(args[i + 1], out var v)) return v;
        return def;
    }

    private static void Emit(object? o)
    {
        var json = o is JsonElement el
            ? el.GetRawText()
            : JsonSerializer.Serialize(o, GuioJson.Options);
        Console.WriteLine(json);
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"GuIO — 通用自动化工具（Windows / Android 同一契约）

设备:
  guio devices                                列出设备
  guio connect [--device win://app/记事本]    接入设备

感知:
  guio observe [--ocr true] [--tree true]     观测（语义树/文字/帧）
  guio screenshot --out shot.png              截屏保存
  guio vision [--image shot.png] 本地视觉模型识图
  guio find --text ""确定""                     查找元素（打印命中坐标）

动作（目标用 --x/--y、--nx/--ny、--text、--image、--device 组合）:
  guio click  --text ""开始""                   按文字点击
  guio click  --x 100 --y 200                 按坐标点击
  guio double --text ""文件""                   双击
  guio right  --text ""项目""                   右键
  guio longpress --x 100 --y 200              长按
  guio type   --text ""确定"" --unicode true    输入文本（支持中文）
  guio key    --key Enter --modifiers Ctrl     按键/组合键
  guio scroll --dy -3                          滚动
  guio drag   --x 10 --y 10 --target 200,200   拖拽（--target x,y）
  guio syskey --key Back                       系统键（Back/Home/Recents）
  guio app    --op launch --target notepad     启动应用
  guio shell  --command ""dir""                 执行命令

闭环:
  guio wait   --text ""完成"" --timeoutMs 10000 等待条件成立

媒体与语音:
  guio audio                                  音频诊断（端点/会话/谁在占用）
  guio stt --file a.wav --model medium        音频转文字（本地 Whisper）
  guio volume --volume 0.5                    设置音量（省略则读取）
  guio play   --path a.wav                     播放音频
  guio record --out rec.wav --seconds 3        录制系统正在播放的声音

服务:
  guio-agent serve --port 0                    启动常驻服务
  guio <cmd> --agent <port>                    通过常驻服务执行
");
    }
}
