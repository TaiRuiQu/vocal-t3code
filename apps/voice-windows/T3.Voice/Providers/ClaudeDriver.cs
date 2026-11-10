using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace T3.Voice;

public sealed class ClaudeDriver : IProviderDriver
{
    public async Task<ProviderResult> RunAsync(ProviderRequest request, Action<ProviderEvent> onEvent, CancellationToken cancellationToken)
    {
        var state = new ClaudeStreamState(onEvent, request.SessionId);
        ProviderProcess? process = null;
        try
        {
            var mcp = JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object>
                {
                    ["t3-voice"] = new
                    {
                        type = "http",
                        url = request.Bridge.Url.ToString(),
                        headers = new { Authorization = "Bearer ${T3_VOICE_BRIDGE_TOKEN}" },
                        timeout = 120_000
                    }
                }
            });
            var arguments = new List<string>
            {
                "--print", "--input-format", "stream-json", "--output-format", "stream-json",
                "--verbose", "--include-partial-messages", "--strict-mcp-config", "--mcp-config", mcp,
                "--permission-mode", "bypassPermissions", "--permission-prompts", "none",
                "--tools", "", "--append-system-prompt", request.Instructions,
                "--disable-slash-commands", "--no-chrome",
                "--settings", InvocationSettings(request.WorkingDirectory), "--setting-sources", "user"
            };
            if (!string.IsNullOrWhiteSpace(request.SessionId)) { arguments.Add("--resume"); arguments.Add(request.SessionId); }
            if (!string.IsNullOrWhiteSpace(request.Model)) { arguments.Add("--model"); arguments.Add(request.Model); }
            await using var ownedProcess = process = new ProviderProcess(request, arguments, cancellationToken);
            onEvent(new ProviderEvent("process", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            onEvent(new ProviderEvent("status", "正在连接本机 Claude Code…"));
            var input = JsonSerializer.Serialize(new
            {
                type = "user",
                message = new { role = "user", content = request.Prompt },
                parent_tool_use_id = (string?)null,
                session_id = request.SessionId ?? ""
            });
            await process.Input.WriteLineAsync(input.AsMemory(), cancellationToken);
            await process.Input.FlushAsync(cancellationToken);
            process.Input.Close();
            await foreach (var line in ProviderLines.ReadAsync(process.Output, cancellationToken))
            {
                using var document = JsonDocument.Parse(line);
                state.Accept(document.RootElement);
            }
            await process.WaitForExitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!state.IsCompleted)
                return new ProviderResult(state.SessionId, "", false, process.SafeError("Claude Code 提前退出，没有返回运行结果。"));
            var result = state.Result;
            if (process.ExitCode != 0 || !result.Success)
                return result with { Success = false, Error = process.SafeError(result.Error ?? $"Claude Code 退出码：{process.ExitCode}") };
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ProviderResult(state.SessionId, "", false,
                process is null ? ProviderProcess.Redact(error.Message, request.Bridge.BearerToken) : process.SafeError(error.Message));
        }
        finally
        {
            if (process is not null) onEvent(new ProviderEvent("process-exit", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    internal static string InvocationSettings(string workingDirectory, string? configurationDirectory = null)
    {
        var directory = configurationDirectory ?? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        directory = Path.GetFullPath(directory, workingDirectory);
        var disabledPlugins = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (relativePath, property) in new[] { ("settings.json", "enabledPlugins"), (Path.Combine("plugins", "installed_plugins.json"), "plugins") })
        {
            var path = Path.Combine(directory, relativePath);
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 2 * 1024 * 1024) continue;
                using var file = File.OpenRead(path);
                using var document = JsonDocument.Parse(file);
                if (document.RootElement.TryGetProperty(property, out var plugins) && plugins.ValueKind == JsonValueKind.Object)
                    foreach (var plugin in plugins.EnumerateObject()) disabledPlugins[plugin.Name] = false;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return JsonSerializer.Serialize(new
        {
            disableAllHooks = true,
            autoMemoryEnabled = false,
            disableBundledSkills = true,
            syncClaudeAiSkills = false,
            syncClaudeAiPlugins = false,
            disableClaudeAiConnectors = true,
            enabledPlugins = disabledPlugins
        });
    }
}

internal sealed class ClaudeStreamState
{
    private readonly Action<ProviderEvent> _onEvent;
    private string _lastAssistantText = "";
    public string? SessionId { get; private set; }
    public bool IsCompleted { get; private set; }
    public ProviderResult Result { get; private set; } = new(null, "", false, "Claude Code 没有返回运行结果。");

    public ClaudeStreamState(Action<ProviderEvent> onEvent, string? sessionId = null) { _onEvent = onEvent; SessionId = sessionId; }

    public void Accept(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("type", out var type)) return;
        if (message.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String) return;
        if (message.TryGetProperty("session_id", out var session) && session.ValueKind == JsonValueKind.String && session.GetString() is { Length: > 0 } sessionId && sessionId != SessionId)
        {
            SessionId = sessionId;
            _onEvent(new ProviderEvent("session", sessionId));
        }
        switch (type.GetString())
        {
            case "system":
                if (message.TryGetProperty("subtype", out var subtype) && subtype.GetString() == "init")
                {
                    if (message.TryGetProperty("mcp_servers", out var servers))
                        foreach (var server in servers.EnumerateArray())
                            if (server.TryGetProperty("name", out var name) && name.GetString() == "t3-voice" && server.TryGetProperty("status", out var status) && status.GetString() == "failed")
                                throw new InvalidOperationException("Claude Code 无法连接本机 T3 语音工具桥。");
                    _onEvent(new ProviderEvent("status", "编排 agent 正在处理…"));
                }
                break;
            case "stream_event":
                if (message.TryGetProperty("event", out var streamEvent) && streamEvent.TryGetProperty("delta", out var delta) && delta.TryGetProperty("type", out var deltaType) && deltaType.GetString() == "text_delta" && delta.TryGetProperty("text", out var text))
                    _onEvent(new ProviderEvent("delta", text.GetString() ?? ""));
                break;
            case "assistant":
                if (message.TryGetProperty("message", out var assistant) && assistant.TryGetProperty("content", out var content))
                {
                    _lastAssistantText = string.Join("\n", content.EnumerateArray().Where(block => block.TryGetProperty("type", out var blockType) && blockType.GetString() == "text").Select(block => block.GetProperty("text").GetString()));
                    if (_lastAssistantText.Length > 1024 * 1024) throw new InvalidDataException("编排回复超过 1 MiB。");
                }
                break;
            case "result":
                var success = !(message.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True);
                if (message.TryGetProperty("subtype", out var resultType) && resultType.GetString() is { } resultSubtype && resultSubtype != "success") success = false;
                var resultText = message.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String ? result.GetString() ?? "" : _lastAssistantText;
                if (resultText.Length > 1024 * 1024) throw new InvalidDataException("编排回复超过 1 MiB。");
                string? error = null;
                if (!success)
                {
                    if (message.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array) error = string.Join("\n", errors.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : "Claude Code 运行失败。"));
                    if (string.IsNullOrWhiteSpace(error)) error = string.IsNullOrWhiteSpace(resultText) ? "Claude Code 运行失败。" : resultText;
                }
                Result = new ProviderResult(SessionId, resultText, success, error);
                IsCompleted = true;
                break;
        }
    }
}
