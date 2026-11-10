using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace T3.Voice;

internal sealed class ProviderProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _stderrReader;
    private readonly StringBuilder _stderr = new();
    private readonly CancellationTokenSource _readCancellation = new();
    private readonly string _token;
    private readonly CancellationTokenRegistration _stopRegistration;
    private int _disposed;

    public StreamReader Output => _process.StandardOutput;
    public StreamWriter Input => _process.StandardInput;
    public string ErrorOutput { get { lock (_stderr) return _stderr.ToString(); } }
    public int Id { get; }

    public ProviderProcess(ProviderRequest request, IEnumerable<string> arguments, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Executable)) throw new ArgumentException("请选择本机 CLI 可执行文件。");
        if (Path.GetExtension(request.Executable) is ".cmd" or ".bat")
            throw new ArgumentException("请选择 CLI 的 .exe 文件，不能使用批处理脚本。");

        _token = request.Bridge.BearerToken;
        var start = new ProcessStartInfo(request.Executable)
        {
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["T3_VOICE_BRIDGE_TOKEN"] = _token;
        start.Environment.Remove("CLAUDECODE");
        _process = new Process { StartInfo = start };
        try
        {
            if (!_process.Start()) throw new InvalidOperationException("无法启动本机编排 CLI。");
            Id = _process.Id;
        }
        catch
        {
            _process.Dispose();
            _readCancellation.Dispose();
            throw;
        }
        _stderrReader = ReadErrorsAsync();
        _stopRegistration = cancellationToken.UnsafeRegister(static state => ((ProviderProcess)state!).StopOwnedProcess(), this);
    }

    private void StopOwnedProcess()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private async Task ReadErrorsAsync()
    {
        var buffer = new char[1024];
        try
        {
            int count;
            while ((count = await _process.StandardError.ReadAsync(buffer.AsMemory(), _readCancellation.Token)) > 0)
            {
                lock (_stderr)
                {
                    _stderr.Append(buffer, 0, count);
                    if (_stderr.Length > 8192) _stderr.Remove(0, _stderr.Length - 8192);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public string SafeError(string message)
    {
        var combined = string.IsNullOrWhiteSpace(ErrorOutput) ? message : $"{message}\n{ErrorOutput}";
        return Redact(combined, _token);
    }

    internal static string Redact(string message, string token)
    {
        if (!string.IsNullOrEmpty(token)) message = message.Replace(token, "[REDACTED]", StringComparison.Ordinal);
        message = Regex.Replace(message, @"(?i)(bearer\s+)[A-Za-z0-9._~+/=-]+", "$1[REDACTED]");
        return message.Length > 8192 ? message[..8192] : message;
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken) => _process.WaitForExitAsync(cancellationToken);
    public int ExitCode => _process.ExitCode;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            try { _process.StandardInput.Close(); } catch (IOException) { }
            if (!_process.HasExited)
            {
                try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException)
                {
                    if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync();
                }
            }
        }
        catch (InvalidOperationException) { }
        finally
        {
            await _readCancellation.CancelAsync();
            _process.StandardOutput.Dispose();
            _process.StandardError.Dispose();
            await _stderrReader;
            _stopRegistration.Dispose();
            _process.Dispose();
            _readCancellation.Dispose();
        }
    }
}

internal sealed class ProviderRpcSession : IAsyncDisposable
{
    public ProviderProcess Process { get; }
    public JsonLineRpc Rpc { get; }

    public ProviderRpcSession(ProviderRequest request, CancellationToken cancellationToken)
    {
        Process = new ProviderProcess(request, CodexInvocation.Arguments(), cancellationToken);
        Rpc = new JsonLineRpc(Process.Output, Process.Input, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        // Closing the child first releases redirected pipe reads that ignore cancellation on Windows.
        try { await Process.DisposeAsync(); }
        finally { await Rpc.DisposeAsync(); }
    }
}

internal static class ProviderLines
{
    internal static async IAsyncEnumerable<string> ReadAsync(TextReader reader, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            for (var index = 0; index < count; index++)
            {
                var character = buffer[index];
                if (character == '\n')
                {
                    if (line.Length > 0 && line[^1] == '\r') line.Length--;
                    if (line.Length > 0) yield return line.ToString();
                    line.Clear();
                }
                else
                {
                    if (line.Length >= 4 * 1024 * 1024) throw new InvalidDataException("CLI 返回了超过 4 MiB 的单条消息。");
                    line.Append(character);
                }
            }
        }
        if (line.Length > 0) yield return line.ToString();
    }
}
