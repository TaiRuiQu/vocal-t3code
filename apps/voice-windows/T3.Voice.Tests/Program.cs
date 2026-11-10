using System.IO;

namespace T3.Voice.Tests;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--eval")) return await VoiceEval.RunAsync(args.Where(a => a != "--eval").ToArray());
        if (args.Contains("--check-codex"))
        {
            await ProviderTests.RunLiveCodexLifecycleAsync();
            Console.WriteLine("PASS installed Codex isolation / startup / shutdown (no model turn)");
            return 0;
        }
        if (args.Contains("--models"))
        {
            var temporary = Path.Combine(Path.GetTempPath(), "t3-voice-models-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                var request = new ProviderRequest("", null, "", new BridgeEndpoint(new Uri("http://127.0.0.1:1/mcp"), "catalog-only"), temporary, "codex");
                await using var connection = new ProviderRpcSession(request, deadline.Token);
                var rpc = connection.Rpc;
                await rpc.RequestAsync("initialize", new { clientInfo = new { name = "t3_voice_catalog", version = "0.1" }, capabilities = new { experimentalApi = false } }, deadline.Token);
                await rpc.NotifyAsync("initialized", new { }, deadline.Token);
                var result = await rpc.RequestAsync("model/list", new { }, deadline.Token);
                foreach (var model in result.GetProperty("data").EnumerateArray()) Console.WriteLine(model.GetProperty("id").GetString());
                return 0;
            }
            finally { Directory.Delete(temporary, true); }
        }
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("Remote MCP / OAuth", RemoteTests.RunAsync),
            ("Streaming ASR / keyboard", SpeechTests.RunAsync),
            ("CLI protocol", ProviderTests.RunAsync),
            ("Durable dispatch", ToolsTests.RunAsync),
            ("Coordinator recovery", CoordinatorTests.RunAsync),
            ("SQLite / recovery", StoreTests.RunAsync)
        };
        foreach (var test in tests)
        {
            try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
            catch (Exception exception) { Console.Error.WriteLine("FAIL " + test.Name + "\n" + exception); return 1; }
        }
        return 0;
    }
}
