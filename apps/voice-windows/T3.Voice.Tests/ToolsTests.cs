using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace T3.Voice.Tests;

public static class ToolsTests
{
    public static async Task RunAsync()
    {
        await QueuesBusyComputerAndDispatchesOnceAsync();
        await RepairsTrackingFromCachedLaunchAsync();
        await RefusesPendingRetryAfterAuthorizationChangesAsync();
        await RetriesWithSameRequestKeyInSameAuthorizationAsync();
        await RejectsCrossHostForwardingAsync();
        await CancelsQueuedCallsUnderMachineGateAsync();
        await QueuesUnsentLaunchWhileOfflineAsync();
        await PreservesAmbiguousPendingCallWhenDiscoveryFailsAsync();
        await RetainsDiscoveryCatalogForOfflineTaskPlanningAsync();
    }

    private static async Task QueuesBusyComputerAndDispatchesOnceAsync()
    {
        using var fixture = new Fixture();
        var tools = new VoiceTools(fixture.Store, fixture.Remote);
        var busy = new RemoteTask { MachineId = fixture.Machine.Id, ThreadId = "existing-thread", Status = "running" };
        fixture.Store.SaveTask(busy);
        var arguments = LaunchArguments();
        var queued = await tools.RemoteCallAsync(fixture.Machine, "t3_thread_launch", arguments, 11, CancellationToken.None);
        Assert(VoiceTools.Payload(queued).GetProperty("queued").GetBoolean() && fixture.Remote.Calls.Count == 0, "busy launch is durable without dispatch");
        Assert(fixture.Store.List<SavedCall>("call:").Single().Value.State == "queued", "queue persisted");
        fixture.Store.SaveTask(busy with { Status = "completed" });
        await tools.DrainQueueAsync(CancellationToken.None);
        await tools.DrainQueueAsync(CancellationToken.None);
        Assert(fixture.Remote.Calls.Count == 1, "queue dispatches once");
        var task = fixture.Store.Tasks().Single(value => value.ThreadId == "created-thread");
        Assert(task.RunId == "created-run" && task.Title == "实现登录" && task.MachineId == fixture.Machine.Id, "actual launch result is tracked");
        Assert(fixture.Store.PendingInbox().Count == 1, "queue result wakes coordinator once");
    }

    private static async Task RepairsTrackingFromCachedLaunchAsync()
    {
        using var original = new Fixture();
        var originalTools = new VoiceTools(original.Store, original.Remote);
        await originalTools.RemoteCallAsync(original.Machine, "t3_thread_launch", LaunchArguments(), 12, CancellationToken.None);
        var saved = original.Store.List<SavedCall>("call:").Single();
        using var restarted = new Fixture();
        restarted.Store.Put(saved.Key, saved.Value);
        var tools = new VoiceTools(restarted.Store, restarted.Remote);
        var reordered = JsonSerializer.SerializeToElement(new { title = "实现登录", projectId = "project-one", message = "完成登录功能" });
        await tools.RemoteCallAsync(restarted.Machine, "t3_thread_launch", reordered, 12, CancellationToken.None);
        Assert(restarted.Remote.Calls.Count == 0, "committed call is recovered without another launch");
        var task = restarted.Store.Tasks().Single();
        Assert(task.ThreadId == "created-thread" && task.RunId == "created-run", "cached result repairs missing tracking after crash");
        restarted.Store.SaveTask(task with { Status = "completed" });
        await tools.RemoteCallAsync(restarted.Machine, "t3_thread_launch", LaunchArguments(), 12, CancellationToken.None);
        Assert(restarted.Store.Tasks().Single().Status == "completed", "cached replay does not revive finished task");
    }

    private static async Task RefusesPendingRetryAfterAuthorizationChangesAsync()
    {
        using var fixture = new Fixture();
        var tools = new VoiceTools(fixture.Store, fixture.Remote);
        fixture.Remote.FailNextCall = true;
        await ThrowsAsync<HttpRequestException>(() => tools.RemoteCallAsync(fixture.Machine, "t3_thread_launch", LaunchArguments(), 13, CancellationToken.None));
        var saved = fixture.Store.List<SavedCall>("call:").Single();
        Assert(saved.Value.State == "pending" && saved.Value.CredentialFingerprint is { Length: 64 }, "pending call binds to credential fingerprint");
        fixture.Store.SetSecret("machine:" + fixture.Machine.Id, "replacement-fixture-credential");
        await ThrowsAsync<InvalidOperationException>(() => tools.RemoteCallAsync(fixture.Machine, "t3_thread_launch", LaunchArguments(), 13, CancellationToken.None));
        Assert(fixture.Remote.Calls.Count == 1, "new OAuth namespace cannot replay ambiguous mutation");
        fixture.Store.Put(saved.Key, saved.Value with { CredentialFingerprint = null });
        await ThrowsAsync<InvalidOperationException>(() => tools.RemoteCallAsync(fixture.Machine, "t3_thread_launch", LaunchArguments(), 13, CancellationToken.None));
        Assert(fixture.Remote.Calls.Count == 1, "legacy pending record needs reconciliation");
    }

    private static async Task RetriesWithSameRequestKeyInSameAuthorizationAsync()
    {
        using var fixture = new Fixture();
        var tools = new VoiceTools(fixture.Store, fixture.Remote);
        fixture.Remote.FailNextCall = true;
        await ThrowsAsync<HttpRequestException>(() => tools.RemoteCallAsync(fixture.Machine, "t3_thread_launch", LaunchArguments(), 14, CancellationToken.None));
        await tools.RemoteCallAsync(fixture.Machine, "t3_thread_launch", LaunchArguments(), 14, CancellationToken.None);
        Assert(fixture.Remote.Calls.Count == 2, "explicit retry after ambiguous response");
        Assert(fixture.Remote.Calls[0].GetProperty("clientRequestId").GetString() == fixture.Remote.Calls[1].GetProperty("clientRequestId").GetString(), "stable remote idempotency key");
    }

    private static async Task RejectsCrossHostForwardingAsync()
    {
        using var fixture = new Fixture();
        var tools = new VoiceTools(fixture.Store, fixture.Remote);
        await ThrowsAsync<InvalidOperationException>(() => tools.RemoteCallAsync(fixture.Machine, "t3_thread_launch",
            JsonSerializer.SerializeToElement(new { title = "转发", environmentId = "unconfigured-environment" }), 15, CancellationToken.None));
        Assert(fixture.Remote.ListCount == 0 && fixture.Remote.Calls.Count == 0, "host routing cannot bypass companion identity tracking");
    }

    private static async Task CancelsQueuedCallsUnderMachineGateAsync()
    {
        using var fixture = new Fixture();
        var tools = new VoiceTools(fixture.Store, fixture.Remote);
        var busy = new RemoteTask { MachineId = fixture.Machine.Id, ThreadId = "busy", Status = "running" };
        fixture.Store.SaveTask(busy);
        var queued = VoiceTools.Payload(await tools.RemoteCallAsync(fixture.Machine, "t3_thread_launch", LaunchArguments(), 16, CancellationToken.None));
        var queueId = queued.GetProperty("queueId").GetString()!;
        await tools.CallAsync("voice_tasks", JsonSerializer.SerializeToElement(new { action = "cancel_queue", queueId }), CancellationToken.None);
        fixture.Store.SaveTask(busy with { Status = "completed" });
        await tools.DrainQueueAsync(CancellationToken.None);
        Assert(fixture.Remote.Calls.Count == 0, "cancelled queue never dispatches");

        fixture.Store.SaveTask(busy with { Status = "running" });
        queued = VoiceTools.Payload(await tools.RemoteCallAsync(fixture.Machine, "t3_thread_launch", LaunchArguments(), 17, CancellationToken.None));
        queueId = queued.GetProperty("queueId").GetString()!;
        fixture.Store.SaveTask(busy with { Status = "completed" });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Remote.BeforeList = () => { entered.TrySetResult(); return release.Task; };
        var drain = tools.DrainQueueAsync(CancellationToken.None);
        await entered.Task;
        var cancel = tools.CallAsync("voice_tasks", JsonSerializer.SerializeToElement(new { action = "cancel_queue", queueId }), CancellationToken.None);
        Assert(!cancel.IsCompleted, "cancel and dispatch serialize under the machine gate");
        release.SetResult();
        await drain;
        await ThrowsAsync<InvalidOperationException>(async () => { await cancel; });
        Assert(fixture.Remote.Calls.Count == 1 && fixture.Store.Get<SavedCall>("call:" + queueId)!.State == "completed", "in-flight dispatch cannot be labelled cancelled");
    }

    private static async Task QueuesUnsentLaunchWhileOfflineAsync()
    {
        foreach (var error in new Exception[]
        {
            new HttpRequestException("fixture offline"),
            new RemoteMcpException("fixture server unavailable", HttpStatusCode.ServiceUnavailable),
            new OperationCanceledException("fixture internal discovery deadline"),
        })
        {
            using var fixture = new Fixture();
            var tools = new VoiceTools(fixture.Store, fixture.Remote);
            fixture.Remote.DiscoveryFailure = error;
            var notifications = 0;
            tools.Changed += () => notifications++;
            var result = VoiceTools.Payload(await tools.RemoteCallAsync(fixture.Machine, "t3_thread_launch", LaunchArguments(), 18, CancellationToken.None));
            Assert(result.GetProperty("queued").GetBoolean(), "offline launch returns durable queue acknowledgement");
            Assert(fixture.Store.List<SavedCall>("call:").Single().Value.State == "queued" && fixture.Remote.Calls.Count == 0, "unsent launch persisted while discovery offline");
            await tools.DrainQueueAsync(CancellationToken.None);
            await tools.DrainQueueAsync(CancellationToken.None);
            Assert(fixture.Store.PendingInbox().Count == 0 && notifications == 1, "offline queue retry does not repeatedly wake a model");
            fixture.Store.SetSecret("machine:" + fixture.Machine.Id, "new-unsent-fixture-credential");
            fixture.Remote.DiscoveryFailure = null;
            await tools.DrainQueueAsync(CancellationToken.None);
            Assert(fixture.Remote.Calls.Count == 1 && fixture.Store.List<SavedCall>("call:").Single().Value.State == "completed", "never-sent queue resumes safely after reconnect and new authorization");
        }
        using var unauthorized = new Fixture();
        unauthorized.Remote.DiscoveryFailure = new RemoteMcpException("fixture revoked", HttpStatusCode.Unauthorized);
        await ThrowsAsync<RemoteMcpException>(() => new VoiceTools(unauthorized.Store, unauthorized.Remote).RemoteCallAsync(unauthorized.Machine, "t3_thread_launch", LaunchArguments(), 19, CancellationToken.None));
        Assert(unauthorized.Store.List<SavedCall>("call:").Count == 0, "authorization failure remains actionable rather than hidden in offline queue");
    }

    private static async Task PreservesAmbiguousPendingCallWhenDiscoveryFailsAsync()
    {
        using var fixture = new Fixture();
        var tools = new VoiceTools(fixture.Store, fixture.Remote);
        fixture.Remote.FailNextCall = true;
        await ThrowsAsync<HttpRequestException>(() => tools.RemoteCallAsync(fixture.Machine, "t3_thread_launch", LaunchArguments(), 20, CancellationToken.None));
        fixture.Remote.DiscoveryFailure = new HttpRequestException("fixture disconnected after possibly committed launch");
        await ThrowsAsync<HttpRequestException>(() => tools.RemoteCallAsync(fixture.Machine, "t3_thread_launch", LaunchArguments(), 20, CancellationToken.None));
        Assert(fixture.Store.List<SavedCall>("call:").Single().Value.State == "pending" && fixture.Remote.Calls.Count == 1, "ambiguous mutation never becomes automatically retryable queue");
        await ThrowsAsync<HttpRequestException>(() => tools.RemoteCallAsync(fixture.Machine, "t3_thread_read", JsonSerializer.SerializeToElement(new { threadId = "example" }), 21, CancellationToken.None));
        Assert(fixture.Store.List<SavedCall>("call:").Count == 1, "read failures do not create queued work");
    }

    private static async Task RetainsDiscoveryCatalogForOfflineTaskPlanningAsync()
    {
        using var fixture = new Fixture();
        var tools = new VoiceTools(fixture.Store, fixture.Remote);
        var arguments = JsonSerializer.SerializeToElement(new { machineId = fixture.Machine.Id });
        var fresh = VoiceTools.Payload(await tools.CallAsync("voice_remote_tools", arguments, CancellationToken.None));
        Assert(fresh.GetProperty("tools").GetArrayLength() == 1 && !fresh.TryGetProperty("stale", out _), "fresh catalog discovered");
        fixture.Remote.DiscoveryFailure = new HttpRequestException("fixture offline after cached discovery");
        var restarted = new VoiceTools(fixture.Store, fixture.Remote);
        var cached = VoiceTools.Payload(await restarted.CallAsync("voice_remote_tools", arguments, CancellationToken.None));
        Assert(cached.GetProperty("tools")[0].GetProperty("name").GetString() == "t3_thread_launch" &&
            cached.GetProperty("stale").GetBoolean() && cached.GetProperty("offline").GetBoolean(), "persistent cached schemas remain discoverable offline");
        Assert(!cached.GetRawText().Contains("fixture-credential", StringComparison.Ordinal), "catalog never contains host credential");
        var queued = VoiceTools.Payload(await restarted.RemoteCallAsync(fixture.Machine, "t3_thread_launch", LaunchArguments(), 22, CancellationToken.None));
        Assert(queued.GetProperty("queued").GetBoolean() && fixture.Remote.Calls.Count == 0, "stale catalog plans an unsent queue without dispatching offline");
        fixture.Remote.DiscoveryFailure = new RemoteMcpException("fixture revoked", HttpStatusCode.Unauthorized);
        await ThrowsAsync<RemoteMcpException>(() => restarted.CallAsync("voice_remote_tools", arguments, CancellationToken.None));
        Assert(fixture.Remote.Calls.Count == 0, "cached schema does not hide revoked authorization");
        fixture.Remote.DiscoveryFailure = new HttpRequestException("fixture offline wrong destination");
        var replacement = fixture.Machine with { Url = "https://replacement.test" };
        fixture.Store.SaveSettings(new AppSettings { Machines = [replacement] });
        await ThrowsAsync<HttpRequestException>(() => restarted.CallAsync("voice_remote_tools", arguments, CancellationToken.None));
    }

    private static JsonElement LaunchArguments() => JsonSerializer.SerializeToElement(new { projectId = "project-one", message = "完成登录功能", title = "实现登录" });
    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new Exception("Tools test failed: " + description);
    }
    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "t3-voice-tools-" + Guid.NewGuid().ToString("N"));
        internal VoiceStore Store { get; }
        internal Machine Machine { get; } = new() { Id = "test-machine", Name = "测试机器", Url = "https://environment.test" };
        internal FakeRemote Remote { get; } = new();
        internal Fixture()
        {
            Store = new VoiceStore(directory);
            Store.SaveSettings(new AppSettings { Machines = [Machine] });
            Store.SetSecret("machine:" + Machine.Id, "fixture-credential");
        }
        public void Dispose()
        {
            Store.Dispose();
            Directory.Delete(directory, true);
        }
    }

    private sealed class FakeRemote : IRemoteMcpClient
    {
        internal List<JsonElement> Calls { get; } = [];
        internal int ListCount;
        internal bool FailNextCall;
        internal Func<Task>? BeforeList;
        internal Exception? DiscoveryFailure;
        public async Task<JsonElement> ListToolsAsync(Machine machine, string accessToken, CancellationToken cancellationToken)
        {
            ListCount++;
            if (BeforeList is not null) await BeforeList();
            if (DiscoveryFailure is not null) throw DiscoveryFailure;
            return JsonSerializer.SerializeToElement(new
            {
                tools = new[] { new
            {
                name = "t3_thread_launch",
                inputSchema = new { type = "object", properties = new { clientRequestId = new { type = "string" } } },
                annotations = new { readOnlyHint = false },
            } }
            });
        }
        public Task<JsonElement> CallToolAsync(Machine machine, string accessToken, string name, JsonElement arguments, CancellationToken cancellationToken)
        {
            Calls.Add(arguments.Clone());
            if (FailNextCall)
            {
                FailNextCall = false;
                throw new HttpRequestException("fixture ambiguous response");
            }
            return Task.FromResult(VoiceTools.Result(new
            {
                threadId = "created-thread",
                projectId = "project-one",
                modelSelection = new { instanceId = "codex", model = "fixture" },
                runId = "created-run",
                status = "queued",
            }));
        }
    }
}
