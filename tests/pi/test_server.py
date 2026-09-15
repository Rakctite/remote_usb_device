from __future__ import annotations

import asyncio
from pathlib import Path

from network_hid_bridge.auth import client_proof, derive_session_key, verify_server_proof
from network_hid_bridge.config import BridgeConfig
from network_hid_bridge.protocol import Frame, FrameCodec, MessageType, SequenceTracker
from network_hid_bridge.server import BridgeServer


PSK = bytes(range(32))


class RecordingHid:
    def __init__(self) -> None:
        self.release_count = 0

    def write_keyboard(self, report: bytes) -> None:
        pass

    def write_mouse(self, buttons: int, dx: int, dy: int, wheel: int, pan: int) -> None:
        pass

    def write_consumer(self, usage: int) -> None:
        pass

    def release_all(self) -> None:
        self.release_count += 1


def test_only_one_client_owns_hid_and_next_is_accepted_after_release() -> None:
    async def scenario() -> None:
        hid = RecordingHid()
        config = BridgeConfig(
            "127.0.0.1",
            0,
            PSK,
            Path("keyboard"),
            Path("mouse"),
            Path("consumer"),
        )
        bridge = BridgeServer(config, hid, heartbeat_timeout=0.5)
        listener = await bridge.start()
        port = listener.sockets[0].getsockname()[1]
        first_writer: asyncio.StreamWriter | None = None
        third_writer: asyncio.StreamWriter | None = None
        try:
            first_reader, first_writer = await asyncio.open_connection("127.0.0.1", port)
            await _authenticate(first_reader, first_writer, nonce_byte=0x10)

            second_reader, second_writer = await asyncio.open_connection("127.0.0.1", port)
            second_writer.write(
                FrameCodec.encode_handshake(Frame(MessageType.CLIENT_HELLO, 0, bytes([0x20]) * 32))
            )
            await second_writer.drain()
            assert await asyncio.wait_for(second_reader.read(1), 0.2) == b""
            second_writer.close()
            await second_writer.wait_closed()

            first_writer.close()
            await first_writer.wait_closed()
            first_writer = None
            await _wait_until(lambda: hid.release_count >= 1)

            third_reader, third_writer = await asyncio.open_connection("127.0.0.1", port)
            await _authenticate(third_reader, third_writer, nonce_byte=0x30)
        finally:
            for writer in (first_writer, third_writer):
                if writer is not None:
                    writer.close()
                    await writer.wait_closed()
            listener.close()
            await listener.wait_closed()

        await _wait_until(lambda: hid.release_count >= 2)

    asyncio.run(scenario())


async def _authenticate(
    reader: asyncio.StreamReader, writer: asyncio.StreamWriter, *, nonce_byte: int
) -> None:
    client_nonce = bytes([nonce_byte]) * 32
    writer.write(FrameCodec.encode_handshake(Frame(MessageType.CLIENT_HELLO, 0, client_nonce)))
    await writer.drain()
    hello = await asyncio.wait_for(
        FrameCodec.read_handshake(reader, MessageType.SERVER_HELLO), timeout=0.2
    )
    server_nonce, proof = hello.payload[:32], hello.payload[32:]
    assert verify_server_proof(proof, PSK, client_nonce, server_nonce)
    writer.write(
        FrameCodec.encode_handshake(
            Frame(MessageType.CLIENT_AUTH, 0, client_proof(PSK, client_nonce, server_nonce))
        )
    )
    session_key = derive_session_key(PSK, client_nonce, server_nonce)
    timestamp = bytes.fromhex("0102030405060708")
    writer.write(
        FrameCodec.encode_authenticated(Frame(MessageType.HEARTBEAT, 1, timestamp), session_key)
    )
    await writer.drain()
    acknowledgement = await asyncio.wait_for(
        FrameCodec.read_authenticated(reader, session_key, SequenceTracker()), timeout=0.2
    )
    assert acknowledgement == Frame(MessageType.HEARTBEAT_ACK, 1, timestamp)


async def _wait_until(predicate: object, timeout: float = 0.3) -> None:
    callback = predicate
    async with asyncio.timeout(timeout):
        while not callback():  # type: ignore[operator]
            await asyncio.sleep(0.005)
