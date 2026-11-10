using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Microsoft.UI.Xaml.Markup;

namespace T3.Voice;

public sealed class PopupWindow : Window
{
    private readonly TextBlock heading;
    private readonly TextBlock body;
    private readonly TextBlock footer;
    private readonly Border card;
    private readonly DispatcherQueueTimer dismiss;
    private readonly Func<AppSettings> settings;
    private bool closed;
    public bool Recording { get; private set; }
    public bool Pinned { get; private set; }

    public PopupWindow(Func<AppSettings> settings)
    {
        this.settings = settings;
        WindowHost.Configure(this, "T3 Voice 提示", 380, 168, popup: true);
        dismiss = DispatcherQueue.CreateTimer();
        dismiss.IsRepeating = false;
        dismiss.Interval = TimeSpan.FromSeconds(14);
        card = (Border)XamlReader.Load("<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Background=\"{ThemeResource SolidBackgroundFillColorBaseBrush}\" Padding=\"20,16\" />");
        var panel = new StackPanel();
        heading = (TextBlock)XamlReader.Load("<TextBlock xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Style=\"{ThemeResource BodyStrongTextBlockStyle}\" TextWrapping=\"Wrap\" />");
        body = new TextBlock
        {
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 12),
            MaxHeight = 300,
            MaxLines = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        footer = (TextBlock)XamlReader.Load("<TextBlock xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Style=\"{ThemeResource CaptionTextBlockStyle}\" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\" TextWrapping=\"Wrap\" />");
        panel.Children.Add(heading);
        panel.Children.Add(body);
        panel.Children.Add(footer);
        card.Child = panel;
        Content = card;
        ApplyTheme();
        dismiss.Tick += (_, _) => Dismiss();
        card.Loaded += (_, _) => Place();
        Closed += (_, _) => { closed = true; dismiss.Stop(); };
    }

    public void Present(string title, string text, bool pinned = false, bool recording = false)
    {
        if (closed) return;
        dismiss.Stop();
        Recording = recording;
        Pinned = pinned;
        heading.Text = title;
        body.Text = text.Length == 0 ? "请说出你的任务…" : text;
        footer.Text = recording ? "双击右 Shift 发送 · Esc 取消" : pinned ? "双击右 Shift 回答 · 托盘可查看完整记录" : "双击右 Shift 继续 · 完整结果已保存";
        Place();
        WindowHost.ShowInactive(this);
        if (!pinned && !recording) dismiss.Start();
    }

    public void Place()
    {
        if (closed) return;
        ApplyTheme();
        card.Measure(new Size(380, double.PositiveInfinity));
        WindowHost.Resize(this, 380, (int)Math.Ceiling(Math.Clamp(card.DesiredSize.Height, 132, 440)));
        WindowHost.PositionCorner(this, settings().PopupPosition);
    }

    public void Dismiss()
    {
        if (closed) return;
        dismiss.Stop();
        Recording = false;
        Pinned = false;
        WindowHost.Hide(this);
    }

    public void ApplyTheme() => Theme.Apply(this, settings().Theme);
}
