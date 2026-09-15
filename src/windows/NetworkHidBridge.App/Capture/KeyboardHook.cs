using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NetworkHidBridge.App.Capture;

internal readonly record struct KeyboardHookEvent(
    uint VirtualKey,
    uint ScanCode,
    bool Extended,
    bool IsDown,
    bool Injected);

internal sealed class KeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint WmQuit = 0x0012;
    private const uint LlkhfExtended = 0x01;
    private const uint LlkhfInjected = 0x10;

    private readonly Func<KeyboardHookEvent, bool> _handler;
    private readonly ManualResetEventSlim _started = new();
    private readonly HookProc _hookProc;
    private Thread? _thread;
    private nint _hook;
    private uint _threadId;
    private Exception? _startupException;

    public KeyboardHook(Func<KeyboardHookEvent, bool> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _hookProc = HookCallback;
    }

    public void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("Keyboard hook is already started.");
        }

        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "NetworkHidBridge.KeyboardHook",
        };
        _thread.Start();
        _started.Wait();
        if (_startupException is not null)
        {
            throw new InvalidOperationException("Unable to install the keyboard hook.", _startupException);
        }
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            _ = PostThreadMessage(_threadId, WmQuit, 0, 0);
        }
        _thread?.Join(TimeSpan.FromSeconds(2));
        _started.Dispose();
    }

    private void ThreadMain()
    {
        _threadId = GetCurrentThreadId();
        try
        {
            using Process process = Process.GetCurrentProcess();
            using ProcessModule? module = process.MainModule;
            nint moduleHandle = GetModuleHandle(module?.ModuleName);
            _hook = SetWindowsHookEx(WhKeyboardLl, _hookProc, moduleHandle, 0);
            if (_hook == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        catch (Exception exception)
        {
            _startupException = exception;
            _started.Set();
            return;
        }

        _started.Set();
        try
        {
            while (GetMessage(out Message message, 0, 0, 0) > 0)
            {
                TranslateMessage(in message);
                DispatchMessage(in message);
            }
        }
        finally
        {
            if (_hook != 0)
            {
                _ = UnhookWindowsHookEx(_hook);
                _hook = 0;
            }
        }
    }

    private nint HookCallback(int code, nuint wParam, nint lParam)
    {
        if (code >= 0 && wParam is WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp)
        {
            KbdLlHookStruct data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            bool isDown = wParam is WmKeyDown or WmSysKeyDown;
            var value = new KeyboardHookEvent(
                data.VirtualKey,
                data.ScanCode,
                (data.Flags & LlkhfExtended) != 0,
                isDown,
                (data.Flags & LlkhfInjected) != 0);
            if (_handler(value))
            {
                return 1;
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private delegate nint HookProc(int code, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct KbdLlHookStruct
    {
        public readonly uint VirtualKey;
        public readonly uint ScanCode;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Value;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hook, HookProc callback, nint module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(in Message message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(in Message message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? moduleName);
}
