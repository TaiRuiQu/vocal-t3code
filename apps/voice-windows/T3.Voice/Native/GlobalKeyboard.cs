using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace T3.Voice.Native;

[Flags]
public enum KeyboardGesture
{
    None = 0,
    ToggleRecording = 1,
    CancelRecording = 2,
    Suppress = 4,
}

public sealed class KeyboardGestureState
{
    public const int RightShift = 0xA1;
    public const int Escape = 0x1B;
    private readonly HashSet<int> _down = [];
    private readonly uint _doubleTapMilliseconds;
    private readonly uint _maximumTapMilliseconds;
    private uint _shiftPressedAt;
    private uint? _previousTap;
    private bool _shiftChord;
    private bool _suppressEscape;

    public KeyboardGestureState(uint doubleTapMilliseconds = 450, uint maximumTapMilliseconds = 350)
    {
        _doubleTapMilliseconds = doubleTapMilliseconds;
        _maximumTapMilliseconds = maximumTapMilliseconds;
    }

    public void SeedPressedKey(int virtualKey) => _down.Add(virtualKey);

    public KeyboardGesture Process(int virtualKey, bool pressed, uint timestamp, bool recording)
    {
        var fresh = pressed ? _down.Add(virtualKey) : _down.Remove(virtualKey);
        if (virtualKey == Escape)
        {
            if (pressed && fresh && recording) _suppressEscape = true;
            var suppress = _suppressEscape;
            var cancel = pressed && fresh && suppress;
            if (!pressed) _suppressEscape = false;
            if (pressed)
            {
                _previousTap = null;
                _shiftChord = true;
            }
            return (suppress ? KeyboardGesture.Suppress : KeyboardGesture.None)
                | (cancel ? KeyboardGesture.CancelRecording : KeyboardGesture.None);
        }

        if (!fresh) return KeyboardGesture.None;
        if (virtualKey != RightShift)
        {
            if (pressed)
            {
                _previousTap = null;
                _shiftChord = true;
            }
            return KeyboardGesture.None;
        }

        if (pressed)
        {
            _shiftPressedAt = timestamp;
            _shiftChord = _down.Count > 1;
            return KeyboardGesture.None;
        }

        if (_shiftChord || unchecked(timestamp - _shiftPressedAt) > _maximumTapMilliseconds)
        {
            _previousTap = null;
            return KeyboardGesture.None;
        }

        if (_previousTap is { } previous && unchecked(timestamp - previous) <= _doubleTapMilliseconds)
        {
            _previousTap = null;
            return KeyboardGesture.ToggleRecording;
        }

        _previousTap = timestamp;
        return KeyboardGesture.None;
    }
}

public sealed class GlobalKeyboard : IDisposable
{
    private readonly KeyboardGestureState _gesture = new();
    private readonly HookProcedure _callback;
    private SynchronizationContext? _dispatcher;
    private nint _hook;
    private bool _disposed;

    public event Action? ToggleRecording;
    public event Action? CancelRecording;
    public bool Recording { get; set; }

    public GlobalKeyboard() => _callback = OnKeyboard;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hook != 0) return;
        _dispatcher = SynchronizationContext.Current ?? throw new InvalidOperationException("请在 UI 线程注册语音快捷键。");
        for (var key = 8; key < 255; key++)
        {
            if (key is 0x10 or 0x11 or 0x12) continue;
            if ((GetAsyncKeyState(key) & 0x8000) != 0) _gesture.SeedPressedKey(key);
        }
        _hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0);
        if (_hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册右 Shift 语音快捷键。");
    }

    private nint OnKeyboard(int code, nuint message, nint data)
    {
        if (code < 0) return CallNextHookEx(_hook, code, message, data);
        var keyboard = Marshal.PtrToStructure<KeyboardInput>(data);
        if ((keyboard.Flags & 0x10) != 0) return CallNextHookEx(_hook, code, message, data);
        var pressed = message is 0x100 or 0x104;
        if (!pressed && message is not (0x101 or 0x105)) return CallNextHookEx(_hook, code, message, data);
        var key = (int)keyboard.VirtualKey;
        if (key == 0x10 && keyboard.ScanCode == 0x36) key = KeyboardGestureState.RightShift;
        var action = _gesture.Process(key, pressed, keyboard.Time, Recording);
        // Defer application work so the native hook returns before Windows' timeout.
        if ((action & KeyboardGesture.ToggleRecording) != 0)
            _dispatcher?.Post(_ => { if (!_disposed) ToggleRecording?.Invoke(); }, null);
        if ((action & KeyboardGesture.CancelRecording) != 0)
            _dispatcher?.Post(_ => { if (!_disposed) CancelRecording?.Invoke(); }, null);
        return (action & KeyboardGesture.Suppress) != 0 ? 1 : CallNextHookEx(_hook, code, message, data);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hook == 0) return;
        UnhookWindowsHookEx(_hook);
        _hook = 0;
        GC.KeepAlive(_callback);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint HookProcedure(int code, nuint message, nint data);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint SetWindowsHookEx(int hook, HookProcedure callback, nint module, uint thread);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
