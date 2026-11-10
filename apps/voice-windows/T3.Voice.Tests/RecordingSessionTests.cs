using System;
using System.Threading;
using System.Threading.Tasks;
using T3.Voice.Speech;

namespace T3.Voice.Tests;

internal static class RecordingSessionTests
{
    public static async Task RunAsync()
    {
        await EscapeWhileConnectingAsync();
        await EscapeWhileAwaitingFinalResultAsync();
        await EscapeAfterFinalResultBeforeSubmissionAsync();
        await FinishesAndCleansOnlyOnceAsync();
        await DisposesAfterStartupFailureAsync();
    }

    private static async Task EscapeWhileConnectingAsync()
    {
        var connecting = Signal();
        var connected = Signal();
        var microphoneStarts = 0;
        var cleanups = 0;
        await using var session = new RecordingSession(async token =>
        {
            connecting.TrySetResult();
            await connected.Task.WaitAsync(token);
            microphoneStarts++;
        }, _ => Task.FromResult("unexpected"), () => { cleanups++; return Task.CompletedTask; });
        var startup = session.StartAsync();
        await connecting.Task;
        session.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => startup);
        await session.DisposeAsync();
        Assert(microphoneStarts == 0 && cleanups == 1, "Escape during task-started wait does not open the microphone or clean twice");
        Assert(session.Token.IsCancellationRequested, "captured cancellation token remains readable after disposal");
    }

    private static async Task EscapeWhileAwaitingFinalResultAsync()
    {
        var finishing = Signal();
        var final = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var submissions = 0;
        var cleanups = 0;
        await using var session = new RecordingSession(_ => Task.CompletedTask, _ =>
        {
            finishing.TrySetResult();
            return final.Task;
        }, () => { cleanups++; return Task.CompletedTask; });
        await session.StartAsync();
        var completion = session.FinishAsync(_ => submissions++);
        await finishing.Task;
        session.Cancel();
        var disposing = session.DisposeAsync().AsTask();
        Assert(!disposing.IsCompleted, "cleanup must wait for the in-flight finish operation to release its resources");
        final.TrySetResult("late final transcript");
        await ThrowsAsync<OperationCanceledException>(() => completion);
        await disposing;
        Assert(submissions == 0 && cleanups == 1, "Escape before task-finished suppresses a late successful transcript and releases once");
    }

    private static async Task EscapeAfterFinalResultBeforeSubmissionAsync()
    {
        var cleaning = Signal();
        var release = Signal();
        var submissions = 0;
        await using var session = new RecordingSession(_ => Task.CompletedTask, _ => Task.FromResult("recognized task"), async () =>
        {
            cleaning.TrySetResult();
            await release.Task;
        });
        await session.StartAsync();
        var completion = session.FinishAsync(_ => submissions++);
        await cleaning.Task;
        session.Cancel();
        release.TrySetResult();
        await ThrowsAsync<OperationCanceledException>(() => completion);
        Assert(submissions == 0, "Escape after final ASR result but before dispatch must still cancel the task");
    }

    private static async Task FinishesAndCleansOnlyOnceAsync()
    {
        var final = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var finishes = 0;
        var cleanups = 0;
        var submissions = 0;
        await using var session = new RecordingSession(_ => { starts++; return Task.CompletedTask; }, _ => { finishes++; return final.Task; },
            () => { cleanups++; return Task.CompletedTask; });
        await Task.WhenAll(session.StartAsync(), session.StartAsync());
        var first = session.FinishAsync(_ => submissions++);
        var duplicate = session.FinishAsync(_ => submissions++);
        final.TrySetResult("task");
        await Task.WhenAll(first, duplicate);
        await Task.WhenAll(session.DisposeAsync().AsTask(), session.DisposeAsync().AsTask());
        session.Fail(new InvalidOperationException("late callback"));
        Assert(starts == 1 && finishes == 1 && cleanups == 1 && submissions == 1, "duplicate gestures and cleanup paths execute each operation once");
        Assert(session.Submitted && session.Failure is null, "late failure callbacks cannot change an already submitted recording");
    }

    private static async Task DisposesAfterStartupFailureAsync()
    {
        var cleanups = 0;
        await using var session = new RecordingSession(_ => Task.FromException(new InvalidOperationException("connection refused")),
            _ => Task.FromResult("unused"), () => { cleanups++; return Task.CompletedTask; });
        await ThrowsAsync<InvalidOperationException>(() => session.StartAsync());
        await session.DisposeAsync();
        Assert(cleanups == 1 && session.Failure?.Message == "connection refused", "a startup failure retains its error and disposes once");
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Assert(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
}
