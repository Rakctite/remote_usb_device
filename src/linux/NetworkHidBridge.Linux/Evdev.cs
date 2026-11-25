using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NetworkHidBridge.Linux;

internal sealed class Evdev : IDisposable
{
    internal string Path { get; }
    private readonly SafeFileHandle _file;
    private readonly IntPtr _device;

    internal Evdev(string path)
    {
        Path = path;
        int fd = Open(path, 0x80800); // O_CLOEXEC | O_NONBLOCK
        if (fd < 0)
            throw new IOException($"Cannot open {path} (errno {Marshal.GetLastPInvokeError()}). Check device access.");
        _file = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        int result = Create(fd, out _device);
        if (result < 0)
        {
            _file.Dispose();
            throw new IOException($"Cannot initialize {path} ({result}).");
        }
    }

    internal static string[] Discover(string role) =>
        new[] { "/dev/input/by-id", "/dev/input/by-path" }
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, $"*-event-{role}"))
            .Order().ToArray();

    internal bool HasPressedKeys()
    {
        for (uint code = 0; code <= 0x2ff; code++)
            if (Value(_device, 1, code) != 0)
                return true;
        return false;
    }

    internal bool Read(out InputEvent input)
    {
        int result = Next(_device, 2, out input); // LIBEVDEV_READ_FLAG_NORMAL
        return result switch
        {
            0 => true,
            -11 => false, // EAGAIN
            1 => throw new IOException($"Input state lost on {Path}. Reconnect to resume."),
            _ => throw new IOException($"Input read failed on {Path} ({result}).")
        };
    }

    internal void Grab(bool grab)
    {
        int result = SetGrab(_device, grab ? 3 : 4);
        if (result < 0)
            throw new IOException($"Cannot {(grab ? "grab" : "release")} {Path} ({result}).");
    }

    public void Dispose()
    {
        Free(_device);
        _file.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct InputEvent
    {
        private readonly long _seconds, _microseconds; // Linux x86-64 timeval
        internal readonly ushort Type, Code;
        internal readonly int Value;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libevdev.so.2", EntryPoint = "libevdev_new_from_fd")]
    private static extern int Create(int fd, out IntPtr device);
    [DllImport("libevdev.so.2", EntryPoint = "libevdev_free")]
    private static extern void Free(IntPtr device);
    [DllImport("libevdev.so.2", EntryPoint = "libevdev_next_event")]
    private static extern int Next(IntPtr device, uint flags, out InputEvent input);
    [DllImport("libevdev.so.2", EntryPoint = "libevdev_get_event_value")]
    private static extern int Value(IntPtr device, uint type, uint code);
    [DllImport("libevdev.so.2", EntryPoint = "libevdev_grab")]
    private static extern int SetGrab(IntPtr device, int mode);
}
