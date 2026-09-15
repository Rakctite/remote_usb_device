from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SERVICE = ROOT / "raspi" / "network-hid-bridge.service"
INSTALLER = ROOT / "raspi" / "install.sh"


def test_receiver_service_has_dependencies_identity_and_restart_policy() -> None:
    service = SERVICE.read_text(encoding="utf-8")

    assert "Requires=usb-hid-gadget.service" in service
    assert "After=usb-hid-gadget.service network-online.target" in service
    assert "User=hidbridge" in service
    assert "Group=hidbridge" in service
    assert "WorkingDirectory=/opt/network-hid-bridge" in service
    assert "ExecStart=/usr/bin/python3 -m network_hid_bridge --config /etc/network-hid-bridge/config.json" in service
    assert "Restart=on-failure" in service
    for node in range(3):
        assert f"ExecStartPre=/usr/bin/test -c /dev/hidg{node}" in service


def test_receiver_service_is_hardened_but_can_write_hid_nodes() -> None:
    service = SERVICE.read_text(encoding="utf-8")

    for directive in (
        "NoNewPrivileges=true",
        "PrivateTmp=true",
        "ProtectSystem=strict",
        "ProtectHome=true",
        "ReadWritePaths=/dev/hidg0 /dev/hidg1 /dev/hidg2",
        "UMask=0077",
    ):
        assert directive in service


def test_installer_creates_locked_down_account_source_and_atomic_config() -> None:
    installer = INSTALLER.read_text(encoding="utf-8")

    assert "groupadd --system hidbridge" in installer
    assert "useradd --system" in installer
    assert "/usr/sbin/nologin" in installer
    assert "/opt/network-hid-bridge" in installer
    assert "/etc/network-hid-bridge" in installer
    assert "mktemp" in installer
    assert "chmod 0640" in installer
    assert "chown root:hidbridge" in installer
    assert 'mv -f "$CONFIG_TEMP" "$CONFIG_PATH"' in installer
    assert "systemctl daemon-reload" in installer
    assert "systemctl enable --now usb-hid-gadget.service network-hid-bridge.service" in installer


def test_installer_accepts_explicit_values_and_hides_interactive_psk() -> None:
    installer = INSTALLER.read_text(encoding="utf-8")

    assert "--listen-address" in installer
    assert "--port" in installer
    assert "--psk" in installer
    assert "read -r -s" in installer
    assert "PSK must not be empty" in installer
    assert "base64.b64decode" in installer
    assert "validate=True" in installer


def test_installer_does_not_change_network_or_radio_configuration() -> None:
    installer = INSTALLER.read_text(encoding="utf-8").lower()

    for forbidden in ("rfkill", "dtoverlay", "config.txt", "nmcli", "networkctl", "wpa_supplicant"):
        assert forbidden not in installer
