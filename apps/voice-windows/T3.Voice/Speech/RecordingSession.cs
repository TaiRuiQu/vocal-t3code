using System;
using System.Threading;
using System.Threading.Tasks;

namespace T3.Voice.Speech;

internal sealed class RecordingSession : IAsyncDisposable
{
    private readonly object state = new();
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource cancellation = new();
    private readonly Func<CancellationToken, Task> start;
    private readonly Func<CancellationToken, Task<string>> finish;
    private readonly Func<Task> cleanup;
    private Task? starting;
    private Task<string>? finishing;
    private Task? disposing;
    private bool cleaned;
    private bool disposed;
    private bool committed;
    private bool completed;
    private bool userCanceled;
    private Exception? failure;
    private string finalText = "";

    public CancellationToken Token { get; }
    public bool Completed { get { lock (state) return completed; } }
    public bool UserCanceled { get { lock (state) return userCanceled; } }
    public bool Submitted { get { lock (state) return committed; } }
    public Exception? Failure { get { lock (state) return failure; } }
    public string FinalText { get { lock (state) return finalText; } }

    public RecordingSession(Func<CancellationToken, Task> start, Func<CancellationToken, Task<string>> finish, Func<Task> cleanup)
    {
        this.start = start;
        this.finish = finish;
        this.cleanup = cleanup;
        Token = cancellation.Token;
    }

    public Task StartAsync()
    {
        lock (state) return starting ??= StartCoreAsync();
    }

    private async Task StartCoreAsync()
    {
        await serial.WaitAsync();
        try
        {
            Token.ThrowIfCancellationRequested();
            await start(Token);
            Token.ThrowIfCancellationRequested();
        }
        catch (Exception error)
        {
            if (error is not OperationCanceledException || !Token.IsCancellationRequested) Fail(error);
            await CleanupCoreAsync();
            throw;
        }
        finally { serial.Release(); }
    }

    public Task<string> FinishAsync(Action<string> submit)
    {
        lock (state)
        {
            if (starting is null) throw new InvalidOperationException("录音尚未开始。");
            return finishing ??= FinishCoreAsync(starting, submit);
        }
    }

    private async Task<string> FinishCoreAsync(Task startup, Action<string> submit)
    {
        await startup;
        await serial.WaitAsync();
        try
        {
            Token.ThrowIfCancellationRequested();
            var text = await finish(Token);
            Token.ThrowIfCancellationRequested();
            await CleanupCoreAsync();
            lock (state)
            {
                Token.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    submit(text);
                    committed = true;
                }
                finalText = text;
                completed = true;
            }
            return text;
        }
        catch (Exception error)
        {
            if (error is not OperationCanceledException || !Token.IsCancellationRequested) Fail(error);
            await CleanupCoreAsync();
            throw;
        }
        finally { serial.Release(); }
    }

    public void Cancel() => CancelCore(true, null);
    public void Fail(Exception error) => CancelCore(false, error);

    private void CancelCore(bool user, Exception? error)
    {
        lock (state)
        {
            if (disposed || committed || completed) return;
            if (user) userCanceled = true;
            if (error is not null && !userCanceled) failure ??= error;
            cancellation.Cancel();
        }
    }

    private async Task CleanupCoreAsync()
    {
        if (cleaned) return;
        cleaned = true;
        try { await cleanup(); }
        catch (Exception error)
        {
            lock (state) failure = failure is null ? error : new AggregateException(failure, error);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (state) return new ValueTask(disposing ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        CancelCore(false, null);
        await serial.WaitAsync();
        try { await CleanupCoreAsync(); }
        finally
        {
            lock (state)
            {
                disposed = true;
                cancellation.Dispose();
            }
            serial.Release();
        }
    }
}
