using System.Collections.Generic;
using System.Text.Json;

namespace T3.Voice;

internal static class CodexInvocation
{
    private static readonly string[] DisabledFeatures =
    [
        "plugins", "remote_plugin", "plugin_discovery_prefetch", "apps", "hooks", "shell_tool",
        "shell_snapshot", "multi_agent", "computer_use", "browser_use", "image_generation", "view_image"
    ];

    public static IEnumerable<string> Arguments()
    {
        foreach (var feature in DisabledFeatures)
        {
            yield return "-c";
            yield return $"features.{feature}=false";
        }
        foreach (var value in new[] { "web_search=\"disabled\"", "apps._default.enabled=false", "plugins._default.enabled=false", "skills.bundled.enabled=false", "skills.include_instructions=false", "project_doc_max_bytes=0" })
        {
            yield return "-c";
            yield return value;
        }
        yield return "app-server";
        yield return "--listen";
        yield return "stdio://";
    }

    public static Dictionary<string, object?> ThreadConfig(JsonElement effective, BridgeEndpoint bridge)
    {
        var config = new Dictionary<string, object?>();
        if (effective.TryGetProperty("config", out var saved) && saved.TryGetProperty("mcp_servers", out var servers) && servers.ValueKind == JsonValueKind.Object)
            foreach (var server in servers.EnumerateObject()) config[$"mcp_servers.{server.Name}.enabled"] = false;
        config["mcp_servers.t3-voice"] = new
        {
            enabled = true,
            url = bridge.Url.ToString(),
            bearer_token_env_var = "T3_VOICE_BRIDGE_TOKEN",
            required = true,
            startup_timeout_sec = 30,
            tool_timeout_sec = 120
        };
        return config;
    }
}
