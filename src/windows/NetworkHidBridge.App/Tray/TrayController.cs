using System.Drawing;
using System.Windows.Forms;
using NetworkHidBridge.Core.Input;
using NetworkHidBridge.Core.Networking;

namespace NetworkHidBridge.App.Tray;

internal sealed class TrayController : IDisposable
{
    private readonly ModeController _modeController;
    private readonly Action _showWindow;
    private readonly Func<Task> _reconnect;
    private readonly Func<Task> _exit;
    private readonly NotifyIcon _icon;
    private readonly Dictionary<RoutingMode, ToolStripMenuItem> _modeItems = [];

    public TrayController(ModeController modeController, Action showWindow, Func<Task> reconnect, Func<Task> exit)
    {
        _modeController = modeController;
        _showWindow = showWindow;
        _reconnect = reconnect;
        _exit = exit;
        var menu = new ContextMenuStrip();
        menu.Items.Add("설정 열기", null, (_, _) => _showWindow());
        menu.Items.Add("다시 연결", null, async (_, _) => await ReconnectAsync());
        menu.Items.Add(new ToolStripSeparator());
        AddMode(menu, "기본 (메인 O / 원격 X)", RoutingMode.Default);
        AddMode(menu, "원격 전용 (메인 X / 원격 O)", RoutingMode.RemoteOnly);
        AddMode(menu, "공유 (메인 O / 원격 O)", RoutingMode.Shared);
        AddMode(menu, "전체 차단 (메인 X / 원격 X)", RoutingMode.BlockAll);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, async (_, _) => await _exit());
        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Network HID Bridge - 연결 안 됨",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => _showWindow();
        SetMode(modeController.Mode);
    }

    public void SetConnection(ConnectionSnapshot snapshot)
    {
        string state = snapshot.State == ConnectionState.Connected ? "연결됨" : "연결 안 됨";
        _icon.Text = $"Network HID Bridge - {state}";
    }

    public void SetMode(RoutingMode mode)
    {
        foreach ((RoutingMode itemMode, ToolStripMenuItem item) in _modeItems)
        {
            item.Checked = itemMode == mode;
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private void AddMode(ContextMenuStrip menu, string text, RoutingMode mode)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) =>
        {
            if (!_modeController.TrySetMode(mode))
            {
                _icon.ShowBalloonTip(2500, "연결 필요", "인증된 Pi 연결이 필요합니다.", ToolTipIcon.Warning);
            }
        };
        _modeItems.Add(mode, item);
        menu.Items.Add(item);
    }

    private async Task ReconnectAsync()
    {
        try
        {
            await _reconnect();
        }
        catch (Exception exception)
        {
            _icon.ShowBalloonTip(2500, "재연결 실패", exception.Message, ToolTipIcon.Error);
        }
    }
}
