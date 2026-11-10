using System;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using T3.Voice.Native;
using T3.Voice.Speech;

namespace T3.Voice.Tests;

public static class SpeechTests
{
    public static async Task RunAsync()
    {
        TranscriptCorrections();
        ProtocolCompletion();
        RightShiftGesture();
        ChordsAndEscape();
        await StreamsAndAwaitsFinalResultAsync();
        await CancelsAndRejectsAudioOverflowAsync();
        await RecordingSessionTests.RunAsync();
    }

    private static JsonElement Message(string kind, object payload) => JsonSerializer.SerializeToElement(new
    {
        header = new { task_id = "test-task", @event = kind },
        payload,
    });

    private static JsonElement Sentence(int id, string text, bool final, bool heartbeat = false) =>
        Message("result-generated", new
        {
            output = new { sentence = new { sentence_id = id, text, sentence_end = final, heartbeat } },
        });

    private static void TranscriptCorrections()
    {
        var protocol = new AsrProtocol("test-task");
        protocol.Accept(Message("task-started", new { }));
        protocol.Accept(Sentence(1, "把 A", false));
        protocol.Accept(Sentence(1, "把 B 项目", false));
        Equal("把 B 项目", protocol.Transcript, "中间结果必须替换同一句，不能重复追加");
        Equal("", protocol.FinalTranscript, "中间结果不能作为可派发的最终文本");
        protocol.Accept(Sentence(2, "然后运行测试。", true));
        protocol.Accept(Sentence(1, "把 B 项目修好。", true));
        Equal("把 B 项目修好。然后运行测试。", protocol.Transcript, "晚到的前句最终结果必须排回原来的顺序");
        protocol.Accept(Sentence(1, "过时的中间结果", false));
        protocol.Accept(Sentence(0, "", false, heartbeat: true));
        Equal("把 B 项目修好。然后运行测试。", protocol.FinalTranscript, "最终结果不能被过时中间结果或心跳覆盖");

        var english = new AsrProtocol("test-task");
        english.Accept(Message("task-started", new { }));
        english.Accept(Sentence(1, "Fix the bug.", true));
        english.Accept(Sentence(2, "Run tests.", true));
        Equal("Fix the bug. Run tests.", english.Transcript, "英文句子之间需要保留词边界");
    }

    private static void ProtocolCompletion()
    {
        var protocol = new AsrProtocol("test-task");
        protocol.Accept(Message("task-started", new { }));
        protocol.Accept(Sentence(1, "尚未确认", false));
        protocol.MarkFinishing();
        Throws(() => protocol.Accept(Message("task-finished", new { })), "只有未完成文本时不能派发任务");
        protocol.Accept(Sentence(1, "最终确认。", true));
        Equal(AsrServerEvent.Finished, protocol.Accept(Message("task-finished", new { })), "必须等待 task-finished 才能完成");
        Equal("最终确认。", protocol.FinalTranscript, "最终消息应包含结束阶段收到的修正");

        var early = new AsrProtocol("test-task");
        early.Accept(Message("task-started", new { }));
        Throws(() => early.Accept(Message("task-finished", new { })), "发送 finish-task 前的意外结束必须报错");
        var failed = JsonSerializer.SerializeToElement(new
        {
            header = new { task_id = "test-task", @event = "task-failed", error_code = "DENIED", error_message = "No access" },
            payload = new { },
        });
        Throws(() => early.Accept(failed), "服务端失败必须终止识别");
        var mismatch = new AsrProtocol("another-task");
        Throws(() => mismatch.Accept(Message("task-started", new { })), "其他任务的消息不能污染当前录音");

        using var run = JsonDocument.Parse(AsrProtocol.RunTask("test-task", "fun-asr-realtime"));
        Equal("run-task", run.RootElement.GetProperty("header").GetProperty("action").GetString(), "启动指令协议");
        var parameters = run.RootElement.GetProperty("payload").GetProperty("parameters");
        Equal("pcm", parameters.GetProperty("format").GetString(), "录音必须与请求格式一致");
        Equal(16000, parameters.GetProperty("sample_rate").GetInt32(), "录音采样率必须与请求一致");

        var bounded = new AsrProtocol("test-task");
        bounded.Accept(Message("task-started", new { }));
        var longSentence = new string('字', 65_536);
        bounded.Accept(Sentence(1, longSentence, false));
        bounded.Accept(Sentence(1, longSentence, true));
        Equal(65_536, bounded.FinalTranscript.Length, "同一句修订不能累计占用转写额度");
        Throws(() => bounded.Accept(Sentence(2, "超", true)), "累计转写超过上限必须失败，不能截断并派发");
        bounded.Clear();
        bounded.Accept(Sentence(1, longSentence, true));
        Equal(65_536, bounded.FinalTranscript.Length, "清空录音必须释放已用转写额度");
    }

    private static void RightShiftGesture()
    {
        var state = new KeyboardGestureState();
        Equal(KeyboardGesture.None, state.Process(0xA1, true, 10, false), "第一次按下不能启动录音");
        Equal(KeyboardGesture.None, state.Process(0xA1, true, 30, false), "自动重复不能计为第二次按键");
        Equal(KeyboardGesture.None, state.Process(0xA1, false, 80, false), "只释放一次不能启动录音");
        Equal(KeyboardGesture.None, state.Process(0xA1, true, 100, false), "第二次按下也要等待释放");
        Equal(KeyboardGesture.ToggleRecording, state.Process(0xA1, false, 160, false), "两次完整右 Shift 轻敲切换录音");
        Equal(KeyboardGesture.None, state.Process(0xA1, true, 180, false), "三连击不能重用上次已消耗的轻敲");
        Equal(KeyboardGesture.None, state.Process(0xA1, false, 220, false), "三连击只触发一次");
        state.Process(0xA1, true, 800, false);
        Equal(KeyboardGesture.None, state.Process(0xA1, false, 1200, false), "游戏中的长按不能计为轻敲");

        var wrapped = new KeyboardGestureState();
        wrapped.Process(0xA1, true, uint.MaxValue - 100, false);
        wrapped.Process(0xA1, false, uint.MaxValue - 50, false);
        wrapped.Process(0xA1, true, 20, false);
        Equal(KeyboardGesture.ToggleRecording, wrapped.Process(0xA1, false, 80, false), "Win32 时间戳溢出时仍应正确检测");
    }

    private static void ChordsAndEscape()
    {
        var state = new KeyboardGestureState();
        state.Process(0xA1, true, 10, false);
        state.Process(0x41, true, 20, false);
        state.Process(0x41, false, 30, false);
        Equal(KeyboardGesture.None, state.Process(0xA1, false, 40, false), "Shift 组合键不能被当作轻敲");
        state.Process(0xA1, true, 50, false);
        Equal(KeyboardGesture.None, state.Process(0xA1, false, 80, false), "组合键不能为后续轻敲提供第一次计数");
        state.Process(0xA0, true, 90, false);
        state.Process(0xA0, false, 100, false);
        state.Process(0xA1, true, 110, false);
        Equal(KeyboardGesture.None, state.Process(0xA1, false, 140, false), "左 Shift 不应完成右 Shift 双击");
        Equal(KeyboardGesture.None, state.Process(0x1B, true, 200, false), "空闲时不能吞掉游戏 Esc");
        state.Process(0x1B, false, 210, false);
        Equal(KeyboardGesture.CancelRecording | KeyboardGesture.Suppress, state.Process(0x1B, true, 220, true), "录音时 Esc 应取消并阻止传播");
        Equal(KeyboardGesture.Suppress, state.Process(0x1B, true, 230, false), "Esc 长按不能重复取消或穿透");
        Equal(KeyboardGesture.Suppress, state.Process(0x1B, false, 240, false), "已吞掉的 Esc 按下必须对应吞掉释放");
        Equal(KeyboardGesture.None, state.Process(0x1B, true, 260, false), "结束录音后新的 Esc 应交还游戏");
    }

    private static async Task StreamsAndAwaitsFinalResultAsync()
    {
        using var socket = new FakeSocket { BlockBinary = true };
        await using var asr = new AliyunAsr(new AppSettings(), socket);
        var start = asr.StartAsync();
        var run = await socket.Sent.Reader.ReadAsync();
        using var runMessage = JsonDocument.Parse(run.Data);
        var taskId = runMessage.RootElement.GetProperty("header").GetProperty("task_id").GetString()!;
        Equal(true, asr.TrySendAudio(new byte[3200]), "启动阶段可以缓冲音频");
        Equal(false, socket.BinaryEntered.Task.IsCompleted, "task-started 前不能发送音频");
        socket.Push(taskId, "task-started", new { });
        await start;
        await socket.BinaryEntered.Task;
        var binary = await socket.Sent.Reader.ReadAsync();
        Equal(WebSocketMessageType.Binary, binary.Type, "音频必须为二进制帧");
        var partial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        asr.TranscriptChanged += text => { if (text == "实时修正") partial.TrySetResult(); };
        socket.PushSentence(taskId, 1, "实时文字", false);
        socket.PushSentence(taskId, 1, "实时修正", false);
        await partial.Task;
        var finish = asr.FinishAsync();
        Equal(false, finish.IsCompleted, "音频未排空不能结束识别");
        Equal(false, socket.Sent.Reader.TryPeek(out _), "队列排空前不能发送 finish-task");
        socket.ReleaseBinary.TrySetResult();
        var finishFrame = await socket.Sent.Reader.ReadAsync();
        using var finishMessage = JsonDocument.Parse(finishFrame.Data);
        Equal("finish-task", finishMessage.RootElement.GetProperty("header").GetProperty("action").GetString(), "排空音频后通知服务端结束");
        Equal(false, finish.IsCompleted, "发送 finish-task 不等于任务已完成");
        socket.PushSentence(taskId, 1, "最终修正。", true);
        socket.Push(taskId, "task-finished", new { });
        Equal("最终修正。", await finish, "派发文本必须包含 task-finished 前的最终修正");
    }

    private static async Task CancelsAndRejectsAudioOverflowAsync()
    {
        using (var socket = new FakeSocket())
        {
            using var cancellation = new CancellationTokenSource();
            await using var asr = new AliyunAsr(new AppSettings(), socket);
            var start = asr.StartAsync(cancellation.Token);
            var run = await socket.Sent.Reader.ReadAsync();
            using var runMessage = JsonDocument.Parse(run.Data);
            var id = runMessage.RootElement.GetProperty("header").GetProperty("task_id").GetString()!;
            socket.Push(id, "task-started", new { });
            await start;
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(async () => { await asr.Completion; });
            Equal(false, asr.TrySendAudio(new byte[3200]), "取消后不能再接收音频或派发文本");
        }

        using (var socket = new FakeSocket { BlockBinary = true })
        {
            await using var asr = new AliyunAsr(new AppSettings(), socket);
            var errors = 0;
            asr.Failed += _ => errors++;
            var start = asr.StartAsync();
            var run = await socket.Sent.Reader.ReadAsync();
            using var runMessage = JsonDocument.Parse(run.Data);
            var id = runMessage.RootElement.GetProperty("header").GetProperty("task_id").GetString()!;
            socket.Push(id, "task-started", new { });
            await start;
            asr.TrySendAudio(new byte[3200]);
            await socket.BinaryEntered.Task;
            for (var frame = 0; frame < 50; frame++)
                Equal(true, asr.TrySendAudio(new byte[3200]), "有界音频队列在容量内可写入");
            Equal(false, asr.TrySendAudio(new byte[3200]), "队列溢出必须终止，不能静默丢帧");
            await ThrowsAsync<InvalidOperationException>(async () => { await asr.Completion; });
            Equal(1, errors, "音频溢出只能报告一次失败");
        }
    }

    private sealed record SocketFrame(byte[] Data, WebSocketMessageType Type);

    private sealed class FakeSocket : WebSocket
    {
        private readonly Channel<byte[]> _received = Channel.CreateUnbounded<byte[]>();
        private WebSocketState _state = WebSocketState.Open;
        public Channel<SocketFrame> Sent { get; } = Channel.CreateUnbounded<SocketFrame>();
        public TaskCompletionSource BinaryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBinary { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockBinary { get; init; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() => _state = WebSocketState.Closed;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public void Push(string taskId, string kind, object payload) => _received.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(new
        {
            header = new { task_id = taskId, @event = kind },
            payload,
        }));

        public void PushSentence(string taskId, int id, string text, bool final) => Push(taskId, "result-generated", new
        {
            output = new { sentence = new { sentence_id = id, text, sentence_end = final } },
        });

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            Sent.Writer.TryWrite(new SocketFrame(buffer.ToArray(), messageType));
            if (messageType != WebSocketMessageType.Binary) return;
            BinaryEntered.TrySetResult();
            if (BlockBinary) await ReleaseBinary.Task.WaitAsync(cancellationToken);
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var message = await _received.Reader.ReadAsync(cancellationToken);
            message.AsSpan().CopyTo(buffer.AsSpan());
            return new WebSocketReceiveResult(message.Length, WebSocketMessageType.Text, true);
        }
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private static void Equal<T>(T expected, T actual, string reason)
    {
        if (!Equals(expected, actual)) throw new InvalidOperationException($"{reason}: expected {expected}, got {actual}");
    }

    private static void Throws(Action action, string reason)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException(reason);
    }
}
