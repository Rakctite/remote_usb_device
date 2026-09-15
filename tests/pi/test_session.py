from __future__ import annotations

import asyncio
from collections.abc import Iterable

import pytest

from network_hid_bridge.auth import client_proof, derive_session_key
from network_hid_bridge.protocol import Frame, FrameCodec, MessageType, ProtocolError
from network_hid_bridge.session import SessionEnd, run_session


PSK = bytes(range(32))
CLIENT_NONCE = bytes(range(0x20, 0x40))
SERVER_NONCE = bytes(range(0x80, 0xA0))
SESSION_KEY = derive_session_key(PSK, CLIENT_NONCE, SERVER_NONCE)


class Reader:
    def __init__(self, data: bytes, *, block_at_end: bool = False) -> None:
        self.data = data
        self.block_at_end = block_at_end

    async def read(self, length: int) -> bytes:
        if self.data:
            result, self.data = self.data[:length], self.data[length:]
            return result
        if self.block_at_end:
            await asyncio.Future()
        return b""


class Writer:
    def __init__(self) -> None:
        self.data = bytearray()
        self.closed = False

    def write(self, data: bytes) -> None:
        self.data.extend(data)

    async def drain(self) -> None:
        pass

    def close(self) -> None:
        self.closed = True

    async def wait_closed(self) -> None:
        pass


class RecordingHid:
    def __init__(self) -> None:
        self.keyboard: list[bytes] = []
        self.mouse: list[tuple[int, int, int, int, int]] = []
        self.consumer: list[int] = []

    def write_keyboard(self, report: bytes) -> None:
        self.keyboard.append(report)

    def write_mouse(self, buttons: int, dx: int, dy: int, wheel: int, pan: int) -> None:
        self.mouse.append((buttons, dx, dy, wheel, pan))

    def write_consumer(self, usage: int) -> None:
        self.consumer.append(usage)

    def release_all(self) -> None:
        self.keyboard.append(bytes(8))
        self.mouse.append((0, 0, 0, 0, 0))
        self.consumer.append(0)


def test_dispatches_all_input_messages_and_full_state() -> None:
    keyboard = bytes.fromhex("0200040000000000")
    full_keyboard = bytes.fromhex("0100050000000000")
    incoming = _client_stream(
        [
            (MessageType.KEYBOARD_STATE, keyboard),
            (MessageType.MOUSE_STATE, bytes.fromhex("05ffffff9c000000c80003fffc")),
            (MessageType.CONSUMER_STATE, bytes.fromhex("00e9")),
            (MessageType.FULL_STATE, full_keyboard + bytes.fromhex("0300ea")),
            (MessageType.RELEASE_ALL, b""),
        ]
    )
    hid = RecordingHid()

    result = asyncio.run(_run(Reader(incoming), Writer(), hid))

    assert result is SessionEnd.PROTOCOL_ERROR
    assert keyboard in hid.keyboard
    assert (5, -100, 200, 3, -4) in hid.mouse
    assert 0x00E9 in hid.consumer
    assert full_keyboard in hid.keyboard
    assert (3, 0, 0, 0, 0) in hid.mouse
    assert 0x00EA in hid.consumer
    _assert_released(hid)


def test_heartbeat_is_acknowledged_with_same_timestamp() -> None:
    timestamp = bytes.fromhex("0102030405060708")
    writer = Writer()
    hid = RecordingHid()

    asyncio.run(_run(Reader(_client_stream([(MessageType.HEARTBEAT, timestamp)])), writer, hid))

    server_hello_length = 20 + 64
    ack = FrameCodec.decode_authenticated(bytes(writer.data[server_hello_length:]), SESSION_KEY, 1)
    assert ack == Frame(MessageType.HEARTBEAT_ACK, 1, timestamp)
    assert writer.closed
    _assert_released(hid)


def test_timeout_releases_every_hid_state() -> None:
    hid = RecordingHid()

    result = asyncio.run(
        _run(Reader(_client_stream([]), block_at_end=True), Writer(), hid, timeout=0.02)
    )

    assert result is SessionEnd.TIMEOUT
    _assert_released(hid)


def test_unauthenticated_connection_cannot_hold_output_owner_forever() -> None:
    hid = RecordingHid()

    async def scenario() -> SessionEnd:
        return await asyncio.wait_for(
            _run(Reader(b"", block_at_end=True), Writer(), hid, timeout=0.02),
            timeout=0.2,
        )

    result = asyncio.run(scenario())

    assert result is SessionEnd.TIMEOUT
    _assert_released(hid)


def test_invalid_hmac_after_input_releases_every_hid_state() -> None:
    valid = FrameCodec.encode_authenticated(
        Frame(MessageType.KEYBOARD_STATE, 1, bytes.fromhex("0200040000000000")), SESSION_KEY
    )
    invalid = bytearray(
        FrameCodec.encode_authenticated(Frame(MessageType.CONSUMER_STATE, 2, b"\x00\xe9"), SESSION_KEY)
    )
    invalid[-1] ^= 0xFF
    hid = RecordingHid()

    result = asyncio.run(_run(Reader(_handshake() + valid + invalid), Writer(), hid))

    assert result is SessionEnd.PROTOCOL_ERROR
    assert bytes.fromhex("0200040000000000") in hid.keyboard
    _assert_released(hid)


def test_malformed_state_length_releases_every_hid_state() -> None:
    malformed_header = bytes.fromhex("4e48494401100000000000000000000100000007")
    hid = RecordingHid()

    result = asyncio.run(_run(Reader(_handshake() + malformed_header), Writer(), hid))

    assert result is SessionEnd.PROTOCOL_ERROR
    _assert_released(hid)


def test_cancellation_releases_every_hid_state_and_propagates() -> None:
    hid = RecordingHid()

    async def scenario() -> None:
        task = asyncio.create_task(
            _run(Reader(_client_stream([]), block_at_end=True), Writer(), hid)
        )
        await asyncio.sleep(0.01)
        task.cancel()
        with pytest.raises(asyncio.CancelledError):
            await task

    asyncio.run(scenario())
    _assert_released(hid)


def test_unexpected_server_only_message_is_a_protocol_error() -> None:
    hid = RecordingHid()
    incoming = _client_stream([(MessageType.HEARTBEAT_ACK, bytes(8))])

    result = asyncio.run(_run(Reader(incoming), Writer(), hid))

    assert result is SessionEnd.PROTOCOL_ERROR
    _assert_released(hid)


async def _run(
    reader: Reader, writer: Writer, hid: RecordingHid, timeout: float = 1.0
) -> SessionEnd:
    return await run_session(
        reader,
        writer,
        PSK,
        hid,
        heartbeat_timeout=timeout,
        nonce_factory=lambda _: SERVER_NONCE,
    )


def _handshake() -> bytes:
    return FrameCodec.encode_handshake(Frame(MessageType.CLIENT_HELLO, 0, CLIENT_NONCE)) + FrameCodec.encode_handshake(
        Frame(MessageType.CLIENT_AUTH, 0, client_proof(PSK, CLIENT_NONCE, SERVER_NONCE))
    )


def _client_stream(messages: Iterable[tuple[MessageType, bytes]]) -> bytes:
    encoded = bytearray(_handshake())
    for sequence, (message_type, payload) in enumerate(messages, start=1):
        encoded.extend(FrameCodec.encode_authenticated(Frame(message_type, sequence, payload), SESSION_KEY))
    return bytes(encoded)


def _assert_released(hid: RecordingHid) -> None:
    assert hid.keyboard[-1] == bytes(8)
    assert hid.mouse[-1] == (0, 0, 0, 0, 0)
    assert hid.consumer[-1] == 0
