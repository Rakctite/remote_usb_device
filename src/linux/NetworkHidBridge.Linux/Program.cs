using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using NetworkHidBridge.Core.Input;
using NetworkHidBridge.Core.Networking;

namespace NetworkHidBridge.Linux;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}

public sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new SenderWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class SenderWindow : Window
{
    private readonly TextBox _address = new() { Text = "192.168.0.4" };
    private readonly TextBox _port = new() { Text = "45821" };
    private readonly TextBox _key = new() { PasswordChar = '●' };
    private readonly CheckBox _remember = new() { Content = "Save key in desktop keyring" };
    private readonly ComboBox _keyboard = new() { ItemsSource = Evdev.Discover("kbd") };
    private readonly ComboBox _mouse = new() { ItemsSource = Evdev.Discover("mouse") };
    private readonly TextBlock _status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _connection = new();
    private readonly Button _connect = new() { Content = "Connect" };
    private CancellationTokenSource? _lifetime;
    private Sender? _sender;
    private Task? _run;
    private bool _closing;

    private static string SettingsFile => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } directory
            ? directory : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "network-hid-bridge", "settings.json");

    internal SenderWindow()
    {
        Title = "Network HID Bridge";
        Width = 620;
        Height = 720;
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(22) };
        Content = new ScrollViewer { Content = panel };
        void Field(string label, Control control)
        {
            panel.Children.Add(new TextBlock { Text = label });
            panel.Children.Add(control);
        }
        Field("Pi IP address", _address);
        Field("TCP port", _port);
        Field("Shared key (Base64)", _key);
        panel.Children.Add(_remember);
        Field("Keyboard", _keyboard);
        Field("Mouse", _mouse);
        _connect.Click += async (_, _) => await ToggleConnection();
        panel.Children.Add(_connect);
        foreach (RoutingMode mode in Enum.GetValues<RoutingMode>())
        {
            var button = new Button { Content = mode.ToString() };
            button.Click += (_, _) => _sender?.Request(mode);
            panel.Children.Add(button);
        }
        panel.Children.Add(new TextBlock { Text = "Numpad − and * toggle remote mode after keys are released." });
        panel.Children.Add(_connection);
        panel.Children.Add(_status);
        Opened += async (_, _) => await LoadSettings();
        Closing += async (_, e) =>
        {
            if (_closing || _run is null)
                return;
            e.Cancel = true;
            await _lifetime!.CancelAsync();
            await _run;
            _closing = true;
            Close();
        };
    }

    private async Task ToggleConnection()
    {
        if (_lifetime is not null)
        {
            await _lifetime.CancelAsync();
            return;
        }

        try
        {
            if (!IPAddress.TryParse(_address.Text, out var address) ||
                address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
                !int.TryParse(_port.Text, out int port) ||
                _keyboard.SelectedItem is not string keyboard || _mouse.SelectedItem is not string mouse)
                throw new ArgumentException("Enter the Pi IP and port and select both input devices.");
            var options = new BridgeClientOptions(address, port, Convert.FromBase64String(_key.Text ?? ""));
            _connect.IsEnabled = false;
            await SaveSettings(new(address.ToString(), port, keyboard, mouse, _remember.IsChecked == true));
            using var lifetime = new CancellationTokenSource();
            _lifetime = lifetime;
            var sender = new Sender(options, keyboard, mouse);
            _sender = sender;
            sender.Status += message => Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_sender, sender)) return;
                if (message.StartsWith("Mode:", StringComparison.Ordinal))
                    _status.Text = message;
                else
                    _connection.Text = message;
            });
            _connect.Content = "Disconnect";
            _connect.IsEnabled = true;
            _status.Text = "Mode: Default";
            Task monitor = UpdateSession(sender, lifetime.Token);
            _run = Task.Run(async () =>
            {
                try { await sender.RunAsync(lifetime.Token); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => _status.Text = exception.Message);
                }
                finally { await lifetime.CancelAsync(); }
            });
            await _run;
            await monitor;
        }
        catch (Exception exception) { _status.Text = exception.Message; }
        finally
        {
            _run = null;
            _sender = null;
            _lifetime = null;
            _connection.Text = "Disconnected · Default";
            _connect.Content = "Connect";
            _connect.IsEnabled = true;
        }
    }

    private async Task LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return;
            var settings = JsonSerializer.Deserialize<Preferences>(await File.ReadAllTextAsync(SettingsFile));
            if (settings is null) return;
            _address.Text = settings.Address;
            _port.Text = settings.Port.ToString();
            _keyboard.SelectedItem = settings.Keyboard;
            _mouse.SelectedItem = settings.Mouse;
            _remember.IsChecked = settings.Remember;
            if (settings.Remember)
            {
                _key.Text = await RunTool("secret-tool", ["lookup", "application", "network-hid-bridge"], null);
                if (string.IsNullOrEmpty(_key.Text))
                    _status.Text = "Keyring is unavailable or has no saved key. Enter the key for this session.";
            }
        }
        catch (Exception exception) { _status.Text = $"Settings could not be loaded. {exception.Message}"; }
    }

    private async Task SaveSettings(Preferences settings)
    {
        try
        {
            if (settings.Remember)
            {
                if (await RunTool("secret-tool", ["store", "--label=Network HID Bridge", "application", "network-hid-bridge"], _key.Text) is null)
                    throw new IOException("Keyring storage is unavailable.");
            }
            else if (File.Exists(SettingsFile) &&
                JsonSerializer.Deserialize<Preferences>(await File.ReadAllTextAsync(SettingsFile))?.Remember == true)
            {
                if (await RunTool("secret-tool", ["clear", "application", "network-hid-bridge"], null) is null)
                    throw new IOException("The previously saved key could not be removed from the keyring.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            string temp = SettingsFile + "." + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(settings));
                File.Move(temp, SettingsFile, overwrite: true);
            }
            finally { File.Delete(temp); }
        }
        catch (Exception exception)
        {
            _status.Text = $"Using session settings. {exception.Message}";
        }
    }

    private static async Task UpdateSession(Sender sender, CancellationToken token)
    {
        string? session = Environment.GetEnvironmentVariable("XDG_SESSION_ID");
        if (string.IsNullOrEmpty(session)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            do
            {
                string? state = await RunTool("loginctl",
                    ["show-session", session, "-p", "Active", "-p", "LockedHint"], null, token);
                var properties = state?.Split('\n', StringSplitOptions.TrimEntries);
                sender.SetSessionActive(properties?.Contains("Active=yes") == true &&
                    properties.Contains("LockedHint=no"));
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (System.ComponentModel.Win32Exception) { sender.SetSessionActive(false); }
    }

    private static async Task<string?> RunTool(string executable, string[] arguments, string? input,
        CancellationToken token = default)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(timeout.Token);
            await errors;
            return process.ExitCode == 0 ? (await output).Trim() : null;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            token.ThrowIfCancellationRequested();
            return null;
        }
    }

    private sealed record Preferences(string Address, int Port, string Keyboard, string Mouse, bool Remember);
}
