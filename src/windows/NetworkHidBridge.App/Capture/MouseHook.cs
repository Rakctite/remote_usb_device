using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NetworkHidBridge.App.Capture;

internal enum MouseHookKind
{
    Movement,
    Button,
    Wheel,
}

internal readonly record struct MouseHookEvent(MouseHookKind Kind, int Button, bool IsDown, bool Injected);

internal sealed class MouseHook : IDisposable
{
    private const int WhMouseLl = 14;
    private const uint WmQuit = 0x0012;
    private const uint LlmhfInjected = 0x01;
    private readonly Func<MouseHookEvent, bool> _handler;
    private readonly ManualResetEventSlim _started = new();
    private readonly HookProc _hookProc;
    private Thread? _thread;
    private nint _hook;
    private uint _threadId;
    private Exception? _startupException;

    public MouseHook(Func<MouseHookEvent, bool> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _hookProc = HookCallback;
    }

    public void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("Mouse hook is already started.");
        }
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "NetworkHidBridge.MouseHook" };
        _thread.Start();
        _started.Wait();
        if (_startupException is not null)
        {
            throw new InvalidOperationException("Unable to install the mouse hook.", _startupException);
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
            _hook = SetWindowsHookEx(WhMouseLl, _hookProc, GetModuleHandle(module?.ModuleName), 0);
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
        if (code >= 0)
        {
            MsLlHookStruct data = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
            MouseHookEvent? value = CreateEvent((uint)wParam, data);
            if (value is { } hookEvent && _handler(hookEvent))
            {
                return 1;
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static MouseHookEvent? CreateEvent(uint message, MsLlHookStruct data)
    {
        bool injected = (data.Flags & LlmhfInjected) != 0;
        return message switch
        {
            0x0200 => new(MouseHookKind.Movement, 0, false, injected),
            0x0201 => new(MouseHookKind.Button, 1, true, injected),
            0x0202 => new(MouseHookKind.Button, 1, false, injected),
            0x0204 => new(MouseHookKind.Button, 2, true, injected),
            0x0205 => new(MouseHookKind.Button, 2, false, injected),
            0x0207 => new(MouseHookKind.Button, 3, true, injected),
            0x0208 => new(MouseHookKind.Button, 3, false, injected),
            0x020B => new(MouseHookKind.Button, HighWord(data.MouseData) == 1 ? 4 : 5, true, injected),
            0x020C => new(MouseHookKind.Button, HighWord(data.MouseData) == 1 ? 4 : 5, false, injected),
            0x020A or 0x020E => new(MouseHookKind.Wheel, 0, false, injected),
            _ => null,
        };
    }

    private static ushort HighWord(uint value) => (ushort)(value >> 16);

    private delegate nint HookProc(int code, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MsLlHookStruct
    {
        public readonly int X;
        public readonly int Y;
        public readonly uint MouseData;
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

    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int hook, HookProc callback, nint module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(in Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(in Message message);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint GetModuleHandle(string? moduleName);
}
