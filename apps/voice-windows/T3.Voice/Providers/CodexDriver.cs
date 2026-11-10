using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace T3.Voice;

public sealed class CodexDriver : IProviderDriver
{
    public async Task<ProviderResult> RunAsync(ProviderRequest request, Action<ProviderEvent> onEvent, CancellationToken cancellationToken)
    {
        string? sessionId = request.SessionId;
        ProviderProcess? process = null;
        try
        {
            await using var connection = new ProviderRpcSession(request, cancellationToken);
            process = connection.Process;
            onEvent(new ProviderEvent("process", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var rpc = connection.Rpc;
            var state = new CodexTurnState(onEvent);
            rpc.Notification = state.Accept;
            rpc.ServerRequest = (method, parameters) => AnswerRequestAsync(method, parameters, state, onEvent);
            onEvent(new ProviderEvent("status", "正在连接本机 Codex…"));
            await rpc.RequestAsync("initialize", new
            {
                clientInfo = new { name = "t3_voice_windows", title = "T3 Voice", version = "0.2.0" },
                capabilities = new { experimentalApi = false }
            }, cancellationToken);
            await rpc.NotifyAsync("initialized", new { }, cancellationToken);

            var effective = await rpc.RequestAsync("config/read", new { includeLayers = false, cwd = request.WorkingDirectory }, cancellationToken);
            var config = CodexInvocation.ThreadConfig(effective, request.Bridge);
            var threadParameters = new Dictionary<string, object?>
            {
                ["cwd"] = request.WorkingDirectory,
                ["approvalPolicy"] = "never",
                ["sandbox"] = "danger-full-access",
                ["developerInstructions"] = request.Instructions,
                ["config"] = config
            };
            if (!string.IsNullOrWhiteSpace(request.Model)) threadParameters["model"] = request.Model;
            var method = "thread/start";
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                method = "thread/resume";
                threadParameters["threadId"] = sessionId;
                threadParameters["excludeTurns"] = true;
            }
            var thread = await rpc.RequestAsync(method, threadParameters, cancellationToken);
            sessionId = thread.GetProperty("thread").GetProperty("id").GetString()
                ?? throw new InvalidDataException("Codex 没有返回会话编号。");
            onEvent(new ProviderEvent("session", sessionId));
            state.Begin(sessionId);
            var turnParameters = new Dictionary<string, object?>
            {
                ["threadId"] = sessionId,
                ["input"] = new[] { new { type = "text", text = request.Prompt } },
                ["approvalPolicy"] = "never",
                ["sandboxPolicy"] = new { type = "dangerFullAccess" }
            };
            if (!string.IsNullOrWhiteSpace(request.Model)) turnParameters["model"] = request.Model;
            var started = await rpc.RequestAsync("turn/start", turnParameters, cancellationToken);
            if (started.TryGetProperty("turn", out var turn) && turn.TryGetProperty("id", out var turnId)) state.SetTurnId(turnId.GetString());
            await Task.WhenAny(state.Completed.Task, rpc.Completion).WaitAsync(cancellationToken);
            if (!state.Completed.Task.IsCompleted) await rpc.Completion;
            var result = await state.Completed.Task;
            cancellationToken.ThrowIfCancellationRequested();
            if (state.IsPaused && state.TurnId is not null)
                await rpc.RequestAsync("turn/interrupt", new { threadId = sessionId, turnId = state.TurnId }, cancellationToken);
            return result with { SessionId = sessionId, Error = result.Error is null ? null : process.SafeError(result.Error) };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ProviderResult(sessionId, "", false,
                process is null ? ProviderProcess.Redact(error.Message, request.Bridge.BearerToken) : process.SafeError(error.Message));
        }
        finally
        {
            if (process is not null) onEvent(new ProviderEvent("process-exit", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    internal static Task<object?> AnswerRequestAsync(string method, JsonElement parameters, CodexTurnState state, Action<ProviderEvent> onEvent)
    {
        object result;
        switch (method)
        {
            case "item/commandExecution/requestApproval":
            case "item/fileChange/requestApproval":
                result = new { decision = "acceptForSession" };
                break;
            case "item/permissions/requestApproval":
                result = new { permissions = parameters.GetProperty("permissions").Clone(), scope = "session" };
                break;
            case "item/tool/requestUserInput":
            case "tool/requestUserInput":
                var questions = parameters.TryGetProperty("questions", out var values)
                    ? string.Join("\n", values.EnumerateArray().Select(question => question.TryGetProperty("question", out var text) ? text.GetString() : "请补充任务信息。"))
                    : "请补充任务信息。";
                onEvent(new ProviderEvent("question", questions));
                state.PauseForQuestion(questions);
                result = new { answers = new Dictionary<string, object>() };
                break;
            case "mcpServer/elicitation/request":
                var question = parameters.TryGetProperty("message", out var message) ? message.GetString() ?? "远端需要补充信息。" : "远端需要补充信息。";
                onEvent(new ProviderEvent("question", question));
                state.PauseForQuestion(question);
                result = new { action = "cancel", content = (object?)null };
                break;
            case "currentTime/read":
                result = new { currentTimeAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                break;
            default:
                throw new NotSupportedException($"Codex 请求需要受支持的客户端处理：{method}");
        }
        return Task.FromResult<object?>(result);
    }
}

internal sealed class CodexTurnState
{
    private sealed record Message(string Text, string? Phase);
    private readonly Action<ProviderEvent> _onEvent;
    private readonly Dictionary<string, Message> _messages = new();
    private readonly List<string> _order = [];
    private string? _threadId;
    private string? _turnId;
    private int _totalCharacters;
    public bool IsPaused { get; private set; }
    public string? TurnId => _turnId;
    public TaskCompletionSource<ProviderResult> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CodexTurnState(Action<ProviderEvent> onEvent) => _onEvent = onEvent;
    public void Begin(string threadId) => _threadId = threadId;
    public void SetTurnId(string? turnId) => _turnId ??= turnId;
    public void PauseForQuestion(string question)
    {
        IsPaused = true;
        Completed.TrySetResult(new ProviderResult(_threadId, question, true));
    }

    public void Accept(string method, JsonElement parameters)
    {
        if (_threadId is null || parameters.ValueKind != JsonValueKind.Object) return;
        if (parameters.TryGetProperty("threadId", out var threadId) && threadId.GetString() != _threadId) return;
        if (parameters.TryGetProperty("turnId", out var turnId) && _turnId is not null && turnId.GetString() != _turnId) return;
        switch (method)
        {
            case "turn/started":
                if (parameters.TryGetProperty("turn", out var started)) SetTurnId(started.GetProperty("id").GetString());
                _onEvent(new ProviderEvent("status", "编排 agent 正在处理…"));
                break;
            case "item/agentMessage/delta":
                var id = parameters.GetProperty("itemId").GetString() ?? "message";
                var delta = parameters.GetProperty("delta").GetString() ?? "";
                if (!_messages.TryGetValue(id, out var old)) old = new Message("", null);
                Store(id, old.Text + delta, old.Phase);
                _onEvent(new ProviderEvent("delta", delta));
                break;
            case "item/completed":
                if (parameters.TryGetProperty("item", out var item)) Collect(item);
                break;
            case "item/started":
                if (parameters.TryGetProperty("item", out var active) && active.TryGetProperty("type", out var type) && type.GetString() == "mcpToolCall")
                    _onEvent(new ProviderEvent("status", "正在操作云端 T3…"));
                break;
            case "turn/completed":
                var turn = parameters.GetProperty("turn");
                if (_turnId is not null && turn.GetProperty("id").GetString() != _turnId) return;
                if (turn.TryGetProperty("items", out var items)) foreach (var completedItem in items.EnumerateArray()) Collect(completedItem);
                var status = turn.GetProperty("status").GetString();
                string? error = null;
                if (turn.TryGetProperty("error", out var failure) && failure.ValueKind == JsonValueKind.Object && failure.TryGetProperty("message", out var detail)) error = detail.GetString();
                Completed.TrySetResult(new ProviderResult(_threadId, FinalText(), status == "completed", error ?? (status == "completed" ? null : $"Codex 运行状态：{status}")));
                break;
        }
    }

    private void Collect(JsonElement item)
    {
        if (!item.TryGetProperty("type", out var type) || type.GetString() != "agentMessage") return;
        var id = item.GetProperty("id").GetString() ?? "message";
        var text = item.TryGetProperty("text", out var value) ? value.GetString() ?? "" : "";
        var phase = item.TryGetProperty("phase", out var tag) && tag.ValueKind == JsonValueKind.String ? tag.GetString() : null;
        Store(id, text, phase);
    }

    private void Store(string id, string text, string? phase)
    {
        var previousLength = _messages.TryGetValue(id, out var previous) ? previous.Text.Length : 0;
        if (text.Length > 1024 * 1024 || _totalCharacters - previousLength + text.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("编排回复超过允许的文本大小。");
        if (!_messages.ContainsKey(id))
        {
            if (_order.Count >= 128) throw new InvalidDataException("单次编排包含过多回复消息。");
            _order.Add(id);
        }
        _totalCharacters += text.Length - previousLength;
        _messages[id] = new Message(text, phase);
    }

    private string FinalText()
    {
        var messages = _order.Select(id => _messages[id]).ToArray();
        var final = messages.LastOrDefault(message => message.Phase == "final_answer");
        return final?.Text ?? string.Join("\n\n", messages.Select(message => message.Text));
    }
}
