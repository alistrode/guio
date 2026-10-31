using Android.App;
using Android.Content;
using Android.Media;
using GuIO.Contracts;
using ErrorCode = GuIO.Contracts.ErrorCode;

namespace GuIO.Driver.Android;

/// <summary>Android 媒体：音量/静音、播放、可选的回环捕获（App 需 opt-in）。</summary>
internal sealed class AndroidMedia : IMediaDevice, IDisposable
{
    private readonly Context _ctx;
    private readonly AudioManager? _am;
    private MediaPlayer? _player;

    public AndroidMedia(Context ctx)
    {
        _ctx = ctx;
        _am = ctx.GetSystemService(Context.AudioService) as AudioManager;
    }

    public Task<GuioResult<double>> GetVolumeAsync(CancellationToken ct = default)
    {
        try
        {
            if (_am is null) return Task.FromResult(GuioResult<double>.Fail(ErrorCode.Unsupported, "无 AudioManager"));
            int max = _am.GetStreamMaxVolume(global::Android.Media.Stream.Music);
            int cur = _am.GetStreamVolume(global::Android.Media.Stream.Music);
            return Task.FromResult(GuioResult<double>.Success(max <= 0 ? 0 : (double)cur / max));
        }
        catch (Exception ex) { return Task.FromResult(GuioResult<double>.Fail(ErrorCode.Internal, ex.Message)); }
    }

    public Task<GuioResult<bool>> SetVolumeAsync(double volume, CancellationToken ct = default)
    {
        try
        {
            if (_am is null) return Task.FromResult(GuioResult<bool>.Fail(ErrorCode.Unsupported, "无 AudioManager"));
            int max = _am.GetStreamMaxVolume(global::Android.Media.Stream.Music);
            int v = (int)Math.Round(Math.Clamp(volume, 0, 1) * max);
            _am.SetStreamVolume(global::Android.Media.Stream.Music, v, VolumeNotificationFlags.RemoveSoundAndVibrate);
            return Task.FromResult(GuioResult<bool>.Success(true));
        }
        catch (Exception ex) { return Task.FromResult(GuioResult<bool>.Fail(ErrorCode.Internal, ex.Message)); }
    }

    public Task<GuioResult<bool>> SetMuteAsync(bool mute, CancellationToken ct = default)
    {
        try
        {
            if (_am is null) return Task.FromResult(GuioResult<bool>.Fail(ErrorCode.Unsupported, "无 AudioManager"));
            _am.AdjustStreamVolume(global::Android.Media.Stream.Music, mute ? Adjust.Raise : Adjust.Lower, VolumeNotificationFlags.RemoveSoundAndVibrate);
            // 使用 Adjust 的 Mute 语义依赖于 API；此处用 RingerMode 兜底
            _am.RingerMode = mute ? RingerMode.Silent : RingerMode.Normal;
            return Task.FromResult(GuioResult<bool>.Success(true));
        }
        catch (Exception ex) { return Task.FromResult(GuioResult<bool>.Fail(ErrorCode.Internal, ex.Message)); }
    }

    public Task<GuioResult<bool>> PlayAsync(string path, double volume = 1.0, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            try
            {
                _player ??= new MediaPlayer();
                _player.Reset();
                _player.SetDataSource(path);
                _player.SetVolume((float)volume, (float)volume);
                _player.Prepare();
                _player.Start();
                return GuioResult<bool>.Success(true);
            }
            catch (Exception ex) { return GuioResult<bool>.Fail(ErrorCode.Internal, ex.Message); }
        }, ct);
    }

    /// <summary>
    /// 回环录音：端侧需 MediaProjection + 目标应用 opt-in AudioPlaybackCapture，
    /// 由宿主实现并注入；未接入时明确返回不支持（能力不对等的显式声明）。
    /// </summary>
    public Func<string, int, Task<GuioResult<string>>>? LoopbackRecorder { get; set; }

    public Task<GuioResult<string>> StartLoopbackRecordAsync(string outPath, int seconds, CancellationToken ct = default)
        => LoopbackRecorder is null
            ? Task.FromResult(GuioResult<string>.Fail(ErrorCode.Unsupported, "端侧回环录音未接入（需 MediaProjection 且被捕获应用允许）"))
            : LoopbackRecorder(outPath, seconds);

    public void Dispose()
    {
        try { _player?.Release(); _player?.Dispose(); } catch { }
    }
}
