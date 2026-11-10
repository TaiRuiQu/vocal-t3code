using System.IO;
using System.Text.Json;

namespace T3.Voice.Tests;

public static class StoreTests
{
    public static Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "t3-voice-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var store = new VoiceStore(directory))
            {
                store.SaveSettings(new AppSettings { Provider = "claude", PopupPosition = "left" });
                store.SetSecret("test", "sample-test-secret-测试");
                store.AddMessage("user", "继续开发科技树 🌲");
                Require(store.Enqueue("user", "任务一", "same"), "Initial inbox insert failed");
                Require(!store.Enqueue("user", "任务一", "same"), "Duplicate inbox was inserted");
                var task = new RemoteTask { Id = "task", MachineId = "machine", ThreadId = "thread" };
                store.ObserveTask(task with { Status = "completed" }, "任务完成", "completion:task");
                store.ObserveTask(task with { Status = "completed" }, "任务完成", "completion:task");
                store.Put("memory:project", "小游戏");
            }
            using (var reopened = new VoiceStore(directory))
            {
                Require(reopened.LoadSettings().Provider == "claude", "Settings did not survive restart");
                Require(reopened.GetSecret("test") == "sample-test-secret-测试", "Encrypted secret round trip failed");
                Require(reopened.RecentMessages()[0].Text == "继续开发科技树 🌲", "Unicode conversation lost");
                Require(reopened.PendingInbox().Count == 2, "Completion transaction/dedup failed");
                Require(reopened.Tasks().Single().Status == "completed", "Task state lost");
                reopened.CompleteInbox(reopened.PendingInbox()[0].Id);
                Require(reopened.PendingInbox().Count == 1, "Acknowledgment not persisted");
                reopened.CompleteTurn(reopened.PendingInbox()[0].Id, "codex", "sample-session", "任务完成，已创建 PR");
                Require(reopened.PendingInbox().Count == 0 && reopened.Get<string>("session:codex") == "sample-session" && reopened.RecentMessages().Last().Text == "任务完成，已创建 PR", "Turn result, session and receipt must commit together");
                Require(reopened.List<string>("memory:").Single().Value == "小游戏", "Durable memory missing");
                reopened.Put("question:sample", "使用哪个项目？");
                var submitted = reopened.SubmitInput("继续科技树项目");
                Require(submitted.QuestionKey == "question:sample" && submitted.HistoryThroughId == reopened.RecentMessages().Last().Id, "Input must atomically capture its question and conversation boundary");
                Require(JsonSerializer.Deserialize<UserInput>(reopened.PendingInbox().Single().Text, VoiceStore.Json) == submitted, "Input history and durable inbox must agree");
                using var parsed = JsonDocument.Parse("""{"thread":{"status":"completed","pendingRequestCount":1,"latestRunId":"run"},"items":[]}""");
                Require(Coordinator.Observe(new RemoteTask(), parsed.RootElement).Status == "waiting", "Pending question reported complete");
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
