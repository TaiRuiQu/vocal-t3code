using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace T3.Voice;

public sealed record SavedCall(string MachineId, string Tool, string Arguments, string State, string? Result, long InboxId, string? CredentialFingerprint = null);
public sealed record SavedCatalog(string Origin, string Json, string UpdatedAt);

public sealed class VoiceTools(VoiceStore store, IRemoteMcpClient remote)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> machineLocks = new();
    public long ActiveInboxId { get; set; }
    public event Action? Changed;
    public event Action<string>? Asked;
    public static bool IsActive(RemoteTask task) => task.Status is "running" or "waiting" or "offline";

    private static object Schema(object properties, params string[] required) => new { type = "object", properties, required, additionalProperties = false };
    private static object StringField(string description) => new { type = "string", description };
    private static object Tool(string name, string description, object schema, bool readOnly = false) => new { name, description, inputSchema = schema, annotations = new { readOnlyHint = readOnly } };
    public static object Catalog() => new
    {
        tools = new[] {
        Tool("voice_machines", "List configured computers and tracked tasks. No credentials. Inspect remote projects and provider capabilities before choosing a destination.", Schema(new { }), true),
        Tool("voice_remote_tools", "Discover a computer's T3 tools and their exact schemas. Use the returned names and fields with voice_remote_call.", Schema(new { machineId = StringField("Computer id from voice_machines") }, "machineId"), true),
        Tool("voice_remote_call", "Call any discovered T3 tool on a computer. New thread launches queue durably when that computer is busy. Launches and follow-ups are tracked automatically; completion wakes this coordinator. End your turn after dispatch. Do not wait or poll. Retry ambiguous operations with identical arguments.", Schema(new { machineId = StringField("Computer id"), tool = StringField("Exact T3 tool name"), arguments = new { type = "object", additionalProperties = true } }, "machineId", "tool", "arguments")),
        Tool("voice_tasks", "Read tracked remote tasks and queued operations. action=cancel_queue with queueId cancels an operation that has not been sent. Stop remote work through its T3 interrupt tool.", Schema(new { action = StringField("list (default), or cancel_queue"), queueId = StringField("Queued call id") })),
        Tool("voice_track_thread", "Track an existing T3 conversation for completion or questions. Useful when adopting work started outside this assistant.", Schema(new { machineId = StringField("Computer id"), threadId = StringField("T3 thread id"), title = StringField("Short task title") }, "machineId", "threadId", "title")),
        Tool("voice_ask_user", "Show a persistent question to the user. Their next voice input answers it unless they explicitly start a new task. Ask only for missing intent or information, not operation approval. End your turn after asking.", Schema(new { question = StringField("Clear Chinese question with enough context") }, "question")),
        Tool("voice_questions", "List persistent questions. action=resolve with key removes a question only after its answer has been used or the user explicitly dismissed it. Starting a new task does not resolve another question.", Schema(new { action = StringField("list (default), or resolve"), key = StringField("Question key") })),
        Tool("voice_memory", "Read or save durable project aliases, preferences, and context shared across provider switches and restarts. Omit value to read; empty value deletes. Never store credentials here.", Schema(new { key = StringField("Memory key; omit to list"), value = StringField("Memory text") }))
    }
    };

    public async Task<JsonElement> CallAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case "voice_machines": return Result(new { machines = store.LoadSettings().Machines, tasks = store.Tasks().Take(100).Select(t => new { t.Id, t.MachineId, t.ThreadId, t.Title, t.Status }) });
            case "voice_tasks":
                if (Text(arguments, "action") == "cancel_queue")
                {
                    var key = "call:" + Required(arguments, "queueId");
                    var queued = store.Get<SavedCall>(key) ?? throw new InvalidOperationException("找不到排队任务");
                    var queueGate = machineLocks.GetOrAdd(queued.MachineId, _ => new(1, 1));
                    await queueGate.WaitAsync(cancellationToken);
                    try
                    {
                        queued = store.Get<SavedCall>(key) ?? throw new InvalidOperationException("找不到排队任务");
                        if (queued.State != "queued") throw new InvalidOperationException("任务已经下发，请使用远端中断工具");
                        store.Put(key, queued with { State = "cancelled" });
                        return Result(new { cancelled = true });
                    }
                    finally { queueGate.Release(); }
                }
                return Result(new { tasks = store.Tasks().Take(100), queued = store.List<SavedCall>("call:").Where(x => x.Value.State == "queued").Select(x => new { id = x.Key[5..], x.Value.MachineId, x.Value.Tool, x.Value.Arguments }) });
            case "voice_memory":
                var memoryKey = Text(arguments, "key");
                if (string.IsNullOrWhiteSpace(memoryKey)) return Result(store.List<string>("memory:"));
                var value = Text(arguments, "value");
                if (value is not null)
                {
                    if (value.Length > 16_000 || memoryKey.Length > 200) throw new InvalidOperationException("记忆内容过长");
                    if (value.Length == 0) store.Delete("memory:" + memoryKey); else store.Put("memory:" + memoryKey, value);
                }
                return Result(new { key = memoryKey, value = store.Get<string>("memory:" + memoryKey) });
            case "voice_ask_user":
                var question = Required(arguments, "question");
                if (!store.List<string>("question:").Any(q => q.Value == question)) store.Put("question:" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfffffff") + Guid.NewGuid().ToString("N"), question);
                Asked?.Invoke(store.List<string>("question:").First().Value);
                return Result(new { delivered = true, next = "End your turn and wait for voice input." });
            case "voice_questions":
                if (Text(arguments, "action") == "resolve")
                {
                    var key = Required(arguments, "key");
                    if (!key.StartsWith("question:", StringComparison.Ordinal)) throw new InvalidOperationException("问题 key 无效");
                    store.Delete(key);
                    var next = store.List<string>("question:").FirstOrDefault();
                    if (next.Value is not null) Asked?.Invoke(next.Value);
                }
                return Result(new { questions = store.List<string>("question:") });
            case "voice_track_thread":
                var trackedMachine = FindMachine(Required(arguments, "machineId"));
                Track(trackedMachine.Id, Required(arguments, "threadId"), Required(arguments, "title"), null);
                return Result(new { tracked = true });
            case "voice_remote_tools":
                var machine = FindMachine(Required(arguments, "machineId"));
                return Result(await GetCatalogAsync(machine, cancellationToken));
            case "voice_remote_call":
                return await RemoteCallAsync(FindMachine(Required(arguments, "machineId")), Required(arguments, "tool"), arguments.GetProperty("arguments"), ActiveInboxId, cancellationToken);
            default: throw new InvalidOperationException("未知工具：" + name);
        }
    }

    public Machine FindMachine(string id) => store.LoadSettings().Machines.FirstOrDefault(m => m.Id == id) ?? throw new InvalidOperationException("未配置这台云电脑，请在设置中连接");
    public string Credential(Machine machine) => store.GetSecret("machine:" + machine.Id) ?? throw new InvalidOperationException($"请重新授权 {machine.Name}");

    public async Task<JsonElement> GetCatalogAsync(Machine machine, CancellationToken cancellationToken)
    {
        var credential = Credential(machine);
        try
        {
            var catalog = await remote.ListToolsAsync(machine, credential, cancellationToken);
            SaveCatalog(machine, catalog, credential);
            return catalog;
        }
        catch (Exception error) when (CanQueueDiscoveryFailure(error, cancellationToken))
        {
            var cached = store.Get<SavedCatalog>("catalog:" + machine.Id);
            if (cached is null || cached.Origin != CatalogOrigin(machine)) throw;
            var parsed = JsonNode.Parse(cached.Json)?.AsObject();
            if (parsed is null || parsed["tools"] is not JsonArray) throw;
            parsed["stale"] = true;
            parsed["offline"] = true;
            parsed["cachedAt"] = cached.UpdatedAt;
            parsed["next"] = "This is a cached schema, not evidence the host is online. A new t3_thread_launch can be saved to its durable offline queue. Other operations require reconnection.";
            return JsonSerializer.SerializeToElement(parsed, VoiceStore.Json);
        }
    }

    private void SaveCatalog(Machine machine, JsonElement catalog, string credential)
    {
        if (catalog.ValueKind != JsonValueKind.Object || !catalog.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array) return;
        var json = catalog.GetRawText().Replace(credential, "[credential redacted]", StringComparison.Ordinal);
        store.Put("catalog:" + machine.Id, new SavedCatalog(CatalogOrigin(machine), json, DateTimeOffset.UtcNow.ToString("O")));
    }

    private static string CatalogOrigin(Machine machine) => RemoteHttp.Origin(new Uri(machine.Url, UriKind.Absolute)).AbsoluteUri;

    public async Task<JsonElement> RemoteCallAsync(Machine machine, string tool, JsonElement arguments, long inboxId, CancellationToken cancellationToken, bool fromQueue = false)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("arguments 必须是对象");
        if (arguments.TryGetProperty("environmentId", out _))
            throw new InvalidOperationException("请使用 machineId 直接选择已配置的云电脑，不要在工具参数中转发 environmentId。");
        var gate = machineLocks.GetOrAdd(machine.Id, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{inboxId}:{machine.Id}:{tool}:{Canonical(arguments)}"))).ToLowerInvariant();
            var saved = store.Get<SavedCall>("call:" + hash);
            if (saved?.State == "completed" && saved.Result is not null)
            {
                var cached = JsonSerializer.Deserialize<JsonElement>(saved.Result);
                if (!IsError(cached)) TrackResult(machine, tool, arguments, Payload(cached));
                return cached;
            }
            if (saved?.State == "cancelled") return Result(new { cancelled = true });
            if (saved?.State == "queued" && !fromQueue) return Result(new { queued = true, queueId = hash, next = "The queue resumes automatically. End this turn." });
            var credential = Credential(machine);
            var credentialFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));
            if (saved?.State == "pending" && saved.CredentialFingerprint != credentialFingerprint)
                throw new InvalidOperationException("上次调用的结果尚不明确，且远端授权已更换。请先读取远端状态核实，不能自动重试此操作。");
            JsonElement QueueLaunch(string reason)
            {
                if (saved?.State != "queued")
                {
                    store.Put("call:" + hash, new SavedCall(machine.Id, tool, arguments.GetRawText(), "queued", null, inboxId, credentialFingerprint));
                    Changed?.Invoke();
                }
                return Result(new { queued = true, queueId = hash, reason, next = "The unsent task is saved. Dispatch resumes automatically when the computer is available; end this turn." });
            }
            if (tool == "t3_thread_launch" && saved?.State != "pending" && store.Tasks().Any(t => t.MachineId == machine.Id && IsActive(t)))
                return QueueLaunch("busy");

            JsonElement discovered;
            try
            {
                discovered = await remote.ListToolsAsync(machine, credential, cancellationToken);
                SaveCatalog(machine, discovered, credential);
            }
            catch (Exception error) when (tool == "t3_thread_launch" && saved?.State != "pending" && CanQueueDiscoveryFailure(error, cancellationToken))
            {
                return QueueLaunch("offline");
            }
            var catalog = discovered.TryGetProperty("tools", out var tools) ? tools : discovered;
            var metadata = catalog.EnumerateArray().FirstOrDefault(t => Text(t, "name") == tool);
            if (metadata.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("这台电脑未提供工具：" + tool);
            var readOnly = metadata.TryGetProperty("annotations", out var annotations) && annotations.TryGetProperty("readOnlyHint", out var hint) && hint.ValueKind == JsonValueKind.True;
            var acceptsKey = metadata.TryGetProperty("inputSchema", out var schema) && schema.TryGetProperty("properties", out var properties) && properties.TryGetProperty("clientRequestId", out _);
            if (saved?.State == "pending" && !acceptsKey && !readOnly) throw new InvalidOperationException("上次调用的结果尚不明确。请先读取远端状态核实，不能重复下发此操作。");

            var outgoing = JsonNode.Parse(arguments.GetRawText())!.AsObject();
            if (acceptsKey && !outgoing.ContainsKey("clientRequestId")) outgoing["clientRequestId"] = "voice:" + hash;
            if (!readOnly) store.Put("call:" + hash, new SavedCall(machine.Id, tool, arguments.GetRawText(), "pending", null, inboxId, credentialFingerprint));
            var result = await remote.CallToolAsync(machine, credential, tool, JsonSerializer.SerializeToElement(outgoing, VoiceStore.Json), cancellationToken);
            if (!readOnly) store.Put("call:" + hash, new SavedCall(machine.Id, tool, arguments.GetRawText(), "completed", result.GetRawText(), inboxId, credentialFingerprint));
            if (!IsError(result)) TrackResult(machine, tool, arguments, Payload(result));
            return result;
        }
        finally { gate.Release(); }
    }

    private static bool CanQueueDiscoveryFailure(Exception error, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && (error is HttpRequestException or TimeoutException or OperationCanceledException ||
        error is RemoteMcpException { StatusCode: { } status } && ((int)status >= 500 ||
            status is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests));

    private void TrackResult(Machine machine, string tool, JsonElement arguments, JsonElement result)
    {
        if (tool is not ("t3_thread_launch" or "create_threads" or "t3_thread_send")) return;
        if (result.TryGetProperty("threads", out var threads))
        {
            foreach (var thread in threads.EnumerateArray()) TrackResult(machine, "t3_thread_launch", arguments, thread);
            return;
        }
        var threadId = Text(result, "threadId") ?? Text(arguments, "threadId");
        if (threadId is null && result.TryGetProperty("thread", out var nested)) threadId = Text(nested, "threadId") ?? Text(nested, "id");
        if (threadId is not null) Track(machine.Id, threadId, Text(result, "title") ?? Text(arguments, "title") ?? "远端任务", Text(result, "runId"));
    }

    public void Track(string machineId, string threadId, string title, string? runId)
    {
        var existing = store.Tasks().FirstOrDefault(t => t.MachineId == machineId && t.ThreadId == threadId);
        if (existing is not null && runId is not null && runId == existing.RunId) return;
        store.SaveTask((existing ?? new RemoteTask { MachineId = machineId, ThreadId = threadId }) with { Title = title, RunId = runId ?? existing?.RunId, Status = "running", Fingerprint = "", UpdatedAt = DateTimeOffset.UtcNow.ToString("O") });
        Changed?.Invoke();
    }

    public async Task DrainQueueAsync(CancellationToken cancellationToken)
    {
        foreach (var entry in store.List<SavedCall>("call:").Where(e => e.Value.State == "queued").OrderBy(e => e.Value.InboxId))
        {
            var call = entry.Value;
            if (store.Tasks().Any(t => t.MachineId == call.MachineId && IsActive(t))) continue;
            try
            {
                var result = await RemoteCallAsync(FindMachine(call.MachineId), call.Tool, JsonSerializer.Deserialize<JsonElement>(call.Arguments), call.InboxId, cancellationToken, true);
                if (Payload(result).TryGetProperty("queued", out _)) continue;
                store.Enqueue("remote", "排队操作的执行结果：" + result.GetRawText(), "queue-result:" + entry.Key);
                Changed?.Invoke();
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                if (store.Enqueue("remote", "排队任务暂未执行：" + exception.Message, "queue-error:" + entry.Key)) Changed?.Invoke();
            }
        }
    }

    public static string? Text(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    public static string Required(JsonElement value, string property) => Text(value, property) is { Length: > 0 } text ? text : throw new InvalidOperationException("缺少参数：" + property);
    public static bool IsError(JsonElement result) => result.ValueKind == JsonValueKind.Object && result.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True;
    public static JsonElement Payload(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) return result;
        if (result.TryGetProperty("structuredContent", out var structured)) return structured;
        if (result.TryGetProperty("content", out var content))
            foreach (var block in content.EnumerateArray())
                if (Text(block, "type") == "text" && Text(block, "text") is { } text)
                    try { return JsonSerializer.Deserialize<JsonElement>(text); } catch (JsonException) { }
        return result;
    }
    public static JsonElement Result(object value) => JsonSerializer.SerializeToElement(new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(value, VoiceStore.Json) } }, structuredContent = JsonSerializer.SerializeToElement(value, VoiceStore.Json) }, VoiceStore.Json);
    private static string Canonical(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(Canonical)) + "]",
        _ => value.GetRawText()
    };
}
