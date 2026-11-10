using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace T3.Voice;

public sealed class RemoteMcpException : Exception
{
    public HttpStatusCode? StatusCode { get; }
    public bool RequiresAuthorization => StatusCode == HttpStatusCode.Unauthorized;

    public RemoteMcpException(string message, HttpStatusCode? statusCode = null) : base(message)
        => StatusCode = statusCode;
}

internal static class RemoteHttp
{
    internal const int MaximumResponseBytes = 8 * 1024 * 1024;

    internal static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    internal static Uri Origin(Uri environment)
    {
        if (!environment.IsAbsoluteUri || (environment.Scheme != "https" && environment.Scheme != "http") ||
            environment.UserInfo.Length != 0 || environment.Query.Length != 0 || environment.Fragment.Length != 0 ||
            (environment.AbsolutePath != "/" && environment.AbsolutePath.TrimEnd('/') != "/mcp"))
            throw new ArgumentException("请填写 T3 环境的 HTTP(S) 地址，不要包含配对码或其他路径。", nameof(environment));
        return new Uri(environment.GetLeftPart(UriPartial.Authority) + "/");
    }

    internal static void RequireSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new RemoteMcpException("远端授权已过期或被撤销，请重新授权这台云电脑。", response.StatusCode);
        throw new RemoteMcpException($"远端请求失败（HTTP {(int)response.StatusCode}）。请检查云电脑连接与授权。", response.StatusCode);
    }

    internal static async Task<JsonElement> ReadJsonAsync(HttpContent content, CancellationToken cancellationToken,
        int maximumBytes = MaximumResponseBytes)
    {
        if (content.Headers.ContentLength > maximumBytes)
            throw new RemoteMcpException("远端响应超过大小限制。");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > maximumBytes) throw new RemoteMcpException("远端响应超过大小限制。");
            buffer.Write(bytes, 0, count);
        }
        try
        {
            using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new RemoteMcpException("远端返回了无效的 JSON 响应。");
        }
    }

    internal static string RequiredString(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
            throw new RemoteMcpException($"远端响应缺少有效的 {name}。");
        return property.GetString()!;
    }
}

internal sealed class LimitedReadStream(Stream inner, long maximumBytes) : Stream
{
    private long consumed;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    private int Count(int read)
    {
        consumed += read;
        if (consumed > maximumBytes) throw new RemoteMcpException("远端 MCP 事件流超过大小限制。");
        return read;
    }
    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
