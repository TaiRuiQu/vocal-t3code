using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace T3.Voice.Native;

public sealed record MicrophoneDevice(int Id, string Name);

public sealed class WaveMicrophone : IAsyncDisposable
{
    public const int SampleRate = 16000;
    private const int BufferBytes = SampleRate * 2 / 10;
    private static readonly int HeaderSize = Marshal.SizeOf<WaveHeader>();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private readonly List<AudioBuffer> _buffers = [];
    private ManualResetEvent? _ready;
    private nint _device;
    private Task? _pump;
    private bool _stopping;
    private bool _disposed;

    public event Action<Exception>? Failed;

    public static IReadOnlyList<MicrophoneDevice> ListDevices()
    {
        var devices = new List<MicrophoneDevice> { new(-1, "系统默认麦克风") };
        var count = waveInGetNumDevs();
        for (uint index = 0; index < count; index++)
        {
            if (waveInGetDevCaps(index, out var capabilities, (uint)Marshal.SizeOf<WaveCapabilities>()) == 0)
                devices.Add(new((int)index, capabilities.Name));
        }
        return devices;
    }

    public void Start(int deviceId, Action<ReadOnlyMemory<byte>> onData)
    {
        ArgumentNullException.ThrowIfNull(onData);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_device != 0) throw new InvalidOperationException("麦克风已经开始录音。");
            _stopping = false;
            _ready = new ManualResetEvent(false);
            var format = new WaveFormat
            {
                FormatTag = 1,
                Channels = 1,
                SamplesPerSecond = SampleRate,
                AverageBytesPerSecond = SampleRate * 2,
                BlockAlignment = 2,
                BitsPerSample = 16,
            };
            try
            {
                Check(waveInOpen(out _device, unchecked((uint)deviceId), ref format,
                    _ready.SafeWaitHandle.DangerousGetHandle(), 0, 0x50000), "打开麦克风");
                for (var index = 0; index < 4; index++)
                {
                    var buffer = new AudioBuffer(BufferBytes);
                    _buffers.Add(buffer);
                    Check(waveInPrepareHeader(_device, buffer.Header, (uint)HeaderSize), "准备录音缓冲区");
                    buffer.Prepared = true;
                    Check(waveInAddBuffer(_device, buffer.Header, (uint)HeaderSize), "提交录音缓冲区");
                    buffer.Queued = true;
                }
                Check(waveInStart(_device), "启动录音");
                // CALLBACK_EVENT avoids calling WinMM functions inside a native audio callback.
                _pump = Task.Factory.StartNew(() => Pump(onData), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            catch
            {
                if (_device != 0) waveInReset(_device);
                ReleaseDevice();
                throw;
            }
        }
    }

    private void Pump(Action<ReadOnlyMemory<byte>> onData)
    {
        var next = 0;
        try
        {
            while (true)
            {
                _ready!.WaitOne();
                _ready.Reset();
                while (true)
                {
                    AudioBuffer buffer;
                    byte[]? data = null;
                    lock (_gate)
                    {
                        if (_stopping && _buffers.All(item => !item.Queued)) return;
                        buffer = _buffers[next];
                        if (!buffer.Queued) break;
                        var header = Marshal.PtrToStructure<WaveHeader>(buffer.Header);
                        if ((header.Flags & 1) == 0) break;
                        buffer.Queued = false;
                        if (header.BytesRecorded > BufferBytes)
                            throw new InvalidOperationException("麦克风返回了无效的音频长度。");
                        if (header.BytesRecorded > 0)
                        {
                            data = new byte[header.BytesRecorded];
                            Marshal.Copy(buffer.Data, data, 0, data.Length);
                        }
                        next = (next + 1) % _buffers.Count;
                    }
                    if (data is not null) onData(data);
                    lock (_gate)
                    {
                        if (_stopping) continue;
                        Check(waveInAddBuffer(_device, buffer.Header, (uint)HeaderSize), "继续录音");
                        buffer.Queued = true;
                    }
                }
            }
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                _stopping = true;
                if (_device != 0) waveInReset(_device);
            }
            Failed?.Invoke(error);
            throw;
        }
    }

    public async Task StopAsync()
    {
        await _stopGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Task? pump;
            lock (_gate)
            {
                if (_device == 0) return;
                _stopping = true;
                Check(waveInReset(_device), "停止录音");
                _ready!.Set();
                pump = _pump;
            }
            try
            {
                if (pump is not null) await pump.ConfigureAwait(false);
            }
            finally
            {
                lock (_gate) ReleaseDevice();
            }
        }
        finally { _stopGate.Release(); }
    }

    private void ReleaseDevice()
    {
        Exception? failure = null;
        foreach (var buffer in _buffers)
        {
            if (buffer.Prepared && _device != 0)
            {
                var result = waveInUnprepareHeader(_device, buffer.Header, (uint)HeaderSize);
                if (result != 0)
                {
                    failure ??= Error(result, "释放录音缓冲区");
                    continue;
                }
            }
            buffer.Dispose();
        }
        _buffers.Clear();
        if (_device != 0)
        {
            var result = waveInClose(_device);
            if (result != 0) failure ??= Error(result, "关闭麦克风");
        }
        _device = 0;
        _pump = null;
        if (failure is null)
        {
            _ready?.Dispose();
            _ready = null;
        }
        if (failure is not null) throw failure;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }

    private static void Check(uint result, string operation)
    {
        if (result != 0) throw Error(result, operation);
    }

    private static Exception Error(uint result, string operation)
    {
        var description = new StringBuilder(256);
        waveInGetErrorText(result, description, (uint)description.Capacity);
        return new InvalidOperationException($"{operation}失败：{description} ({result})");
    }

    private sealed class AudioBuffer : IDisposable
    {
        public nint Data { get; }
        public nint Header { get; }
        public bool Prepared { get; set; }
        public bool Queued { get; set; }

        public AudioBuffer(int bytes)
        {
            Data = Marshal.AllocHGlobal(bytes);
            Header = Marshal.AllocHGlobal(HeaderSize);
            Marshal.StructureToPtr(new WaveHeader { Data = Data, BufferLength = (uint)bytes }, Header, false);
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Header);
            Marshal.FreeHGlobal(Data);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlignment;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public nint Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public nuint User;
        public uint Flags;
        public uint Loops;
        public nint Next;
        public nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveCapabilities
    {
        public ushort Manufacturer;
        public ushort Product;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Name;
        public uint Formats;
        public ushort Channels;
        public ushort Reserved;
    }

    [DllImport("winmm.dll")]
    private static extern uint waveInGetNumDevs();
    [DllImport("winmm.dll", EntryPoint = "waveInGetDevCapsW", CharSet = CharSet.Unicode)]
    private static extern uint waveInGetDevCaps(nuint device, out WaveCapabilities capabilities, uint size);
    [DllImport("winmm.dll")]
    private static extern uint waveInOpen(out nint device, uint deviceId, ref WaveFormat format, nint callback, nuint instance, uint flags);
    [DllImport("winmm.dll")]
    private static extern uint waveInPrepareHeader(nint device, nint header, uint size);
    [DllImport("winmm.dll")]
    private static extern uint waveInAddBuffer(nint device, nint header, uint size);
    [DllImport("winmm.dll")]
    private static extern uint waveInStart(nint device);
    [DllImport("winmm.dll")]
    private static extern uint waveInReset(nint device);
    [DllImport("winmm.dll")]
    private static extern uint waveInUnprepareHeader(nint device, nint header, uint size);
    [DllImport("winmm.dll")]
    private static extern uint waveInClose(nint device);
    [DllImport("winmm.dll", EntryPoint = "waveInGetErrorTextW", CharSet = CharSet.Unicode)]
    private static extern uint waveInGetErrorText(uint error, StringBuilder text, uint size);
}
