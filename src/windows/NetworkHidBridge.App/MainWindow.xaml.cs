using System.ComponentModel;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using NetworkHidBridge.Core.Input;
using NetworkHidBridge.Core.Networking;
using NetworkHidBridge.Core.Settings;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace NetworkHidBridge.App;

public partial class MainWindow : System.Windows.Window
{
    private readonly ModeController _modeController;
    private readonly Func<AppSettings, Task> _save;
    private readonly Func<Task> _reconnect;
    private bool _allowClose;

    public MainWindow(ModeController modeController, AppSettings settings, Func<AppSettings, Task> save, Func<Task> reconnect)
    {
        InitializeComponent();
        _modeController = modeController;
        _save = save;
        _reconnect = reconnect;
        AddressBox.Text = settings.Address.ToString();
        PortBox.Text = settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PskBox.Text = Convert.ToBase64String(settings.CopyPsk());
        StartupCheckBox.IsChecked = settings.StartWithWindows;
        EndpointText.Text = $"{settings.Address}:{settings.Port}";
        Closing += HandleClosing;
    }

    public void SetConnection(ConnectionSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetConnection(snapshot)); return; }
        EndpointText.Text = snapshot.Endpoint.ToString();
        string rtt = snapshot.RoundTripTime is null ? string.Empty : $" / RTT {snapshot.RoundTripTime.Value.TotalMilliseconds:F1} ms";
        ConnectionText.Text = snapshot.State switch
        {
            ConnectionState.Connected => "연결됨" + rtt,
            ConnectionState.Connecting => "연결 중",
            ConnectionState.Authenticating => "인증 중",
            _ => snapshot.LastError == ConnectionError.None ? "연결 안 됨" : $"연결 안 됨 ({snapshot.LastError})",
        };
    }

    public void SetMode(RoutingMode mode)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetMode(mode)); return; }
        ModeText.Text = mode switch
        {
            RoutingMode.Default => "기본",
            RoutingMode.RemoteOnly => "원격 전용",
            RoutingMode.Shared => "공유",
            RoutingMode.BlockAll => "전체 차단",
            _ => mode.ToString(),
        };
    }

    public void AddDiagnostic(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => AddDiagnostic(message)); return; }
        DiagnosticsList.Items.Add($"{DateTimeOffset.Now:HH:mm:ss}  {message}");
        while (DiagnosticsList.Items.Count > 200) DiagnosticsList.Items.RemoveAt(0);
        DiagnosticsList.ScrollIntoView(DiagnosticsList.Items[DiagnosticsList.Items.Count - 1]);
    }

    public void AllowClose() => _allowClose = true;

    private void HandleClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Hide();
    }

    private async void Save_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try { await _save(ReadSettings()); AddDiagnostic("설정을 저장하고 연결을 다시 시작했습니다."); }
        catch (Exception exception) { MessageBox.Show(exception.Message, "설정 오류", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void Reconnect_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try { await _reconnect(); AddDiagnostic("수동 재연결을 시작했습니다."); }
        catch (Exception exception) { AddDiagnostic($"재연결 실패: {exception.Message}"); }
    }

    private void GeneratePsk_Click(object sender, System.Windows.RoutedEventArgs e) => PskBox.Text = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private void DefaultMode_Click(object sender, System.Windows.RoutedEventArgs e) => SetModeFromUi(RoutingMode.Default);
    private void RemoteMode_Click(object sender, System.Windows.RoutedEventArgs e) => SetModeFromUi(RoutingMode.RemoteOnly);
    private void SharedMode_Click(object sender, System.Windows.RoutedEventArgs e) => SetModeFromUi(RoutingMode.Shared);
    private void BlockMode_Click(object sender, System.Windows.RoutedEventArgs e) => SetModeFromUi(RoutingMode.BlockAll);

    private void SetModeFromUi(RoutingMode mode)
    {
        if (!_modeController.TrySetMode(mode)) MessageBox.Show("인증된 Pi 연결이 있어야 원격 입력 모드를 사용할 수 있습니다.", "연결 필요");
    }

    private AppSettings ReadSettings()
    {
        if (!IPAddress.TryParse(AddressBox.Text.Trim(), out IPAddress? address)) throw new InvalidDataException("올바른 Pi IP 주소를 입력하세요.");
        if (!int.TryParse(PortBox.Text.Trim(), out int port)) throw new InvalidDataException("올바른 TCP 포트를 입력하세요.");
        byte[] psk;
        try { psk = Convert.FromBase64String(PskBox.Text.Trim()); }
        catch (FormatException exception) { throw new InvalidDataException("PSK는 32바이트 Base64 값이어야 합니다.", exception); }
        try
        {
            return new(address, port, psk, StartupCheckBox.IsChecked == true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(psk);
        }
    }
}
