using GuIO.Contracts;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GuIO.Driver.Windows;

/// <summary>
/// Windows 媒体能力：播放 / WASAPI 回环录音 / 音量静音。
/// 回环录音用于自动化验证「音效到底响了没有」—— 这是原先工具完全缺失的能力。
/// </summary>
internal sealed class WindowsMedia : IMediaDevice, IDisposable
{
    private WasapiLoopbackCapture? _loopback;
    private WaveFileWriter? _writer;
    private TaskCompletionSource<string>? _recordDone;
    private readonly MMDeviceEnumerator _enumerator = new();

    // ---- 默认播放设备切换（CoreAudio IPolicyConfig）----
    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class CPolicyConfigClient { }

    [System.Runtime.InteropServices.Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        int GetMixFormat(string a, IntPtr b);
        int GetDeviceFormat(string a, bool b, IntPtr c);
        int ResetDeviceFormat(string a);
        int SetDeviceFormat(string a, IntPtr b, IntPtr c);
        int GetProcessingPeriod(string a, bool b, IntPtr c, IntPtr d);
        int SetProcessingPeriod(string a, IntPtr b);
        int GetShareMode(string a, IntPtr b);
        int SetShareMode(string a, IntPtr b);
        int GetPropertyValue(string a, IntPtr b, IntPtr c);
        int SetPropertyValue(string a, IntPtr b, IntPtr c);
        int SetDefaultEndpoint([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string deviceId, int role);
        int SetEndpointVisibility(string a, bool b);
    }

    /// <summary>
    /// 把默认播放设备切到指定端点（同时覆盖 控制台/多媒体/通信 三个角色）。
    /// 用于修复「默认输出被切到虚拟声卡导致听不到声音」这类问题。
    /// </summary>
    public GuioResult<bool> SetDefaultOutput(string deviceId)
    {
        try
        {
            var t = Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"))!;
            object obj = Activator.CreateInstance(t)!;
            try
            {
                var pc = (IPolicyConfig)obj;
                int r0 = pc.SetDefaultEndpoint(deviceId, 0);
                int r1 = pc.SetDefaultEndpoint(deviceId, 1);
                int r2 = pc.SetDefaultEndpoint(deviceId, 2);
                if (r0 == 0 || r1 == 0 || r2 == 0) return GuioResult<bool>.Success(true);
                return GuioResult<bool>.Fail(ErrorCode.Internal, $"SetDefaultEndpoint 返回 {r0}/{r1}/{r2}");
            }
            finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(obj); }
        }
        catch (Exception ex) { return GuioResult<bool>.Fail(ErrorCode.Internal, ex.Message); }
    }

    /// <summary>一条音频会话（哪个进程在用音频、音量、是否静音）。</summary>
    public sealed record AudioSessionInfo(int Pid, string Process, string State, double Volume, bool Mute);

    /// <summary>一个音频端点。</summary>
    public sealed record AudioEndpointInfo(string Id, string Name, string State, bool IsDefault);

    /// <summary>音频诊断报告：端点状态 + 所有会话 + 全部端点。</summary>
    public sealed record AudioDiagnostic(string Device, double Volume, bool Mute, int SessionCount,
        IReadOnlyList<AudioSessionInfo> Sessions, IReadOnlyList<AudioEndpointInfo> Endpoints);

    /// <summary>
    /// 枚举当前音频端点与会话。用于诊断「谁占用了音频」「某进程是否被静音」。
    /// 只读，不改变任何状态。
    /// </summary>
    public AudioDiagnostic Diagnose()
    {
        var dev = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = new List<AudioSessionInfo>();
        try
        {
            var mgr = dev.AudioSessionManager;
            mgr.RefreshSessions();
            var list = mgr.Sessions;
            for (int i = 0; i < list.Count; i++)
            {
                var sess = list[i];
                string proc = "?";
                try { proc = System.Diagnostics.Process.GetProcessById((int)sess.GetProcessID).ProcessName; } catch { }
                double vol = -1; bool mute = false;
                try { var v = sess.SimpleAudioVolume; vol = v.Volume; mute = v.Mute; } catch { }
                sessions.Add(new AudioSessionInfo((int)sess.GetProcessID, proc, sess.State.ToString(), vol, mute));
            }
        }
        catch { }
        // 枚举所有渲染端点，标出哪个是默认——用于诊断「默认设备被切到虚拟声卡」
        var endpoints = new List<AudioEndpointInfo>();
        string defaultId = "";
        try { defaultId = dev.ID; } catch { }
        try
        {
            var col = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            for (int i = 0; i < col.Count; i++)
            {
                var d = col[i];
                string id = d.ID;
                endpoints.Add(new AudioEndpointInfo(
                    id,
                    d.FriendlyName,
                    d.State.ToString(),
                    string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch { }

        return new AudioDiagnostic(
            dev.FriendlyName,
            dev.AudioEndpointVolume.MasterVolumeLevelScalar,
            dev.AudioEndpointVolume.Mute,
            sessions.Count,
            sessions,
            endpoints);
    }

    public Task<GuioResult<bool>> PlayAsync(string path, double volume = 1.0, CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            try
            {
                if (!File.Exists(path)) return GuioResult<bool>.Fail(ErrorCode.InvalidArgument, $"音频文件不存在：{path}");
                using var reader = new AudioFileReader(path) { Volume = (float)Math.Clamp(volume, 0, 1) };
                using var output = new WaveOutEvent();
                output.Init(reader);
                output.Play();
                while (output.PlaybackState == PlaybackState.Playing && !ct.IsCancellationRequested)
                    await Task.Delay(50, ct);
                return GuioResult<bool>.Success(true);
            }
            catch (Exception ex) { return GuioResult<bool>.Fail(ErrorCode.Internal, ex.Message); }
        }, ct);
    }

    /// <summary>录制「系统正在播放的声音」（WASAPI loopback），固定时长或直到停止。</summary>
    public Task<GuioResult<string>> StartLoopbackRecordAsync(string outPath, int seconds, CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
                _recordDone = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

                var defaultDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _loopback = new WasapiLoopbackCapture(defaultDevice);
                _writer = new WaveFileWriter(outPath, _loopback.WaveFormat);
                _loopback.DataAvailable += (_, e) =>
                {
                    _writer?.Write(e.Buffer, 0, e.BytesRecorded);
                };
                _loopback.RecordingStopped += (_, e) =>
                {
                    try { _writer?.Dispose(); _writer = null; _loopback?.Dispose(); _loopback = null; } catch { }
                    _recordDone?.TrySetResult(outPath);
                };
                _loopback.StartRecording();

                if (seconds > 0)
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(seconds));
                    try { await Task.Delay(Timeout.Infinite, cts.Token); }
                    catch (OperationCanceledException) { }
                    StopLoopback();
                }

                var done = await Task.WhenAny(_recordDone.Task, Task.Delay(Timeout.Infinite, ct));
                if (done == _recordDone.Task) await _recordDone.Task;
                return GuioResult<string>.Success(outPath);
            }
            catch (Exception ex) { return GuioResult<string>.Fail(ErrorCode.Internal, ex.Message); }
        }, ct);
    }

    public void StopLoopback()
    {
        try { _loopback?.StopRecording(); } catch { }
    }

    public Task<GuioResult<double>> GetVolumeAsync(CancellationToken ct = default)
    {
        try
        {
            var dev = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return Task.FromResult(GuioResult<double>.Success(dev.AudioEndpointVolume.MasterVolumeLevelScalar));
        }
        catch (Exception ex) { return Task.FromResult(GuioResult<double>.Fail(ErrorCode.Internal, ex.Message)); }
    }

    public Task<GuioResult<bool>> SetVolumeAsync(double volume, CancellationToken ct = default)
    {
        try
        {
            var dev = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var v = (float)Math.Clamp(volume, 0, 1);
            // 设置非零音量时顺带解除静音：用户「调音量」的意图几乎总是「我要听到声音」。
            // 只改 scalar 而不解静音，会出现「音量 70% 却依然静音」的假象。
            if (v > 0f && dev.AudioEndpointVolume.Mute) dev.AudioEndpointVolume.Mute = false;
            dev.AudioEndpointVolume.MasterVolumeLevelScalar = v;
            return Task.FromResult(GuioResult<bool>.Success(true));
        }
        catch (Exception ex) { return Task.FromResult(GuioResult<bool>.Fail(ErrorCode.Internal, ex.Message)); }
    }

    public Task<GuioResult<bool>> SetMuteAsync(bool mute, CancellationToken ct = default)
    {
        try
        {
            var dev = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            dev.AudioEndpointVolume.Mute = mute;
            return Task.FromResult(GuioResult<bool>.Success(true));
        }
        catch (Exception ex) { return Task.FromResult(GuioResult<bool>.Fail(ErrorCode.Internal, ex.Message)); }
    }

    public void Dispose()
    {
        try { _loopback?.Dispose(); } catch { }
        try { _writer?.Dispose(); } catch { }
        _enumerator.Dispose();
    }
}
