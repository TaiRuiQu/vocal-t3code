using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Forms = System.Windows.Forms;

namespace T3.Voice;

internal static class WindowHost
{
    private const uint DwmBorderColor = 34;
    private const uint DwmColorNone = 0xFFFFFFFE;
    private sealed class Dimensions(int width, int height) { public int Width = width; public int Height = height; }
    private static readonly ConditionalWeakTable<Window, Dimensions> Sizes = new();
    public static IntPtr Handle(Window window) => WinRT.Interop.WindowNative.GetWindowHandle(window);
    private static AppWindow NativeWindow(Window window) => AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(Handle(window)));

    public static void Configure(Window window, string title, int width, int height, bool popup = false)
    {
        window.Title = title;
        var native = NativeWindow(window);
        native.SetIcon(AppIcon.FilePath);
        if (native.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = !popup;
            presenter.IsMaximizable = !popup;
            if (popup)
            {
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
                presenter.SetBorderAndTitleBar(false, false);
            }
        }
        Resize(window, width, height);
        if (!popup)
        {
            var area = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
            var size = native.Size;
            native.Resize(new SizeInt32(Math.Min(size.Width, Math.Max(320, area.Width - 48)), Math.Min(size.Height, Math.Max(360, area.Height - 48))));
            native.Move(new PointInt32(area.Left + (area.Width - native.Size.Width) / 2, area.Top + (area.Height - native.Size.Height) / 2));
            return;
        }
        var handle = Handle(window);
        var style = GetWindowLongPtr(handle, -20).ToInt64();
        SetWindowLongPtr(handle, -20, new IntPtr((style | 0x08000000 | 0x00000080 | 0x00000020) & ~0x00040000L));
        var border = DwmColorNone;
        DwmSetWindowAttribute(handle, DwmBorderColor, ref border, sizeof(uint));
        var corners = 2u;
        DwmSetWindowAttribute(handle, 33, ref corners, sizeof(uint));
        SetWindowSubclass(handle, PopupProcedure, 1, 0);
    }

    public static void Resize(Window window, int width, int height)
    {
        var dimensions = Sizes.GetValue(window, _ => new Dimensions(width, height));
        dimensions.Width = width; dimensions.Height = height;
        var dpi = GetDpiForWindow(Handle(window));
        var scale = dpi == 0 ? 1 : dpi / 96d;
        NativeWindow(window).Resize(new SizeInt32((int)Math.Round(width * scale), (int)Math.Round(height * scale)));
    }
    public static void Activate(Window window) => window.Activate();
    public static void SetDarkCaption(Window window, bool dark)
    {
        var value = dark ? 1u : 0u;
        DwmSetWindowAttribute(Handle(window), 20, ref value, sizeof(uint));
    }
    public static void ShowInactive(Window window)
    {
        NativeWindow(window).Show(false);
        var handle = Handle(window);
        ShowWindow(handle, 4);
        SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    }
    public static void Hide(Window window) => NativeWindow(window).Hide();

    public static void PositionCorner(Window window, string position)
    {
        var area = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
        var monitor = MonitorFromPoint(new NativePoint { X = area.Left + 1, Y = area.Top + 1 }, 2);
        var dpi = GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 ? dpiX : 96u;
        var margin = (int)Math.Round(20 * dpi / 96d);
        var native = NativeWindow(window);
        if (Sizes.TryGetValue(window, out var dimensions)) native.Resize(new SizeInt32((int)Math.Round(dimensions.Width * dpi / 96d), (int)Math.Round(dimensions.Height * dpi / 96d)));
        var width = native.Size.Width;
        var x = position == "left" ? area.Left + margin : area.Right - width - margin;
        native.Move(new PointInt32(x, area.Top + margin));
    }

    private static readonly SubclassProcedure PopupProcedure = OnPopupMessage;
    private static IntPtr OnPopupMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (message == 0x21) return new IntPtr(3);
        if (message == 0x84) return new IntPtr(-1);
        if (message == 0x82) RemoveWindowSubclass(window, PopupProcedure, id);
        return DefSubclassProc(window, message, wParam, lParam);
    }
    private delegate IntPtr SubclassProcedure(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("shcore")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("comctl32")] private static extern bool SetWindowSubclass(IntPtr window, SubclassProcedure callback, UIntPtr id, UIntPtr data);
    [DllImport("comctl32")] private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProcedure callback, UIntPtr id);
    [DllImport("comctl32")] private static extern IntPtr DefSubclassProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi")] private static extern int DwmSetWindowAttribute(IntPtr window, uint attribute, ref uint value, int size);
}
