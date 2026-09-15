from __future__ import annotations

import base64
import json
from pathlib import Path

import pytest

from network_hid_bridge.config import BridgeConfig, ConfigError, load_config


PSK = bytes(range(32))


def test_loads_complete_strict_configuration(tmp_path: Path) -> None:
    path = tmp_path / "config.json"
    path.write_text(
        json.dumps(
            {
                "listen_address": "192.168.0.4",
                "port": 45821,
                "psk": base64.b64encode(PSK).decode("ascii"),
                "keyboard_path": "/dev/hidg0",
                "mouse_path": "/dev/hidg1",
                "consumer_path": "/dev/hidg2",
            }
        ),
        encoding="utf-8",
    )

    config = load_config(path)

    assert config == BridgeConfig(
        listen_address="192.168.0.4",
        port=45821,
        psk=PSK,
        keyboard_path=Path("/dev/hidg0"),
        mouse_path=Path("/dev/hidg1"),
        consumer_path=Path("/dev/hidg2"),
    )


@pytest.mark.parametrize(
    "change",
    [
        {"listen_address": "0.0.0.0"},
        {"listen_address": "::"},
        {"listen_address": "not-an-ip"},
        {"port": 0},
        {"port": 65536},
        {"psk": ""},
        {"psk": base64.b64encode(b"short").decode("ascii")},
        {"extra": True},
    ],
)
def test_rejects_wildcards_invalid_values_and_unknown_properties(
    tmp_path: Path, change: dict[str, object]
) -> None:
    values: dict[str, object] = {
        "listen_address": "127.0.0.1",
        "port": 45821,
        "psk": base64.b64encode(PSK).decode("ascii"),
        "keyboard_path": "/dev/hidg0",
        "mouse_path": "/dev/hidg1",
        "consumer_path": "/dev/hidg2",
    }
    values.update(change)
    path = tmp_path / "config.json"
    path.write_text(json.dumps(values), encoding="utf-8")

    with pytest.raises(ConfigError):
        load_config(path)


@pytest.mark.parametrize(
    "missing",
    ["listen_address", "port", "psk", "keyboard_path", "mouse_path", "consumer_path"],
)
def test_rejects_every_missing_required_property(tmp_path: Path, missing: str) -> None:
    values = {
        "listen_address": "127.0.0.1",
        "port": 45821,
        "psk": base64.b64encode(PSK).decode("ascii"),
        "keyboard_path": "/dev/hidg0",
        "mouse_path": "/dev/hidg1",
        "consumer_path": "/dev/hidg2",
    }
    del values[missing]
    path = tmp_path / "config.json"
    path.write_text(json.dumps(values), encoding="utf-8")

    with pytest.raises(ConfigError):
        load_config(path)


@pytest.mark.parametrize("contents", ["{", "[]", '"text"'])
def test_rejects_malformed_or_non_object_json(tmp_path: Path, contents: str) -> None:
    path = tmp_path / "config.json"
    path.write_text(contents, encoding="utf-8")

    with pytest.raises(ConfigError):
        load_config(path)
