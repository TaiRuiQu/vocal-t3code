using System;
using System.Linq;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace T3.Voice;

public sealed class HistoryWindow : Window
{
    private readonly VoiceStore store;
    private readonly Coordinator coordinator;
    private readonly TextBlock activity = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock resourceUsage = Theme.Label("");
    private readonly StackPanel tasks = new() { Spacing = 12, Margin = new Thickness(0, 16, 0, 16) };
    private readonly StackPanel messages = new() { Spacing = 12, Margin = new Thickness(0, 16, 0, 16) };
    private readonly InfoBar questions = new() { IsClosable = false, Severity = InfoBarSeverity.Warning };
    private readonly InfoBar error = new() { IsClosable = true, Severity = InfoBarSeverity.Error };
    private readonly TextBox input = new()
    {
        PlaceholderText = "也可以在这里输入任务或回答…",
        MinHeight = 64,
        MaxHeight = 120,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
    };
    private int refreshQueued;
    private bool closed;

    public HistoryWindow(VoiceStore store, Coordinator coordinator)
    {
        this.store = store;
        this.coordinator = coordinator;
        var root = new Grid { RowSpacing = 16, Padding = new Thickness(28, 24, 28, 24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel { Spacing = 10 };
        header.Children.Add(Theme.Heading("任务与对话", true));
        header.Children.Add(activity);
        header.Children.Add(resourceUsage);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(Theme.Button("刷新", (_, _) => Refresh()));
        actions.Children.Add(Theme.Button("重试待处理任务", (_, _) => coordinator.Wake()));
        header.Children.Add(actions);
        header.Children.Add(questions);
        root.Children.Add(header);

        var tabs = new TabView { IsAddTabButtonVisible = false, TabWidthMode = TabViewWidthMode.Equal };
        tabs.TabItems.Add(new TabViewItem
        {
            Header = "云端任务",
            IsClosable = false,
            Content = new ScrollViewer { Content = tasks, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
        });
        tabs.TabItems.Add(new TabViewItem
        {
            Header = "对话记录 · 最新优先",
            IsClosable = false,
            Content = new ScrollViewer { Content = messages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
        });
        Grid.SetRow(tabs, 1);
        root.Children.Add(tabs);

        var composer = new StackPanel { Spacing = 10 };
        composer.Children.Add(error);
        var composeRow = new Grid { ColumnSpacing = 10 };
        composeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        composeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        composeRow.Children.Add(input);
        var send = Theme.Button("发送文字", (_, _) => Send(), accent: true);
        send.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(send, 1);
        composeRow.Children.Add(send);
        composer.Children.Add(composeRow);
        Grid.SetRow(composer, 2);
        root.Children.Add(composer);
        Content = root;
        Theme.Apply(this, store.LoadSettings().Theme);
        WindowHost.Configure(this, "T3 Voice · 任务与对话", 900, 800);
        coordinator.Changed += QueueRefresh;
        Closed += (_, _) => { closed = true; coordinator.Changed -= QueueRefresh; };
        Refresh();
    }

    public void Show() => WindowHost.Activate(this);

    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref refreshQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref refreshQueued, 0);
            if (!closed) Refresh();
        })) Interlocked.Exchange(ref refreshQueued, 0);
    }

    private void Send()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(input.Text)) return;
            coordinator.Submit(input.Text);
            input.Text = "";
            error.IsOpen = false;
            Refresh();
        }
        catch (Exception exception)
        {
            error.Message = exception.Message;
            error.IsOpen = true;
        }
    }

    private void Refresh()
    {
        var resources = coordinator.Resources.Read();
        activity.Text = coordinator.Busy ? "正在编排任务" : "编排空闲 · CLI 已释放";
        resourceUsage.Text = $"本机助手与编排进程 {resources.Megabytes:F1} MiB · {resources.ProcessCount} 个进程"
            + (resources.OverBudget ? " · 超过 500 MiB 目标" : "");
        var pending = store.List<string>("question:");
        questions.IsOpen = pending.Count > 0;
        questions.Title = "等待你的回答";
        questions.Message = string.Join("\n", pending.Select(question => question.Value));

        var settings = store.LoadSettings();
        tasks.Children.Clear();
        var recentTasks = store.Tasks().Take(60).ToArray();
        if (recentTasks.Length == 0) tasks.Children.Add(Theme.Label("还没有云端任务。通过语音或下方文字输入开始。"));
        foreach (var task in recentTasks)
        {
            var row = new StackPanel { Spacing = 8 };
            var title = new Grid { ColumnSpacing = 16 };
            title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            title.Children.Add(Theme.Heading(task.Title));
            var state = Theme.Label(StatusName(task.Status));
            state.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(state, 1);
            title.Children.Add(state);
            row.Children.Add(title);
            row.Children.Add(Theme.Label(settings.Machines.FirstOrDefault(machine => machine.Id == task.MachineId)?.Name ?? task.MachineId));
            var reference = Theme.Label("对话 " + task.ThreadId);
            reference.IsTextSelectionEnabled = true;
            row.Children.Add(reference);
            tasks.Children.Add(Theme.Card(row));
        }

        messages.Children.Clear();
        var history = store.RecentMessages(100);
        if (history.Count == 0) messages.Children.Add(Theme.Label("对话记录会保存在这台电脑上。"));
        foreach (var message in history.Reverse())
        {
            var row = new StackPanel { Spacing = 8 };
            row.Children.Add(Theme.Label(RoleName(message.Role) + " · " + message.CreatedAt));
            row.Children.Add(new TextBlock { Text = message.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            messages.Children.Add(Theme.Card(row));
        }
    }

    private static string RoleName(string role) => role switch
    {
        "user" => "我",
        "assistant" => "编排 agent",
        "error" => "错误",
        _ => role,
    };

    private static string StatusName(string status) => status switch
    {
        "running" => "执行中",
        "queued" => "排队中",
        "waiting" => "等待输入",
        "completed" => "已完成",
        "failed" => "失败",
        "cancelled" => "已取消",
        "interrupted" => "已中断",
        "offline" => "离线，等待重连",
        "detached" => "已停止跟踪",
        "rolled_back" => "已回退",
        _ => status,
    };
}
