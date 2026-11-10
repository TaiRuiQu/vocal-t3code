using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.WebUtilities;

namespace T3.Voice.Tests;

public static class RemoteTests
{
    private static readonly Machine TestMachine = new() { Id = "test", Name = "fixture", Url = "https://environment.test/" };

    public static async Task RunAsync()
    {
        await NegotiatesSessionAndPaginatesToolsAsync();
        await ReadsCorrelatedSseAndPreservesResultAsync();
        await DoesNotReplayMutationAfterSessionLossAsync();
        await CancelsInFlightRequestAsync();
        await RejectsMismatchedAndOversizedResponsesAsync();
        await CompletesPkceWithValidatedCallbackAsync();
        await RejectsChangedHostedAuthorizationAsync();
        await RejectsWrongOAuthResourceAsync();
        await CancelsOAuthCallbackWaitAsync();
    }

    private static async Task NegotiatesSessionAndPaginatesToolsAsync()
    {
        var initializedCount = 0;
        var listCount = 0;
        using var http = new HttpClient(new FakeHandler(async (request, ct) =>
        {
            Assert(request.RequestUri == new Uri("https://environment.test/mcp"), "MCP endpoint");
            Assert(request.Headers.Authorization?.Parameter == "fixture-token", "bearer authorization");
            using var message = await ParseAsync(request, ct);
            var root = message.RootElement;
            var method = root.GetProperty("method").GetString();
            if (method == "initialize")
            {
                initializedCount++;
                Assert(!request.Headers.Contains("Mcp-Session-Id"), "initialize has no stale session");
                var response = Rpc(root, new { protocolVersion = "2025-06-18", capabilities = new { } });
                response.Headers.Add("Mcp-Session-Id", "fixture-session");
                return response;
            }
            Assert(request.Headers.GetValues("Mcp-Session-Id").Single() == "fixture-session", "negotiated session");
            Assert(request.Headers.GetValues("MCP-Protocol-Version").Single() == "2025-06-18", "negotiated protocol");
            if (method == "notifications/initialized")
            {
                Assert(!root.TryGetProperty("id", out _), "initialized is a notification");
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }
            Assert(method == "tools/list", "tools/list method");
            listCount++;
            if (listCount == 1) return Rpc(root, new { tools = new[] { new { name = "first" } }, nextCursor = "page-two" });
            Assert(root.GetProperty("params").GetProperty("cursor").GetString() == "page-two", "pagination cursor");
            return Rpc(root, new { tools = new[] { new { name = "second" } } });
        }));
        using var client = new RemoteMcpClient(http);
        var result = await client.ListToolsAsync(TestMachine, "fixture-token", CancellationToken.None);
        Assert(result.GetProperty("tools").GetArrayLength() == 2, "all tool pages returned");
        Assert(!result.TryGetProperty("nextCursor", out _), "combined directory does not expose stale cursor");
        Assert(initializedCount == 1 && listCount == 2, "single initialization per session");
    }

    private static async Task ReadsCorrelatedSseAndPreservesResultAsync()
    {
        using var http = new HttpClient(new FakeHandler(async (request, ct) =>
        {
            using var document = await ParseAsync(request, ct);
            var root = document.RootElement;
            if (Handshake(root) is { } handshake) return handshake;
            var id = root.GetProperty("id").GetInt64();
            var result = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                result = new
                {
                    content = new[] { new { type = "text", text = "任务结果" } },
                    structuredContent = new { threadId = "remote-thread" },
                    isError = true,
                },
            });
            var sse = ": heartbeat\r\nevent: message\r\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\"}\r\n\r\n" +
                "data: {\"jsonrpc\":\"2.0\",\"id\":999999,\"result\":{}}\r\n\r\n" +
                "data: " + result.Replace(",\"result\":", ",\ndata: \"result\":", StringComparison.Ordinal) + "\r\n\r\n";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FragmentedStream(Encoding.UTF8.GetBytes(sse))),
            };
            response.Content.Headers.ContentType = new("text/event-stream");
            return response;
        }));
        using var client = new RemoteMcpClient(http);
        var result = await client.CallToolAsync(TestMachine, "fixture-token", "t3_thread_launch", Object(), CancellationToken.None);
        Assert(result.GetProperty("content")[0].GetProperty("text").GetString() == "任务结果", "SSE UTF-8 text");
        Assert(result.GetProperty("structuredContent").GetProperty("threadId").GetString() == "remote-thread", "structured result retained");
        Assert(result.GetProperty("isError").GetBoolean(), "tool error result is not discarded");
    }

    private static async Task DoesNotReplayMutationAfterSessionLossAsync()
    {
        var launches = 0;
        var initializes = 0;
        using var http = new HttpClient(new FakeHandler(async (request, ct) =>
        {
            using var document = await ParseAsync(request, ct);
            var root = document.RootElement;
            if (root.GetProperty("method").GetString() == "initialize") initializes++;
            if (Handshake(root) is { } handshake) return handshake;
            launches++;
            return launches == 1 ? new HttpResponseMessage(HttpStatusCode.NotFound) : Rpc(root, new { threadId = "created" });
        }));
        using var client = new RemoteMcpClient(http);
        var error = await ThrowsAsync<RemoteMcpException>(() => client.CallToolAsync(TestMachine, "fixture-token", "t3_thread_launch", Object(), CancellationToken.None));
        Assert(error.StatusCode == HttpStatusCode.NotFound && launches == 1, "mutation not retried after session loss");
        await client.CallToolAsync(TestMachine, "fixture-token", "t3_thread_read", Object(), CancellationToken.None);
        Assert(initializes == 2 && launches == 2, "next explicit call initializes a fresh session");

        using var unauthorizedHttp = new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));
        using var unauthorizedClient = new RemoteMcpClient(unauthorizedHttp);
        var unauthorized = await ThrowsAsync<RemoteMcpException>(() => unauthorizedClient.ListToolsAsync(TestMachine, "fixture-token", CancellationToken.None));
        Assert(unauthorized.RequiresAuthorization, "expired authorization is actionable");
    }

    private static async Task CancelsInFlightRequestAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var http = new HttpClient(new FakeHandler(async (request, ct) =>
        {
            using var document = await ParseAsync(request, ct);
            if (Handshake(document.RootElement) is { } handshake) return handshake;
            calls++;
            entered.SetResult();
            var blocked = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(() => blocked.TrySetCanceled(ct));
            return await blocked.Task;
        }));
        using var client = new RemoteMcpClient(http);
        using var cancellation = new CancellationTokenSource();
        var pending = client.CallToolAsync(TestMachine, "fixture-token", "t3_thread_wait", Object(), cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(async () => { await pending; });
        Assert(calls == 1, "cancellation does not resend requests");
    }

    private static async Task RejectsMismatchedAndOversizedResponsesAsync()
    {
        var oversized = false;
        using var http = new HttpClient(new FakeHandler(async (request, ct) =>
        {
            using var document = await ParseAsync(request, ct);
            if (Handshake(document.RootElement) is { } handshake) return handshake;
            var response = Json(new { jsonrpc = "2.0", id = -1, result = new { } });
            if (oversized) response.Content.Headers.ContentLength = 9 * 1024 * 1024;
            return response;
        }));
        using var client = new RemoteMcpClient(http);
        await ThrowsAsync<RemoteMcpException>(() => client.CallToolAsync(TestMachine, "fixture-token", "read", Object(), CancellationToken.None));
        oversized = true;
        await ThrowsAsync<RemoteMcpException>(() => client.CallToolAsync(TestMachine, "fixture-token", "read", Object(), CancellationToken.None));
    }

    private static async Task CompletesPkceWithValidatedCallbackAsync()
    {
        foreach (var approval in new[]
        {
            (Page: "/connect-agent", Browser: "https://environment.test/oauth/mcp/authorize"),
            (Page: "https://app.t3.codes/approve-agent", Browser: "https://nightly.app.t3.codes/approve-agent"),
            (Page: "https://app.t3.codes.other.test/approve-agent", Browser: "https://environment.test/oauth/mcp/authorize"),
        })
            await CompletesPkceWithValidatedCallbackAsync(approval.Page, approval.Browser);
    }

    private static async Task CompletesPkceWithValidatedCallbackAsync(string approvalPage, string expectedBrowserPage)
    {
        string? registeredRedirect = null;
        string? issuedChallenge = null;
        string? authorizationQuery = null;
        Task browser = Task.CompletedTask;
        using var http = new HttpClient(new FakeHandler(async (request, ct) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/.well-known/oauth-protected-resource/mcp": return ResourceMetadata();
                case "/.well-known/oauth-authorization-server": return ServerMetadata();
                case "/oauth/mcp/register":
                    using (var body = await ParseAsync(request, ct))
                    {
                        registeredRedirect = body.RootElement.GetProperty("redirect_uris")[0].GetString();
                        Assert(new Uri(registeredRedirect!).IsLoopback, "callback binds loopback");
                        Assert(!body.RootElement.TryGetProperty("software_id", out _), "independent client is not a peer environment");
                    }
                    return Json(new { client_id = "registered-client" }, HttpStatusCode.Created);
                case "/oauth/mcp/authorize":
                    authorizationQuery = request.RequestUri.Query;
                    var handoff = new HttpResponseMessage(HttpStatusCode.Found);
                    handoff.Headers.Location = new Uri(approvalPage + authorizationQuery + "&environment=fixture-environment", UriKind.RelativeOrAbsolute);
                    return handoff;
                case "/oauth/mcp/token":
                    var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(ct));
                    Assert(form["code"] == "fixture-code" && form["resource"] == "https://environment.test/mcp", "token resource and code");
                    Assert(form["redirect_uri"] == registeredRedirect, "same registered callback");
                    var verifier = form["code_verifier"].ToString();
                    var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                    Assert(challenge == issuedChallenge && verifier.Length >= 43, "PKCE challenge verified");
                    return Json(new { access_token = "fixture-access", token_type = "Bearer", expires_in = 3600, scope = "orchestration:read orchestration:operate" });
                default: throw new Exception("Unexpected OAuth request");
            }
        }));
        using var oauth = new McpOAuth(http);
        var credential = await oauth.AuthorizeAsync(new Uri(TestMachine.Url), authorization =>
        {
            var query = QueryHelpers.ParseQuery(authorization.Query);
            Assert(authorization.GetLeftPart(UriPartial.Path) == expectedBrowserPage, "opens the expected approval page");
            if (expectedBrowserPage == "https://nightly.app.t3.codes/approve-agent")
            {
                Assert(authorization.Query == authorizationQuery + "&environment=fixture-environment", "hosted approval preserves the full authorization query");
            }
            else
            {
                Assert(authorization.AbsolutePath == "/oauth/mcp/authorize", "uses remote environment authorization UI");
            }
            Assert(query["code_challenge_method"] == "S256" && query["client_id"] == "registered-client", "authorization PKCE fields");
            issuedChallenge = query["code_challenge"].ToString();
            browser = SimulateBrowserAsync(query["redirect_uri"].ToString(), query["state"].ToString());
        }, CancellationToken.None);
        await browser;
        Assert(credential.AccessToken == "fixture-access" && credential.ExpiresAt > DateTimeOffset.UtcNow, "credential expiry returned");
    }

    private static async Task RejectsChangedHostedAuthorizationAsync()
    {
        using var http = new HttpClient(new FakeHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/.well-known/oauth-protected-resource/mcp")
                return Task.FromResult(ResourceMetadata());
            if (request.RequestUri.AbsolutePath == "/.well-known/oauth-authorization-server")
                return Task.FromResult(ServerMetadata());
            if (request.RequestUri.AbsolutePath == "/oauth/mcp/register")
                return Task.FromResult(Json(new { client_id = "registered-client" }, HttpStatusCode.Created));
            Assert(request.RequestUri.AbsolutePath == "/oauth/mcp/authorize", "authorization request precedes hosted handoff");
            var fields = QueryHelpers.ParseQuery(request.RequestUri.Query)
                .ToDictionary(field => field.Key, field => (string?)field.Value.ToString());
            fields["redirect_uri"] = "http://127.0.0.1:12345/wrong-callback";
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(QueryHelpers.AddQueryString("https://app.t3.codes/approve-agent", fields));
            return Task.FromResult(response);
        }));
        using var oauth = new McpOAuth(http);
        var openedBrowser = false;
        await ThrowsAsync<RemoteMcpException>(() => oauth.AuthorizeAsync(new Uri(TestMachine.Url), _ => openedBrowser = true, CancellationToken.None));
        Assert(!openedBrowser, "changed authorization cannot open a hosted approval page");
    }

    private static async Task SimulateBrowserAsync(string redirect, string state)
    {
        using var callbackHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        async Task<HttpStatusCode> Callback(string actualState, string? issuer)
        {
            var fields = new Dictionary<string, string?> { ["state"] = actualState, ["code"] = "fixture-code", ["iss"] = issuer };
            using var response = await callbackHttp.GetAsync(QueryHelpers.AddQueryString(redirect, fields));
            return response.StatusCode;
        }
        Assert(await Callback("wrong-state", "https://environment.test") == HttpStatusCode.BadRequest, "wrong state rejected");
        Assert(await Callback(state, "https://other.test") == HttpStatusCode.BadRequest, "wrong issuer rejected");
        Assert(await Callback(state, null) == HttpStatusCode.BadRequest, "required issuer rejected when absent");
        Assert(await Callback(state, "https://environment.test") == HttpStatusCode.OK, "valid callback accepted after invalid probes");
    }

    private static async Task RejectsWrongOAuthResourceAsync()
    {
        using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(Json(new
        {
            resource = "https://other.test/mcp",
            authorization_servers = new[] { "https://environment.test" },
        }))));
        using var oauth = new McpOAuth(http);
        var openedBrowser = false;
        await ThrowsAsync<RemoteMcpException>(() => oauth.AuthorizeAsync(new Uri(TestMachine.Url), _ => openedBrowser = true, CancellationToken.None));
        Assert(!openedBrowser, "mismatched discovery cannot open authorization page");
    }

    private static async Task CancelsOAuthCallbackWaitAsync()
    {
        using var http = new HttpClient(new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/.well-known/oauth-protected-resource/mcp" => ResourceMetadata(),
            "/.well-known/oauth-authorization-server" => ServerMetadata(),
            "/oauth/mcp/register" => Json(new { client_id = "registered-client" }, HttpStatusCode.Created),
            "/oauth/mcp/authorize" => new HttpResponseMessage(HttpStatusCode.OK),
            _ => throw new Exception("Unexpected request after OAuth cancellation"),
        })));
        using var oauth = new McpOAuth(http);
        using var cancellation = new CancellationTokenSource();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = oauth.AuthorizeAsync(new Uri(TestMachine.Url), _ => opened.SetResult(), cancellation.Token);
        await opened.Task;
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(async () => { await pending; });
    }

    private static HttpResponseMessage ResourceMetadata() => Json(new
    {
        resource = "https://environment.test/mcp",
        authorization_servers = new[] { "https://environment.test" },
    });
    private static HttpResponseMessage ServerMetadata() => Json(new
    {
        issuer = "https://environment.test",
        authorization_endpoint = "https://environment.test/oauth/mcp/authorize",
        registration_endpoint = "https://environment.test/oauth/mcp/register",
        token_endpoint = "https://environment.test/oauth/mcp/token",
        code_challenge_methods_supported = new[] { "S256" },
        authorization_response_iss_parameter_supported = true,
    });
    private static HttpResponseMessage? Handshake(JsonElement root) => root.GetProperty("method").GetString() switch
    {
        "initialize" => Rpc(root, new { protocolVersion = "2025-06-18", capabilities = new { } }),
        "notifications/initialized" => new HttpResponseMessage(HttpStatusCode.Accepted),
        _ => null,
    };
    private static HttpResponseMessage Rpc(JsonElement request, object result) => Json(new { jsonrpc = "2.0", id = request.GetProperty("id").GetInt64(), result });
    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
    };
    private static async Task<JsonDocument> ParseAsync(HttpRequestMessage request, CancellationToken ct)
        => JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
    private static JsonElement Object() => JsonSerializer.SerializeToElement(new { });
    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new Exception("Remote test failed: " + description);
    }
    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => handle(request, ct);
    }
    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 7)], ct);
    }
}
