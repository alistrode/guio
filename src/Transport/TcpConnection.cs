using System.Net;
using System.Net.Sockets;
using System.Text;
using GuIO.Contracts;

namespace GuIO.Transport;

/// <summary>一条连接上的传输通道：控制 / 视频流 / 事件。</summary>
public enum ChannelKind { Control = 0, Video = 1, Event = 2 }

/// <summary>连接抽象。只管收发帧，不管业务语义。</summary>
public interface IConnection : IAsyncDisposable
{
    bool IsConnected { get; }
    Task SendAsync(ChannelKind channel, ReadOnlyMemory<byte> payload, CancellationToken ct = default);
    Task<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken ct = default);
    event Action<ChannelKind, ReadOnlyMemory<byte>>? OnMessage;
}

/// <summary>
/// 跨机 WebSocket 连接（JSON 控制 + 二进制流）。
/// 本机场景用 TCP loopback；远程遥控手机使用同一协议。
/// </summary>
public sealed class TcpConnection : IConnection
{
    private readonly TcpClient _client;
    private NetworkStream _stream;
    private readonly object _sendGate = new();
    private bool _loopStarted;

    public event Action<ChannelKind, ReadOnlyMemory<byte>>? OnMessage;
    public bool IsConnected => _client.Connected;

    public TcpConnection(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;
        _stream = client.GetStream();
    }

    /// <summary>启动基于事件回调的接收循环。与 ReceiveAsync 互斥：二选一，禁止双读者。</summary>
    public void StartReceiveLoop()
    {
        if (_loopStarted) return;
        _loopStarted = true;
        _ = Task.Run(ReceiveLoopAsync);
    }

    public static async Task<TcpConnection> ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        var c = new TcpClient();
        await c.ConnectAsync(host, port, ct).ConfigureAwait(false);
        return new TcpConnection(c);
    }

    /// <summary>消息帧结构：[1字节 channel][4字节长度][payload]。</summary>
    public async Task SendAsync(ChannelKind channel, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var header = new byte[5];
        header[0] = (byte)channel;
        BitConverter.TryWriteBytes(header.AsSpan(1), payload.Length);
        lock (_sendGate)
        {
            _stream.Write(header, 0, 5);
            _stream.Write(payload.Span);
            _stream.Flush();
        }
        await Task.CompletedTask;
    }

    public async Task<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken ct = default)
    {
        var header = new byte[5];
        if (!await ReadExactAsync(header, ct).ConfigureAwait(false)) return null;
        int len = BitConverter.ToInt32(header, 1);
        if (len < 0 || len > 64 * 1024 * 1024) return null;
        var body = new byte[len];
        if (len > 0 && !await ReadExactAsync(body, ct).ConfigureAwait(false)) return null;
        return new ReadOnlyMemory<byte>(body);
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (true)
            {
                var header = new byte[5];
                if (!await ReadExactAsync(header, CancellationToken.None).ConfigureAwait(false)) break;
                int len = BitConverter.ToInt32(header, 1);
                if (len < 0 || len > 64 * 1024 * 1024) break;
                var body = new byte[len];
                if (len > 0 && !await ReadExactAsync(body, CancellationToken.None).ConfigureAwait(false)) break;
                OnMessage?.Invoke((ChannelKind)header[0], body);
            }
        }
        catch { /* 连接中断 */ }
    }

    private async Task<bool> ReadExactAsync(byte[] buf, CancellationToken ct)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = await _stream.ReadAsync(buf.AsMemory(got, buf.Length - got), ct).ConfigureAwait(false);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    public ValueTask DisposeAsync()
    {
        try { _stream.Dispose(); _client.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }
}

/// <summary>本机监听器（守护进程侧）。</summary>
public sealed class LocalServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private CancellationTokenSource? _cts;
    public int Port { get; }
    public event Func<TcpConnection, Task>? OnClient;

    public LocalServer(int port = 0)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                    var conn = new TcpConnection(client);
                    if (OnClient is not null) await OnClient(conn).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        });
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        try { _listener.Stop(); } catch { }
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>JSON 消息编解码辅助。</summary>
public static class WireCodec
{
    public static byte[] EncodeJson<T>(T value) =>
        Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(value, GuioJson.Options));

    public static T? DecodeJson<T>(ReadOnlyMemory<byte> data) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(data.Span, GuioJson.Options);
}
