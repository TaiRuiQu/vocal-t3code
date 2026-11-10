using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace T3.Voice.Speech;

public enum AsrServerEvent { Ignored, Started, Transcript, Finished }

public sealed class AsrProtocol
{
    private sealed record Sentence(string Text, bool Final);
    private readonly string _taskId;
    private readonly SortedDictionary<int, Sentence> _sentences = [];
    private bool _started;
    private bool _finishing;
    private bool _finished;
    private int _characters;

    public AsrProtocol(string taskId) => _taskId = taskId;
    public string Transcript => Join(_sentences.Values.Select(sentence => sentence.Text));
    public string FinalTranscript => Join(_sentences.Values.Where(sentence => sentence.Final).Select(sentence => sentence.Text));

    public void MarkFinishing() => _finishing = true;
    public void Clear() { _sentences.Clear(); _characters = 0; }

    public AsrServerEvent Accept(JsonElement message)
    {
        var header = message.GetProperty("header");
        if (header.GetProperty("task_id").GetString() != _taskId)
            throw new InvalidOperationException("语音服务返回了不匹配的任务 ID。");
        var kind = header.GetProperty("event").GetString();
        if (kind == "task-failed")
        {
            var code = header.TryGetProperty("error_code", out var codeValue) ? codeValue.GetString() : "unknown";
            var detail = header.TryGetProperty("error_message", out var detailValue) ? detailValue.GetString() : "未知错误";
            throw new InvalidOperationException($"阿里云语音识别失败 ({code})：{detail}");
        }
        if (_finished) throw new InvalidOperationException("语音服务在任务结束后继续返回结果。");
        switch (kind)
        {
            case "task-started":
                _started = true;
                return AsrServerEvent.Started;
            case "result-generated":
                if (!_started) throw new InvalidOperationException("语音服务在任务启动前返回了识别结果。");
                var sentence = message.GetProperty("payload").GetProperty("output").GetProperty("sentence");
                if (sentence.TryGetProperty("heartbeat", out var heartbeat) && heartbeat.ValueKind == JsonValueKind.True)
                    return AsrServerEvent.Ignored;
                var id = sentence.GetProperty("sentence_id").GetInt32();
                if (id < 1) throw new InvalidOperationException("语音服务返回了无效的句子序号。");
                var text = sentence.GetProperty("text").GetString() ?? "";
                var final = sentence.GetProperty("sentence_end").GetBoolean();
                if (_sentences.TryGetValue(id, out var previous) && previous.Final && !final)
                    return AsrServerEvent.Ignored;
                var characters = _characters - (previous?.Text.Length ?? 0) + text.Length;
                if (characters > 65_536 || (!_sentences.ContainsKey(id) && _sentences.Count >= 2_048))
                    throw new InvalidOperationException("录音转写内容过长，本次未发送。请将任务拆成较短的语音指令。");
                _characters = characters;
                _sentences[id] = new Sentence(text, final);
                return AsrServerEvent.Transcript;
            case "task-finished":
                if (!_started || !_finishing)
                    throw new InvalidOperationException("语音服务提前结束了任务，请重新录音。");
                if (_sentences.Values.Any(sentence => !sentence.Final && !string.IsNullOrWhiteSpace(sentence.Text)))
                    throw new InvalidOperationException("语音服务没有返回完整的最终识别结果，请重新录音。");
                _finished = true;
                return AsrServerEvent.Finished;
            default:
                return AsrServerEvent.Ignored;
        }
    }

    public static byte[] RunTask(string taskId, string model) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        header = new { action = "run-task", task_id = taskId, streaming = "duplex" },
        payload = new
        {
            task_group = "audio",
            task = "asr",
            function = "recognition",
            model,
            parameters = new { format = "pcm", sample_rate = 16000 },
            input = new { },
        },
    });

    public static byte[] FinishTask(string taskId) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        header = new { action = "finish-task", task_id = taskId, streaming = "duplex" },
        payload = new { input = new { } },
    });

    private static string Join(IEnumerable<string> sentences)
    {
        var text = new StringBuilder();
        foreach (var sentence in sentences)
        {
            if (sentence.Length == 0) continue;
            if (text.Length > 0 && char.IsAsciiLetterOrDigit(sentence[0])
                && (char.IsAsciiLetterOrDigit(text[^1]) || text[^1] is '.' or '!' or '?')) text.Append(' ');
            text.Append(sentence);
        }
        return text.ToString().Trim();
    }
}
