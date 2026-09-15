"""Single-owner asyncio TCP server for the Network HID bridge."""

from __future__ import annotations

import asyncio
import logging
import socket

from .config import BridgeConfig
from .hid import HidOutput
from .session import HidSink, SessionEnd, run_session


_LOG = logging.getLogger(__name__)


class BridgeServer:
    def __init__(
        self,
        config: BridgeConfig,
        hid: HidSink,
        *,
        heartbeat_timeout: float = 1.0,
        logger: logging.Logger = _LOG,
    ) -> None:
        self._config = config
        self._hid = hid
        self._heartbeat_timeout = heartbeat_timeout
        self._logger = logger
        self._owner_active = False

    async def start(self) -> asyncio.Server:
        return await asyncio.start_server(
            self._handle_client,
            self._config.listen_address,
            self._config.port,
        )

    async def _handle_client(
        self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter
    ) -> None:
        if self._owner_active:
            self._logger.warning("connection rejected: output already owned")
            await _close_writer(writer)
            return

        self._owner_active = True
        self._logger.info("control session opened")
        try:
            accepted_socket = writer.get_extra_info("socket")
            if accepted_socket is not None:
                accepted_socket.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            result = await run_session(
                reader,
                writer,
                self._config.psk,
                self._hid,
                heartbeat_timeout=self._heartbeat_timeout,
            )
            self._logger.info("control session ended: %s", result.name.lower())
        except asyncio.CancelledError:
            self._logger.info("control session cancelled")
            raise
        except Exception:
            self._logger.exception("control session failed")
            try:
                self._hid.release_all()
            except OSError:
                self._logger.error("HID release failed")
            await _close_writer(writer)
        finally:
            self._owner_active = False


async def serve(config: BridgeConfig) -> None:
    with HidOutput(config.keyboard_path, config.mouse_path, config.consumer_path) as hid:
        bridge = BridgeServer(config, hid)
        server = await bridge.start()
        _LOG.info("receiver listening")
        async with server:
            await server.serve_forever()


async def _close_writer(writer: asyncio.StreamWriter) -> None:
    writer.close()
    try:
        await writer.wait_closed()
    except OSError:
        pass
