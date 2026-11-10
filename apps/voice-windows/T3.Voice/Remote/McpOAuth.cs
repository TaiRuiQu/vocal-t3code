using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace T3.Voice;

public sealed class McpOAuth : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public McpOAuth() : this(RemoteHttp.CreateClient(), true) { }
    public McpOAuth(HttpClient httpClient) : this(httpClient, false) { }
    private McpOAuth(HttpClient httpClient, bool ownsHttp)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttp = ownsHttp;
    }

    public async Task<OAuthCredential> AuthorizeAsync(Uri environment, Action<Uri> openBrowser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(openBrowser);
        var origin = RemoteHttp.Origin(environment);
        var resource = new Uri(origin, "mcp");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(8));
        var ct = deadline.Token;
        var resourceMetadata = await GetJsonAsync(new Uri(origin, ".well-known/oauth-protected-resource/mcp"), ct)
            .ConfigureAwait(false);
        if (!Uri.TryCreate(RemoteHttp.RequiredString(resourceMetadata, "resource"), UriKind.Absolute, out var advertisedResource) ||
            advertisedResource != resource || advertisedResource.UserInfo.Length != 0 || advertisedResource.Fragment.Length != 0 ||
            !resourceMetadata.TryGetProperty("authorization_servers", out var issuers) ||
            issuers.ValueKind != JsonValueKind.Array || issuers.GetArrayLength() != 1 ||
            issuers[0].ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(issuers[0].GetString(), UriKind.Absolute, out var issuer) ||
            issuer != origin || issuer.UserInfo.Length != 0 || issuer.Query.Length != 0 || issuer.Fragment.Length != 0)
            throw new RemoteMcpException("远端 OAuth 资源或签发方与这台 T3 环境不匹配。");
        var metadata = await GetJsonAsync(new Uri(issuer, ".well-known/oauth-authorization-server"), ct).ConfigureAwait(false);
        if (!Uri.TryCreate(RemoteHttp.RequiredString(metadata, "issuer"), UriKind.Absolute, out var actualIssuer) ||
            actualIssuer != issuer || actualIssuer.UserInfo.Length != 0 || actualIssuer.Query.Length != 0 || actualIssuer.Fragment.Length != 0)
            throw new RemoteMcpException("远端 OAuth 签发方元数据不匹配。");
        var authorizationEndpoint = Endpoint(metadata, "authorization_endpoint", origin);
        var tokenEndpoint = Endpoint(metadata, "token_endpoint", origin);
        var registrationEndpoint = Endpoint(metadata, "registration_endpoint", origin);
        if (!metadata.TryGetProperty("code_challenge_methods_supported", out var challengeMethods) ||
            challengeMethods.ValueKind != JsonValueKind.Array ||
            !challengeMethods.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "S256"))
            throw new RemoteMcpException("远端 OAuth 不支持所需的 PKCE S256。");
        var requireIssuer = metadata.TryGetProperty("authorization_response_iss_parameter_supported", out var issuerSupport) &&
            issuerSupport.ValueKind == JsonValueKind.True;

        var state = RandomValue();
        var verifier = RandomValue();
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var callbackPath = "/oauth/callback/" + RandomValue();
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            options.Limits.MaxRequestBodySize = 0;
            options.Limits.MaxRequestLineSize = 8192;
        });
        await using var app = builder.Build();
        app.Run(async context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.ContentType = "text/plain; charset=utf-8";
            if (context.Request.Method != "GET" || context.Request.Path != callbackPath)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            var query = context.Request.Query;
            if (!TrySingle(query, "state", out var receivedState) || !SameSecret(state, receivedState) ||
                (requireIssuer && !TrySingle(query, "iss", out _)) ||
                (query.ContainsKey("iss") && (!TrySingle(query, "iss", out var receivedIssuer) ||
                    !Uri.TryCreate(receivedIssuer, UriKind.Absolute, out var returnedIssuer) || returnedIssuer != issuer ||
                    returnedIssuer.UserInfo.Length != 0 || returnedIssuer.Query.Length != 0 || returnedIssuer.Fragment.Length != 0)))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("授权回调验证失败。请回到 T3 Voice 重新连接。", context.RequestAborted).ConfigureAwait(false);
                return;
            }
            if (TrySingle(query, "error", out _))
            {
                await context.Response.WriteAsync("未授权 T3 Voice。可以关闭此页面。", context.RequestAborted).ConfigureAwait(false);
                await context.Response.CompleteAsync().ConfigureAwait(false);
                completion.TrySetException(new RemoteMcpException("远端授权被取消或拒绝。"));
                return;
            }
            if (!TrySingle(query, "code", out var code) || code.Length > 4096)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("授权回调缺少有效凭据。", context.RequestAborted).ConfigureAwait(false);
                return;
            }
            await context.Response.WriteAsync("已收到授权，可以关闭此页面并返回 T3 Voice。", context.RequestAborted).ConfigureAwait(false);
            await context.Response.CompleteAsync().ConfigureAwait(false);
            completion.TrySetResult(code);
        });
        await app.StartAsync(ct).ConfigureAwait(false);
        try
        {
            var redirect = new Uri(new Uri(app.Urls.Single()), callbackPath).AbsoluteUri;
            var registration = await PostJsonAsync(registrationEndpoint, new
            {
                client_name = "T3 Voice Windows",
                redirect_uris = new[] { redirect },
                token_endpoint_auth_method = "none",
            }, ct).ConfigureAwait(false);
            var clientId = RemoteHttp.RequiredString(registration, "client_id");
            var authorize = QueryHelpers.AddQueryString(authorizationEndpoint.AbsoluteUri, new Dictionary<string, string?>
            {
                ["response_type"] = "code",
                ["client_id"] = clientId,
                ["redirect_uri"] = redirect,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
                ["state"] = state,
                ["resource"] = resource.AbsoluteUri,
            });
            openBrowser(await ResolveAuthorizationPageAsync(new Uri(authorize), ct).ConfigureAwait(false));
            var code = await completion.Task.WaitAsync(ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = redirect,
                    ["client_id"] = clientId,
                    ["code_verifier"] = verifier,
                    ["resource"] = resource.AbsoluteUri,
                }),
            };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            RemoteHttp.RequireSuccess(response);
            var token = await RemoteHttp.ReadJsonAsync(response.Content, ct, 1024 * 1024).ConfigureAwait(false);
            if (!string.Equals(RemoteHttp.RequiredString(token, "token_type"), "Bearer", StringComparison.OrdinalIgnoreCase))
                throw new RemoteMcpException("远端 OAuth 返回了不支持的凭据类型。");
            var accessToken = RemoteHttp.RequiredString(token, "access_token");
            if (accessToken.Length > 16384 || accessToken.Any(character => character is '\r' or '\n'))
                throw new RemoteMcpException("远端 OAuth 凭据格式无效。");
            if (!token.TryGetProperty("expires_in", out var expires) || expires.ValueKind != JsonValueKind.Number || !expires.TryGetDouble(out var seconds) ||
                !double.IsFinite(seconds) || seconds <= 0 || seconds > TimeSpan.FromDays(3660).TotalSeconds)
                throw new RemoteMcpException("远端 OAuth 没有返回有效的授权期限。");
            return new OAuthCredential(accessToken, DateTimeOffset.UtcNow.AddSeconds(seconds));
        }
        finally
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await app.StopAsync(shutdown.Token).ConfigureAwait(false);
        }
    }

    private async Task<Uri> ResolveAuthorizationPageAsync(Uri authorization, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, authorization);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is not { } location)
            return authorization;
        var page = new Uri(authorization, location);
        if (page.GetLeftPart(UriPartial.Path) != "https://app.t3.codes/approve-agent" ||
            page.UserInfo.Length != 0 || page.Fragment.Length != 0)
            return authorization;

        var originalQuery = QueryHelpers.ParseQuery(authorization.Query);
        var hostedQuery = QueryHelpers.ParseQuery(page.Query);
        if (originalQuery.Any(field => !hostedQuery.TryGetValue(field.Key, out var value) || !field.Value.SequenceEqual(value)))
            throw new RemoteMcpException("远端授权跳转与本次请求不匹配，请重新连接。");

        // Source builds can advertise hosted approval before the stable web app ships the route.
        return new UriBuilder(page) { Host = "nightly.app.t3.codes" }.Uri;
    }

    private async Task<JsonElement> GetJsonAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        RemoteHttp.RequireSuccess(response);
        return await RemoteHttp.ReadJsonAsync(response.Content, cancellationToken, 1024 * 1024).ConfigureAwait(false);
    }

    private async Task<JsonElement> PostJsonAsync(Uri endpoint, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        RemoteHttp.RequireSuccess(response);
        return await RemoteHttp.ReadJsonAsync(response.Content, cancellationToken, 1024 * 1024).ConfigureAwait(false);
    }

    private static Uri Endpoint(JsonElement metadata, string property, Uri origin)
    {
        if (!Uri.TryCreate(RemoteHttp.RequiredString(metadata, property), UriKind.Absolute, out var endpoint) ||
            endpoint.GetLeftPart(UriPartial.Authority) != origin.GetLeftPart(UriPartial.Authority) ||
            endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0)
            throw new RemoteMcpException("远端 OAuth 端点与这台 T3 环境不匹配。");
        return endpoint;
    }

    private static bool TrySingle(IQueryCollection query, string key, out string value)
    {
        value = "";
        if (!query.TryGetValue(key, out var values) || values.Count != 1 || string.IsNullOrEmpty(values[0])) return false;
        value = values[0]!;
        return true;
    }

    private static bool SameSecret(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
    private static string RandomValue() => Base64Url(RandomNumberGenerator.GetBytes(32));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
