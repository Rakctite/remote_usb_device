"""Launch the production receiver with regular files standing in for HID nodes."""

from __future__ import annotations

import argparse
import asyncio
from pathlib import Path

from network_hid_bridge.config import load_config
from network_hid_bridge.hid import HidOutput
from network_hid_bridge.session import run_session


async def run_once(config_path: Path) -> None:
    config = load_config(config_path)
    finished = asyncio.Event()
    with HidOutput(config.keyboard_path, config.mouse_path, config.consumer_path) as hid:
        async def handle(reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
            try:
                await run_session(reader, writer, config.psk, hid)
            finally:
                finished.set()

        server = await asyncio.start_server(handle, config.listen_address, config.port)
        async with server:
            await finished.wait()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", type=Path, required=True)
    arguments = parser.parse_args()
    asyncio.run(run_once(arguments.config))


if __name__ == "__main__":
    main()
