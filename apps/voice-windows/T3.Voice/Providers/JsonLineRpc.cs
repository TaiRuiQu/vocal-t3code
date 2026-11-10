using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace T3.Voice;

internal sealed class JsonLineRpc : IAsyncDisposable
{
    private readonly TextReader _reader;
    private readonly TextWriter _writer;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1);
    private readonly CancellationTokenSource _lifetime;
    private readonly Task _readLoop;
    private int _nextId;

    public Action<string, JsonElement>? Notification { get; set; }
    public Func<string, JsonElement, Task<object?>>? ServerRequest { get; set; }
    public Task Completion => _readLoop;

    public JsonLineRpc(TextReader reader, TextWriter writer, CancellationToken cancellationToken)
    {
        _reader = reader;
        _writer = writer;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _readLoop = ReadAsync();
    }

    public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(key, completion)) throw new InvalidOperationException("重复的 CLI 请求编号。");
        try
        {
            await WriteAsync(new { id, method, @params = parameters }, cancellationToken);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
        }
        finally { _pending.TryRemove(key, out _); }
    }

    public Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken) =>
        WriteAsync(new { method, @params = parameters }, cancellationToken);

    private async Task WriteAsync(object value, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _writer.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), cancellationToken);
            await _writer.FlushAsync(cancellationToken);
        }
        finally { _writeLock.Release(); }
    }

    private async Task ReadAsync()
    {
        Exception? failure = null;
        try
        {
            await foreach (var line in ProviderLines.ReadAsync(_reader, _lifetime.Token))
            {
                using var document = JsonDocument.Parse(line);
                await AcceptAsync(document.RootElement);
            }
            throw new EndOfStreamException("Codex CLI 在编排完成前关闭了连接。");
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            foreach (var pending in _pending.Values)
                pending.TrySetException(failure ?? new IOException("CLI 连接已关闭。"));
        }
    }

    internal async Task AcceptAsync(JsonElement message)
    {
        var hasId = message.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null;
        if (message.TryGetProperty("method", out var method))
        {
            var name = method.GetString() ?? "";
            var parameters = message.TryGetProperty("params", out var value) ? value : default;
            if (!hasId) Notification?.Invoke(name, parameters);
            else
            {
                try
                {
                    if (ServerRequest is null) throw new NotSupportedException($"无法处理 CLI 请求：{name}");
                    var result = await ServerRequest(name, parameters);
                    await WriteAsync(new { id = id.Clone(), result }, _lifetime.Token);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    await WriteAsync(new { id = id.Clone(), error = new { code = -32603, message = error.Message } }, _lifetime.Token);
                }
            }
            return;
        }
        if (!hasId || !_pending.TryRemove(id.ToString(), out var pending)) return;
        if (message.TryGetProperty("error", out var rpcError))
        {
            var errorMessage = rpcError.TryGetProperty("message", out var text) ? text.GetString() : "CLI 请求失败。";
            pending.TrySetException(new InvalidOperationException(errorMessage));
        }
        else if (message.TryGetProperty("result", out var result)) pending.TrySetResult(result.Clone());
        else pending.TrySetException(new InvalidDataException("CLI 响应缺少 result 或 error。"));
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        try { await _readLoop; }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (JsonException) { }
        catch (ObjectDisposedException) { }
        _lifetime.Dispose();
        _writeLock.Dispose();
    }
}
