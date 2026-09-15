using System.Reflection;
using NetworkHidBridge.App.Capture;
using NetworkHidBridge.App.Tray;
using NetworkHidBridge.Core.Input;
using NetworkHidBridge.Core.Networking;
using NetworkHidBridge.Core.Settings;
using ExitEventArgs = System.Windows.ExitEventArgs;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using StartupEventArgs = System.Windows.StartupEventArgs;

namespace NetworkHidBridge.App;

public partial class App : System.Windows.Application
{
    private readonly SemaphoreSlim _runtimeGate = new(1, 1);
    private readonly ModeController _modeController = new();
    private readonly ProtectedSettingsStore _settingsStore = new();
    private readonly StartupTaskManager _startupTasks = new();
    private Mutex? _instanceMutex;
    private MainWindow? _window;
    private TrayController? _tray;
    private BridgeClient? _bridge;
    private InputCaptureService? _capture;
    private CancellationTokenSource? _networkCancellation;
    private Task? _networkTask;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instanceMutex = new Mutex(true, @"Local\IoTSys.NetworkHidBridge", out bool created);
        if (!created)
        {
            MessageBox.Show("Network HID Bridge가 이미 실행 중입니다.", "Network HID Bridge");
            Shutdown();
            return;
        }

        SettingsLoadResult loaded = _settingsStore.LoadOrCreate();
        _window = new MainWindow(_modeController, loaded.Settings, SaveAndRestartAsync, ReconnectAsync);
        _tray = new TrayController(
            _modeController,
            () => _window.Dispatcher.Invoke(() =>
            {
                _window.Show();
                _window.Activate();
            }),
            ReconnectAsync,
            ExitAsync);
        _modeController.ModeChanged += (_, mode) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                _window.SetMode(mode);
                _tray.SetMode(mode);
            });
        };
        if (loaded.Warning is not null)
        {
            _window.AddDiagnostic(loaded.Warning);
        }
        _window.Show();
        _ = StartInitialRuntimeAsync(loaded.Settings);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _networkCancellation?.Cancel();
        _capture?.Dispose();
        _tray?.Dispose();
        if (_instanceMutex is not null)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
            _instanceMutex.Dispose();
        }
        _runtimeGate.Dispose();
        base.OnExit(e);
    }

    private async Task StartInitialRuntimeAsync(AppSettings settings)
    {
        try
        {
            await RestartRuntimeAsync(settings).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _window?.AddDiagnostic($"입력 캡처 시작 실패: {exception.Message}");
            MessageBox.Show(
                "키보드/마우스 캡처를 시작하지 못했습니다. 프로그램을 종료합니다.",
                "Network HID Bridge",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            await ExitAsync().ConfigureAwait(true);
        }
    }

    private async Task SaveAndRestartAsync(AppSettings settings)
    {
        _settingsStore.Save(settings);
        string executable = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
        StartupTaskResult startup = await _startupTasks.SetEnabledAsync(
            settings.StartWithWindows,
            executable).ConfigureAwait(true);
        _window?.AddDiagnostic(startup.Success
            ? "Windows 시작 설정을 적용했습니다."
            : $"Windows 시작 설정 실패: {startup.Message}");
        await RestartRuntimeAsync(settings).ConfigureAwait(true);
    }

    private Task ReconnectAsync()
    {
        SettingsLoadResult loaded = _settingsStore.LoadOrCreate();
        return RestartRuntimeAsync(loaded.Settings);
    }

    private async Task RestartRuntimeAsync(AppSettings settings)
    {
        await _runtimeGate.WaitAsync().ConfigureAwait(true);
        try
        {
            await StopRuntimeAsync().ConfigureAwait(true);
            byte[] psk = settings.CopyPsk();
            try
            {
                _bridge = new BridgeClient(
                    new BridgeClientOptions(settings.Address, settings.Port, psk),
                    _modeController);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(psk);
            }
            _bridge.ConnectionChanged += HandleConnectionChanged;
            _capture = new InputCaptureService(_modeController, _bridge);
            _capture.Start();
            _networkCancellation = new CancellationTokenSource();
            _networkTask = RunNetworkAsync(_bridge, _networkCancellation.Token);
            _window?.AddDiagnostic($"연결 대상: {settings.Address}:{settings.Port}");
        }
        finally
        {
            _runtimeGate.Release();
        }
    }

    private async Task StopRuntimeAsync()
    {
        _modeController.TrySetMode(RoutingMode.Default);
        _capture?.Dispose();
        _capture = null;
        _networkCancellation?.Cancel();
        if (_networkTask is not null)
        {
            try
            {
                await _networkTask.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
        }
        _networkCancellation?.Dispose();
        _networkCancellation = null;
        _networkTask = null;
        if (_bridge is not null)
        {
            _bridge.ConnectionChanged -= HandleConnectionChanged;
        }
        _bridge = null;
    }

    private static async Task RunNetworkAsync(BridgeClient bridge, CancellationToken cancellationToken)
    {
        try
        {
            await bridge.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void HandleConnectionChanged(object? sender, ConnectionSnapshot snapshot)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _window?.SetConnection(snapshot);
            _tray?.SetConnection(snapshot);
        });
    }

    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;
        await _runtimeGate.WaitAsync().ConfigureAwait(true);
        try
        {
            await StopRuntimeAsync().ConfigureAwait(true);
        }
        finally
        {
            _runtimeGate.Release();
        }
        _window?.AllowClose();
        _tray?.Dispose();
        _tray = null;
        Shutdown();
    }
}
