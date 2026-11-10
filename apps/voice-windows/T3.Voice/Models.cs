using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace T3.Voice;

public sealed record Machine
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "云电脑";
    public string Url { get; init; } = "";
}

public sealed record AppSettings
{
    public string Provider { get; init; } = "codex";
    public string CodexPath { get; init; } = "codex";
    public string ClaudePath { get; init; } = "claude";
    public string CodexModel { get; init; } = "";
    public string ClaudeModel { get; init; } = "";
    public string PopupPosition { get; init; } = "right";
    public string Theme { get; init; } = "system";
    public bool AutoStart { get; init; } = true;
    public string AsrEndpoint { get; init; } = "wss://dashscope.aliyuncs.com/api-ws/v1/inference";
    public string AsrModel { get; init; } = "fun-asr-realtime";
    public string AsrWorkspace { get; init; } = "";
    public int MicrophoneId { get; init; } = -1;
    public List<Machine> Machines { get; init; } = [];
}

public sealed record BridgeEndpoint(Uri Url, string BearerToken);
public sealed record ProviderRequest(
    string Prompt,
    string? SessionId,
    string Instructions,
    BridgeEndpoint Bridge,
    string WorkingDirectory,
    string Executable,
    string? Model = null);
public sealed record ProviderEvent(string Kind, string Text);
public sealed record ProviderResult(string? SessionId, string Text, bool Success, string? Error = null);

public interface IProviderDriver
{
    Task<ProviderResult> RunAsync(ProviderRequest request, Action<ProviderEvent> onEvent, CancellationToken cancellationToken);
}

public sealed record RemoteTask
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string MachineId { get; init; } = "";
    public string ThreadId { get; init; } = "";
    public string Title { get; init; } = "远端任务";
    public string Status { get; init; } = "running";
    public long Cursor { get; init; }
    public string? RunId { get; init; }
    public string Fingerprint { get; init; } = "";
    public string Snapshot { get; init; } = "{}";
    public string UpdatedAt { get; init; } = DateTimeOffset.UtcNow.ToString("O");
}

public sealed record ChatMessage(long Id, string Role, string Text, string CreatedAt);
public sealed record InboxMessage(long Id, string Kind, string Text, string DeduplicationKey);
public sealed record OAuthCredential(string AccessToken, DateTimeOffset? ExpiresAt);

public interface IRemoteMcpClient
{
    Task<JsonElement> ListToolsAsync(Machine machine, string accessToken, CancellationToken cancellationToken);
    Task<JsonElement> CallToolAsync(Machine machine, string accessToken, string name, JsonElement arguments, CancellationToken cancellationToken);
}
