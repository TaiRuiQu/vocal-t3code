using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using T3.Voice;

namespace T3.Voice.Tests;

public static class CoordinatorTests
{
    public static async Task RunAsync()
    {
        SettlementAndQuestionIdentity();
        await WatchesDescendantsBeforeCompletionAsync();
        await PreservesQuestionsAndOrdersVoiceContextAsync();
        StaleObservationCannotOverwriteFollowUp();
        await DeletedThreadReleasesQueuedWorkAsync();
        await TransportNotFoundKeepsTaskOfflineAsync();
    }

    private static void SettlementAndQuestionIdentity()
    {
        var task = new RemoteTask { RunId = "parent-run" };
        var completed = Read("completed", "parent-run", 0);
        Assert(Coordinator.Observe(task, completed).Status == "running", "A completed parent turn is not proof that delegated work finished.");
        var waiting = JsonSerializer.SerializeToElement(new { runId = "parent-run", timedOut = true, status = "completed" });
        Assert(Coordinator.Observe(task, completed, waiting).Status == "running", "Timed-out descendant waits must keep the machine occupied.");
        var settled = JsonSerializer.SerializeToElement(new { runId = "parent-run", timedOut = false, status = "completed", resultRun = new { runId = "final-run", status = "completed" } });
        var done = Coordinator.Observe(task, Read("completed", "final-run", 0), settled);
        Assert(done.Status == "completed" && done.RunId == "final-run", "The final result run should identify the completed task.");
        Assert(Coordinator.Observe(task, Read("completed", "newer-run", 0), settled).Status == "running", "A wait for an older run cannot settle a newer completed run.");
        var first = Coordinator.Observe(task, Read("completed", "parent-run", 1), settled, ["request-one"]);
        var second = Coordinator.Observe(first, Read("completed", "parent-run", 1), settled, ["request-two"]);
        Assert(first.Status == "waiting" && second.Status == "waiting" && first.Fingerprint != second.Fingerprint, "Consecutive questions within one run must wake the coordinator separately.");
    }

    private static async Task WatchesDescendantsBeforeCompletionAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = new RemoteTask { MachineId = fixture.Machine.Id, ThreadId = "thread-one", RunId = "parent-run", Title = "科技树" };
        fixture.Store.SaveTask(task);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Coordinator.Changed += () =>
        {
            if (fixture.Store.Tasks().Single().Status == "completed") finished.TrySetResult();
        };
        await fixture.Coordinator.StartAsync();
        await fixture.Remote.WaitEntered.Task;
        Assert(fixture.Store.Tasks().Single().Status == "running", "The parent turn finishing must not release the computer before descendant settlement.");
        Assert(fixture.Store.PendingInbox().All(message => message.Kind != "remote"), "A parent turn must not announce completion while its children run.");
        fixture.Remote.ReleaseWait.SetResult();
        await finished.Task;
        Assert(fixture.Store.Tasks().Single().RunId == "final-run", "The watcher must refresh the final run after the wait.");
        Assert(fixture.Remote.TailAfterPosition == 56, "The watcher should fetch the tail of a long timeline rather than its first messages.");
        using var snapshot = JsonDocument.Parse(fixture.Store.Tasks().Single().Snapshot);
        Assert(snapshot.RootElement.GetProperty("items")[0].GetProperty("text").GetString() == "最终 PR 结果", "Completion must retain the final message rather than old conversation text.");
        Assert(fixture.Store.PendingInbox().Single(message => message.Kind == "remote").Text.Contains(snapshot.RootElement.GetRawText(), StringComparison.Ordinal), "The completion event must carry its final snapshot.");
        Assert(fixture.Remote.WaitCalls == 1, "Only one watcher should wait for a tracked task.");
    }

    private static async Task PreservesQuestionsAndOrdersVoiceContextAsync()
    {
        await using var fixture = await Fixture.CreateAsync(immediateDriver: true);
        const string questionKey = "question:pending-project";
        fixture.Store.Put(questionKey, "之前那个修复要用哪个项目？");
        fixture.Coordinator.Submit("新任务：创建一个小游戏");
        fixture.Coordinator.Submit("第二个任务：修复网站导航");
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Coordinator.Changed += () =>
        {
            if (!fixture.Coordinator.Busy && fixture.Store.PendingInbox().Count == 0) processed.TrySetResult();
        };
        await fixture.Coordinator.StartAsync();
        await processed.Task;
        var first = await fixture.Driver.Requests.Reader.ReadAsync();
        var second = await fixture.Driver.Requests.Reader.ReadAsync();
        Assert(!first.Prompt.Contains("第二个任务：修复网站导航", StringComparison.Ordinal), "A queued later utterance must not appear in an earlier turn's context.");
        Assert(second.Prompt.Contains("上一轮已经处理", StringComparison.Ordinal), "The next queued utterance should see the previous coordinator response.");
        Assert(fixture.Store.Get<string>(questionKey) is not null, "Starting an unrelated task must not consume an unanswered question.");
    }

    private static void StaleObservationCannotOverwriteFollowUp()
    {
        var directory = Path.Combine(Path.GetTempPath(), "t3-voice-observe-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new VoiceStore(directory);
            var original = new RemoteTask { RunId = "old-run", Fingerprint = "old-fingerprint" };
            store.SaveTask(original);
            store.SaveTask(original with { RunId = "follow-up-run", Fingerprint = "", UpdatedAt = "follow-up" });
            var changed = store.TryObserveTask(original, original with { Status = "completed" }, "old completed", "old-event");
            Assert(!changed && store.Tasks().Single().RunId == "follow-up-run" && store.PendingInbox().Count == 0, "A late watcher response must neither overwrite a follow-up nor announce stale completion.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task DeletedThreadReleasesQueuedWorkAsync()
    {
        foreach (var structured in new[] { false, true })
        {
            await using var fixture = await Fixture.CreateAsync();
            var missing = JsonSerializer.SerializeToElement(new { _tag = "OrchestratorMcpFailure", code = "thread_not_found", message = "The thread was deleted." });
            fixture.Remote.ReadFailure = structured
                ? JsonSerializer.SerializeToElement(new { isError = true, structuredContent = missing })
                : JsonSerializer.SerializeToElement(new { isError = true, content = new[] { new { type = "text", text = missing.GetRawText() } } });
            fixture.Store.SaveTask(new RemoteTask { Id = "deleted-task", MachineId = fixture.Machine.Id, ThreadId = "thread-one", RunId = "parent-run", Status = "waiting" });
            var arguments = JsonSerializer.SerializeToElement(new { projectId = "fixture-project", title = "后续任务", message = "继续测试" });
            var queued = VoiceTools.Payload(await fixture.Tools.RemoteCallAsync(fixture.Machine, "t3_thread_launch", arguments, 31, CancellationToken.None));
            Assert(queued.GetProperty("queued").GetBoolean(), "Existing tracked work should initially occupy the machine.");
            var incorrectlyOffline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Coordinator.Notice += (title, _, _) => { if (title == "暂时无法连接云电脑") incorrectlyOffline.TrySetResult(); };
            await fixture.Coordinator.StartAsync();
            Assert(await Task.WhenAny(fixture.Remote.LaunchEntered.Task, incorrectlyOffline.Task) == fixture.Remote.LaunchEntered.Task,
                "A typed missing thread must not be treated as an offline machine.");
            var task = fixture.Store.Tasks().Single(value => value.Id == "deleted-task");
            Assert(task.Status == "failed" && !VoiceTools.IsActive(task), "A typed missing thread is terminal, not an offline machine.");
            Assert(fixture.Store.PendingInbox().Count(message => message.Kind == "remote" && message.Text.Contains("对话已删除", StringComparison.Ordinal)) == 1,
                "Missing thread must produce one durable terminal notice.");
            Assert(fixture.Remote.LaunchCalls == 1, "Deleting a tracked remote thread must release queued work.");
        }
    }

    private static async Task TransportNotFoundKeepsTaskOfflineAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Remote.ReadException = new RemoteMcpException("MCP endpoint unavailable", System.Net.HttpStatusCode.NotFound);
        fixture.Store.SaveTask(new RemoteTask { Id = "offline-task", MachineId = fixture.Machine.Id, ThreadId = "thread-one", RunId = "parent-run" });
        var offline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Coordinator.Notice += (title, _, _) => { if (title == "暂时无法连接云电脑") offline.TrySetResult(); };
        await fixture.Coordinator.StartAsync();
        await offline.Task;
        Assert(fixture.Store.Tasks().Single().Status == "offline" && fixture.Store.PendingInbox().Count == 0,
            "HTTP 404 does not prove a thread was deleted or release queued work.");
    }

    private static JsonElement Read(string status, string runId, int pending) => JsonSerializer.SerializeToElement(new
    {
        thread = new { threadId = "thread-one", title = "科技树", status, latestRunId = runId, activeRunId = (string?)null, pendingRequestCount = pending, itemCount = 121, updatedAt = "fixture-update" },
        items = Array.Empty<object>(),
        recentRuns = Array.Empty<object>(),
        hasMore = false,
        nextPosition = (int?)null
    });

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "t3-voice-coordinator-" + Guid.NewGuid().ToString("N"));
        public VoiceStore Store { get; }
        public Machine Machine { get; } = new() { Id = "fixture-machine", Name = "Ubuntu", Url = "https://fixture.test" };
        public FakeRemote Remote { get; } = new();
        public FakeDriver Driver { get; }
        public LocalMcpServer Bridge { get; }
        public Coordinator Coordinator { get; }
        public VoiceTools Tools { get; }
        private Fixture(bool immediateDriver)
        {
            Store = new VoiceStore(directory);
            Store.SaveSettings(new AppSettings { Machines = [Machine] });
            Store.SetSecret("machine:" + Machine.Id, "fixture-credential");
            Driver = new FakeDriver(immediateDriver);
            Tools = new VoiceTools(Store, Remote);
            Bridge = new LocalMcpServer(Tools);
            Coordinator = new Coordinator(Store, Tools, Remote, Bridge, _ => Driver);
        }
        public static async Task<Fixture> CreateAsync(bool immediateDriver = false)
        {
            var fixture = new Fixture(immediateDriver);
            await fixture.Bridge.StartAsync(CancellationToken.None);
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            await Bridge.DisposeAsync();
            Store.Dispose();
            Directory.Delete(directory, true);
        }
    }

    private sealed class FakeDriver(bool immediate) : IProviderDriver
    {
        public Channel<ProviderRequest> Requests { get; } = Channel.CreateUnbounded<ProviderRequest>();
        public async Task<ProviderResult> RunAsync(ProviderRequest request, Action<ProviderEvent> onEvent, CancellationToken cancellationToken)
        {
            await Requests.Writer.WriteAsync(request, cancellationToken);
            if (!immediate) await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(cancellationToken);
            return new ProviderResult("fixture-session", "上一轮已经处理", true);
        }
    }

    private sealed class FakeRemote : IRemoteMcpClient
    {
        public TaskCompletionSource WaitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseWait { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WaitCalls;
        public long? TailAfterPosition;
        public JsonElement? ReadFailure;
        public Exception? ReadException;
        public TaskCompletionSource LaunchEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int LaunchCalls;
        private bool settled;
        public Task<JsonElement> ListToolsAsync(Machine machine, string accessToken, CancellationToken cancellationToken) => Task.FromResult(JsonSerializer.SerializeToElement(new
        {
            tools = new[] { new { name = "t3_thread_launch", inputSchema = new { type = "object", properties = new { clientRequestId = new { type = "string" } } }, annotations = new { readOnlyHint = false } } }
        }));
        public async Task<JsonElement> CallToolAsync(Machine machine, string accessToken, string name, JsonElement arguments, CancellationToken cancellationToken)
        {
            if (name == "t3_thread_launch")
            {
                Interlocked.Increment(ref LaunchCalls);
                LaunchEntered.TrySetResult();
                return VoiceTools.Result(new { threadId = "created-thread", runId = "created-run", status = "running" });
            }
            if (name == "t3_thread_wait")
            {
                Interlocked.Increment(ref WaitCalls);
                WaitEntered.TrySetResult();
                await ReleaseWait.Task.WaitAsync(cancellationToken);
                settled = true;
                return VoiceTools.Result(new { runId = "parent-run", status = "completed", timedOut = false, resultRun = new { runId = "final-run", status = "completed" } });
            }
            if (name == "t3_thread_read")
            {
                if (VoiceTools.Text(arguments, "threadId") == "thread-one")
                {
                    if (ReadException is not null) throw ReadException;
                    if (ReadFailure is { } failure) return failure;
                }
                var read = Read("completed", settled ? "final-run" : "parent-run", 0);
                if (arguments.GetProperty("maxCharsPerItem").GetInt32() > 1)
                {
                    TailAfterPosition = arguments.GetProperty("afterPosition").GetInt64();
                    return VoiceTools.Result(new
                    {
                        thread = read.GetProperty("thread"),
                        recentRuns = Array.Empty<object>(),
                        items = new[] { new { position = 120, type = "assistant_message", text = "最终 PR 结果" } },
                        hasMore = false,
                        nextPosition = 120
                    });
                }
                return VoiceTools.Result(read);
            }
            throw new InvalidOperationException("Unexpected fixture tool " + name);
        }
    }
}
