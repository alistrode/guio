using System.Diagnostics;
using System.Text.Json;
using GuIO.Contracts;

namespace GuIO.Speech;

/// <summary>
/// 基于本地 Whisper 的语音转文字。复用用户机器上已有的 Python 环境与已缓存模型
/// （~/.cache/whisper/*.pt），不重新下载。
///
/// 工作方式：把转写请求交给一个内嵌的 Python 脚本，用 librosa 解码（绕过 ffmpeg 依赖），
/// 再调 whisper.transcribe，最后以 JSON 回传。这样既零重复下载，又避免把重型
/// 推理运行时塞进 .NET 进程。
/// </summary>
public sealed class WhisperTranscriber : ISpeechTranscriber
{
    private readonly string _pythonPath;
    private readonly string _scriptPath;

    /// <summary>默认使用本地已就绪的 venv_cosy 环境。</summary>
    public const string DefaultPython = @"D:\rts_cmd\venv_cosy\Scripts\python.exe";

    public WhisperTranscriber(string? pythonPath = null, string? scriptPath = null)
    {
        _pythonPath = pythonPath ?? DefaultPython;
        _scriptPath = scriptPath ?? Path.Combine(Path.GetTempPath(), "guio_whisper_bridge.py");
    }

    public string Backend => "whisper(local)";

    public bool IsAvailable => File.Exists(_pythonPath);

    public async Task<GuioResult<Transcript>> TranscribeAsync(TranscribeOptions options, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        if (!IsAvailable)
            return GuioResult<Transcript>.Fail(ErrorCode.Unsupported,
                $"未找到 Python 环境：{_pythonPath}（可传入自定义路径）");
        if (!File.Exists(options.File))
            return GuioResult<Transcript>.Fail(ErrorCode.InvalidArgument, $"音频文件不存在：{options.File}");

        try
        {
            WriteBridgeScript();
            var args = new List<string>
            {
                _scriptPath,
                options.File,
                options.Model,
                options.Language ?? "",
                options.InitialPrompt ?? "",
                options.Timestamps ? "1" : "0",
            };

            var psi = new ProcessStartInfo
            {
                FileName = _pythonPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi)!;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (proc.ExitCode != 0)
                return GuioResult<Transcript>.Fail(ErrorCode.Internal,
                    $"whisper 退出码 {proc.ExitCode}: {Trim(stderr, 400)}");

            // 脚本约定：最后一行是 JSON 结果
            var json = LastJsonLine(stdout);
            if (json is null)
                return GuioResult<Transcript>.Fail(ErrorCode.Internal, $"无法解析转写输出: {Trim(stdout, 300)}");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var segments = new List<TranscriptSegment>();
            if (root.TryGetProperty("segments", out var segs) && segs.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in segs.EnumerateArray())
                {
                    segments.Add(new TranscriptSegment
                    {
                        StartSec = s.GetProperty("start").GetDouble(),
                        EndSec = s.GetProperty("end").GetDouble(),
                        Text = s.GetProperty("text").GetString() ?? "",
                    });
                }
            }

            var transcript = new Transcript
            {
                File = options.File,
                Language = root.TryGetProperty("language", out var lg) ? lg.GetString() : options.Language,
                Model = options.Model,
                DurationSec = root.TryGetProperty("duration", out var du) ? du.GetDouble() : 0,
                Text = root.TryGetProperty("text", out var tx) ? (tx.GetString() ?? "") : "",
                Segments = segments,
                ElapsedMs = sw.ElapsedMilliseconds,
            };
            return GuioResult<Transcript>.Success(transcript, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return GuioResult<Transcript>.Fail(ErrorCode.Internal, ex.Message);
        }
    }

    private static string? LastJsonLine(string stdout)
    {
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var t = lines[i].Trim();
            if (t.StartsWith("{") && t.EndsWith("}")) return t;
        }
        return null;
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private void WriteBridgeScript()
    {
        // 只在文件缺失时写入，避免每次覆盖
        if (File.Exists(_scriptPath)) return;
        File.WriteAllText(_scriptPath, BridgeScript, new System.Text.UTF8Encoding(false));
    }

    /// <summary>
    /// Python 桥脚本：用 librosa 解码音频（不依赖 ffmpeg），调 whisper 转写，输出 JSON。
    /// </summary>
    private const string BridgeScript = """
# -*- coding: utf-8 -*-
import sys, json, io
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.stderr.reconfigure(encoding="utf-8", errors="replace")

def main():
    if len(sys.argv) < 3:
        print("usage: bridge <file> <model> [lang] [prompt] [timestamps]", file=sys.stderr)
        return 2
    path = sys.argv[1]
    model_name = sys.argv[2] or "base"
    lang = sys.argv[3] if len(sys.argv) > 3 and sys.argv[3] else None
    prompt = sys.argv[4] if len(sys.argv) > 4 and sys.argv[4] else None
    with_ts = (len(sys.argv) > 5 and sys.argv[5] == "1")

    import os
    os.environ["TQDM_DISABLE"] = "1"
    import librosa
    import whisper
    import numpy as np
    # librosa 解码为 16k 单声道，绕开 ffmpeg
    audio, sr = librosa.load(path, sr=16000, mono=True)
    duration = float(len(audio) / sr)

    model = whisper.load_model(model_name)
    kwargs = {"fp16": False}
    if lang: kwargs["language"] = lang
    if prompt: kwargs["initial_prompt"] = prompt
    result = model.transcribe(audio, verbose=False, **kwargs)

    segments = []
    if with_ts:
        for s in result.get("segments", []):
            segments.append({
                "start": round(float(s["start"]), 2),
                "end": round(float(s["end"]), 2),
                "text": s["text"].strip(),
            })
    out = {
        "language": result.get("language", lang or ""),
        "duration": round(duration, 2),
        "text": " ".join(s["text"].strip() for s in result.get("segments", [])).strip(),
        "segments": segments,
    }
    print(json.dumps(out, ensure_ascii=False))
    return 0

if __name__ == "__main__":
    sys.exit(main())
""";
}
