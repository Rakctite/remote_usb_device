"""Command-line entry point for the Raspberry Pi receiver service."""

from __future__ import annotations

import argparse
import asyncio
from collections.abc import Sequence
import logging
from pathlib import Path

from .config import load_config
from .server import serve


def parse_args(arguments: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Authenticated network-to-USB HID receiver")
    parser.add_argument(
        "--config",
        type=Path,
        default=Path("/etc/network-hid-bridge/config.json"),
        help="strict JSON configuration path",
    )
    return parser.parse_args(arguments)


def main() -> None:
    arguments = parse_args()
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
    )
    asyncio.run(serve(load_config(arguments.config)))


if __name__ == "__main__":
    main()
