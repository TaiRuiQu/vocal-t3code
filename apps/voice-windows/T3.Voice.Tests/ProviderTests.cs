using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using T3.Voice;

public static class ProviderTests
{
    public static async Task RunLiveCodexLifecycleAsync(string executable = "codex")
    {
        var directory = Path.Combine(Path.GetTempPath(), "t3-voice-provider-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var cancelled = new CancellationTokenSource();
            using var store = new VoiceStore(directory);
            await using var bridge = new LocalMcpServer(new VoiceTools(store, new UnusedRemote()));
            await bridge.StartAsync(CancellationToken.None);
            using var inherited = new TcpListener(IPAddress.Loopback, 0);
            inherited.Start();
            var inheritedPort = ((IPEndPoint)inherited.LocalEndpoint).Port;
            var inheritedConnection = inherited.AcceptTcpClientAsync(cancelled.Token).AsTask();
            var request = new ProviderRequest("", null, "", bridge.Endpoint, directory, executable);
            var connection = new ProviderRpcSession(request, cancelled.Token);
            var processId = connection.Process.Id;
            try
            {
                await connection.Rpc.RequestAsync("initialize", new { clientInfo = new { name = "t3_voice_lifecycle", version = "0.1.0" }, capabilities = new { experimentalApi = false } }, CancellationToken.None);
                await connection.Rpc.NotifyAsync("initialized", new { }, CancellationToken.None);
                var effective = await connection.Rpc.RequestAsync("config/read", new { includeLayers = false, cwd = directory }, CancellationToken.None);
                var settings = effective.GetProperty("config");
                var features = settings.GetProperty("features");
                Assert(!features.GetProperty("plugins").GetBoolean() && !features.GetProperty("shell_tool").GetBoolean() && !features.GetProperty("apps").GetBoolean(), "The coordinator must not load inherited plugins, app connectors or command tools.");
                var threadConfig = CodexInvocation.ThreadConfig(effective, bridge.Endpoint);
                threadConfig["mcp_servers.fixture-inherited"] = new { url = $"http://127.0.0.1:{inheritedPort}/mcp", enabled = false };
                var started = await connection.Rpc.RequestAsync("thread/start", new { cwd = directory, approvalPolicy = "never", sandbox = "danger-full-access", config = threadConfig }, CancellationToken.None);
                var threadId = started.GetProperty("thread").GetProperty("id").GetString();
                var catalog = await connection.Rpc.RequestAsync("mcpServerStatus/list", new { threadId }, CancellationToken.None);
                var servers = catalog.GetProperty("data").EnumerateArray().Where(server => !server.TryGetProperty("runtimeStatus", out var status) || status.GetString() != "disabled").ToArray();
                Assert(servers.Length == 1 && servers[0].GetProperty("name").GetString() == "t3-voice" && servers[0].GetProperty("runtimeStatus").GetString() == "connected", "The coordinator must connect only its local T3 bridge.");
                Assert(!inheritedConnection.IsCompleted, "A disabled inherited MCP server must never be contacted.");
                await cancelled.CancelAsync();
            }
            finally { await connection.DisposeAsync(); }
            try { using var unwanted = await inheritedConnection; throw new InvalidOperationException("A disabled MCP server was contacted."); }
            catch (OperationCanceledException) { }
            var exited = false;
            try { using var child = Process.GetProcessById(processId); exited = child.HasExited; }
            catch (ArgumentException) { exited = true; }
            Assert(exited, "Disposing a cancelled Codex connection must terminate its captured child and release the redirected reader.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class UnusedRemote : IRemoteMcpClient
    {
        public Task<JsonElement> ListToolsAsync(Machine machine, string accessToken, CancellationToken cancellationToken) => throw new InvalidOperationException("The lifecycle fixture must not access a cloud machine.");
        public Task<JsonElement> CallToolAsync(Machine machine, string accessToken, string name, JsonElement arguments, CancellationToken cancellationToken) => throw new InvalidOperationException("The lifecycle fixture must not dispatch tasks.");
    }

    public static async Task RunAsync()
    {
        var codexEvents = new List<ProviderEvent>();
        var codex = new CodexTurnState(codexEvents.Add);
        codex.Begin("thread-a");
        codex.SetTurnId("turn-new");
        codex.Accept("item/agentMessage/delta", Json("""{"threadId":"thread-other","turnId":"turn-new","itemId":"noise","delta":"wrong"}"""));
        codex.Accept("item/agentMessage/delta", Json("""{"threadId":"thread-a","turnId":"turn-old","itemId":"noise","delta":"wrong"}"""));
        codex.Accept("item/agentMessage/delta", Json("""{"threadId":"thread-a","turnId":"turn-new","itemId":"answer","delta":"正在修"}"""));
        codex.Accept("item/agentMessage/delta", Json("""{"threadId":"thread-a","turnId":"turn-new","itemId":"answer","delta":"复"}"""));
        codex.Accept("turn/completed", Json("""{"threadId":"thread-a","turn":{"id":"turn-old","status":"failed","items":[]}}"""));
        Assert(!codex.Completed.Task.IsCompleted, "An old turn completion must not finish the current turn.");
        codex.Accept("item/completed", Json("""{"threadId":"thread-a","turnId":"turn-new","item":{"id":"answer","type":"agentMessage","text":"已修复并创建 PR","phase":"final_answer"}}"""));
        codex.Accept("turn/completed", Json("""{"threadId":"thread-a","turn":{"id":"turn-new","status":"completed","items":[{"id":"answer","type":"agentMessage","text":"已修复并创建 PR","phase":"final_answer"}]}}"""));
        var completed = await codex.Completed.Task;
        Assert(completed.Success && completed.Text == "已修复并创建 PR", "Final Codex items must replace streamed drafts without duplication.");
        Assert(string.Concat(codexEvents.Where(item => item.Kind == "delta").Select(item => item.Text)) == "正在修复", "Unrelated Codex thread and turn events must not appear in the UI.");

        var failedCodex = new CodexTurnState(_ => { });
        failedCodex.Begin("thread-failed");
        failedCodex.SetTurnId("turn-failed");
        failedCodex.Accept("turn/completed", Json("""{"threadId":"thread-failed","turn":{"id":"turn-failed","status":"failed","items":[],"error":{"message":"model unavailable"}}}"""));
        var failedResult = await failedCodex.Completed.Task;
        Assert(!failedResult.Success && failedResult.Error == "model unavailable", "A failed Codex turn must never be reported as completion.");

        var questionEvents = new List<ProviderEvent>();
        var questioning = new CodexTurnState(questionEvents.Add);
        questioning.Begin("question-thread");
        await CodexDriver.AnswerRequestAsync("item/tool/requestUserInput", Json("""{"questions":[{"id":"project","question":"你指的是哪个项目？"}]}"""), questioning, questionEvents.Add);
        Assert(questioning.IsPaused && (await questioning.Completed.Task).Text == "你指的是哪个项目？" && questionEvents.Single(item => item.Kind == "question").Text == "你指的是哪个项目？", "Built-in Codex questions must be preserved for the voice UI and pause the turn.");

        var claudeEvents = new List<ProviderEvent>();
        var claude = new ClaudeStreamState(claudeEvents.Add);
        claude.Accept(Json("""{"type":"system","subtype":"init","session_id":"session-root","mcp_servers":[{"name":"t3-voice","status":"connected"}]}"""));
        claude.Accept(Json("""{"type":"assistant","session_id":"session-child","parent_tool_use_id":"child-tool","message":{"content":[{"type":"text","text":"child result"}]}}"""));
        claude.Accept(Json("""{"type":"stream_event","parent_tool_use_id":null,"event":{"delta":{"type":"text_delta","text":"你好"}}}"""));
        claude.Accept(Json("""{"type":"assistant","parent_tool_use_id":null,"message":{"content":[{"type":"text","text":"你好，任务已交给 Ubuntu。"}]}}"""));
        claude.Accept(Json("""{"type":"result","subtype":"success","is_error":false,"session_id":"session-root","result":"你好，任务已交给 Ubuntu。"}"""));
        Assert(claude.IsCompleted && claude.Result.Success && claude.Result.Text == "你好，任务已交给 Ubuntu。", "Claude final results must not repeat partial tokens.");
        Assert(claude.SessionId == "session-root" && claudeEvents.Count(item => item.Kind == "session") == 1, "A child session must not replace the coordinator's resume ID.");
        Assert(claudeEvents.Single(item => item.Kind == "delta").Text == "你好", "Only root assistant text deltas should reach the UI.");

        var failedClaude = new ClaudeStreamState(_ => { }, "resume-session");
        failedClaude.Accept(Json("""{"type":"result","subtype":"error_max_turns","is_error":false,"errors":["maximum turns reached"]}"""));
        Assert(!failedClaude.Result.Success && failedClaude.Result.Error == "maximum turns reached", "Claude error result subtypes must fail even when is_error is false.");
        var mcpFailed = false;
        try { failedClaude.Accept(Json("""{"type":"system","subtype":"init","mcp_servers":[{"name":"t3-voice","status":"failed"}]}""")); }
        catch (InvalidOperationException) { mcpFailed = true; }
        Assert(mcpFailed, "A failed required coordinator bridge must stop the CLI run.");

        await VerifyRpcCorrelationAsync();
        await VerifyOwnedPipeCancellationAsync();
        var redacted = ProviderProcess.Redact("token-secret and Bearer another-secret", "token-secret");
        Assert(!redacted.Contains("token-secret", StringComparison.Ordinal) && !redacted.Contains("another-secret", StringComparison.Ordinal), "CLI diagnostics must redact the bridge credential.");
    }

    private static async Task VerifyOwnedPipeCancellationAsync()
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var directory = Path.Combine(Path.GetTempPath(), "t3-voice-pipe-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var cancelled = new CancellationTokenSource();
            var request = new ProviderRequest("", null, "", new BridgeEndpoint(new Uri("http://127.0.0.1:1/mcp"), "fixture-only"), directory, executable);
            await using var process = new ProviderProcess(request,
                ["-NoProfile", "-NonInteractive", "-Command", "[Console]::ReadLine() | Out-Null; [Console]::WriteLine('READY'); [Console]::Out.Flush(); [Console]::ReadLine() | Out-Null"], cancelled.Token);
            await process.Input.WriteLineAsync("start");
            await process.Input.FlushAsync();
            Assert(await process.Output.ReadLineAsync() == "READY", "The blocked-pipe fixture did not start.");
            var blockedRead = process.Output.ReadLineAsync();
            await cancelled.CancelAsync();
            Assert(await blockedRead is null, "Cancellation must close the owned child's output pipe.");
            await process.WaitForExitAsync(CancellationToken.None);
            Assert(process.ExitCode != 0, "The cancelled fixture must be terminated rather than left running.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task VerifyRpcCorrelationAsync()
    {
        using var source = new ChannelReader();
        using var sink = new ChannelWriter();
        await using var rpc = new JsonLineRpc(source, sink, CancellationToken.None);
        var first = rpc.RequestAsync("first", new { }, CancellationToken.None);
        var firstRequest = await sink.Lines.Reader.ReadAsync();
        var firstId = Json(firstRequest).GetProperty("id").GetInt32();
        var second = rpc.RequestAsync("second", new { }, CancellationToken.None);
        var secondRequest = await sink.Lines.Reader.ReadAsync();
        var secondId = Json(secondRequest).GetProperty("id").GetInt32();

        rpc.ServerRequest = (method, _) => Task.FromResult<object?>(new { decision = "acceptForSession", method });
        await source.FeedAsync(JsonSerializer.Serialize(new { id = firstId, method = "item/commandExecution/requestApproval", @params = new { } }));
        var approval = Json(await sink.Lines.Reader.ReadAsync());
        Assert(approval.GetProperty("id").GetInt32() == firstId && approval.GetProperty("result").GetProperty("decision").GetString() == "acceptForSession", "A server request sharing a client request number must be answered as a server request.");
        await source.FeedAsync(JsonSerializer.Serialize(new { id = secondId, result = new { value = "second-result" } }));
        await source.FeedAsync("""{"id":999999,"result":{"value":"unrelated"}}""");
        await source.FeedAsync(JsonSerializer.Serialize(new { id = firstId, result = new { value = "first-result" } }));
        Assert((await first).GetProperty("value").GetString() == "first-result", "The first CLI call received a mismatched response.");
        Assert((await second).GetProperty("value").GetString() == "second-result", "Out-of-order CLI responses must resolve their own requests.");

        var pending = rpc.RequestAsync("dies", new { }, CancellationToken.None);
        await sink.Lines.Reader.ReadAsync();
        source.Complete();
        var failed = false;
        try { await pending; }
        catch (EndOfStreamException) { failed = true; }
        Assert(failed, "A provider disconnect must reject pending requests instead of leaving the coordinator hanging.");
    }

    private static JsonElement Json(string source)
    {
        using var document = JsonDocument.Parse(source);
        return document.RootElement.Clone();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ChannelReader : TextReader
    {
        private readonly Channel<string> _chunks = Channel.CreateUnbounded<string>();
        private string _current = "";
        private int _offset;
        public ValueTask FeedAsync(string line) => _chunks.Writer.WriteAsync(line + "\n");
        public void Complete() => _chunks.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset == _current.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken)) return 0;
                _current = await _chunks.Reader.ReadAsync(cancellationToken);
                _offset = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
    }

    private sealed class ChannelWriter : TextWriter
    {
        public Channel<string> Lines { get; } = Channel.CreateUnbounded<string>();
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) => Lines.Writer.WriteAsync(buffer.ToString(), cancellationToken).AsTask();
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
