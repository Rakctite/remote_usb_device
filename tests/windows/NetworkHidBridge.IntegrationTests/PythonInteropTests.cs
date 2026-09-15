using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NetworkHidBridge.Core.Input;
using NetworkHidBridge.Core.Networking;

namespace NetworkHidBridge.IntegrationTests;

public sealed class PythonInteropTests
{
    [Fact]
    public async Task ProductionClientAndServerWriteExactReportsAndReleaseOnDisconnect()
    {
        string root = FindRepositoryRoot();
        string temporary = Path.Combine(Path.GetTempPath(), $"nhid-interop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        string keyboardPath = Path.Combine(temporary, "keyboard");
        string mousePath = Path.Combine(temporary, "mouse");
        string consumerPath = Path.Combine(temporary, "consumer");
        File.WriteAllBytes(keyboardPath, []);
        File.WriteAllBytes(mousePath, []);
        File.WriteAllBytes(consumerPath, []);
        byte[] psk = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        int port = ReservePort();
        string configPath = Path.Combine(temporary, "config.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(new
        {
            listen_address = "127.0.0.1",
            port,
            psk = Convert.ToBase64String(psk),
            keyboard_path = keyboardPath,
            mouse_path = mousePath,
            consumer_path = consumerPath,
        }));

        using Process python = StartPython(root, configPath);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var modes = new ModeController();
        var client = new BridgeClient(new(IPAddress.Loopback, port, psk), modes);
        int acknowledgedHeartbeats = 0;
        client.ConnectionChanged += (_, snapshot) =>
        {
            if (snapshot.RoundTripTime is not null) Interlocked.Increment(ref acknowledgedHeartbeats);
        };
        Task clientTask = client.RunAsync(cancellation.Token);
        try
        {
            await WaitUntilAsync(() => modes.IsAuthenticated, python, cancellation.Token);
            Assert.True(modes.TrySetMode(RoutingMode.RemoteOnly));
            client.SetRemoteRouting(true);

            byte[] keyboard = Convert.FromHexString("0200040000000000");
            Assert.True(client.QueueKeyboard(keyboard));
            client.SetMouseButton(1, true);
            client.AddMouseMovement(12, -7);
            client.AddMouseWheel(1, -1);
            Assert.True(client.QueueConsumer(0x00E9));

            await WaitUntilAsync(
                () => Volatile.Read(ref acknowledgedHeartbeats) > 0,
                python,
                cancellation.Token);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clientTask);
            using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await python.WaitForExitAsync(exitTimeout.Token);

            Assert.True(ContainsReport(keyboardPath, keyboard, 8));
            Assert.True(ContainsReport(mousePath, Convert.FromHexString("010CF901"), 4));
            Assert.True(ContainsReport(consumerPath, Convert.FromHexString("E90000"), 3));
            Assert.True(ContainsReportWithFinalByte(consumerPath, 0xFF, 3));
            Assert.True(EndsWith(keyboardPath, new byte[8]));
            Assert.True(EndsWith(mousePath, new byte[4]));
            Assert.True(EndsWith(consumerPath, new byte[3]));
        }
        finally
        {
            cancellation.Cancel();
            try { await clientTask; } catch (OperationCanceledException) { }
            if (!python.HasExited)
            {
                python.Kill(entireProcessTree: true);
                await python.WaitForExitAsync();
            }
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static Process StartPython(string root, string configPath)
    {
        var start = new ProcessStartInfo("python")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(Path.Combine(root, "tests", "pi", "fake_hid_server.py"));
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(configPath);
        start.Environment["PYTHONPATH"] = Path.Combine(root, "src", "pi");
        return Process.Start(start) ?? throw new InvalidOperationException("Unable to start Python.");
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        Process python,
        CancellationToken cancellationToken)
    {
        while (!condition())
        {
            if (python.HasExited)
            {
                string error = await python.StandardError.ReadToEndAsync(cancellationToken);
                throw new InvalidOperationException($"Python receiver exited early: {error}");
            }
            await Task.Delay(20, cancellationToken);
        }
    }

    private static bool ContainsReport(string path, byte[] expected, int reportSize)
    {
        byte[] data = File.ReadAllBytes(path);
        for (int offset = 0; offset + reportSize <= data.Length; offset += reportSize)
        {
            if (data.AsSpan(offset, reportSize).SequenceEqual(expected)) return true;
        }
        return false;
    }

    private static bool ContainsReportWithFinalByte(string path, byte expected, int reportSize)
    {
        byte[] data = File.ReadAllBytes(path);
        for (int offset = 0; offset + reportSize <= data.Length; offset += reportSize)
        {
            if (data[offset + reportSize - 1] == expected) return true;
        }
        return false;
    }

    private static bool EndsWith(string path, byte[] expected)
    {
        byte[] data = File.ReadAllBytes(path);
        return data.Length >= expected.Length && data.AsSpan(data.Length - expected.Length).SequenceEqual(expected);
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pyproject.toml")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
