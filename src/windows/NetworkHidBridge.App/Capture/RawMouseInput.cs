using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NetworkHidBridge.App.Capture;

internal readonly record struct RawMousePacket(int DeltaX, int DeltaY, ushort ButtonFlags, short ButtonData);

internal sealed class RawMouseInput : IDisposable
{
    private readonly Action<RawMousePacket> _handler;
    private readonly ManualResetEventSlim _started = new();
    private Thread? _thread;
    private RawInputWindow? _window;
    private Exception? _startupException;

    public RawMouseInput(Action<RawMousePacket> handler) =>
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));

    public void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("Raw mouse input is already started.");
        }
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "NetworkHidBridge.RawMouseInput" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _started.Wait();
        if (_startupException is not null)
        {
            throw new InvalidOperationException("Unable to register raw mouse input.", _startupException);
        }
    }

    public void Dispose()
    {
        nint handle = _window?.Handle ?? 0;
        if (handle != 0)
        {
            _ = PostMessage(handle, 0x0010, 0, 0);
        }
        _thread?.Join(TimeSpan.FromSeconds(2));
        _started.Dispose();
    }

    private void ThreadMain()
    {
        try
        {
            _window = new RawInputWindow(_handler);
            _window.Register();
        }
        catch (Exception exception)
        {
            _startupException = exception;
            _started.Set();
            return;
        }
        _started.Set();
        System.Windows.Forms.Application.Run();
        _window.DestroyHandle();
        _window = null;
    }

    private sealed class RawInputWindow : NativeWindow
    {
        private const uint RidInput = 0x10000003;
        private readonly Action<RawMousePacket> _handler;

        public RawInputWindow(Action<RawMousePacket> handler)
        {
            _handler = handler;
            CreateHandle(new CreateParams
            {
                Caption = "NetworkHidBridge.RawInput",
                Parent = new nint(-3),
            });
        }

        public void Register()
        {
            RawInputDevice[] devices =
            [
                new() { UsagePage = 0x01, Usage = 0x02, Flags = 0x00000100, Target = Handle },
            ];
            if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RawInputDevice>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        protected override void WndProc(ref System.Windows.Forms.Message message)
        {
            if (message.Msg == 0x00FF)
            {
                ReadInput(message.LParam);
            }
            else if (message.Msg == 0x0010)
            {
                System.Windows.Forms.Application.ExitThread();
                return;
            }
            base.WndProc(ref message);
        }

        private void ReadInput(nint rawInput)
        {
            uint size = 0;
            uint headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
            _ = GetRawInputData(rawInput, RidInput, 0, ref size, headerSize);
            if (size < headerSize + Marshal.SizeOf<RawMouse>())
            {
                return;
            }

            nint buffer = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                if (GetRawInputData(rawInput, RidInput, buffer, ref size, headerSize) != size)
                {
                    return;
                }
                RawInputHeader header = Marshal.PtrToStructure<RawInputHeader>(buffer);
                if (header.Type != 0)
                {
                    return;
                }
                RawMouse mouse = Marshal.PtrToStructure<RawMouse>(nint.Add(buffer, checked((int)headerSize)));
                _handler(new(mouse.LastX, mouse.LastY, mouse.ButtonFlags, unchecked((short)mouse.ButtonData)));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RawInputHeader
    {
        public readonly uint Type;
        public readonly uint Size;
        public readonly nint Device;
        public readonly nuint WParam;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private readonly struct RawMouse
    {
        [FieldOffset(0)] public readonly ushort Flags;
        [FieldOffset(4)] public readonly uint Buttons;
        [FieldOffset(4)] public readonly ushort ButtonFlags;
        [FieldOffset(6)] public readonly ushort ButtonData;
        [FieldOffset(8)] public readonly uint RawButtons;
        [FieldOffset(12)] public readonly int LastX;
        [FieldOffset(16)] public readonly int LastY;
        [FieldOffset(20)] public readonly uint ExtraInformation;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] devices,
        uint numberOfDevices,
        uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(nint rawInput, uint command, nint data, ref uint size, uint headerSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
}
