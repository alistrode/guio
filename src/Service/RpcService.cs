using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Http.Json;
using GuIO.Contracts;
using GuIO.Kernel;
using GuIO.Vision;

namespace GuIO.Service;

/// <summary>
/// RPC 服务：把协议方法分派到引擎。这是前后端分离的「后端」唯一对外面。
/// 单一职责 —— 只做「方法 → 引擎调用」的翻译与结果封装，不含业务逻辑。
/// </summary>
public sealed class RpcService
{
    private readonly DeviceManager _devices;
    private readonly IClock _clock;
    private static readonly HttpClient HttpClientSingleton = new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromMinutes(3),
    };

    public RpcService(DeviceManager devices, IClock? clock = null)
    {
        _devices = devices;
        _clock = clock ?? new SystemClock();
    }

    public async Task<RpcResponse> HandleAsync(RpcRequest req, CancellationToken ct = default)
    {
        try
        {
            var result = await DispatchAsync(req, ct).ConfigureAwait(false);
            return RpcResponse.Ok(req.Id, result, GuioJson.Options);
        }
        catch (Exception ex)
        {
            return RpcResponse.Fail(req.Id, -32000, ex.Message);
        }
    }

    private async Task<object?> DispatchAsync(RpcRequest req, CancellationToken ct)
    {
        var p = req.Params;
        switch (req.Method)
        {
            case ProtocolMethods.Ping:
                return new { pong = true, time = _clock.UnixMs };

            case ProtocolMethods.ListDevices:
                return new { devices = _devices.List() };

            case ProtocolMethods.ListWindows:
            {
                var (session, err) = await RequireSession(p, ct);
                if (session is null) return err;
                if (session.Driver is not IWindowEnumerable we)
                    return Fail(ErrorCode.Unsupported, "当前平台不支持窗口枚举");
                var filter = Str(p, "filter");
                var wins = we.ListWindows();
                if (!string.IsNullOrWhiteSpace(filter))
                    wins = wins.Where(w =>
                        w.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        w.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
                return new { count = wins.Count, windows = wins };
            }

            case ProtocolMethods.DescribeDevice:
            {
                var (session, err) = await RequireSession(p, ct);
                if (session is null) return err;
                return session.Descriptor;
            }

            case ProtocolMethods.Connect:
            {
                var uri = Str(p, "device") ?? "";
                var alias = Str(p, "alias");
                var r = uri.Length == 0
                    ? await _devices.ConnectDefaultAsync(ct)
                    : await _devices.ConnectAsync(uri, alias, ct);
                if (!r.Ok || r.Value is null) return Fail(r.Error, r.Message);
                return new { connected = true, device = r.Value.Session.Descriptor };
            }

            case ProtocolMethods.Disconnect:
                await _devices.DisconnectAsync(Str(p, "device") ?? "");
                return new { disconnected = true };

            case ProtocolMethods.Observe:
            {
                var (session, err) = await RequireSession(p, ct);
                if (session is null) return err;
                var opts = new ObserveOptions
                {
                    WithFrame = Bool(p, "frame") ?? true,
                    WithTree = Bool(p, "tree") ?? true,
                    WithOcr = Bool(p, "ocr") ?? false,
                };
                var r = await session.ObserveAsync(opts, ct).ConfigureAwait(false);
                if (!r.Ok || r.Value is null) return Fail(r.Error, r.Message);
                var o = r.Value;
                return new
                {
                    device = o.Device.ToString(),
                    frame = o.Frame is null ? null : new { o.Frame.Width, o.Frame.Height },
                    nodes = o.Nodes.Count,
                    text = o.Text.Select(t => new { t.Text, x = t.Bounds.Left, y = t.Bounds.Top, w = t.Bounds.Width, h = t.Bounds.Height }),
                    nodesDetail = o.Nodes.Take(200).Select(n => new { n.Kind, n.Name, n.Text, n.AutomationId, n.Bounds, n.Clickable }),
                };
            }

            case ProtocolMethods.Screenshot:
            {
                var (session, err) = await RequireSession(p, ct);
                if (session is null) return err;
                var r = await session.Driver.Perception.CaptureAsync(null, ct).ConfigureAwait(false);
                if (!r.Ok || r.Value is null) return Fail(r.Error, r.Message);
                var path = Str(p, "out");
                if (!string.IsNullOrEmpty(path))
                {
                    session.Codec.SavePng(r.Value, path);
                    return new { saved = path, r.Value.Width, r.Value.Height };
                }
                var png = session.Codec.EncodePng(r.Value);
                return new { base64 = Convert.ToBase64String(png), r.Value.Width, r.Value.Height };
            }

            case ProtocolMethods.VisionAnalyze:
            {
                var imagePath = Str(p, "image");
                byte[] imageBytes;
                int? width = null, height = null;
                string source;
                if (!string.IsNullOrWhiteSpace(imagePath))
                {
                    var fullPath = Path.GetFullPath(imagePath);
                    var info = new FileInfo(fullPath);
                    if (!info.Exists) return Fail(ErrorCode.InvalidArgument, $"图片不存在：{fullPath}");
                    if (info.Length is <= 0 or > 25_000_000)
                        return Fail(ErrorCode.InvalidArgument, "图片大小必须在 1 字节到 25 MB 之间");
                    imageBytes = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
                    source = fullPath;
                }
                else
                {
                    var (session, err) = await RequireSession(p, ct);
                    if (session is null) return err;
                    var capture = await session.Driver.Perception.CaptureAsync(null, ct).ConfigureAwait(false);
                    if (!capture.Ok || capture.Value is null) return Fail(capture.Error, capture.Message);
                    imageBytes = session.Codec.EncodePng(capture.Value);
                    width = capture.Value.Width;
                    height = capture.Value.Height;
                    source = "screenshot";
                }

                var endpoint = Environment.GetEnvironmentVariable("GUIO_OLLAMA_URL") ?? "http://127.0.0.1:11434";
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var ollamaUri) || !ollamaUri.IsLoopback || ollamaUri.Scheme != Uri.UriSchemeHttp)
                    return Fail(ErrorCode.InvalidArgument, "为保障图片本地处理，GUIO_OLLAMA_URL 只允许指向本机 HTTP 服务");
                var model = Environment.GetEnvironmentVariable("GUIO_VLM_MODEL") ?? "qwen3-vl:4b";
                var prompt = Str(p, "prompt") ?? "请用中文识别并描述这张图片。对于界面截图，列出可读文字和主要控件。控件位置必须用四个整数表示为 [左,上,右,下]，每个值范围 0 到 1000，原点为图片左上角；例如图片中央的框可写 [400,400,600,600]。禁止用 0 到 1 小数、百分数或像素值表示坐标。不确定时说明，不要臆测。";
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ollamaUri, "/api/chat"))
                {
                    Content = JsonContent.Create(new
                    {
                        model,
                        stream = false,
                        messages = new[] { new { role = "user", content = prompt, images = new[] { Convert.ToBase64String(imageBytes) } } },
                        options = new { temperature = 0.1, num_ctx = 4096 },
                    }),
                };
                HttpResponseMessage response;
                try
                {
                    response = await HttpClientSingleton.SendAsync(request, ct).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    return Fail(ErrorCode.Unsupported, $"无法连接本机 Ollama ({ollamaUri}). 请启动 Ollama 桌面服务后重试。详情：{ex.Message}");
                }
                using (response)
                {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return Fail(ErrorCode.Unsupported, $"本地视觉模型调用失败（{(int)response.StatusCode}）：{body}");
                using var result = JsonDocument.Parse(body);
                var analysis = result.RootElement.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content)
                    ? content.GetString() : null;
                return string.IsNullOrWhiteSpace(analysis)
                    ? Fail(ErrorCode.Unsupported, "本地视觉模型没有返回识图结果")
                    : new { ok = true, model, source, width, height, coordinateScale = 1000, analysis };
                }
            }

            case ProtocolMethods.Find:
            {
                var (session, err) = await RequireSession(p, ct);
                if (session is null) return err;
                var sel = ParseSelector(p);
                if (sel is null) return Fail(ErrorCode.InvalidArgument, "缺少选择器参数（node/text/image/point）");
                var r = await session.ResolveAsync(sel, ct).ConfigureAwait(false);
                if (!r.Ok || r.Value is null) return Fail(r.Error, r.Message);
                return new { found = true, via = r.Value.Via.ToString(), x = r.Value.Point.X, y = r.Value.Point.Y, bounds = r.Value.Bounds, score = r.Value.Score };
            }

            case ProtocolMethods.Act:
            {
                var (session, err) = await RequireSession(p, ct);
                if (session is null) return err;
                var action = ParseAction(p);
                if (action is null) return Fail(ErrorCode.InvalidArgument, "无法解析动作参数");
                var r = await session.ExecuteAsync(action, ct).ConfigureAwait(false);
                return r.Ok ? new { ok = true, elapsedMs = r.ElapsedMs } : Fail(r.Error, r.Message);
            }

            case ProtocolMethods.Wait:
            {
                var (session, err) = await RequireSession(p, ct);
                if (session is null) return err;
                var goal = ParseGoal(p);
                if (goal is null) return Fail(ErrorCode.InvalidArgument, "缺少等待条件");
                var loop = new IntentLoop(_clock);
                var outcome = await loop.RunAsync(session, goal, new PassiveLaw(),
                    new LoopOptions { TimeoutMs = Int(p, "timeoutMs") ?? 10000 }, ct).ConfigureAwait(false);
                return new { converged = outcome.Converged, attempts = outcome.Attempts, elapsedMs = outcome.ElapsedMs, error = outcome.Error.ToString() };
            }

            case ProtocolMethods.RunFlow:
            {
                var (session, err) = await RequireSession(p, ct);
                if (session is null) return err;
                var flowJson = Str(p, "flow");
                Flow? flow = null;
                if (!string.IsNullOrEmpty(flowJson)) flow = FlowSerializer.FromJson(flowJson);
                else if (p is { } pp && pp.TryGetProperty("steps", out _)) flow = FlowSerializer.FromJson(p.Value.GetRawText());
                if (flow is null) return Fail(ErrorCode.InvalidArgument, "无法解析流程");
                var engine = new FlowEngine(session, _clock);
                var outcome = await engine.RunAsync(flow, ct).ConfigureAwait(false);
                return new { ok = outcome.Ok, steps = outcome.StepsExecuted, elapsedMs = outcome.ElapsedMs, error = outcome.Error.ToString(), failedStep = outcome.FailedStepId };
            }

            case ProtocolMethods.RecordStart:
            {
                var (session, err) = await RequireSession(p, ct);
                if (session is null) return err;
                session.Recorder.Start(Str(p, "name") ?? "recording", session.Descriptor.Id.ToString());
                return new { recording = true };
            }

            case ProtocolMethods.RecordStop:
            {
                var (session, err) = await RequireSession(p, ct);
                if (session is null) return err;
                var rec = session.Recorder.Stop();
                var path = Str(p, "out");
                if (!string.IsNullOrEmpty(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                    File.WriteAllText(path, Recorder.ToJson(rec));
                    return new { ok = true, file = path, steps = rec.Steps.Count, durationMs = rec.DurationMs };
                }
                return new { ok = true, steps = rec.Steps.Count, durationMs = rec.DurationMs };
            }

            case ProtocolMethods.Replay:
            {
                var (session, err) = await RequireSession(p, ct);
                if (session is null) return err;
                Recording? rec = null;
                var inline = Str(p, "recording");
                var path = Str(p, "file");
                if (!string.IsNullOrEmpty(inline)) rec = Recorder.FromJson(inline);
                else if (!string.IsNullOrEmpty(path) && File.Exists(path)) rec = Recorder.FromJson(File.ReadAllText(path));
                if (rec is null) return Fail(ErrorCode.InvalidArgument, "缺少录制内容");
                var replayer = new Replayer(session, _clock);
                var outcome = await replayer.ReplayAsync(rec, Num(p, "speed") ?? 1.0, Bool(p, "rescale") ?? false, null, ct).ConfigureAwait(false);
                return new { ok = outcome.Ok, executed = outcome.Executed, failed = outcome.Failed, elapsedMs = outcome.ElapsedMs, message = outcome.Message };
            }

            case ProtocolMethods.MediaPlay:
            {
                var (session, err) = await RequireSession(p, ct);
                if (session is null) return err;
                var r = await session.Driver.Media.PlayAsync(Str(p, "path") ?? "", Num(p, "volume") ?? 1.0, ct).ConfigureAwait(false);
                return r.Ok ? new { ok = true } : Fail(r.Error, r.Message);
            }

            case ProtocolMethods.MediaSetDefault:
            {
                var (session, err) = await RequireSession(p, ct);
                if (session is null) return err;
                if (session.Driver is not IAudioDiagnosable ad)
                    return Fail(ErrorCode.Unsupported, "当前平台不支持切换音频设备");
                var id = Str(p, "id");
                if (string.IsNullOrWhiteSpace(id)) return Fail(ErrorCode.InvalidArgument, "缺少 id 参数");
                var r = ad.SetDefaultOutput(id!);
                return r.Ok ? new { ok = true, device = id } : Fail(r.Error, r.Message);
            }

            case ProtocolMethods.SpeechTranscribe:
            {
                var file = Str(p, "file");
                if (string.IsNullOrWhiteSpace(file)) return Fail(ErrorCode.InvalidArgument, "缺少 file 参数");
                var transcriber = new GuIO.Speech.WhisperTranscriber(Str(p, "python"));
                var opts = new TranscribeOptions
                {
                    File = file!,
                    Language = Str(p, "language"),
                    Model = Str(p, "model") ?? "base",
                    InitialPrompt = Str(p, "prompt"),
                    Timestamps = Bool(p, "timestamps") ?? true,
                };
                var r = await transcriber.TranscribeAsync(opts, ct).ConfigureAwait(false);
                if (!r.Ok || r.Value is null) return Fail(r.Error, r.Message);
                return r.Value;
            }

            case ProtocolMethods.MediaDiag:
            {
                var (session, err) = await RequireSession(p, ct);
                if (session is null) return err;
                if (session.Driver is not IAudioDiagnosable ad)
                    return Fail(ErrorCode.Unsupported, "当前平台不支持音频诊断");
                return ad.DiagnoseAudio();
            }

            case ProtocolMethods.MediaVolume:
            {
                var (session, err) = await RequireSession(p, ct);
                if (session is null) return err;
                if ((Num(p, "value") ?? Num(p, "volume")) is { } v)
                {
                    var r = await session.Driver.Media.SetVolumeAsync(v, ct).ConfigureAwait(false);
                    return r.Ok ? new { ok = true, volume = v } : Fail(r.Error, r.Message);
                }
                var g = await session.Driver.Media.GetVolumeAsync(ct).ConfigureAwait(false);
                return g.Ok ? new { volume = g.Value } : Fail(g.Error, g.Message);
            }

            case ProtocolMethods.MediaRecord:
            {
                var (session, err) = await RequireSession(p, ct);
                if (session is null) return err;
                var r = await session.Driver.Media.StartLoopbackRecordAsync(
                    Str(p, "out") ?? Path.Combine(Path.GetTempPath(), $"guio_rec_{_clock.UnixMs}.wav"),
                    Int(p, "seconds") ?? 3, ct).ConfigureAwait(false);
                return r.Ok ? new { ok = true, file = r.Value } : Fail(r.Error, r.Message);
            }

            default:
                return Fail(ErrorCode.Unsupported, $"未知方法 {req.Method}");
        }
    }

    private async Task<(IDeviceSession? session, object? error)> RequireSession(JsonElement? p, CancellationToken ct)
    {
        var uri = Str(p, "device");
        DeviceEntry? entry = null;

        if (!string.IsNullOrEmpty(uri) && DeviceId.TryParse(uri, out var id))
        {
            entry = _devices.Scheduler.Get(id);
            if (entry is null)
            {
                // 指定了尚未接入的设备：自动接入
                var att = await _devices.ConnectAsync(uri, null, ct).ConfigureAwait(false);
                if (!att.Ok || att.Value is null) return (null, Fail(att.Error, att.Message));
                entry = att.Value;
            }
        }

        if (entry is null)
        {
            entry = _devices.Scheduler.Devices.FirstOrDefault();
            if (entry is null)
            {
                var c = await _devices.ConnectDefaultAsync(ct).ConfigureAwait(false);
                if (!c.Ok || c.Value is null) return (null, Fail(c.Error, c.Message));
                entry = c.Value;
            }
        }
        return (entry.Session, null);
    }

    private static object Fail(ErrorCode code, string? message) => new { ok = false, error = code.ToString(), message };

    // ---------- 参数解析 ----------
    private static string? Str(JsonElement? p, string name)
        => p is { } e && e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement? p, string name)
        => p is { } e && e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : null;

    private static double? Num(JsonElement? p, string name)
        => p is { } e && e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : null;

    private static bool? Bool(JsonElement? p, string name)
        => p is { } e && e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;

    /// <summary>从参数解析选择器：支持 node/text/screentext/image/point 五形态。</summary>
    public static Selector? ParseSelector(JsonElement? p)
    {
        if (p is not { } e || e.ValueKind != JsonValueKind.Object) return null;

        if (e.TryGetProperty("selector", out var selEl) && selEl.ValueKind == JsonValueKind.Object) return ParseSelector(selEl);

        if (e.TryGetProperty("node", out var n) && n.ValueKind == JsonValueKind.Object)
        {
            var id = n.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var name = n.TryGetProperty("name", out var nm) ? nm.GetString() : null;
            return Selector.Node(id ?? "", name);
        }
        if (e.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            return Selector.Text(t.GetString()!);
        if (e.TryGetProperty("screenText", out var st) && st.ValueKind == JsonValueKind.String)
            return Selector.ScreenText(st.GetString()!);
        if (e.TryGetProperty("image", out var im) && im.ValueKind == JsonValueKind.String)
        {
            double thr = e.TryGetProperty("threshold", out var th) && th.TryGetDouble(out var tv) ? tv : 0.85;
            return Selector.Image(im.GetString()!, thr);
        }
        if (e.TryGetProperty("point", out var pt) && pt.ValueKind == JsonValueKind.Object)
        {
            if (pt.TryGetProperty("norm", out var norm) && norm.ValueKind == JsonValueKind.Array && norm.GetArrayLength() == 2)
                return Selector.AtNorm(norm[0].GetDouble(), norm[1].GetDouble());
            if (pt.TryGetProperty("x", out var px) && pt.TryGetProperty("y", out var py))
                return Selector.At(new PixelPoint(px.GetInt32(), py.GetInt32()));
        }
        // 顶层直接 x/y
        if (e.TryGetProperty("x", out var x) && e.TryGetProperty("y", out var y))
            return Selector.At(new PixelPoint(x.GetInt32(), y.GetInt32()));
        return null;
    }

    /// <summary>从参数解析动作：{"action":"click","node":{...}} 或 {"action":"type","text":"..."} 等。</summary>
    public static GuioAction? ParseAction(JsonElement? p)
    {
        if (p is not { } e || e.ValueKind != JsonValueKind.Object) return null;
        var kind = e.TryGetProperty("action", out var k) ? k.GetString()?.ToLowerInvariant() : null;
        if (kind is null && e.TryGetProperty("type", out var k2)) kind = k2.GetString()?.ToLowerInvariant();

        var target = ParseSelector(p);
        switch (kind)
        {
            case "click":
                return new ClickAction
                {
                    Target = target ?? throw new ArgumentException("click 需要目标"),
                    Count = e.TryGetProperty("count", out var c) ? c.GetInt32() : 1,
                    HoldMs = e.TryGetProperty("holdMs", out var h) ? h.GetDouble() : 0,
                };
            case "double":
                return new ClickAction { Target = target!, Count = 2 };
            case "right":
                return new ClickAction { Target = target!, Button = PointerButton.Right };
            case "longpress":
                return new ClickAction { Target = target!, HoldMs = e.TryGetProperty("holdMs", out var h2) ? h2.GetDouble() : 800 };
            case "move":
                return new MoveAction { Target = target! };
            case "type":
                return new TypeTextAction
                {
                    Text = e.TryGetProperty("value", out var v) ? v.GetString()! : "",
                    Target = target,
                    ForceUnicode = e.TryGetProperty("unicode", out var u) && u.GetBoolean(),
                    ClearFirst = e.TryGetProperty("clear", out var cl) && cl.GetBoolean(),
                };
            case "key":
            {
                var key = e.TryGetProperty("key", out var kv) ? kv.GetString()! : "";
                var mods = new List<string>();
                if (e.TryGetProperty("modifiers", out var m) && m.ValueKind == JsonValueKind.Array)
                    foreach (var x in m.EnumerateArray()) mods.Add(x.GetString()!);
                return new KeyAction { Key = key, Modifiers = mods, Repeat = e.TryGetProperty("repeat", out var rp) ? rp.GetInt32() : 1 };
            }
            case "scroll":
                return new ScrollAction
                {
                    At = target,
                    DeltaX = e.TryGetProperty("dx", out var dx) ? dx.GetInt32() : 0,
                    DeltaY = e.TryGetProperty("dy", out var dy) ? dy.GetInt32() : -3,
                };
            case "syskey":
            {
                var sk = e.TryGetProperty("key", out var skv) ? skv.GetString()! : "back";
                return new SystemKeyAction { Key = Enum.Parse<SystemKey>(sk, true) };
            }
            case "app":
            {
                var op = e.TryGetProperty("op", out var opv) ? opv.GetString()! : "launch";
                var t = e.TryGetProperty("target", out var tv) ? tv.GetString() : null;
                return new AppAction { Op = Enum.Parse<AppOp>(op, true), Target = t };
            }
            case "activate":
                return new ActivateAction();
            case "shell":
                return new ShellAction { Command = e.TryGetProperty("command", out var cmd) ? cmd.GetString()! : "" };
            default:
                return null;
        }
    }

    /// <summary>从参数解析目标条件：goal=found/gone/changed/stable。</summary>
    public static Goal? ParseGoal(JsonElement? p)
    {
        if (p is not { } e || e.ValueKind != JsonValueKind.Object) return null;
        var kind = e.TryGetProperty("goal", out var g) ? g.GetString()?.ToLowerInvariant() : "found";
        var sel = ParseSelector(p);
        double tol = e.TryGetProperty("tolerance", out var t) && t.TryGetDouble(out var tv) ? tv : 6.0;
        return kind switch
        {
            "found" => sel is null ? null : Goal.Found(sel),
            "gone" => sel is null ? null : Goal.Gone(sel),
            "changed" => Goal.Changed(tol),
            "stable" => Goal.Stable(tol),
            _ => sel is null ? null : Goal.Found(sel),
        };
    }
}
