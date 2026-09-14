"""Binary framing and sequence validation for Network HID protocol v1."""

from __future__ import annotations

from dataclasses import dataclass
from enum import IntEnum
import hashlib
import hmac
import struct
from typing import Protocol


MAGIC = b"NHID"
VERSION = 1
HEADER_LENGTH = 20
TAG_LENGTH = 16
MAXIMUM_PAYLOAD_LENGTH = 4096
_HEADER = struct.Struct(">4sBBHQI")
_MAX_SEQUENCE = (1 << 64) - 1


class MessageType(IntEnum):
    CLIENT_HELLO = 0x01
    SERVER_HELLO = 0x02
    CLIENT_AUTH = 0x03
    KEYBOARD_STATE = 0x10
    MOUSE_STATE = 0x11
    CONSUMER_STATE = 0x12
    FULL_STATE = 0x13
    RELEASE_ALL = 0x14
    HEARTBEAT = 0x20
    HEARTBEAT_ACK = 0x21


_PAYLOAD_LENGTHS = {
    MessageType.CLIENT_HELLO: 32,
    MessageType.SERVER_HELLO: 64,
    MessageType.CLIENT_AUTH: 32,
    MessageType.KEYBOARD_STATE: 8,
    MessageType.MOUSE_STATE: 13,
    MessageType.CONSUMER_STATE: 2,
    MessageType.FULL_STATE: 11,
    MessageType.RELEASE_ALL: 0,
    MessageType.HEARTBEAT: 8,
    MessageType.HEARTBEAT_ACK: 8,
}
_HANDSHAKE_TYPES = frozenset(
    (MessageType.CLIENT_HELLO, MessageType.SERVER_HELLO, MessageType.CLIENT_AUTH)
)
_AUTHENTICATED_TYPES = frozenset(MessageType) - _HANDSHAKE_TYPES


class ProtocolError(ValueError):
    """A fatal protocol-v1 framing, authentication, or sequence error."""


@dataclass(frozen=True, slots=True)
class Frame:
    message_type: MessageType
    sequence: int
    payload: bytes


class AsyncReader(Protocol):
    async def read(self, length: int) -> bytes: ...


async def read_exactly(reader: AsyncReader, length: int) -> bytes:
    """Read exactly *length* bytes, converting clean or partial EOF to a protocol error."""
    chunks: list[bytes] = []
    remaining = length
    while remaining:
        chunk = await reader.read(remaining)
        if not chunk:
            raise ProtocolError("truncated frame")
        if len(chunk) > remaining:
            raise ProtocolError("reader returned more bytes than requested")
        chunks.append(chunk)
        remaining -= len(chunk)
    return b"".join(chunks)


def _validated_header(encoded: bytes) -> tuple[MessageType, int, int]:
    if len(encoded) != HEADER_LENGTH:
        raise ProtocolError("invalid header length")
    magic, version, raw_type, flags, sequence, payload_length = _HEADER.unpack(encoded)
    if magic != MAGIC:
        raise ProtocolError("invalid magic")
    if version != VERSION:
        raise ProtocolError("unsupported version")
    if flags != 0:
        raise ProtocolError("nonzero flags")
    try:
        message_type = MessageType(raw_type)
    except ValueError as error:
        raise ProtocolError("unknown message type") from error
    if payload_length > MAXIMUM_PAYLOAD_LENGTH:
        raise ProtocolError("payload too large")
    if payload_length != _PAYLOAD_LENGTHS[message_type]:
        raise ProtocolError("invalid payload length for message type")
    return message_type, sequence, payload_length


def _encode_header(frame: Frame, allowed_types: frozenset[MessageType]) -> bytes:
    try:
        message_type = MessageType(frame.message_type)
    except ValueError as error:
        raise ProtocolError("unknown message type") from error
    if message_type not in allowed_types:
        raise ProtocolError("message type is invalid in this protocol phase")
    if not isinstance(frame.payload, bytes):
        raise TypeError("payload must be bytes")
    payload_length = len(frame.payload)
    if payload_length > MAXIMUM_PAYLOAD_LENGTH:
        raise ProtocolError("payload too large")
    if payload_length != _PAYLOAD_LENGTHS[message_type]:
        raise ProtocolError("invalid payload length for message type")
    if not 0 <= frame.sequence <= _MAX_SEQUENCE:
        raise ProtocolError("sequence is outside uint64 range")
    return _HEADER.pack(MAGIC, VERSION, int(message_type), 0, frame.sequence, payload_length)


def _validate_key(session_key: bytes) -> None:
    if not isinstance(session_key, bytes) or len(session_key) != 32:
        raise ValueError("session key must be exactly 32 bytes")


class FrameCodec:
    """Stateless wire encoder/decoder for handshake and authenticated frames."""

    @staticmethod
    def encode_handshake(frame: Frame) -> bytes:
        if frame.sequence != 0:
            raise ProtocolError("handshake sequence must be zero")
        return _encode_header(frame, _HANDSHAKE_TYPES) + frame.payload

    @staticmethod
    def decode_handshake(encoded: bytes, expected_type: MessageType | None = None) -> Frame:
        if len(encoded) < HEADER_LENGTH:
            raise ProtocolError("truncated frame")
        message_type, sequence, payload_length = _validated_header(encoded[:HEADER_LENGTH])
        if message_type not in _HANDSHAKE_TYPES:
            raise ProtocolError("message type is invalid during handshake")
        if expected_type is not None and message_type != expected_type:
            raise ProtocolError("unexpected handshake message type")
        if sequence != 0:
            raise ProtocolError("handshake sequence must be zero")
        expected_length = HEADER_LENGTH + payload_length
        if len(encoded) != expected_length:
            raise ProtocolError("invalid handshake frame length")
        return Frame(message_type, sequence, encoded[HEADER_LENGTH:])

    @staticmethod
    async def read_handshake(
        reader: AsyncReader, expected_type: MessageType | None = None
    ) -> Frame:
        encoded_header = await read_exactly(reader, HEADER_LENGTH)
        message_type, sequence, payload_length = _validated_header(encoded_header)
        if message_type not in _HANDSHAKE_TYPES:
            raise ProtocolError("message type is invalid during handshake")
        if expected_type is not None and message_type != expected_type:
            raise ProtocolError("unexpected handshake message type")
        if sequence != 0:
            raise ProtocolError("handshake sequence must be zero")
        payload = await read_exactly(reader, payload_length)
        return Frame(message_type, sequence, payload)

    @staticmethod
    def encode_authenticated(frame: Frame, session_key: bytes) -> bytes:
        _validate_key(session_key)
        if frame.sequence == 0:
            raise ProtocolError("authenticated sequence must be nonzero")
        encoded = _encode_header(frame, _AUTHENTICATED_TYPES) + frame.payload
        tag = hmac.new(session_key, encoded, hashlib.sha256).digest()[:TAG_LENGTH]
        return encoded + tag

    @staticmethod
    def decode_authenticated(encoded: bytes, session_key: bytes, expected_sequence: int) -> Frame:
        _validate_key(session_key)
        if len(encoded) < HEADER_LENGTH:
            raise ProtocolError("truncated frame")
        message_type, sequence, payload_length = _validated_header(encoded[:HEADER_LENGTH])
        if message_type not in _AUTHENTICATED_TYPES:
            raise ProtocolError("message type is invalid after authentication")
        expected_length = HEADER_LENGTH + payload_length + TAG_LENGTH
        if len(encoded) != expected_length:
            raise ProtocolError("invalid authenticated frame length")
        if expected_sequence == 0 or not 0 < expected_sequence <= _MAX_SEQUENCE:
            raise ProtocolError("expected sequence is outside authenticated range")
        if sequence != expected_sequence:
            raise ProtocolError("unexpected sequence")
        body = encoded[:-TAG_LENGTH]
        received_tag = encoded[-TAG_LENGTH:]
        expected_tag = hmac.new(session_key, body, hashlib.sha256).digest()[:TAG_LENGTH]
        if not hmac.compare_digest(received_tag, expected_tag):
            raise ProtocolError("authentication tag mismatch")
        return Frame(message_type, sequence, encoded[HEADER_LENGTH:-TAG_LENGTH])

    @staticmethod
    async def read_authenticated(
        reader: AsyncReader, session_key: bytes, tracker: SequenceTracker
    ) -> Frame:
        tracker.expected_sequence
        encoded_header = await read_exactly(reader, HEADER_LENGTH)
        message_type, _, payload_length = _validated_header(encoded_header)
        if message_type not in _AUTHENTICATED_TYPES:
            raise ProtocolError("message type is invalid after authentication")
        payload = await read_exactly(reader, payload_length)
        tag = await read_exactly(reader, TAG_LENGTH)
        return tracker.decode_authenticated(encoded_header + payload + tag, session_key)


class SequenceTracker:
    """Tracks one direction's next authenticated sequence number."""

    __slots__ = ("_expected_sequence",)

    def __init__(self, expected_sequence: int = 1) -> None:
        if not 0 < expected_sequence <= _MAX_SEQUENCE:
            raise ValueError("expected sequence must be between 1 and 2^64-1")
        self._expected_sequence: int | None = expected_sequence

    @property
    def expected_sequence(self) -> int:
        if self._expected_sequence is None:
            raise ProtocolError("sequence space exhausted")
        return self._expected_sequence

    def decode_authenticated(self, encoded: bytes, session_key: bytes) -> Frame:
        expected = self.expected_sequence
        frame = FrameCodec.decode_authenticated(encoded, session_key, expected)
        self._expected_sequence = None if expected == _MAX_SEQUENCE else expected + 1
        return frame
