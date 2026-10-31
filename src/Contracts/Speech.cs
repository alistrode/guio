namespace GuIO.Contracts;

/// <summary>一段转写结果（带时间戳）。</summary>
public sealed record TranscriptSegment
{
    [System.Text.Json.Serialization.JsonPropertyName("start")]
    public required double StartSec { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("end")]
    public required double EndSec { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("text")]
    public required string Text { get; init; }
}

/// <summary>一次转写的完整结果。</summary>
public sealed record Transcript
{
    [System.Text.Json.Serialization.JsonPropertyName("file")]
    public required string File { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("language")] public string? Language { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("model")] public string? Model { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("duration")] public double DurationSec { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("text")] public required string Text { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("segments")] public IReadOnlyList<TranscriptSegment> Segments { get; init; } = Array.Empty<TranscriptSegment>();
    [System.Text.Json.Serialization.JsonPropertyName("elapsedMs")] public long ElapsedMs { get; init; }
}

/// <summary>语音转文字引擎（音频 → 文本）。桌面与手机皆可实现。</summary>
public interface ISpeechTranscriber
{
    bool IsAvailable { get; }
    string Backend { get; }
    Task<GuioResult<Transcript>> TranscribeAsync(TranscribeOptions options, CancellationToken ct = default);
}

public sealed record TranscribeOptions
{
    /// <summary>音频文件路径（wav/mp3/m4a 等，取决于后端）。</summary>
    public required string File { get; init; }
    /// <summary>语言，如 zh / en；null 为自动检测。</summary>
    public string? Language { get; init; }
    /// <summary>模型规模：tiny/base/small/medium/large。</summary>
    public string Model { get; init; } = "base";
    /// <summary>提示词，可显著改善专有名词与简繁。</summary>
    public string? InitialPrompt { get; init; }
    /// <summary>是否输出逐段时间戳。</summary>
    public bool Timestamps { get; init; } = true;
}
