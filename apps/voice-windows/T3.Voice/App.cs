using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.XamlTypeInfo;
using Microsoft.Win32;
using T3.Voice.Native;
using T3.Voice.Speech;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Forms = System.Windows.Forms;

namespace T3.Voice;

public sealed class App : Application, IXamlMetadataProvider
{
    private XamlControlsXamlMetaDataProvider? metadata;
    private XamlControlsXamlMetaDataProvider Metadata => metadata ??= new XamlControlsXamlMetaDataProvider();
    public IXamlType GetXamlType(Type type) => Metadata.GetXamlType(type);
    public IXamlType GetXamlType(string fullName) => Metadata.GetXamlType(fullName);
    public XmlnsDefinition[] GetXmlnsDefinitions() => Metadata.GetXmlnsDefinitions();
    private VoiceStore store = null!;
    private RemoteMcpClient remote = null!;
    private LocalMcpServer bridge = null!;
    private Coordinator coordinator = null!;
    private GlobalKeyboard keyboard = null!;
    private PopupWindow popup = null!;
    private Forms.NotifyIcon tray = null!;
    private System.Drawing.Icon? trayIcon;
    private SettingsWindow? settingsWindow;
    private HistoryWindow? historyWindow;
    private RecordingSession? recording;
    private bool transitioning;
    private bool exiting;
    private string latestTranscript = "";
    private string displayedTranscript = "";
    private readonly List<(string Title, string Text, bool Pinned)> deferredNotices = [];
    private readonly DispatcherQueue ui;
    private readonly DispatcherQueueTimer transcriptTimer;
    private readonly bool demo;
    private readonly bool compactDemo;
    private readonly string? customDataDirectory;
    private readonly string? readyFile;
    private readonly string? previewTheme;
    private readonly string? previewOutput;

    private App(string[] arguments)
    {
        StartupDiagnostics.Stage("App constructor");
        demo = arguments.Contains("--demo");
        compactDemo = arguments.Contains("--demo-compact");
        var homeIndex = Array.IndexOf(arguments, "--data-dir");
        if (homeIndex >= 0 && homeIndex + 1 < arguments.Length) customDataDirectory = arguments[homeIndex + 1];
        var readyIndex = Array.IndexOf(arguments, "--ready-file");
        if (readyIndex >= 0 && readyIndex + 1 < arguments.Length) readyFile = arguments[readyIndex + 1];
        var themeIndex = Array.IndexOf(arguments, "--theme");
        if (demo && themeIndex >= 0 && themeIndex + 1 < arguments.Length) previewTheme = arguments[themeIndex + 1];
        var previewIndex = Array.IndexOf(arguments, "--preview-output");
        if (demo && previewIndex >= 0 && previewIndex + 1 < arguments.Length) previewOutput = arguments[previewIndex + 1];
        UnhandledException += (_, e) =>
        {
            StartupDiagnostics.Error("WinUI unhandled exception", e.Exception);
            e.Handled = true;
            if (demo && readyFile is not null && popup is null) Exit();
            else Notify("出现错误", e.Exception.Message, true);
        };
        ui = DispatcherQueue.GetForCurrentThread();
        transcriptTimer = ui.CreateTimer();
        transcriptTimer.Interval = TimeSpan.FromMilliseconds(80);
        transcriptTimer.IsRepeating = false;
        StartupDiagnostics.Stage("App constructor completed");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void StartDesktop(string[] arguments)
    {
        Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
        StartupDiagnostics.Stage("Initializing WinRT COM wrappers");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        StartupDiagnostics.Stage("Starting WinUI Application");
        Application.Start(initialization =>
        {
            StartupDiagnostics.Stage("WinUI Application callback");
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App(arguments);
        });
        StartupDiagnostics.Stage("WinUI Application exited");
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs e)
    {
        base.OnLaunched(e);
        StartupDiagnostics.Stage("OnLaunched");
        try
        {
            StartupDiagnostics.Stage("Loading WinUI control resources");
            Resources.MergedDictionaries.Add(new XamlControlsResources());
            var dataDirectory = customDataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "T3Voice");
            store = new VoiceStore(dataDirectory);
            if (demo && previewTheme is "light" or "dark" or "system") store.SaveSettings(store.LoadSettings() with { Theme = previewTheme });
            remote = new RemoteMcpClient();
            var tools = new VoiceTools(store, remote);
            bridge = new LocalMcpServer(tools);
            await bridge.StartAsync(CancellationToken.None);
            coordinator = new Coordinator(store, tools, remote, bridge, provider => provider == "claude" ? new ClaudeDriver() : new CodexDriver());
            popup = new PopupWindow(store.LoadSettings);
            coordinator.Notice += Notify;
            coordinator.Changed += () => ui.TryEnqueue(() => { if (tray is not null) tray.Text = coordinator.Busy ? "T3 Voice · 编排中" : "T3 Voice · 双击右 Shift"; });
            CreateTray();
            keyboard = new GlobalKeyboard();
            keyboard.ToggleRecording += () => RunRecordingAction(ToggleRecordingAsync);
            keyboard.CancelRecording += () => RunRecordingAction(CancelRecordingAsync);
            keyboard.Start();
            transcriptTimer.Tick += (_, _) =>
            {
                transcriptTimer.Stop();
                if (recording is null || transitioning || latestTranscript == displayedTranscript) return;
                displayedTranscript = latestTranscript;
                popup.Present("正在听", displayedTranscript, true, true);
            };
            if (demo)
            {
                if (!compactDemo) OpenSettings();
                popup.Present("界面预览 · 正在听", "在 Ubuntu 云电脑上继续科技树项目，先查看 Git 仓库里的最新进度。", true, true);
                await WaitForContentAsync(popup);
                if (settingsWindow is not null) await WaitForContentAsync(settingsWindow);
                if (previewOutput is not null) await RenderPreviewAsync(popup.Content, previewOutput);
            }
            else
            {
                await coordinator.StartAsync();
                UpdateAutoStart();
                if (store.LoadSettings().Machines.Count == 0 || store.GetSecret("aliyun") is null) OpenSettings();
                else
                {
                    var question = store.List<string>("question:").FirstOrDefault();
                    Notify(question.Value is null ? "已就绪" : "需要你回答", question.Value ?? "双击右 Shift，说出任务。", question.Value is not null);
                }
            }
            if (readyFile is not null)
            {
                var path = Path.GetFullPath(readyFile);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + "." + Environment.ProcessId + ".tmp";
                File.WriteAllText(temporary, "WinUI 3 ready");
                File.Move(temporary, path, true);
            }
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Error("OnLaunched", exception);
            if (!(demo && readyFile is not null)) Forms.MessageBox.Show("启动失败：" + exception.Message, "T3 Voice");
            await ExitAsync();
        }
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("任务与对话", null, (_, _) => ui.TryEnqueue(OpenHistory));
        menu.Items.Add("设置", null, (_, _) => ui.TryEnqueue(OpenSettings));
        menu.Items.Add("开始 / 发送录音", null, (_, _) => ui.TryEnqueue(() => RunRecordingAction(ToggleRecordingAsync)));
        menu.Items.Add("重试待处理任务", null, (_, _) => coordinator.Wake());
        menu.Items.Add("隐藏提示", null, (_, _) => ui.TryEnqueue(() => popup.Dismiss()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ui.TryEnqueue(async () => await ExitAsync()));
        trayIcon = AppIcon.LoadTrayIcon();
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "T3 Voice · 双击右 Shift", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => ui.TryEnqueue(OpenHistory);
    }

    private static async Task WaitForContentAsync(Window window)
    {
        if (window.Content is not FrameworkElement content || content.IsLoaded) return;
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLoaded(object sender, RoutedEventArgs args) => loaded.TrySetResult();
        content.Loaded += OnLoaded;
        try { await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { content.Loaded -= OnLoaded; }
    }

    private static async Task RenderPreviewAsync(UIElement content, string destination)
    {
        StartupDiagnostics.Stage("Rendering demo popup content");
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(content);
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) throw new InvalidOperationException("预览内容尚未布局，无法导出。");
        var buffer = await bitmap.GetPixelsAsync();
        var pixels = new byte[checked((int)buffer.Length)];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
        using var encoded = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
        var dpi = 96 * (content.XamlRoot?.RasterizationScale ?? 1);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, dpi, dpi, pixels);
        await encoder.FlushAsync();
        if (encoded.Size > 8 * 1024 * 1024) throw new InvalidOperationException("预览图片超过大小限制。");
        using var input = encoded.GetInputStreamAt(0);
        using var pngReader = new DataReader(input);
        var length = checked((uint)encoded.Size);
        if (await pngReader.LoadAsync(length) != length) throw new InvalidOperationException("预览图片编码不完整。");
        var png = new byte[checked((int)length)];
        pngReader.ReadBytes(png);
        var path = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Environment.ProcessId + ".tmp";
        await File.WriteAllBytesAsync(temporary, png);
        File.Move(temporary, path, true);
        StartupDiagnostics.Stage("Demo popup content exported; native window decoration excluded");
    }

    private async Task ToggleRecordingAsync()
    {
        if (transitioning || exiting) return;
        if (recording is null) await StartRecordingAsync();
        else await FinishRecordingAsync();
    }

    private async Task StartRecordingAsync()
    {
        if (demo) { popup.Present("界面预览 · 正在听", "这是界面预览，不会发送音频或调用模型。", true, true); return; }
        transitioning = true;
        AliyunAsr? sessionAsr = null;
        WaveMicrophone? sessionMicrophone = null;
        RecordingSession current = null!;
        current = new RecordingSession(async token =>
        {
            var apiKey = store.GetSecret("aliyun") ?? throw new InvalidOperationException("请先在设置中配置阿里云 DashScope API Key");
            var settings = store.LoadSettings();
            sessionAsr = new AliyunAsr(settings, apiKey);
            sessionAsr.TranscriptChanged += text => PostRecordingCallback(() => QueueTranscript(current, text));
            sessionAsr.Failed += error => QueueRecordingFailure(current, error);
            await sessionAsr.StartAsync(token);
            token.ThrowIfCancellationRequested();
            sessionMicrophone = new WaveMicrophone();
            sessionMicrophone.Failed += error => QueueRecordingFailure(current, error);
            sessionMicrophone.Start(settings.MicrophoneId, audio => sessionAsr.TrySendAudio(audio));
        }, async token =>
        {
            if (sessionMicrophone is not null) await sessionMicrophone.StopAsync();
            token.ThrowIfCancellationRequested();
            return await sessionAsr!.FinishAsync(token);
        }, async () =>
        {
            Exception? microphoneError = null;
            try { if (sessionMicrophone is not null) await sessionMicrophone.DisposeAsync(); }
            catch (Exception error) { microphoneError = error; }
            try { if (sessionAsr is not null) await sessionAsr.DisposeAsync(); }
            catch (Exception error)
            {
                if (microphoneError is not null) throw new AggregateException(microphoneError, error);
                throw;
            }
            if (microphoneError is not null) throw microphoneError;
        });
        recording = current;
        keyboard.Recording = true;
        latestTranscript = "";
        displayedTranscript = "";
        try
        {
            popup.Present("正在连接语音服务", "连接成功后即可说话。Esc 取消。", true, true);
            await current.StartAsync();
            if (recording != current || current.Token.IsCancellationRequested) return;
            transitioning = false;
            popup.Present("正在听", latestTranscript, true, true);
            displayedTranscript = latestTranscript;
        }
        catch (Exception error) { current.Fail(error); await CompleteRecordingAsync(current); }
    }

    private async Task FinishRecordingAsync()
    {
        var current = recording;
        if (current is null || current.Token.IsCancellationRequested) return;
        transitioning = true;
        transcriptTimer.Stop();
        popup.Present("正在确认最后一句", latestTranscript, true, true);
        try
        {
            await current.FinishAsync(text =>
            {
                current.Token.ThrowIfCancellationRequested();
                coordinator.Submit(text);
            });
        }
        catch (Exception error) { current.Fail(error); }
        finally { await CompleteRecordingAsync(current); }
    }

    private async Task CancelRecordingAsync()
    {
        var current = recording;
        if (current is null) return;
        transitioning = true;
        current.Cancel();
        await CompleteRecordingAsync(current);
    }

    private async Task RecordingFailedAsync(RecordingSession current, Exception exception)
    {
        if (recording != current) return;
        transitioning = true;
        current.Fail(exception);
        await CompleteRecordingAsync(current);
    }

    private async Task CleanupRecordingAsync()
    {
        transcriptTimer.Stop();
        if (recording is { } current) await CompleteRecordingAsync(current);
    }

    private async Task CompleteRecordingAsync(RecordingSession current)
    {
        Exception? cleanupError = null;
        var ended = false;
        try { await current.DisposeAsync(); }
        catch (Exception error) { cleanupError = error; }
        finally
        {
            if (recording == current)
            {
                ended = true;
                transcriptTimer.Stop();
                recording = null;
                keyboard.Recording = false;
                transitioning = false;
            }
        }
        if (!ended || exiting) return;
        if ((current.Failure ?? cleanupError) is { } failure)
            Notify("语音识别失败，本次未发送", failure.Message, true);
        else if (current.UserCanceled)
        {
            popup.Dismiss();
            var question = store.List<string>("question:").FirstOrDefault();
            Notify(question.Value is null ? "录音已取消" : "需要你回答", question.Value ?? "本次录音没有发送。", question.Value is not null);
        }
        else if (current.Submitted) Notify("已收到", current.FinalText, false);
        else if (current.Completed) Notify("没有识别到语音", "双击右 Shift 重新录音。", false);
        FlushDeferredNotices();
    }

    private void QueueTranscript(RecordingSession current, string text)
    {
        if (recording != current || current.Token.IsCancellationRequested || text == latestTranscript) return;
        latestTranscript = text;
        if (!transitioning && !transcriptTimer.IsRunning) transcriptTimer.Start();
    }

    private void QueueRecordingFailure(RecordingSession current, Exception error)
    {
        current.Fail(error);
        PostRecordingCallback(() =>
        {
            if (recording == current) RunRecordingAction(() => RecordingFailedAsync(current, error));
        });
    }

    private void PostRecordingCallback(Action action)
    {
        if (!exiting) ui.TryEnqueue(() => action());
    }

    private void RunRecordingAction(Func<Task> action) => _ = ObserveRecordingActionAsync(action);

    private async Task ObserveRecordingActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Notify("录音操作失败", error.Message, true); }
    }

    private void Notify(string title, string text, bool pinned)
    {
        if (exiting || popup is null) return;
        ui.TryEnqueue(() =>
        {
            if (recording is not null)
            {
                if (deferredNotices.Count == 16) deferredNotices.RemoveAt(0);
                deferredNotices.Add((title, text, pinned));
                return;
            }
            var pendingQuestion = store.List<string>("question:").FirstOrDefault();
            if (pendingQuestion.Value is not null && title != "需要你回答")
                popup.Present("需要你回答", pendingQuestion.Value + "\n\n" + title + "\n" + text, true);
            else popup.Present(title, text, pinned);
        });
    }

    private void FlushDeferredNotices()
    {
        ui.TryEnqueue(() =>
        {
            if (recording is not null || deferredNotices.Count == 0) return;
            var question = store.List<string>("question:").FirstOrDefault();
            if (question.Value is not null) popup.Present("需要你回答", question.Value, true);
            else
            {
                var notices = deferredNotices.TakeLast(3).ToArray();
                popup.Present(notices.Length == 1 ? notices[0].Title : "录音期间的任务进展", string.Join("\n\n", notices.Select(notice => notice.Title + "\n" + notice.Text)), notices.Any(notice => notice.Pinned));
            }
            deferredNotices.Clear();
        });
    }

    private void OpenSettings()
    {
        if (settingsWindow is not null) { WindowHost.Activate(settingsWindow); return; }
        settingsWindow = new SettingsWindow(store, () => { if (!demo) UpdateAutoStart(); coordinator.Wake(); popup.Place(); if (historyWindow is not null) Theme.Apply(historyWindow, store.LoadSettings().Theme); }, () => recording is not null);
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        WindowHost.Activate(settingsWindow);
    }
    private void OpenHistory()
    {
        if (historyWindow is not null) { WindowHost.Activate(historyWindow); return; }
        historyWindow = new HistoryWindow(store, coordinator);
        historyWindow.Closed += (_, _) => historyWindow = null;
        WindowHost.Activate(historyWindow);
    }
    private void UpdateAutoStart()
    {
        using var run = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
        if (store.LoadSettings().AutoStart && Environment.ProcessPath is { } executable)
            run.SetValue("T3Voice", "\"" + executable + "\"" + (customDataDirectory is null ? "" : " --data-dir \"" + store.DataDirectory + "\""));
        else run.DeleteValue("T3Voice", false);
    }
    private async Task ExitAsync()
    {
        if (exiting) return;
        exiting = true;
        recording?.Cancel();
        try { await CleanupRecordingAsync(); } catch (Exception) { }
        keyboard?.Dispose();
        tray?.Dispose();
        trayIcon?.Dispose();
        if (coordinator is not null) await coordinator.DisposeAsync();
        if (bridge is not null) await bridge.DisposeAsync();
        remote?.Dispose(); store?.Dispose();
        settingsWindow?.Close(); historyWindow?.Close(); popup?.Close();
        Exit();
    }
}

public static class VoiceEntryPoint
{
    [STAThread]
    public static int Main(string[] arguments)
    {
        StartupDiagnostics.Initialize(arguments);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            StartupDiagnostics.Error("AppDomain unhandled exception", eventArgs.ExceptionObject as Exception);
        try
        {
            StartupDiagnostics.Stage("Managed entry point");
            using var single = new Mutex(true, "Local\\T3.Voice." + Environment.UserName + (arguments.Contains("--demo") ? ".demo" : ""), out var created);
            if (!created)
            {
                StartupDiagnostics.Stage("Another instance is running");
                if (!StartupDiagnostics.Automated) Forms.MessageBox.Show("T3 Voice 已在托盘中运行。", "T3 Voice");
                return 0;
            }
            App.StartDesktop(arguments);
            return 0;
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Error("Managed entry point", exception);
            if (!StartupDiagnostics.Automated) Forms.MessageBox.Show("启动失败：" + exception.Message, "T3 Voice");
            return 1;
        }
    }
}

internal static class StartupDiagnostics
{
    private static string? path;
    internal static bool Automated { get; private set; }

    internal static void Initialize(string[] arguments)
    {
        var readyIndex = Array.IndexOf(arguments, "--ready-file");
        if (readyIndex < 0 || readyIndex + 1 >= arguments.Length) return;
        Automated = arguments.Contains("--demo");
        try
        {
            path = Path.GetFullPath(arguments[readyIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }
        catch (Exception) { path = null; }
    }

    internal static void Stage(string stage) => Write(".startup.log", stage);
    internal static void Error(string stage, Exception? exception)
        => Write(".error.log", stage + Environment.NewLine + (exception?.ToString() ?? "Non-managed exception"));

    private static void Write(string suffix, string text)
    {
        if (path is null) return;
        try { File.AppendAllText(path + suffix, DateTimeOffset.UtcNow.ToString("O") + " " + text + Environment.NewLine); }
        catch (Exception) { }
    }
}
