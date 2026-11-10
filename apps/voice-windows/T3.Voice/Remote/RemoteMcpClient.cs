using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace T3.Voice;

public sealed class RemoteMcpClient : IRemoteMcpClient, IDisposable
{
    private const string RequestedProtocol = "2025-06-18";
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private long _nextRequestId;

    private sealed class Session
    {
        internal readonly SemaphoreSlim Initialization = new(1, 1);
        internal string? Id;
        internal string? Protocol;
    }

    public RemoteMcpClient() : this(RemoteHttp.CreateClient(), true) { }
    public RemoteMcpClient(HttpClient httpClient) : this(httpClient, false) { }
    private RemoteMcpClient(HttpClient httpClient, bool ownsHttp)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttp = ownsHttp;
    }

    public async Task<JsonElement> ListToolsAsync(Machine machine, string accessToken, CancellationToken cancellationToken)
    {
        var tools = new List<JsonElement>();
        var totalBytes = 0;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        JsonElement firstPage = default;
        string? cursor = null;
        for (var page = 0; page < 32; page++)
        {
            var result = await RequestAsync(machine, accessToken, "tools/list",
                cursor is null ? new { } : (object)new { cursor }, cancellationToken).ConfigureAwait(false);
            if (page == 0) firstPage = result;
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("tools", out var pageTools) ||
                pageTools.ValueKind != JsonValueKind.Array)
                throw new RemoteMcpException("远端工具目录格式无效。");
            foreach (var tool in pageTools.EnumerateArray())
            {
                if (tools.Count >= 4096) throw new RemoteMcpException("远端工具目录超过大小限制。");
                totalBytes += Encoding.UTF8.GetByteCount(tool.GetRawText());
                if (totalBytes > RemoteHttp.MaximumResponseBytes) throw new RemoteMcpException("远端工具目录超过大小限制。");
                tools.Add(tool.Clone());
            }
            if (!result.TryGetProperty("nextCursor", out var next) || next.ValueKind == JsonValueKind.Null)
            {
                var combined = firstPage.EnumerateObject()
                    .Where(property => property.Name != "tools" && property.Name != "nextCursor")
                    .ToDictionary(property => property.Name, property => (object)property.Value.Clone());
                combined["tools"] = tools;
                return JsonSerializer.SerializeToElement(combined);
            }
            if (next.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(cursor = next.GetString()) ||
                !cursors.Add(cursor))
                throw new RemoteMcpException("远端工具目录的分页游标无效。");
        }
        throw new RemoteMcpException("远端工具目录分页过多。");
    }

    public Task<JsonElement> CallToolAsync(Machine machine, string accessToken, string name, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("工具名称不能为空。", nameof(name));
        if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("工具参数必须是 JSON 对象。", nameof(arguments));
        return RequestAsync(machine, accessToken, "tools/call", new { name, arguments }, cancellationToken);
    }

    private async Task<JsonElement> RequestAsync(Machine machine, string token, string method, object parameters,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new RemoteMcpException("这台云电脑尚未授权。", HttpStatusCode.Unauthorized);
        var endpoint = new Uri(RemoteHttp.Origin(new Uri(machine.Url, UriKind.Absolute)), "mcp");
        var credentialHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var session = _sessions.GetOrAdd(endpoint.AbsoluteUri + "|" + credentialHash, _ => new Session());
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        await session.Initialization.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            if (session.Protocol is null)
            {
                var initialized = await PostAsync(endpoint, token, null, null, "initialize", new
                {
                    protocolVersion = RequestedProtocol,
                    capabilities = new { },
                    clientInfo = new { name = "t3-voice-windows", version = "0.2.0" },
                }, false, deadline.Token).ConfigureAwait(false);
                var protocol = RemoteHttp.RequiredString(initialized.Result, "protocolVersion");
                if (protocol is not ("2025-03-26" or "2025-06-18" or "2025-11-25"))
                    throw new RemoteMcpException("远端协商的 MCP 协议版本不受支持，请更新客户端。");
                await PostAsync(endpoint, token, initialized.SessionId, protocol, "notifications/initialized", null,
                    true, deadline.Token).ConfigureAwait(false);
                session.Id = initialized.SessionId;
                session.Protocol = protocol;
            }
        }
        finally { session.Initialization.Release(); }

        try
        {
            var response = await PostAsync(endpoint, token, session.Id, session.Protocol, method, parameters,
                false, deadline.Token).ConfigureAwait(false);
            return response.Result;
        }
        catch (RemoteMcpException error) when (error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
        {
            // The request may have committed; only a future call may open a new session.
            _sessions.TryRemove(new KeyValuePair<string, Session>(endpoint.AbsoluteUri + "|" + credentialHash, session));
            throw;
        }
    }

    private async Task<(JsonElement Result, string? SessionId)> PostAsync(Uri endpoint, string token, string? sessionId,
        string? protocol, string method, object? parameters, bool notification, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        object message = notification
            ? new { jsonrpc = "2.0", method }
            : new { jsonrpc = "2.0", id, method, @params = parameters };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (sessionId is not null) request.Headers.Add("Mcp-Session-Id", sessionId);
        if (protocol is not null) request.Headers.Add("MCP-Protocol-Version", protocol);
        request.Content = new StringContent(JsonSerializer.Serialize(message), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        RemoteHttp.RequireSuccess(response);
        var returnedSession = response.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.SingleOrDefault() : null;
        if (returnedSession is not null && (returnedSession.Length is 0 or > 1024 || returnedSession.Any(character => character < '!' || character > '~')))
            throw new RemoteMcpException("远端 MCP 会话标识无效。");
        if (notification) return (default, returnedSession);
        if (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.NoContent)
            throw new RemoteMcpException("远端未在请求中返回 MCP 结果；请检查服务端协议支持。");
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var envelope = string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase)
            ? await ReadSseResponseAsync(response.Content, id, cancellationToken).ConfigureAwait(false)
            : await RemoteHttp.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
        return (ResultFromEnvelope(envelope, id, token), returnedSession);
    }

    private static JsonElement ResultFromEnvelope(JsonElement envelope, long id, string token)
    {
        if (!IsResponse(envelope, id)) throw new RemoteMcpException("远端 MCP 响应与请求编号不匹配。");
        if (envelope.TryGetProperty("error", out var error))
        {
            var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var text) &&
                text.ValueKind == JsonValueKind.String ? text.GetString() ?? "MCP 请求失败" : "MCP 请求失败";
            message = message.Replace(token, "[已隐藏]", StringComparison.Ordinal);
            throw new RemoteMcpException("远端 MCP：" + message[..Math.Min(message.Length, 1000)]);
        }
        return envelope.GetProperty("result").Clone();
    }

    private static bool IsResponse(JsonElement envelope, long id) =>
        envelope.ValueKind == JsonValueKind.Object &&
        envelope.TryGetProperty("jsonrpc", out var version) && version.ValueKind == JsonValueKind.String &&
        version.GetString() == "2.0" && !envelope.TryGetProperty("method", out _) &&
        envelope.TryGetProperty("id", out var responseId) && responseId.ValueKind == JsonValueKind.Number &&
        responseId.TryGetInt64(out var actualId) && actualId == id &&
        (envelope.TryGetProperty("result", out _) ^ envelope.TryGetProperty("error", out _));

    private static async Task<JsonElement> ReadSseResponseAsync(HttpContent content, long id, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var limited = new LimitedReadStream(stream, RemoteHttp.MaximumResponseBytes);
        using var reader = new StreamReader(limited, new UTF8Encoding(false, true));
        var buffer = new char[4096];
        var line = new StringBuilder();
        var data = new StringBuilder();
        var total = 0;
        var priorCarriageReturn = false;
        JsonElement? HandleLine()
        {
            if (line.Length == 0)
            {
                if (data.Length == 0) return null;
                try
                {
                    using var document = JsonDocument.Parse(data.ToString());
                    return IsResponse(document.RootElement, id) ? document.RootElement.Clone() : null;
                }
                catch (JsonException) { throw new RemoteMcpException("远端 MCP 事件包含无效的 JSON。"); }
                finally { data.Clear(); }
            }
            var text = line.ToString();
            line.Clear();
            if (text == "data") data.Append('\n');
            else if (text.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = text.AsSpan(5);
                if (value.StartsWith(" ")) value = value[1..];
                data.Append(value).Append('\n');
            }
            return null;
        }
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                if (line.Length > 0) HandleLine();
                var final = HandleLine();
                if (final is { } result) return result;
                throw new RemoteMcpException("远端 MCP 事件流在返回结果前关闭。");
            }
            total += count;
            if (total > RemoteHttp.MaximumResponseBytes) throw new RemoteMcpException("远端 MCP 事件流超过大小限制。");
            for (var index = 0; index < count; index++)
            {
                var character = buffer[index];
                if (character == '\n' && priorCarriageReturn) { priorCarriageReturn = false; continue; }
                priorCarriageReturn = character == '\r';
                if (character is '\r' or '\n')
                {
                    var response = HandleLine();
                    if (response is { } result) return result;
                }
                else
                {
                    line.Append(character);
                    if (line.Length + data.Length > 2 * 1024 * 1024)
                        throw new RemoteMcpException("远端 MCP 单条事件超过大小限制。");
                }
            }
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
