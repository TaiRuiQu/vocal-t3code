using System;
using System.Drawing;
using System.IO;

namespace T3.Voice;

internal static class AppIcon
{
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "Assets", "t3-voice.ico");

    public static Icon LoadTrayIcon()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("T3.Voice.Assets.t3-voice.ico")
            ?? throw new InvalidOperationException("找不到 T3 Voice 图标资源。");
        using var icon = new Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
        return (Icon)icon.Clone();
    }
}
