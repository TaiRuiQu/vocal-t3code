using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using System.Runtime.CompilerServices;

namespace T3.Voice;

internal static class Theme
{
    private static Style? rootPanelStyle;
    private static Style? rootBorderStyle;
    private static Style? secondaryTextStyle;
    private static Style? cardStyle;
    private static readonly ConditionalWeakTable<Window, object> captionBindings = new();

    public static Style SecondaryTextStyle => secondaryTextStyle ??= LoadStyle("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="TextBlock">
            <Setter Property="Foreground" Value="{ThemeResource TextFillColorSecondaryBrush}" />
        </Style>
        """);

    public static void Apply(Window window, string theme = "system")
    {
        if (window.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
            captionBindings.GetValue(window, owner =>
            {
                root.ActualThemeChanged += (_, _) => WindowHost.SetDarkCaption(owner, root.ActualTheme == ElementTheme.Dark);
                return new object();
            });
            WindowHost.SetDarkCaption(window, root.ActualTheme == ElementTheme.Dark);
        }
        if (window.Content is Panel panel)
            panel.Style = rootPanelStyle ??= LoadStyle("""
                <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Panel">
                    <Setter Property="Background" Value="{ThemeResource SolidBackgroundFillColorBaseBrush}" />
                </Style>
                """);
        else if (window.Content is Border border && border.Style is null)
            border.Style = rootBorderStyle ??= LoadStyle("""
                <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Border">
                    <Setter Property="Background" Value="{ThemeResource SolidBackgroundFillColorBaseBrush}" />
                </Style>
                """);
    }

    public static TextBlock Heading(string text, bool large = false)
    {
        var heading = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (FindStyle(large ? "TitleTextBlockStyle" : "SubtitleTextBlockStyle") is { } style) heading.Style = style;
        return heading;
    }

    public static TextBlock Label(string text) => new()
    {
        Text = text,
        Style = SecondaryTextStyle,
        TextWrapping = TextWrapping.Wrap,
    };

    public static Button Button(string text, RoutedEventHandler action, bool accent = false)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left };
        if (accent && FindStyle("AccentButtonStyle") is { } style) button.Style = style;
        button.Click += action;
        return button;
    }

    public static Border Card(UIElement content) => new()
    {
        Child = content,
        Style = cardStyle ??= LoadStyle("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Border">
                <Setter Property="Background" Value="{ThemeResource CardBackgroundFillColorDefaultBrush}" />
                <Setter Property="BorderBrush" Value="{ThemeResource CardStrokeColorDefaultBrush}" />
            </Style>
            """),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(20),
    };

    public static StackPanel Section(Panel parent, string title, string description)
    {
        var section = new StackPanel { Spacing = 14 };
        section.Children.Add(Heading(title));
        if (description.Length > 0) section.Children.Add(Label(description));
        parent.Children.Add(Card(section));
        return section;
    }

    private static Style LoadStyle(string xaml) => (Style)XamlReader.Load(xaml);

    private static Style? FindStyle(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var value) == true ? value as Style : null;
}
