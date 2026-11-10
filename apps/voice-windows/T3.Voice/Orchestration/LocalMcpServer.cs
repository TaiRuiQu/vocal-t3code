using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace T3.Voice;

public sealed class LocalMcpServer(VoiceTools tools) : IAsyncDisposable
{
    private WebApplication? app;
    private readonly string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public BridgeEndpoint Endpoint { get; private set; } = null!;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Limits.MaxRequestBodySize = 1_048_576).UseUrls("http://127.0.0.1:0");
        app = builder.Build();
        app.MapPost("/mcp", async context =>
        {
            var supplied = context.Request.Headers.Authorization.ToString();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes("Bearer " + token)))
            { context.Response.StatusCode = 401; return; }
            JsonElement request;
            try { request = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, cancellationToken: context.RequestAborted); }
            catch (JsonException) { context.Response.StatusCode = 400; return; }
            var method = VoiceTools.Text(request, "method");
            if (!request.TryGetProperty("id", out var id)) { context.Response.StatusCode = 202; return; }
            try
            {
                object result = method switch
                {
                    "initialize" => new { protocolVersion = "2025-03-26", capabilities = new { tools = new { listChanged = false } }, serverInfo = new { name = "t3-voice", version = "0.2.0" } },
                    "ping" => new { },
                    "tools/list" => VoiceTools.Catalog(),
                    "tools/call" => await tools.CallAsync(VoiceTools.Required(request.GetProperty("params"), "name"), request.GetProperty("params").TryGetProperty("arguments", out var arguments) ? arguments : JsonSerializer.SerializeToElement(new { }), context.RequestAborted),
                    _ => throw new InvalidOperationException("不支持的 MCP 方法：" + method)
                };
                await context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id, result }, VoiceStore.Json, context.RequestAborted);
            }
            catch (Exception exception) when (!context.RequestAborted.IsCancellationRequested)
            {
                if (method == "tools/call") await context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id, result = new { isError = true, content = new[] { new { type = "text", text = exception.Message } } } }, VoiceStore.Json, context.RequestAborted);
                else await context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id, error = new { code = -32601, message = exception.Message } }, VoiceStore.Json, context.RequestAborted);
            }
        });
        await app.StartAsync(cancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Endpoint = new(new Uri(address + "/mcp"), token);
    }

    public async ValueTask DisposeAsync()
    {
        if (app is null) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await app.StopAsync(deadline.Token); }
        finally { await app.DisposeAsync(); }
    }
}
