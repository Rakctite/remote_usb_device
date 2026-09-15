"""One authenticated, watchdog-protected HID control session."""

from __future__ import annotations

import asyncio
from collections.abc import Callable
from enum import Enum, auto
import secrets
import struct
from typing import Protocol

from .auth import AsyncWriter, authenticate_server
from .protocol import AsyncReader, Frame, FrameCodec, MessageType, ProtocolError, SequenceTracker


_MOUSE = struct.Struct(">Biihh")


class SessionEnd(Enum):
    DISCONNECTED = auto()
    TIMEOUT = auto()
    PROTOCOL_ERROR = auto()
    OUTPUT_ERROR = auto()


class SessionWriter(AsyncWriter, Protocol):
    def close(self) -> None: ...

    async def wait_closed(self) -> None: ...


class HidSink(Protocol):
    def write_keyboard(self, report: bytes) -> None: ...

    def write_mouse(self, buttons: int, dx: int, dy: int, wheel: int, pan: int) -> None: ...

    def write_consumer(self, usage: int) -> None: ...

    def release_all(self) -> None: ...


async def run_session(
    reader: AsyncReader,
    writer: SessionWriter,
    psk: bytes,
    hid: HidSink,
    *,
    heartbeat_timeout: float = 1.0,
    nonce_factory: Callable[[int], bytes] = secrets.token_bytes,
) -> SessionEnd:
    """Authenticate and process one client, releasing all HID state on every exit."""
    if heartbeat_timeout <= 0:
        raise ValueError("heartbeat_timeout must be positive")

    result = SessionEnd.DISCONNECTED
    try:
        session_key = await asyncio.wait_for(
            authenticate_server(reader, writer, psk, nonce_factory=nonce_factory),
            timeout=heartbeat_timeout,
        )
        receive = SequenceTracker()
        send_sequence = 1
        while True:
            frame = await asyncio.wait_for(
                FrameCodec.read_authenticated(reader, session_key, receive),
                timeout=heartbeat_timeout,
            )
            if frame.message_type is MessageType.HEARTBEAT:
                writer.write(
                    FrameCodec.encode_authenticated(
                        Frame(MessageType.HEARTBEAT_ACK, send_sequence, frame.payload),
                        session_key,
                    )
                )
                await writer.drain()
                if send_sequence == (1 << 64) - 1:
                    raise ProtocolError("send sequence exhausted")
                send_sequence += 1
            else:
                _dispatch_input(frame, hid)
    except TimeoutError:
        result = SessionEnd.TIMEOUT
    except ProtocolError:
        result = SessionEnd.PROTOCOL_ERROR
    except OSError:
        result = SessionEnd.OUTPUT_ERROR
    finally:
        try:
            hid.release_all()
        except OSError:
            result = SessionEnd.OUTPUT_ERROR
        writer.close()
        try:
            await writer.wait_closed()
        except OSError:
            pass
    return result


def _dispatch_input(frame: Frame, hid: HidSink) -> None:
    if frame.message_type is MessageType.KEYBOARD_STATE:
        hid.write_keyboard(frame.payload)
    elif frame.message_type is MessageType.MOUSE_STATE:
        hid.write_mouse(*_MOUSE.unpack(frame.payload))
    elif frame.message_type is MessageType.CONSUMER_STATE:
        hid.write_consumer(int.from_bytes(frame.payload, "big"))
    elif frame.message_type is MessageType.FULL_STATE:
        hid.write_keyboard(frame.payload[:8])
        hid.write_mouse(frame.payload[8], 0, 0, 0, 0)
        hid.write_consumer(int.from_bytes(frame.payload[9:11], "big"))
    elif frame.message_type is MessageType.RELEASE_ALL:
        hid.release_all()
    else:
        raise ProtocolError("client sent a server-only message")
