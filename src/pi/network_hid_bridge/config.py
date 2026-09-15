"""Strict configuration loading for the Raspberry Pi receiver."""

from __future__ import annotations

import base64
import binascii
from dataclasses import dataclass
import ipaddress
import json
from pathlib import Path
from typing import Any


_FIELDS = frozenset(
    {
        "listen_address",
        "port",
        "psk",
        "keyboard_path",
        "mouse_path",
        "consumer_path",
    }
)


class ConfigError(ValueError):
    """Configuration is missing, malformed, or unsafe."""


@dataclass(frozen=True, slots=True)
class BridgeConfig:
    listen_address: str
    port: int
    psk: bytes
    keyboard_path: Path
    mouse_path: Path
    consumer_path: Path


def load_config(path: str | Path) -> BridgeConfig:
    try:
        raw: Any = json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ConfigError("unable to read configuration") from error
    if not isinstance(raw, dict):
        raise ConfigError("configuration root must be an object")
    keys = frozenset(raw)
    if keys != _FIELDS:
        raise ConfigError("configuration properties do not match the required schema")

    address_text = _required_text(raw, "listen_address")
    try:
        address = ipaddress.ip_address(address_text)
    except ValueError as error:
        raise ConfigError("listen_address must be an IP address") from error
    if address.is_unspecified or address.is_multicast:
        raise ConfigError("listen_address must be a specific unicast address")

    port = raw["port"]
    if isinstance(port, bool) or not isinstance(port, int) or not 1 <= port <= 65535:
        raise ConfigError("port must be between 1 and 65535")

    encoded_psk = _required_text(raw, "psk")
    try:
        psk = base64.b64decode(encoded_psk, validate=True)
    except (binascii.Error, ValueError) as error:
        raise ConfigError("psk must be valid Base64") from error
    if len(psk) != 32:
        raise ConfigError("psk must decode to exactly 32 bytes")

    return BridgeConfig(
        listen_address=str(address),
        port=port,
        psk=psk,
        keyboard_path=Path(_required_text(raw, "keyboard_path")),
        mouse_path=Path(_required_text(raw, "mouse_path")),
        consumer_path=Path(_required_text(raw, "consumer_path")),
    )


def _required_text(raw: dict[str, Any], name: str) -> str:
    value = raw[name]
    if not isinstance(value, str) or not value:
        raise ConfigError(f"{name} must be a non-empty string")
    return value
