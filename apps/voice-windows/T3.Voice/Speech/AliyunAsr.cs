using System;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace T3.Voice.Speech;

public sealed class AliyunAsr : IAsyncDisposable
{
    private const int MaximumFrameBytes = 32_000;
    private readonly AppSettings _settings;
    private readonly WebSocket _socket;
    private readonly Func<Uri, CancellationToken, Task> _connect;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<byte[]> _audio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(50)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false,
    });
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly AsrProtocol _protocol;
    private readonly string _taskId = Guid.NewGuid().ToString();
    private CancellationTokenRegistration _callerCancellation;
    private Task? _sender;
    private Task? _receiver;
    private Task<string>? _finish;
    private int _phase;
    private bool _disposed;

    public event Action<string>? TranscriptChanged;
    public event Action<Exception>? Failed;
    public Task<string> Completion => _finished.Task;

    internal AliyunAsr(AppSettings settings, WebSocket connectedSocket)
    {
        _settings = settings;
        _socket = connectedSocket;
        _connect = (_, _) => Task.CompletedTask;
        _protocol = new AsrProtocol(_taskId);
    }

    public AliyunAsr(AppSettings settings, string apiKey) : this(settings, new ClientWebSocket())
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("请先配置阿里云语音识别 API Key。", nameof(apiKey));
        var client = (ClientWebSocket)_socket;
        _connect = client.ConnectAsync;
        client.Options.SetRequestHeader("Authorization", $"Bearer {apiKey.Trim()}");
        client.Options.SetRequestHeader("user-agent", "T3-Voice-Windows/0.1");
        if (!string.IsNullOrWhiteSpace(settings.AsrWorkspace))
            client.Options.SetRequestHeader("X-DashScope-WorkSpace", settings.AsrWorkspace.Trim());
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.CompareExchange(ref _phase, 1, 0) != 0)
            throw new InvalidOperationException("每段录音只能启动一次识别任务。");
        _callerCancellation = cancellationToken.Register(() => Cancel(cancellationToken));
        try
        {
            var address = _settings.AsrEndpoint.Trim();
            if (address.Contains("{WorkspaceId}", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(_settings.AsrWorkspace))
                    throw new InvalidOperationException("请填写阿里云业务空间 ID。");
                address = address.Replace("{WorkspaceId}", _settings.AsrWorkspace.Trim(), StringComparison.OrdinalIgnoreCase);
            }
            if (!Uri.TryCreate(address, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "wss")
                throw new InvalidOperationException("语音识别地址必须为 wss:// WebSocket 地址。");
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            startup.CancelAfter(TimeSpan.FromSeconds(20));
            await _connect(endpoint, startup.Token).ConfigureAwait(false);
            _receiver = ReceiveAsync();
            _sender = SendAsync();
            await _socket.SendAsync(AsrProtocol.RunTask(_taskId, _settings.AsrModel.Trim()),
                WebSocketMessageType.Text, true, startup.Token).ConfigureAwait(false);
            await _started.Task.WaitAsync(startup.Token).ConfigureAwait(false);
            Interlocked.CompareExchange(ref _phase, 2, 1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Cancel(cancellationToken);
            throw;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            await _finished.Task.ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            var error = new TimeoutException("语音识别连接超时，请检查网络和服务地址。");
            Fail(error);
            throw error;
        }
        catch (Exception error)
        {
            Fail(error);
            throw;
        }
    }

    public bool TrySendAudio(ReadOnlyMemory<byte> pcm)
    {
        if (pcm.Length == 0) return true;
        if (Volatile.Read(ref _phase) is not (1 or 2)) return false;
        if (pcm.Length > MaximumFrameBytes || (pcm.Length & 1) != 0)
        {
            Fail(new InvalidOperationException("录音数据必须是单声道 16 位 PCM，每帧最多一秒。"));
            return false;
        }
        if (_audio.Writer.TryWrite(pcm.ToArray())) return true;
        if (Volatile.Read(ref _phase) is 1 or 2)
            Fail(new InvalidOperationException("语音上传速度跟不上录音，已取消本次指令以避免丢字。请检查网络后重试。"));
        return false;
    }

    public Task<string> FinishAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_phase == 0) throw new InvalidOperationException("语音识别尚未开始。");
            return _finish ??= FinishCoreAsync(cancellationToken);
        }
    }

    private async Task<string> FinishCoreAsync(CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() => Cancel(cancellationToken));
        lock (_gate)
        {
            if (_phase is 1 or 2) _phase = 3;
            _audio.Writer.TryComplete();
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            if (_sender is not null) await _sender.WaitAsync(timeout.Token).ConfigureAwait(false);
            return await _finished.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await _finished.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var error = new TimeoutException("等待语音最终结果超时，本次指令未发送。请重试。");
            Fail(error);
            throw error;
        }
    }

    private async Task SendAsync()
    {
        try
        {
            await _started.Task.ConfigureAwait(false);
            await foreach (var frame in _audio.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
                await _socket.SendAsync(frame, WebSocketMessageType.Binary, true, _lifetime.Token).ConfigureAwait(false);
            lock (_gate) _protocol.MarkFinishing();
            await _socket.SendAsync(AsrProtocol.FinishTask(_taskId), WebSocketMessageType.Text, true, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { Fail(error); }
    }

    private async Task ReceiveAsync()
    {
        var buffer = new byte[8192];
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer.AsMemory(), _lifetime.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new IOException("语音识别连接在任务完成前断开，本次指令未发送。");
                    if (result.MessageType != WebSocketMessageType.Text)
                        throw new InvalidOperationException("语音服务返回了意外的二进制消息。");
                    if (message.Length + result.Count > 1024 * 1024)
                        throw new InvalidOperationException("语音服务返回的单条消息过大。");
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                using var json = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
                AsrServerEvent kind;
                string text;
                lock (_gate)
                {
                    if (_phase >= 5 || _lifetime.IsCancellationRequested) return;
                    kind = _protocol.Accept(json.RootElement);
                    text = kind == AsrServerEvent.Finished ? _protocol.FinalTranscript : _protocol.Transcript;
                    if (kind == AsrServerEvent.Finished && _phase < 5) _phase = 4;
                }
                if (kind == AsrServerEvent.Started) _started.TrySetResult();
                if (kind == AsrServerEvent.Transcript) TranscriptChanged?.Invoke(text);
                if (kind == AsrServerEvent.Finished)
                {
                    _finished.TrySetResult(text);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { Fail(error); }
    }

    private void Fail(Exception error)
    {
        lock (_gate)
        {
            if (_phase >= 4) return;
            _phase = 5;
            _protocol.Clear();
            _started.TrySetException(error);
            _finished.TrySetException(error);
            _audio.Writer.TryComplete(error);
        }
        _lifetime.Cancel();
        _socket.Abort();
        TranscriptChanged?.Invoke("");
        Failed?.Invoke(error);
    }

    private void Cancel(CancellationToken token)
    {
        lock (_gate)
        {
            if (_phase >= 4) return;
            _phase = 5;
            _protocol.Clear();
            _started.TrySetCanceled(token);
            _finished.TrySetCanceled(token);
            _audio.Writer.TryComplete();
        }
        _lifetime.Cancel();
        _socket.Abort();
        TranscriptChanged?.Invoke("");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel(new CancellationToken(true));
        _lifetime.Cancel();
        _socket.Abort();
        try
        {
            if (_sender is not null) await _sender.ConfigureAwait(false);
            if (_receiver is not null) await _receiver.ConfigureAwait(false);
        }
        finally
        {
            _callerCancellation.Dispose();
            _socket.Dispose();
            _lifetime.Dispose();
        }
    }
}
