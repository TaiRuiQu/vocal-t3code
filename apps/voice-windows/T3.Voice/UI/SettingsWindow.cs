using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using T3.Voice.Native;

namespace T3.Voice;

public sealed class SettingsWindow : Window
{
    private readonly VoiceStore store;
    private readonly Action saved;
    private readonly Func<bool> recording;
    private readonly AppSettings original;
    private readonly List<Machine> machines;
    private readonly TextBox codex = new();
    private readonly TextBox claude = new();
    private readonly TextBox codexModel = new();
    private readonly TextBox claudeModel = new();
    private readonly ComboBox provider = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox position = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox theme = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox microphone = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox autoStart = new() { Content = "登录 Windows 后自动启动" };
    private readonly PasswordBox key = new();
    private readonly TextBox endpoint = new();
    private readonly TextBox asrModel = new();
    private readonly TextBox workspace = new();
    private readonly StackPanel machineList = new() { Spacing = 16 };
    private readonly InfoBar status = new() { IsOpen = false, IsClosable = false };
    private readonly Button save;
    private readonly CancellationTokenSource closed = new();
    private readonly CancellationToken lifetime;
    private int authorizing;

    public SettingsWindow(VoiceStore store, Action saved, Func<bool> recording)
    {
        this.store = store;
        this.saved = saved;
        this.recording = recording;
        lifetime = closed.Token;
        original = store.LoadSettings();
        machines = original.Machines.ToList();

        var root = new Grid { RowSpacing = 16 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel { Spacing = 8, Margin = new Thickness(28, 24, 28, 0) };
        header.Children.Add(Theme.Heading("语音编排助手", true));
        header.Children.Add(Theme.Label("双击右 Shift 开始 / 发送，Esc 取消。云端执行，本机编排。"));
        root.Children.Add(header);

        var sections = new StackPanel { Spacing = 20, Margin = new Thickness(28, 0, 28, 4) };
        var orchestration = Theme.Section(sections, "本机编排", "使用已登录的本机 CLI，空闲时释放编排进程。");
        provider.ItemsSource = new[] { "Codex", "Claude Code" };
        provider.SelectedIndex = original.Provider == "claude" ? 1 : 0;
        Add(orchestration, "编排提供商", provider);
        codex.Text = original.CodexPath;
        Add(orchestration, "Codex 可执行文件", codex);
        codexModel.Text = original.CodexModel;
        Add(orchestration, "Codex 模型（留空使用 CLI 默认模型）", codexModel);
        claude.Text = original.ClaudePath;
        Add(orchestration, "Claude Code 可执行文件", claude);
        claudeModel.Text = original.ClaudeModel;
        Add(orchestration, "Claude 模型（留空使用 CLI 默认模型）", claudeModel);

        var voice = Theme.Section(sections, "阿里云流式语音识别", "只在触发录音后发送音频。密钥使用 Windows 当前用户加密保存。");
        Add(voice, "DashScope API Key（留空保留已保存的密钥）", key);
        endpoint.Text = original.AsrEndpoint;
        Add(voice, "WebSocket 地址（需要与 API Key 的地域一致）", endpoint);
        asrModel.Text = original.AsrModel;
        Add(voice, "ASR 模型", asrModel);
        workspace.Text = original.AsrWorkspace;
        Add(voice, "业务空间 ID（可留空）", workspace);
        var devices = new List<MicrophoneDevice> { new(-1, "系统默认麦克风") };
        try { devices = WaveMicrophone.ListDevices().ToList(); }
        catch (Exception error) { SetStatus("麦克风枚举失败：" + error.Message, InfoBarSeverity.Warning); }
        foreach (var device in devices)
        {
            var item = new ComboBoxItem { Content = device.Name, Tag = device.Id };
            microphone.Items.Add(item);
            if (device.Id == original.MicrophoneId) microphone.SelectedItem = item;
        }
        if (microphone.SelectedIndex < 0) microphone.SelectedIndex = 0;
        Add(voice, "麦克风", microphone);

        var cloud = Theme.Section(sections, "云电脑", "在云电脑启动 T3 Code 并启用 T3 Connect，填写 HTTPS 地址；浏览器授权时选择完整权限。");
        cloud.Children.Add(machineList);
        RebuildMachines();
        var name = new TextBox { Text = "云电脑", PlaceholderText = "机器昵称" };
        var url = new TextBox { PlaceholderText = "https://你的云电脑地址" };
        Add(cloud, "新机器昵称", name);
        Add(cloud, "T3 地址", url);
        cloud.Children.Add(Theme.Button("连接云电脑", async (_, _) => await ConnectAsync(name, url)));

        var appearance = Theme.Section(sections, "提示与启动", "提示跟随触发时鼠标所在的显示器。");
        theme.ItemsSource = new[] { "跟随系统", "浅色", "深色" };
        theme.SelectedIndex = original.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        theme.SelectionChanged += (_, _) => Theme.Apply(this, SelectedTheme());
        Add(appearance, "外观", theme);
        position.ItemsSource = new[] { "右上角", "左上角" };
        position.SelectedIndex = original.PopupPosition == "left" ? 1 : 0;
        Add(appearance, "提示位置", position);
        autoStart.IsChecked = original.AutoStart;
        appearance.Children.Add(autoStart);

        var scroll = new ScrollViewer { Content = sections, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        var footer = new StackPanel { Spacing = 12, Margin = new Thickness(28, 0, 28, 24) };
        footer.Children.Add(status);
        save = Theme.Button("保存设置", (_, _) => Save(), accent: true);
        footer.Children.Add(save);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
        Theme.Apply(this, original.Theme);
        WindowHost.Configure(this, "T3 Voice · 设置", 800, 900);
        Closed += (_, _) => { closed.Cancel(); closed.Dispose(); };
    }

    public void Show() => WindowHost.Activate(this);

    private static void Add(Panel panel, string label, UIElement control)
    {
        var field = new StackPanel { Spacing = 6 };
        field.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        field.Children.Add(control);
        panel.Children.Add(field);
    }

    private void RebuildMachines()
    {
        machineList.Children.Clear();
        if (machines.Count == 0) machineList.Children.Add(Theme.Label("尚未连接云电脑。"));
        foreach (var machine in machines.ToArray())
        {
            var row = new StackPanel { Spacing = 8 };
            row.Children.Add(Theme.Heading(machine.Name));
            var address = Theme.Label(machine.Url);
            address.IsTextSelectionEnabled = true;
            row.Children.Add(address);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(Theme.Button("重新授权", async (_, _) => await ReauthorizeAsync(machine)));
            actions.Children.Add(Theme.Button("移除此连接", (_, _) => { machines.Remove(machine); RebuildMachines(); }));
            row.Children.Add(actions);
            machineList.Children.Add(row);
        }
    }

    private bool BeginAuthorization()
    {
        if (lifetime.IsCancellationRequested) return false;
        authorizing++;
        save.IsEnabled = false;
        return true;
    }

    private void EndAuthorization()
    {
        authorizing--;
        if (!lifetime.IsCancellationRequested) save.IsEnabled = authorizing == 0;
    }

    private async Task ConnectAsync(TextBox name, TextBox url)
    {
        if (!BeginAuthorization()) return;
        try
        {
            var uri = ValidateRemoteUrl(url.Text);
            SetStatus("请在浏览器完成授权…");
            using var oauth = new McpOAuth();
            var credential = await oauth.AuthorizeAsync(uri, OpenBrowser, lifetime);
            lifetime.ThrowIfCancellationRequested();
            var machine = new Machine { Name = string.IsNullOrWhiteSpace(name.Text) ? uri.Host : name.Text.Trim(), Url = uri.GetLeftPart(UriPartial.Authority) };
            store.SetSecret("machine:" + machine.Id, credential.AccessToken);
            store.Put("expiry:" + machine.Id, credential.ExpiresAt);
            machines.Add(machine);
            RebuildMachines();
            SetStatus("已授权。保存设置后开始连接。", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { SetStatus("授权已取消"); }
        catch (Exception error) { SetStatus("连接失败：" + error.Message, InfoBarSeverity.Error); }
        finally { EndAuthorization(); }
    }

    private async Task ReauthorizeAsync(Machine machine)
    {
        if (!BeginAuthorization()) return;
        try
        {
            SetStatus("请在浏览器完成授权…");
            using var oauth = new McpOAuth();
            var credential = await oauth.AuthorizeAsync(new Uri(machine.Url), OpenBrowser, lifetime);
            lifetime.ThrowIfCancellationRequested();
            store.SetSecret("machine:" + machine.Id, credential.AccessToken);
            store.Put("expiry:" + machine.Id, credential.ExpiresAt);
            SetStatus("授权已更新", InfoBarSeverity.Success);
            saved();
        }
        catch (OperationCanceledException) { SetStatus("授权已取消"); }
        catch (Exception error) { SetStatus("授权失败：" + error.Message, InfoBarSeverity.Error); }
        finally { EndAuthorization(); }
    }

    private void Save()
    {
        try
        {
            if (authorizing > 0) throw new InvalidOperationException("请先完成或取消浏览器中的授权，再保存设置");
            if (recording()) throw new InvalidOperationException("请先结束或取消当前录音，再保存设置");
            if (!Uri.TryCreate(endpoint.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "wss") throw new InvalidOperationException("ASR 地址必须是有效的 wss:// 地址");
            if (string.IsNullOrWhiteSpace(asrModel.Text)) throw new InvalidOperationException("请输入 ASR 模型");
            var settings = original with
            {
                Provider = provider.SelectedIndex == 1 ? "claude" : "codex",
                CodexPath = codex.Text.Trim(),
                ClaudePath = claude.Text.Trim(),
                CodexModel = codexModel.Text.Trim(),
                ClaudeModel = claudeModel.Text.Trim(),
                PopupPosition = position.SelectedIndex == 1 ? "left" : "right",
                Theme = SelectedTheme(),
                AutoStart = autoStart.IsChecked == true,
                AsrEndpoint = endpoint.Text.Trim(),
                AsrModel = asrModel.Text.Trim(),
                AsrWorkspace = workspace.Text.Trim(),
                MicrophoneId = (microphone.SelectedItem as ComboBoxItem)?.Tag is int id ? id : -1,
                Machines = machines.ToList(),
            };
            if (settings.CodexPath.Length == 0 || settings.ClaudePath.Length == 0) throw new InvalidOperationException("请输入 CLI 路径或命令名");
            if (key.Password.Length > 0) store.SetSecret("aliyun", key.Password.Trim());
            store.SaveSettings(settings);
            foreach (var removed in original.Machines.Where(machine => !machines.Any(current => current.Id == machine.Id)))
            {
                store.SetSecret("machine:" + removed.Id, null);
                store.Delete("expiry:" + removed.Id);
                foreach (var task in store.Tasks().Where(task => task.MachineId == removed.Id && VoiceTools.IsActive(task)))
                    store.SaveTask(task with { Status = "detached", UpdatedAt = DateTimeOffset.UtcNow.ToString("O") });
                foreach (var call in store.List<SavedCall>("call:").Where(call => call.Value.MachineId == removed.Id && call.Value.State == "queued"))
                    store.Put(call.Key, call.Value with { State = "cancelled" });
            }
            saved();
            Close();
        }
        catch (Exception error) { SetStatus(error.Message, InfoBarSeverity.Error); }
    }

    private void SetStatus(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        if (lifetime.IsCancellationRequested) return;
        status.Message = message;
        status.Severity = severity;
        status.IsOpen = true;
    }

    public static Uri ValidateRemoteUrl(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
            throw new InvalidOperationException("请输入 HTTPS T3 地址，本机测试也允许 HTTP localhost");
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0) throw new InvalidOperationException("请输入机器地址，不要填写配对令牌链接");
        return uri;
    }

    private string SelectedTheme() => theme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };

    private static void OpenBrowser(Uri uri) => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
}
