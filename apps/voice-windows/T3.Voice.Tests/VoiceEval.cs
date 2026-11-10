using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace T3.Voice.Tests;

public static class VoiceEval
{
    private const string Harness = "native-voice-orchestrator/1";
    private const string QuestionKey = "question:authored-acme-colour";
    private const string Question = "Acme Web 的登录按钮要蓝色还是绿色？";
    private const string PullRequest = "https://github.com/acme/api/pull/42";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private sealed record Case(string Id, string Description, string Input, bool PendingQuestion = false, bool Completion = false);
    private static readonly Case[] Cases =
    [
        new("ambiguous-project", "Ask which Acme project before dispatching an ambiguous request.", "帮我修复 Acme 项目登录页提交失败的问题。"),
        new("new-task-preserves-question", "A separate explicit task must preserve a previous unanswered question.", "新任务：在 Acme Ubuntu 的 Acme API 项目修复健康检查，让 /health 返回 200。运行相关测试并提交代码。", true),
        new("dispatch-and-return", "Select the specified machine/project, dispatch once and end without waiting.", "请在 Acme Ubuntu 的 Acme API 项目增加 /version 接口，返回当前版本。完成相关测试并提交代码。"),
        new("completion-report", "Report a completed remote result and its PR without launching more work.", "Acme Ubuntu 上的“修复健康检查”任务已完成。执行 agent 报告：/health 已返回 200，相关测试通过，代码已提交，PR：" + PullRequest, Completion: true),
    ];
    private sealed record Options(bool DryRun, bool WithUi, bool CompactUi, string? UiExecutable, string Provider, string? Model, string? Out, string? Executable,
        string[] CaseIds, int Samples, int TimeoutSeconds, string? Baseline);
    private sealed record Check(string Name, bool Passed);
    private sealed record RemoteCall(int Sequence, string MachineId, string Name, JsonElement Arguments);

    public static async Task<int> RunAsync(string[] args)
    {
        Options options;
        try { options = ParseOptions(args); }
        catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 2; }
        var cases = Cases.Where(item => options.CaseIds.Length == 0 || options.CaseIds.Contains(item.Id, StringComparer.Ordinal)).ToArray();
        var instructions = ReadInstructions();
        var instructionHash = Hash(instructions);
        var catalog = JsonSerializer.SerializeToElement(VoiceTools.Catalog(), Json);
        var catalogHash = Hash(catalog.GetRawText());
        if (options.DryRun)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                harness = Harness,
                dryRun = true,
                provider = options.Provider,
                requestedModel = options.Model ?? "provider-configured-default",
                cases = cases.Select(item => new { item.Id, item.Description }),
                samplesPerCase = options.Samples,
                uiFixture = new { enabled = options.WithUi, executableProvided = options.UiExecutable is not null, startup = "Published WinUI 3 executable must signal --ready-file before model calls." },
                imports = new
                {
                    instructions = new { resource = "T3.Voice.Orchestration.OrchestratorInstructions.txt", sha256 = instructionHash, utf8Bytes = Encoding.UTF8.GetByteCount(instructions) },
                    tools = new
                    {
                        source = "VoiceTools.Catalog",
                        sha256 = catalogHash,
                        schemas = catalog.GetProperty("tools").EnumerateArray().Select(tool => new
                        {
                            name = tool.GetProperty("name").GetString(),
                            propertyCount = tool.GetProperty("inputSchema").GetProperty("properties").EnumerateObject().Count(),
                            required = tool.GetProperty("inputSchema").GetProperty("required"),
                        })
                    },
                },
                billing = "No CLI or model launched in dry-run. Real runs consume the selected CLI account's usage; this harness does not measure monetary cost.",
            }, Json));
            return 0;
        }

        var output = Path.GetFullPath(options.Out!);
        Directory.CreateDirectory(output);
        var resultsPath = Path.Combine(output, "results.jsonl");
        if (File.Exists(resultsPath)) { Console.Error.WriteLine("results.jsonl 已存在，请选择新的 --out 目录。"); return 2; }
        using var cancelled = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, message) => { message.Cancel = true; cancelled.Cancel(); };
        Console.CancelKeyPress += handler;
        await using var uiFixture = options.WithUi ? new UiFixture(options.UiExecutable!, options.CompactUi) : null;
        if (uiFixture is not null)
        {
            try { await uiFixture.StartAsync(cancelled.Token); }
            catch (Exception error)
            {
                Console.CancelKeyPress -= handler;
                if (error is OperationCanceledException && cancelled.IsCancellationRequested) return 130;
                Console.Error.WriteLine("WinUI 3 fixture did not become ready; no model calls were started: " + error.Message);
                return 2;
            }
        }
        var outcomes = new List<(string CaseId, bool Passed)>();
        var resourceMeasurementsValid = true;
        await using var results = new StreamWriter(new FileStream(resultsPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        try
        {
            foreach (var item in cases)
            {
                for (var sample = 1; sample <= options.Samples; sample++)
                {
                    cancelled.Token.ThrowIfCancellationRequested();
                    if (uiFixture is not null && !uiFixture.IsAlive)
                    {
                        resourceMeasurementsValid = false;
                        Console.Error.WriteLine("Companion UI exited between samples; no further model calls were started.");
                        break;
                    }
                    Console.WriteLine($"START {item.Id} sample={sample} provider={options.Provider} model={options.Model ?? "provider-configured-default"}");
                    var row = await RunSampleAsync(item, sample, options, instructions, instructionHash, catalogHash, uiFixture, cancelled.Token);
                    outcomes.Add((item.Id, row.Passed));
                    await results.WriteLineAsync(row.JsonLine);
                    await results.FlushAsync(cancelled.Token);
                    Console.WriteLine($"{outcomes.Count}/{cases.Length * options.Samples} {item.Id} sample={sample}: {(row.Passed ? "PASS" : "FAIL")} peak={row.PeakMegabytes:F1} MiB");
                    resourceMeasurementsValid &= row.ResourcesValid;
                    if (!row.ResourcesValid)
                    {
                        Console.Error.WriteLine("Companion UI exited; this sample's UI memory measurement is invalid. Remaining samples were not started.");
                        break;
                    }
                }
                if (!resourceMeasurementsValid) break;
            }
        }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested)
        {
            Console.Error.WriteLine("Eval cancelled; completed rows were retained.");
            return 130;
        }
        finally { Console.CancelKeyPress -= handler; }
        resourceMeasurementsValid &= uiFixture?.IsAlive ?? true;
        var caseOutcomes = outcomes.GroupBy(value => value.CaseId).Select(group => new
        {
            caseId = group.Key,
            samples = group.Count(),
            passedSamples = group.Count(value => value.Passed),
            passed = group.Count(value => !value.Passed) * 2 < group.Count(),
        }).ToArray();
        object? baseline = null;
        if (options.Baseline is not null)
        {
            try { baseline = CompareBaseline(options.Baseline, options, outcomes); }
            catch (Exception error) when (error is IOException or JsonException or InvalidOperationException)
            {
                Console.Error.WriteLine("Baseline unavailable or incompatible: " + error.GetType().Name);
                return 2;
            }
        }
        var summary = new
        {
            harness = Harness,
            provider = options.Provider,
            requestedModel = options.Model ?? "provider-configured-default",
            instructionHash,
            catalogHash,
            cases = caseOutcomes,
            passed = caseOutcomes.All(value => value.passed),
            resourceMeasurementsValid,
            uiFixture = uiFixture?.Description,
            baseline,
            billing = new { source = "local CLI account", measuredAmountUsd = (double?)null, measured = false },
            limits = options.WithUi ? "Authored fake clouds; real CLI/model plus the specified published WinUI 3 companion preview. Peak memory includes the eval harness, companion and their child processes. UI measurements are valid only while the ready companion stays alive." : "Authored fake cloud environments; real local CLI/model. Peak memory includes this test harness and its child process tree, excludes WinUI 3 UI and other applications.",
        };
        await File.WriteAllTextAsync(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions(Json) { WriteIndented = true }));
        return !resourceMeasurementsValid ? 2 : caseOutcomes.All(value => value.passed) ? 0 : 1;
    }

    private static async Task<(bool Passed, string JsonLine, double PeakMegabytes, bool ResourcesValid)> RunSampleAsync(Case item, int sample,
        Options options, string instructions, string instructionHash, string catalogHash, UiFixture? uiFixture, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "t3-voice-eval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var store = new VoiceStore(directory);
            var remote = new AcmeRemote(item.Completion, item.Id == "ambiguous-project");
            var machines = new List<Machine>
            {
                new() { Id = "acme-windows", Name = "Acme Windows", Url = "https://windows.acme.invalid" },
                new() { Id = "acme-ubuntu", Name = "Acme Ubuntu", Url = "https://ubuntu.acme.invalid" },
            };
            store.SaveSettings(new AppSettings { Machines = machines, Provider = options.Provider });
            var fixtureCredential = "eval-only-" + Guid.NewGuid().ToString("N");
            foreach (var machine in machines) store.SetSecret("machine:" + machine.Id, fixtureCredential);
            if (item.PendingQuestion) store.Put(QuestionKey, Question);
            if (item.Completion) store.SaveTask(new RemoteTask
            {
                Id = "acme-task",
                MachineId = "acme-ubuntu",
                ThreadId = "acme-health-thread",
                Title = "修复健康检查",
                RunId = "acme-health-run",
                Status = "completed",
                Snapshot = remote.ReadThread().GetRawText(),
            });
            var tools = new VoiceTools(store, remote) { ActiveInboxId = sample };
            var questions = new ConcurrentQueue<string>();
            tools.Asked += questions.Enqueue;
            await using var bridge = new LocalMcpServer(tools);
            Console.WriteLine($"  {item.Id}: starting isolated loopback tool server");
            await bridge.StartAsync(cancellationToken);
            var prompt = "持久记忆：[]\n近期对话：[]\n当前待答问题：" + JsonSerializer.Serialize(store.List<string>("question:"), Json) + "\n";
            if (item.PendingQuestion) prompt += "录音时正在询问：" + Question + "。这次语音默认回答此问题，但若明确另起任务则按新任务处理。\n";
            prompt += item.Completion ? "后台任务事件：\n" + item.Input + "\n事实：" + remote.ReadThread().GetRawText() : "本次用户语音：\n" + item.Input;
            var request = new ProviderRequest(prompt, null, instructions, bridge.Endpoint, directory,
                options.Executable ?? (options.Provider == "claude" ? "claude" : "codex"), options.Model);
            IProviderDriver driver = options.Provider == "claude" ? new ClaudeDriver() : new CodexDriver();
            var resourceUsage = new ResourceUsage();
            long peakBytes = resourceUsage.Read().WorkingSetBytes;
            long companionAndCliPeakBytes = 0;
            var peakProcesses = 1;
            using var sampling = new CancellationTokenSource();
            async Task SampleAsync()
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
                try
                {
                    while (await timer.WaitForNextTickAsync(sampling.Token))
                    {
                        var usage = resourceUsage.Read();
                        peakBytes = Math.Max(peakBytes, usage.WorkingSetBytes);
                        if (options.WithUi)
                        {
                            using var currentProcess = Process.GetCurrentProcess();
                            companionAndCliPeakBytes = Math.Max(companionAndCliPeakBytes, Math.Max(0, usage.WorkingSetBytes - currentProcess.WorkingSet64));
                        }
                        peakProcesses = Math.Max(peakProcesses, usage.ProcessCount);
                    }
                }
                catch (OperationCanceledException) when (sampling.IsCancellationRequested) { }
            }
            var sampled = SampleAsync();
            var sessions = new ConcurrentQueue<string>();
            var providerQuestions = 0;
            ProviderResult result;
            var timedOut = false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            try
            {
                Console.WriteLine($"  {item.Id}: launching provider; deadline={options.TimeoutSeconds}s");
                result = await driver.RunAsync(request, message =>
                {
                    resourceUsage.ProviderEvent(message);
                    if (message.Kind is "status" or "process" or "process-exit")
                        Console.WriteLine($"  {item.Id}: {message.Kind} {message.Text}");
                    if (message.Kind == "session") sessions.Enqueue(message.Text);
                    if (message.Kind == "question")
                    {
                        Interlocked.Increment(ref providerQuestions);
                        questions.Enqueue(message.Text);
                        store.Put("question:provider-eval-" + Guid.NewGuid().ToString("N"), message.Text);
                    }
                }, timeout.Token);
                if (result.SessionId is not null) sessions.Enqueue(result.SessionId);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
                result = new ProviderResult(null, "", false, "Per-sample deadline exceeded.");
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                result = new ProviderResult(null, "", false, "Harness failure: " + error.GetType().Name);
            }
            finally
            {
                sampling.Cancel();
                await sampled;
            }
            Console.WriteLine($"  {item.Id}: provider returned; success={result.Success}, timedOut={timedOut}");
            var resourcesValid = uiFixture?.IsAlive ?? true;
            var calls = remote.Calls.ToArray();
            var checks = Grade(item, result, timedOut, store, calls, questions.Count, remote.InvalidCalls);
            var secrets = sessions.Concat(new[] { bridge.Endpoint.BearerToken, bridge.Endpoint.Url.AbsoluteUri, fixtureCredential, directory }).Where(value => value.Length > 0).ToArray();
            string Safe(string value, int limit = 4000)
            {
                foreach (var secret in secrets) value = value.Replace(secret, "[redacted]", StringComparison.Ordinal);
                return value[..Math.Min(value.Length, limit)];
            }
            var passed = checks.All(check => check.Passed);
            var row = new
            {
                harness = Harness,
                caseId = item.Id,
                sample,
                provider = options.Provider,
                requestedModel = options.Model ?? "provider-configured-default",
                actualModel = "not reported by CLI driver",
                provenance = "Authored Acme control from the Windows voice coordinator requirements; no captured user data.",
                instructionHash,
                catalogHash,
                inputHash = Hash(prompt),
                passed,
                checks,
                providerSucceeded = result.Success,
                timedOut,
                finalText = Safe(result.Text),
                error = result.Error is null ? null : Safe(result.Error, 1000),
                questions = questions.Select(value => Safe(value, 1000)),
                observations = new
                {
                    providerQuestions,
                    calls = calls.Select(call => new { call.Sequence, call.MachineId, call.Name, arguments = Safe(call.Arguments.GetRawText(), 2000) }),
                    trackedTasks = store.Tasks().Select(task => new { task.MachineId, task.ThreadId, task.Status }),
                    pendingQuestionPreserved = store.Get<string>(QuestionKey) == Question,
                },
                resources = new
                {
                    valid = resourcesValid,
                    invalidReason = resourcesValid ? null : "The ready WinUI 3 companion exited during this trajectory; exclude this UI memory measurement.",
                    peakWorkingSetBytes = resourcesValid ? (long?)peakBytes : null,
                    companionAndCliPeakBytes = options.WithUi && resourcesValid ? (long?)companionAndCliPeakBytes : null,
                    peakProcessCount = peakProcesses,
                    samplingIntervalMs = 200,
                    uiFixture = uiFixture?.Description,
                    scope = options.WithUi ? "eval harness + published WinUI 3 companion preview + CLI process tree; companionAndCliPeakBytes subtracts the eval process itself" : "native test harness + its child process tree; WinUI 3 UI excluded"
                },
                billing = new { source = "local CLI account", measuredAmountUsd = (double?)null, measured = false },
            };
            return (passed, JsonSerializer.Serialize(row, Json), resourcesValid ? peakBytes / 1024d / 1024d : double.NaN, resourcesValid);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static Check[] Grade(Case item, ProviderResult result, bool timedOut, VoiceStore store, RemoteCall[] calls, int questions, int invalidCalls)
    {
        var checks = new List<Check>
        {
            new("provider finished successfully", result.Success && !timedOut),
            new("used fixture tools and targets correctly", invalidCalls == 0),
        };
        var launches = calls.Where(call => call.Name == "t3_thread_launch").ToArray();
        var writes = calls.Where(call => call.Name is "t3_thread_launch" or "t3_thread_send").ToArray();
        if (item.Id == "ambiguous-project")
        {
            checks.Add(new("asked for missing project identity", questions > 0 && store.List<string>("question:").Count > 0));
            checks.Add(new("did not dispatch before project was clear", writes.Length == 0));
        }
        else if (item.Completion)
        {
            checks.Add(new("reported supplied PR and health result", result.Text.Contains(PullRequest, StringComparison.Ordinal) && result.Text.Contains("health", StringComparison.OrdinalIgnoreCase)));
            checks.Add(new("did not launch or wait for completed work", writes.Length == 0 && calls.All(call => call.Name != "t3_thread_wait")));
            checks.Add(new("did not ask for information already supplied", questions == 0));
        }
        else
        {
            var correctLaunch = launches.Length == 1 && launches[0].MachineId == "acme-ubuntu" && VoiceTools.Text(launches[0].Arguments, "projectId") == "acme-api";
            checks.Add(new("dispatched once to selected machine and project", correctLaunch));
            var goal = item.PendingQuestion ? "/health" : "/version";
            checks.Add(new("executor received goal and test requirement", correctLaunch &&
                (VoiceTools.Text(launches[0].Arguments, "message") ?? "").Contains(goal, StringComparison.Ordinal) &&
                (VoiceTools.Text(launches[0].Arguments, "message") ?? "").Contains("测试", StringComparison.Ordinal)));
            checks.Add(new("remote launch is tracked", store.Tasks().Any(task => task.MachineId == "acme-ubuntu" && task.ThreadId == "acme-created-thread")));
            checks.Add(new("returned after dispatch without waiting or polling", launches.Length == 1 && calls.All(call => call.Name != "t3_thread_wait") &&
                calls.All(call => call.Sequence <= launches[0].Sequence)));
            checks.Add(new("did not ask an unnecessary question", questions == 0));
            checks.Add(new("did not claim executor work was already completed", !new[] { "已完成修复", "已修复", "已经修好", "测试已通过", "已经提交" }.Any(phrase => result.Text.Contains(phrase, StringComparison.Ordinal))));
            if (item.PendingQuestion) checks.Add(new("preserved unrelated unanswered question", store.Get<string>(QuestionKey) == Question));
        }
        return checks.ToArray();
    }

    private static Options ParseOptions(string[] args)
    {
        var dryRun = false;
        var withUi = false;
        var compactUi = false;
        var provider = "codex";
        string? model = null, output = null, executable = null, uiExecutable = null, baseline = null;
        var caseIds = Array.Empty<string>();
        var samples = 3;
        var seconds = 120;
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (option == "--eval") continue;
            if (option == "--dry-run") { dryRun = true; continue; }
            if (option == "--with-ui") { withUi = true; continue; }
            if (option == "--ui-compact") { compactUi = true; continue; }
            if (index + 1 == args.Length) throw new ArgumentException("Missing value for " + option);
            var value = args[++index];
            switch (option)
            {
                case "--provider": provider = value; break;
                case "--model": model = value; break;
                case "--out": output = value; break;
                case "--executable": executable = value; break;
                case "--ui-executable": uiExecutable = value; break;
                case "--baseline": baseline = value; break;
                case "--case": caseIds = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
                case "--samples": if (!int.TryParse(value, out samples) || samples is < 1 or > 10) throw new ArgumentException("--samples must be 1..10"); break;
                case "--timeout-seconds": if (!int.TryParse(value, out seconds) || seconds is < 20 or > 600) throw new ArgumentException("--timeout-seconds must be 20..600"); break;
                default: throw new ArgumentException("Unknown eval option: " + option);
            }
        }
        if (provider is not ("codex" or "claude")) throw new ArgumentException("--provider must be codex or claude");
        if (!dryRun && string.IsNullOrWhiteSpace(output)) throw new ArgumentException("--out <directory> is required for real model runs");
        if (withUi && string.IsNullOrWhiteSpace(uiExecutable)) throw new ArgumentException("--with-ui requires --ui-executable <published T3.Voice.exe path>");
        if (!withUi && uiExecutable is not null) throw new ArgumentException("--ui-executable requires --with-ui");
        if (uiExecutable is not null)
        {
            uiExecutable = Path.GetFullPath(uiExecutable);
            if (!File.Exists(uiExecutable) || !string.Equals(Path.GetExtension(uiExecutable), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("--ui-executable must name an existing published T3.Voice.exe file");
        }
        if (caseIds.Any(id => !Cases.Any(item => item.Id == id))) throw new ArgumentException("Unknown --case. Available: " + string.Join(",", Cases.Select(item => item.Id)));
        if (compactUi && !withUi) throw new ArgumentException("--ui-compact requires --with-ui");
        return new Options(dryRun, withUi, compactUi, uiExecutable, provider, model, output, executable, caseIds, samples, seconds, baseline);
    }

    private sealed class UiFixture : IAsyncDisposable
    {
        private Process? process;
        private bool started;
        private readonly string executable;
        private readonly bool compact;
        private readonly string directory = Path.Combine(Path.GetTempPath(), "t3-voice-ui-eval-" + Guid.NewGuid().ToString("N"));
        public bool IsAlive => started && process is not null && !process.HasExited;
        public object Description { get; }
        public UiFixture(string executable, bool compact)
        {
            this.executable = executable;
            this.compact = compact;
            using var binary = File.OpenRead(executable);
            Description = new { framework = "WinUI 3", state = compact ? "tray and popup; settings closed" : "settings and popup open", executable = Path.GetFileName(executable), sha256 = Convert.ToHexString(SHA256.HashData(binary)).ToLowerInvariant() };
        }
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(directory);
            var readyFile = Path.Combine(directory, "ui-ready");
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new FileSystemWatcher(directory, "ui-ready") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
            FileSystemEventHandler appeared = (_, _) => { if (File.Exists(readyFile)) ready.TrySetResult(); };
            RenamedEventHandler renamed = (_, _) => { if (File.Exists(readyFile)) ready.TrySetResult(); };
            watcher.Created += appeared;
            watcher.Changed += appeared;
            watcher.Renamed += renamed;
            watcher.EnableRaisingEvents = true;
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
            foreach (var argument in new[] { "--demo", "--data-dir", directory, "--ready-file", readyFile }) start.ArgumentList.Add(argument);
            if (compact) start.ArgumentList.Add("--demo-compact");
            process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.Exited += (_, _) => ready.TrySetException(new InvalidOperationException("The WinUI 3 companion exited before startup completed."));
            if (!process.Start()) throw new InvalidOperationException("Could not start WinUI 3 companion fixture.");
            started = true;
            Console.WriteLine("Started WinUI 3 companion fixture pid=" + process.Id);
            if (File.Exists(readyFile)) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            if (!IsAlive) throw new InvalidOperationException("The WinUI 3 companion exited immediately after signaling readiness.");
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (started && process is not null && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            finally
            {
                process?.Dispose();
                try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine("The captured UI process was stopped, but its temporary database is still locked; retained fixture data at " + directory);
                }
            }
        }
    }

    private static string ReadInstructions()
    {
        using var stream = typeof(VoiceTools).Assembly.GetManifestResourceStream("T3.Voice.Orchestration.OrchestratorInstructions.txt")
            ?? throw new InvalidOperationException("Production orchestrator instructions were not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static object CompareBaseline(string path, Options options, List<(string CaseId, bool Passed)> current)
    {
        var file = Directory.Exists(path) ? Path.Combine(path, "results.jsonl") : path;
        var previous = new List<(string CaseId, bool Passed)>();
        foreach (var line in File.ReadLines(file))
        {
            if (line.Length > 100_000) throw new InvalidOperationException("Baseline row too large");
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            if (row.GetProperty("provider").GetString() != options.Provider || row.GetProperty("requestedModel").GetString() != (options.Model ?? "provider-configured-default"))
                throw new InvalidOperationException("Baseline provider and model must match");
            previous.Add((row.GetProperty("caseId").GetString()!, row.GetProperty("passed").GetBoolean()));
        }
        return current.GroupBy(value => value.CaseId).Select(group => new
        {
            caseId = group.Key,
            previousSamples = previous.Count(value => value.CaseId == group.Key),
            previousPassed = previous.Count(value => value.CaseId == group.Key && value.Passed),
            currentSamples = group.Count(),
            currentPassed = group.Count(value => value.Passed),
        }).ToArray();
    }

    private sealed class AcmeRemote(bool completed, bool ambiguous) : IRemoteMcpClient
    {
        internal ConcurrentQueue<RemoteCall> Calls { get; } = new();
        internal int InvalidCalls;
        private int sequence;
        public Task<JsonElement> ListToolsAsync(Machine machine, string accessToken, CancellationToken cancellationToken)
        {
            static object Field(string type) => new { type };
            static object Definition(string name, object properties, bool readOnly, params string[] required) => new
            {
                name,
                description = "Authored Acme eval fixture; simulates a T3 operation without contacting a cloud host.",
                inputSchema = new { type = "object", properties, required, additionalProperties = true },
                annotations = new { readOnlyHint = readOnly },
            };
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                tools = new[]
            {
                Definition("t3_project_list", new { }, true),
                Definition("t3_project_read", new { projectId = Field("string") }, true, "projectId"),
                Definition("t3_thread_list", new { projectId = Field("string") }, true),
                Definition("orchestrator_capabilities", new { }, true),
                Definition("t3_thread_launch", new { projectId = Field("string"), title = Field("string"), message = Field("string"), runtimeMode = Field("string"), modelSelection = new { type = "object", properties = new { instanceId = Field("string"), model = Field("string") } }, clientRequestId = Field("string") }, false, "projectId", "title", "message"),
                Definition("t3_thread_send", new { threadId = Field("string"), message = Field("string"), clientRequestId = Field("string") }, false, "threadId", "message"),
                Definition("t3_thread_read", new { threadId = Field("string") }, true, "threadId"),
                Definition("t3_thread_wait", new { threadId = Field("string") }, true, "threadId"),
            }
            }, Json));
        }

        public Task<JsonElement> CallToolAsync(Machine machine, string accessToken, string name, JsonElement arguments, CancellationToken cancellationToken)
        {
            Calls.Enqueue(new RemoteCall(Interlocked.Increment(ref sequence), machine.Id, name, arguments.Clone()));
            var projectId = machine.Id == "acme-ubuntu" ? "acme-api" : "acme-web";
            var project = new
            {
                id = projectId,
                projectId,
                title = machine.Id == "acme-ubuntu" ? "Acme API" : "Acme Web",
                repository = machine.Id == "acme-ubuntu" ? "https://github.com/acme/api" : "https://github.com/acme/web",
                modelSelection = new { instanceId = "fixture-provider", model = "fixture-model" },
            };
            var projects = ambiguous && machine.Id == "acme-windows"
                ? new[]
                {
                    new { id = "acme-web-legacy", projectId = "acme-web-legacy", title = "Acme Web（旧版）", repository = "https://github.com/acme/web-legacy", modelSelection = new { instanceId = "fixture-provider", model = "fixture-model" } },
                    new { id = "acme-web-next", projectId = "acme-web-next", title = "Acme Web（新版）", repository = "https://github.com/acme/web-next", modelSelection = new { instanceId = "fixture-provider", model = "fixture-model" } },
                }
                : new[] { project };
            object result;
            switch (name)
            {
                case "t3_project_list":
                    result = new { projects, nextCursor = (int?)null };
                    break;
                case "t3_project_read":
                    var selectedProject = projects.FirstOrDefault(candidate => candidate.id == VoiceTools.Text(arguments, "projectId"));
                    if (selectedProject is null) return Invalid("Project does not belong to selected machine.");
                    result = selectedProject; break;
                case "t3_thread_list": result = new { threads = Array.Empty<object>(), nextCursor = (int?)null }; break;
                case "orchestrator_capabilities":
                    result = new
                    {
                        providers = new[] { new { providerInstanceId = "fixture-provider", driverKind = "codex", availableForOrchestration = true, models = new[] { new { id = "fixture-model" } } } },
                    }; break;
                case "t3_thread_launch":
                    if (!projects.Any(candidate => candidate.id == VoiceTools.Text(arguments, "projectId")) || string.IsNullOrWhiteSpace(VoiceTools.Text(arguments, "title")) || string.IsNullOrWhiteSpace(VoiceTools.Text(arguments, "message")))
                        return Invalid("Launch requires an existing project on this machine, a title and a task message.");
                    result = new { threadId = "acme-created-thread", runId = "acme-created-run", projectId = VoiceTools.Text(arguments, "projectId"), status = "running" }; break;
                case "t3_thread_read": result = ReadThread(); break;
                case "t3_thread_wait": result = new { status = "running", waitTimedOut = true }; break;
                case "t3_thread_send": result = new { threadId = VoiceTools.Text(arguments, "threadId"), runId = "acme-followup-run", status = "running" }; break;
                default: return Invalid("This authored environment does not offer that operation.");
            }
            return Task.FromResult(VoiceTools.Result(result));
        }

        private Task<JsonElement> Invalid(string message)
        {
            Interlocked.Increment(ref InvalidCalls);
            return Task.FromResult(JsonSerializer.SerializeToElement(new { isError = true, content = new[] { new { type = "text", text = message } } }, Json));
        }
        internal JsonElement ReadThread() => JsonSerializer.SerializeToElement(new
        {
            thread = new { threadId = "acme-health-thread", title = "修复健康检查", status = completed ? "completed" : "running", latestRunId = "acme-health-run", pendingRequestCount = 0 },
            recentRuns = new[] { new { runId = "acme-health-run", status = completed ? "completed" : "running" } },
            items = completed ? new[] { new { type = "assistant_message", text = "/health 已返回 200，相关测试通过，代码已提交。PR：" + PullRequest } } : Array.Empty<object>(),
        }, Json);
    }
}
