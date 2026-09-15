from pathlib import Path

from network_hid_bridge.__main__ import parse_args


def test_cli_uses_system_config_path_by_default() -> None:
    assert parse_args([]).config == Path("/etc/network-hid-bridge/config.json")


def test_cli_accepts_an_explicit_config_path() -> None:
    assert parse_args(["--config", "test-config.json"]).config == Path("test-config.json")
