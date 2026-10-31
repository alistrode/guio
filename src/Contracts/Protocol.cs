using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuIO.Contracts;

/// <summary>协议方法名（单一出口契约：CLI / MCP / GUI / 遥控共用）。</summary>
public static class ProtocolMethods
{
    public const string ListDevices = "devices.list";
    public const string ListWindows = "windows.list";
    public const string DescribeDevice = "devices.describe";
    public const string Connect = "session.connect";
    public const string Disconnect = "session.disconnect";
    public const string Observe = "perception.observe";
    public const string Screenshot = "perception.screenshot";
    public const string VisionAnalyze = "perception.visionAnalyze";
    public const string Find = "perception.find";
    public const string Act = "action.perform";
    public const string Wait = "flow.wait";
    public const string RunFlow = "flow.run";
    public const string RecordStart = "record.start";
    public const string RecordStop = "record.stop";
    public const string Replay = "record.replay";
    public const string MediaPlay = "media.play";
    public const string MediaRecord = "media.recordLoopback";
    public const string MediaVolume = "media.volume";
    public const string MediaDiag = "media.diag";
    public const string MediaSetDefault = "media.setDefaultOutput";
    public const string SpeechTranscribe = "speech.transcribe";
    public const string Ping = "system.ping";
}

/// <summary>JSON-RPC 2.0 请求。</summary>
public sealed record RpcRequest
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; init; } = "2.0";
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("method")] public required string Method { get; init; }
    [JsonPropertyName("params")] public JsonElement? Params { get; init; }
}

/// <summary>JSON-RPC 2.0 响应。</summary>
public sealed record RpcResponse
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; init; } = "2.0";
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("result")] public JsonElement? Result { get; init; }
    [JsonPropertyName("error")] public RpcError? Error { get; init; }

    public static RpcResponse Ok(string? id, object? result, JsonSerializerOptions opts) =>
        new() { Id = id, Result = result is null ? null : JsonSerializer.SerializeToElement(result, opts) };

    public static RpcResponse Fail(string? id, int code, string message) =>
        new() { Id = id, Error = new RpcError(code, message) };
}

public sealed record RpcError(int Code, string Message);

/// <summary>统一序列化选项（源生成友好、AOT 友好）。</summary>
public static class GuioJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
