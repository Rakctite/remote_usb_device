using NetworkHidBridge.Core.Input;
using NetworkHidBridge.Core.Networking;

namespace NetworkHidBridge.Linux;

internal sealed class Sender(BridgeClientOptions options, string keyboardPath, string mousePath)
{
    private readonly ModeController _mode = new();
    private int _requested = -1;
    private volatile bool _sessionActive;
    internal event Action<string>? Status;
    internal void Request(RoutingMode mode) => Interlocked.Exchange(ref _requested, (int)mode);
    internal void SetSessionActive(bool active) => _sessionActive = active;

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using var keyboard = new Evdev(keyboardPath);
        using var mouse = new Evdev(mousePath);
        using var networkLifetime = new CancellationTokenSource();
        var client = new BridgeClient(options, _mode);
        client.ConnectionChanged += (_, snapshot) =>
            Status?.Invoke($"{snapshot.State} · {snapshot.LastError}");
        Task network = client.RunAsync(networkLifetime.Token);
        var report = new KeyboardReportState();
        var consumer = new List<ushort>();
        bool grabbed = false, chord = false, subtract = false, multiply = false;
        RoutingMode active = RoutingMode.Default;
        RoutingMode? pending = null;

        void Apply(RoutingMode next)
        {
            if (next == active)
                return;
            bool exclusive = !next.GetPolicy().AllowLocal;
            if (grabbed != exclusive)
            {
                keyboard.Grab(exclusive);
                try { mouse.Grab(exclusive); }
                catch
                {
                    keyboard.Grab(false);
                    throw;
                }
                grabbed = exclusive;
            }
            client.SetRemoteRouting(false);
            report.Clear();
            consumer.Clear();
            if (!_mode.TrySetMode(next))
                throw new IOException("Pi authentication was lost during mode change.");
            client.SetRemoteRouting(next.GetPolicy().AllowRemote);
            active = next;
            Status?.Invoke($"Mode: {active}");
        }

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(4));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!_mode.IsAuthenticated || !_sessionActive)
                {
                    pending = null;
                    Apply(RoutingMode.Default);
                }
                int requested = Interlocked.Exchange(ref _requested, -1);
                if (requested >= 0)
                    pending = (RoutingMode)requested;

                for (int count = 0; count < 256 && keyboard.Read(out var key); count++)
                {
                    if (key.Type != 1 || key.Value == 2)
                        continue;
                    bool down = key.Value == 1;
                    if (key.Code is 55 or 74)
                    {
                        if (key.Code == 55) multiply = down;
                        else subtract = down;
                        if (multiply && subtract && !chord)
                            pending = active == RoutingMode.Default ? RoutingMode.RemoteOnly : RoutingMode.Default;
                        chord = multiply && subtract;
                        continue;
                    }
                    if (!active.GetPolicy().AllowRemote)
                        continue;

                    ushort usage = LinuxKeyMap.Consumer(key.Code);
                    if (usage != 0)
                    {
                        consumer.Remove(usage);
                        if (down) consumer.Add(usage);
                        client.QueueConsumer(consumer.Count == 0 ? (ushort)0 : consumer[^1]);
                    }
                    else
                    {
                        var mapped = LinuxKeyMap.Map(key.Code);
                        if (mapped != default && report.Set(mapped, down))
                            client.QueueKeyboard(report.BuildReport());
                    }
                }

                for (int count = 0; count < 256 && mouse.Read(out var input); count++)
                {
                    if (!active.GetPolicy().AllowRemote)
                        continue;
                    if (input.Type == 1 && input.Code is >= 272 and <= 276 && input.Value != 2)
                        client.SetMouseButton(input.Code - 271, input.Value == 1);
                    if (input.Type == 2)
                    {
                        switch (input.Code)
                        {
                            case 0: client.AddMouseMovement(input.Value, 0); break;
                            case 1: client.AddMouseMovement(0, input.Value); break;
                            case 6: client.AddMouseWheel(0, input.Value); break;
                            case 8: client.AddMouseWheel(input.Value, 0); break;
                        }
                    }
                }

                if (pending is { } next && !keyboard.HasPressedKeys() && !mouse.HasPressedKeys())
                {
                    pending = null;
                    if (next == RoutingMode.Default || (_sessionActive && _mode.IsAuthenticated))
                        Apply(next);
                    else
                        Status?.Invoke("An active desktop session and authenticated Pi connection are required.");
                }
            }
        }
        finally
        {
            try { client.SetRemoteRouting(false); }
            finally
            {
                try
                {
                    if (grabbed)
                    {
                        try { keyboard.Grab(false); }
                        finally { mouse.Grab(false); }
                    }
                    await Task.WhenAny(network, Task.Delay(75));
                }
                finally { await networkLifetime.CancelAsync(); }
                try { await network; }
                catch (OperationCanceledException) when (networkLifetime.IsCancellationRequested) { }
            }
        }
    }
}
