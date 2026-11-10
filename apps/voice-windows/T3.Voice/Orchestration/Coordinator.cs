using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace T3.Voice;

public sealed record UserInput(string Text, string? QuestionKey, string? Question, long? HistoryThroughId = null);

public sealed class Coordinator : IAsyncDisposable
{
    private readonly VoiceStore store;
    private readonly VoiceTools tools;
    private readonly IRemoteMcpClient remote;
    private readonly LocalMcpServer bridge;
    private readonly Func<string, IProviderDriver> drivers;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly object watcherGate = new();
    private readonly Dictionary<string, Task> watchers = new();
    private Task? worker;
    private Task? queueWorker;
    private readonly string instructions;
    public bool Busy { get; private set; }
    public ResourceUsage Resources { get; } = new();
    public event Action<string, string, bool>? Notice;
    public event Action? Changed;

    public Coordinator(VoiceStore store, VoiceTools tools, IRemoteMcpClient remote, LocalMcpServer bridge, Func<string, IProviderDriver> drivers)
    {
        this.store = store;
        this.tools = tools;
        this.remote = remote;
        this.bridge = bridge;
        this.drivers = drivers;
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("T3.Voice.Orchestration.OrchestratorInstructions.txt")!;
        using var reader = new StreamReader(resource);
        instructions = reader.ReadToEnd();
        tools.Changed += OnToolsChanged;
        tools.Asked += OnAsked;
    }

    public Task StartAsync()
    {
        lock (watcherGate)
        {
            if (worker is not null) throw new InvalidOperationException("编排后台已经启动。");
            worker = Task.Run(() => WorkAsync(stop.Token));
            queueWorker = Task.Run(() => QueueAsync(stop.Token));
        }
        StartWatchers();
        Wake();
        return Task.CompletedTask;
    }

    public void Submit(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var input = store.SubmitInput(text);
        Notice?.Invoke("已收到", input.Text, false);
        Wake();
        Changed?.Invoke();
    }

    public void Wake()
    {
        if (stop.IsCancellationRequested) return;
        try { wake.Release(); }
        catch (SemaphoreFullException) { }
    }
    private void OnToolsChanged() { StartWatchers(); Wake(); Changed?.Invoke(); }
    private void OnAsked(string question) { Notice?.Invoke("需要你回答", question, true); Changed?.Invoke(); }

    private async Task WorkAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await wake.WaitAsync(cancellationToken);
                var paused = false;
                while (!paused)
                {
                    var pendingInbox = store.PendingInbox();
                    if (pendingInbox.Count == 0) break;
                    foreach (var message in pendingInbox)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Busy = true;
                        Changed?.Invoke();
                        tools.ActiveInboxId = message.Id;
                        try
                        {
                            var settings = store.LoadSettings();
                            var user = message.Kind == "user" ? JsonSerializer.Deserialize<UserInput>(message.Text, VoiceStore.Json) : null;
                            var context = new StringBuilder();
                            context.AppendLine("持久记忆：" + JsonSerializer.Serialize(store.List<string>("memory:"), VoiceStore.Json));
                            context.AppendLine("近期对话（用于指代和切换提供商后续接）：");
                            foreach (var prior in store.RecentMessages(100).Where(prior => prior.Role != "user" || user?.HistoryThroughId is null || prior.Id <= user.HistoryThroughId).TakeLast(30))
                                context.AppendLine($"[{prior.Role}] {Clip(prior.Text, 2_000)}");
                            context.AppendLine("当前待答问题：" + JsonSerializer.Serialize(store.List<string>("question:"), VoiceStore.Json));
                            context.AppendLine(user is null ? "后台任务事件，请处理并向用户汇报：" : "本次用户语音：");
                            if (user?.Question is not null)
                                context.AppendLine("录音时正在向用户询问：" + user.Question + "。这次语音默认回答此问题，但若明确另起任务则按新任务处理。问题键：" + user.QuestionKey);
                            context.AppendLine(user?.Text ?? message.Text);
                            Notice?.Invoke("编排中", user?.Text ?? "正在查看云端任务结果", false);
                            var executable = settings.Provider == "claude" ? settings.ClaudePath : settings.CodexPath;
                            var model = settings.Provider == "claude" ? settings.ClaudeModel : settings.CodexModel;
                            var request = new ProviderRequest(context.ToString(), store.Get<string>("session:" + settings.Provider), instructions, bridge.Endpoint, store.DataDirectory, executable, string.IsNullOrWhiteSpace(model) ? null : model);
                            var result = await drivers(settings.Provider).RunAsync(request, providerEvent =>
                            {
                                Resources.ProviderEvent(providerEvent);
                                if (providerEvent.Kind == "session") store.Put("session:" + settings.Provider, providerEvent.Text);
                                if (providerEvent.Kind == "question")
                                {
                                    store.Put("question:" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfffffff") + Guid.NewGuid().ToString("N"), providerEvent.Text);
                                    OnAsked(store.List<string>("question:").First().Value);
                                }
                            }, cancellationToken);
                            if (result.SessionId is not null) store.Put("session:" + settings.Provider, result.SessionId);
                            if (!result.Success) throw new InvalidOperationException(result.Error ?? "编排 agent 未能完成本次处理");
                            store.CompleteTurn(message.Id, settings.Provider, result.SessionId, result.Text);
                            if (!string.IsNullOrWhiteSpace(result.Text))
                            {
                                Notice?.Invoke(message.Kind == "remote" ? "任务进展" : "编排回复", result.Text, false);
                            }
                            var question = store.List<string>("question:").FirstOrDefault();
                            if (question.Value is not null) OnAsked(question.Value);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                        catch (Exception exception)
                        {
                            store.AddMessage("error", exception.Message);
                            Notice?.Invoke("暂未处理，任务已保留", exception.Message + "\n可以切换模型或修复连接后，在托盘中选择“重试待处理任务”。", true);
                            wake.Wait(0);
                            paused = true;
                            break;
                        }
                        finally { Busy = false; tools.ActiveInboxId = 0; Changed?.Invoke(); }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void StartWatchers()
    {
        lock (watcherGate)
        {
            if (stop.IsCancellationRequested) return;
            var configured = store.LoadSettings().Machines.Select(machine => machine.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var task in store.Tasks().Where(task => VoiceTools.IsActive(task) && configured.Contains(task.MachineId)))
            {
                if (!watchers.ContainsKey(task.Id)) watchers[task.Id] = Task.Run(() => WatchOwnedAsync(task.Id, stop.Token));
            }
        }
    }

    private async Task WatchOwnedAsync(string taskId, CancellationToken cancellationToken)
    {
        try { await WatchAsync(taskId, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Notice?.Invoke("任务跟踪暂时中断", exception.Message, true);
            try { await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
        finally
        {
            lock (watcherGate)
            {
                watchers.Remove(taskId);
                var configured = store.LoadSettings().Machines.Select(machine => machine.Id).ToHashSet(StringComparer.Ordinal);
                if (!stop.IsCancellationRequested && store.Tasks().Any(task => task.Id == taskId && VoiceTools.IsActive(task) && configured.Contains(task.MachineId)))
                    watchers[taskId] = Task.Run(() => WatchOwnedAsync(taskId, stop.Token));
            }
        }
    }

    private async Task WatchAsync(string taskId, CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var task = store.Tasks().FirstOrDefault(value => value.Id == taskId);
            if (task is null || !VoiceTools.IsActive(task)) return;
            if (!store.LoadSettings().Machines.Any(machine => machine.Id == task.MachineId)) return;
            try
            {
                var machine = tools.FindMachine(task.MachineId);
                var token = tools.Credential(machine);
                var read = await ReadMetadataAsync(machine, token, task.ThreadId, cancellationToken);
                var requestIds = await PendingRequestIdsAsync(machine, token, task.ThreadId, read, cancellationToken);
                JsonElement? settlement = null;
                if (!HasPendingRequest(read) && IsTerminalStatus(VoiceTools.Text(read.GetProperty("thread"), "status")))
                {
                    settlement = await WaitAsync(machine, token, task.ThreadId, CurrentRunId(read, task), cancellationToken);
                    if (!TimedOut(settlement.Value))
                    {
                        read = await ReadMetadataAsync(machine, token, task.ThreadId, cancellationToken);
                        requestIds = await PendingRequestIdsAsync(machine, token, task.ThreadId, read, cancellationToken);
                    }
                }
                var observation = Observe(task, read, settlement, requestIds);
                var newEvent = observation.Fingerprint != task.Fingerprint && (observation.Status == "waiting" || !VoiceTools.IsActive(observation));
                if (newEvent)
                {
                    read = await ReadTailAsync(machine, token, task.ThreadId, read, cancellationToken);
                    observation = Observe(task, read, settlement, requestIds);
                    newEvent = observation.Fingerprint != task.Fingerprint && (observation.Status == "waiting" || !VoiceTools.IsActive(observation));
                }
                if (!store.TryObserveTask(task, observation, newEvent ? $"电脑 {machine.Name} 的任务“{task.Title}”状态为 {observation.Status}。待答 requestIds：{JsonSerializer.Serialize(requestIds)}。请阅读事实，处理问题或明显未完成之处，再汇报：\n{read.GetRawText()}" : null, $"task:{task.Id}:{observation.Fingerprint}")) continue;
                if (newEvent) Wake();
                Changed?.Invoke();
                failures = 0;
                if (!VoiceTools.IsActive(observation)) { await tools.DrainQueueAsync(cancellationToken); return; }
                if (observation.Status == "waiting")
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
                    continue;
                }
                if (settlement is null)
                    await WaitAsync(machine, token, task.ThreadId, observation.RunId, cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (RemoteThreadGoneException gone)
            {
                var failed = task with
                {
                    Status = "failed",
                    Fingerprint = $"{task.RunId}:failed:thread_not_found",
                    Snapshot = gone.Failure.GetRawText(),
                    UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
                };
                if (!store.TryObserveTask(task, failed, $"任务“{task.Title}”的远端对话已删除或不存在，已结束跟踪并释放这台电脑。请向用户汇报：\n{gone.Failure.GetRawText()}", $"task:{task.Id}:{failed.Fingerprint}")) continue;
                Wake();
                Changed?.Invoke();
                await tools.DrainQueueAsync(cancellationToken);
                return;
            }
            catch (Exception exception)
            {
                failures++;
                var current = store.Tasks().FirstOrDefault(value => value.Id == taskId);
                if (current is null) return;
                if (current.Status != "offline")
                {
                    store.SaveTask(current with { Status = "offline" });
                    Notice?.Invoke("暂时无法连接云电脑", task.Title + "：" + exception.Message + "\n远端任务不会因此被取消，连接恢复后继续跟踪。", true);
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, failures * 10)), cancellationToken);
            }
        }
    }

    private async Task<JsonElement> ReadMetadataAsync(Machine machine, string token, string threadId, CancellationToken cancellationToken) =>
        await CallAsync(machine, token, "t3_thread_read", JsonSerializer.SerializeToElement(new { threadId, view = "messages", limit = 1, maxCharsPerItem = 1, runLimit = 3 }), cancellationToken);

    private async Task<JsonElement> WaitAsync(Machine machine, string token, string threadId, string? runId, CancellationToken cancellationToken)
    {
        var arguments = new Dictionary<string, object?> { ["threadId"] = threadId, ["timeoutMs"] = 10_000, ["untilDelegatedWorkSettled"] = true };
        if (runId is not null) arguments["runId"] = runId;
        return await CallAsync(machine, token, "t3_thread_wait", JsonSerializer.SerializeToElement(arguments), cancellationToken);
    }

    private async Task<JsonElement> CallAsync(Machine machine, string token, string tool, JsonElement arguments, CancellationToken cancellationToken)
    {
        var result = await remote.CallToolAsync(machine, token, tool, arguments, cancellationToken);
        if (VoiceTools.IsError(result))
        {
            var failure = VoiceTools.Payload(result);
            if (VoiceTools.Text(failure, "_tag") == "OrchestratorMcpFailure" && VoiceTools.Text(failure, "code") == "thread_not_found")
                throw new RemoteThreadGoneException(failure.Clone());
            throw new InvalidOperationException(Clip(result.GetRawText(), 2_000));
        }
        return VoiceTools.Payload(result);
    }

    private sealed class RemoteThreadGoneException(JsonElement failure) : Exception("远端对话不存在。")
    {
        internal JsonElement Failure { get; } = failure;
    }

    private async Task<IReadOnlyList<string>> PendingRequestIdsAsync(Machine machine, string token, string threadId, JsonElement read, CancellationToken cancellationToken)
    {
        if (!HasPendingRequest(read)) return [];
        var result = await CallAsync(machine, token, "t3_pending_request", JsonSerializer.SerializeToElement(new { action = "list", threadId }), cancellationToken);
        return result.TryGetProperty("requestIds", out var ids) ? ids.EnumerateArray().Select(id => id.GetString()!).Order(StringComparer.Ordinal).ToArray() : [];
    }

    private async Task<JsonElement> ReadTailAsync(Machine machine, string token, string threadId, JsonElement metadata, CancellationToken cancellationToken)
    {
        var itemCount = metadata.GetProperty("thread").TryGetProperty("itemCount", out var count) ? count.GetInt64() : 0;
        var after = Math.Max(0, itemCount - 64) - 1;
        var tail = new List<JsonElement>();
        var page = metadata;
        for (var pageIndex = 0; pageIndex < 4; pageIndex++)
        {
            var arguments = new Dictionary<string, object?> { ["threadId"] = threadId, ["view"] = "messages", ["limit"] = 16, ["maxCharsPerItem"] = 2_000, ["runLimit"] = 3 };
            if (after >= 0) arguments["afterPosition"] = after;
            page = await CallAsync(machine, token, "t3_thread_read", JsonSerializer.SerializeToElement(arguments), cancellationToken);
            if (page.TryGetProperty("items", out var items)) tail.AddRange(items.EnumerateArray().Select(item => item.Clone()));
            if (tail.Count > 16) tail.RemoveRange(0, tail.Count - 16);
            if (!page.TryGetProperty("hasMore", out var hasMore) || !hasMore.GetBoolean()) break;
            if (!page.TryGetProperty("nextPosition", out var next) || next.ValueKind != JsonValueKind.Number || next.GetInt64() <= after) break;
            after = next.GetInt64();
        }
        return JsonSerializer.SerializeToElement(new
        {
            thread = page.GetProperty("thread"),
            recentRuns = page.TryGetProperty("recentRuns", out var runs) ? runs : JsonSerializer.SerializeToElement(Array.Empty<object>()),
            items = tail,
            nextPosition = page.TryGetProperty("nextPosition", out var nextPosition) ? nextPosition : JsonSerializer.SerializeToElement<object?>(null),
            hasMore = page.TryGetProperty("hasMore", out var more) && more.GetBoolean(),
            boundedTail = true
        });
    }

    private static bool HasPendingRequest(JsonElement read) => read.TryGetProperty("thread", out var thread) && thread.TryGetProperty("pendingRequestCount", out var count) && count.GetInt32() > 0;
    private static bool TimedOut(JsonElement result) => !result.TryGetProperty("timedOut", out var timedOut) || timedOut.ValueKind != JsonValueKind.False;
    private static string? CurrentRunId(JsonElement read, RemoteTask task) => VoiceTools.Text(read.GetProperty("thread"), "activeRunId") ?? VoiceTools.Text(read.GetProperty("thread"), "latestRunId") ?? task.RunId;
    private static bool IsTerminalStatus(string? status) => status is "idle" or "completed" or "failed" or "cancelled" or "interrupted" or "rolled_back";

    public static RemoteTask Observe(RemoteTask task, JsonElement read, JsonElement? settlement = null, IReadOnlyList<string>? pendingRequestIds = null)
    {
        if (!read.TryGetProperty("thread", out var thread)) throw new InvalidOperationException("远端没有返回有效的对话状态");
        var status = VoiceTools.Text(thread, "status") ?? "running";
        var runId = CurrentRunId(read, task);
        var pending = HasPendingRequest(read);
        var confirmed = settlement is { } settled && !TimedOut(settled);
        if (confirmed)
        {
            var latestRunId = VoiceTools.Text(thread, "latestRunId");
            var waitedRunId = VoiceTools.Text(settlement!.Value, "runId");
            var resultRunId = settlement.Value.TryGetProperty("resultRun", out var settledRun) && settledRun.ValueKind == JsonValueKind.Object ? VoiceTools.Text(settledRun, "runId") : null;
            if (latestRunId is not null && latestRunId != waitedRunId && latestRunId != resultRunId) confirmed = false;
        }
        if (pending) status = "waiting";
        else if (!IsTerminalStatus(status) || !confirmed) status = "running";
        else if (settlement!.Value.TryGetProperty("resultRun", out var resultRun) && resultRun.ValueKind == JsonValueKind.Object)
        {
            runId = VoiceTools.Text(resultRun, "runId") ?? runId;
            status = VoiceTools.Text(resultRun, "status") ?? status;
        }
        var questions = pending ? string.Join(",", (pendingRequestIds ?? []).Order(StringComparer.Ordinal)) : "";
        if (pending && questions.Length == 0) questions = VoiceTools.Text(thread, "updatedAt") ?? "pending";
        var fingerprint = $"{runId}:{status}:{questions}";
        var cursor = thread.TryGetProperty("itemCount", out var itemCount) ? Math.Max(0, itemCount.GetInt64() - 1) : task.Cursor;
        return task with { Status = status, RunId = runId, Title = VoiceTools.Text(thread, "title") ?? task.Title, Cursor = cursor, Fingerprint = fingerprint, Snapshot = read.GetRawText(), UpdatedAt = DateTimeOffset.UtcNow.ToString("O") };
    }

    private async Task QueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await tools.DrainQueueAsync(cancellationToken);
                StartWatchers();
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Notice?.Invoke("排队任务暂未处理", exception.Message + "\n任务已保留，重启程序后继续。", true);
        }
    }
    private static string Clip(string text, int maximum) => text.Length <= maximum ? text : text[..maximum] + "…";

    public async ValueTask DisposeAsync()
    {
        tools.Changed -= OnToolsChanged;
        tools.Asked -= OnAsked;
        await stop.CancelAsync();
        Task[] tasks;
        lock (watcherGate) tasks = watchers.Values.Concat(new[] { worker, queueWorker }.OfType<Task>()).ToArray();
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
        stop.Dispose();
        wake.Dispose();
    }
}
