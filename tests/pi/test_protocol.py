import asyncio
import hashlib
import hmac
import struct

import pytest

from network_hid_bridge.protocol import (
    Frame,
    FrameCodec,
    MessageType,
    ProtocolError,
    SequenceTracker,
)


KEY = bytes(range(32))


class ChunkReader:
    def __init__(self, chunks: list[bytes]) -> None:
        self.chunks = list(chunks)
        self.requests: list[int] = []

    async def read(self, length: int) -> bytes:
        self.requests.append(length)
        if not self.chunks:
            return b""
        chunk = self.chunks.pop(0)
        if len(chunk) > length:
            self.chunks.insert(0, chunk[length:])
            return chunk[:length]
        return chunk


def header(message_type: int, sequence: int, payload_length: int, *, magic=b"NHID", version=1, flags=0):
    return struct.pack(">4sBBHQI", magic, version, message_type, flags, sequence, payload_length)


def test_frame_is_immutable() -> None:
    frame = Frame(MessageType.RELEASE_ALL, 1, b"")
    with pytest.raises((AttributeError, TypeError)):
        frame.sequence = 2  # type: ignore[misc]


def test_handshake_round_trip_and_rejects_authenticated_type() -> None:
    frame = Frame(MessageType.CLIENT_HELLO, 0, b"c" * 32)
    assert FrameCodec.decode_handshake(FrameCodec.encode_handshake(frame)) == frame
    with pytest.raises(ProtocolError):
        FrameCodec.encode_handshake(Frame(MessageType.KEYBOARD_STATE, 0, b"\0" * 8))


def test_authenticated_round_trip() -> None:
    frame = Frame(MessageType.KEYBOARD_STATE, 1, b"\x02\0\x04\0\0\0\0\0")
    encoded = FrameCodec.encode_authenticated(frame, KEY)
    assert FrameCodec.decode_authenticated(encoded, KEY, 1) == frame


def test_authenticated_encode_matches_frozen_fixture() -> None:
    session_key = bytes.fromhex(
        "ddfd4bcf514438966c64721355bdd8b01a8a808927214daa4e3b3219b4e5d951"
    )
    frame = Frame(MessageType.KEYBOARD_STATE, 1, bytes.fromhex("0200040000000000"))
    assert FrameCodec.encode_authenticated(frame, session_key).hex() == (
        "4e484944011000000000000000000001000000080200040000000000"
        "4f8aa829f97fc236254a3d176abc6c63"
    )


@pytest.mark.parametrize(
    ("encoded", "match"),
    [
        (header(0x10, 1, 8, magic=b"FAIL") + b"\0" * 24, "magic"),
        (header(0x10, 1, 8, version=2) + b"\0" * 24, "version"),
        (header(0x10, 1, 8, flags=1) + b"\0" * 24, "flags"),
        (header(0x7F, 1, 8) + b"\0" * 24, "message type"),
        (header(0x10, 1, 7) + b"\0" * 23, "payload length"),
        (header(0x10, 1, 4097), "too large"),
    ],
)
def test_decode_rejects_invalid_headers(encoded: bytes, match: str) -> None:
    with pytest.raises(ProtocolError, match=match):
        FrameCodec.decode_authenticated(encoded, KEY, 1)


@pytest.mark.parametrize("suffix", [b"", b"\0" * 15, b"\0" * 17])
def test_decode_rejects_missing_truncated_or_extra_tag(suffix: bytes) -> None:
    encoded = header(0x14, 1, 0) + suffix
    with pytest.raises(ProtocolError, match="frame length"):
        FrameCodec.decode_authenticated(encoded, KEY, 1)


def test_decode_rejects_invalid_tag() -> None:
    encoded = bytearray(FrameCodec.encode_authenticated(Frame(MessageType.RELEASE_ALL, 1, b""), KEY))
    encoded[-1] ^= 1
    with pytest.raises(ProtocolError, match="authentication"):
        FrameCodec.decode_authenticated(bytes(encoded), KEY, 1)


@pytest.mark.parametrize("actual", [0, 1, 3])
def test_decode_rejects_repeated_reordered_or_skipped_sequence(actual: int) -> None:
    if actual == 0:
        body = header(MessageType.RELEASE_ALL, 0, 0)
        encoded = body + hmac.new(KEY, body, hashlib.sha256).digest()[:16]
    else:
        encoded = FrameCodec.encode_authenticated(Frame(MessageType.RELEASE_ALL, actual, b""), KEY)
    with pytest.raises(ProtocolError, match="sequence"):
        FrameCodec.decode_authenticated(encoded, KEY, 2)


def test_sequence_tracker_advances_exactly_and_never_on_invalid_frame() -> None:
    tracker = SequenceTracker()
    valid = FrameCodec.encode_authenticated(Frame(MessageType.RELEASE_ALL, 1, b""), KEY)
    frame = tracker.decode_authenticated(valid, KEY)
    assert frame.sequence == 1
    assert tracker.expected_sequence == 2

    invalid = bytearray(FrameCodec.encode_authenticated(Frame(MessageType.RELEASE_ALL, 2, b""), KEY))
    invalid[-1] ^= 1
    with pytest.raises(ProtocolError):
        tracker.decode_authenticated(bytes(invalid), KEY)
    assert tracker.expected_sequence == 2

    with pytest.raises(ProtocolError):
        tracker.decode_authenticated(
            FrameCodec.encode_authenticated(Frame(MessageType.RELEASE_ALL, 3, b""), KEY), KEY
        )
    assert tracker.expected_sequence == 2


def test_fragmented_reads_and_concatenated_frames() -> None:
    first = FrameCodec.encode_authenticated(Frame(MessageType.RELEASE_ALL, 1, b""), KEY)
    second = FrameCodec.encode_authenticated(Frame(MessageType.HEARTBEAT, 2, b"12345678"), KEY)
    reader = ChunkReader([first[:3], first[3:19], first[19:] + second])
    tracker = SequenceTracker()

    async def run() -> tuple[Frame, Frame]:
        return (
            await FrameCodec.read_authenticated(reader, KEY, tracker),
            await FrameCodec.read_authenticated(reader, KEY, tracker),
        )

    assert asyncio.run(run()) == (
        Frame(MessageType.RELEASE_ALL, 1, b""),
        Frame(MessageType.HEARTBEAT, 2, b"12345678"),
    )


@pytest.mark.parametrize("wire", [b"NHI", header(0x10, 1, 8) + b"123", header(0x10, 1, 8) + b"12345678short"])
def test_async_read_rejects_eof_and_truncation(wire: bytes) -> None:
    with pytest.raises(ProtocolError, match="truncated"):
        asyncio.run(FrameCodec.read_authenticated(ChunkReader([wire]), KEY, SequenceTracker()))


def test_oversize_is_rejected_before_payload_read() -> None:
    reader = ChunkReader([header(0x10, 1, 4097), b"must not be read"])
    with pytest.raises(ProtocolError, match="too large"):
        asyncio.run(FrameCodec.read_authenticated(reader, KEY, SequenceTracker()))
    assert reader.requests == [20]


def test_async_read_propagates_cancellation() -> None:
    class CancelledReader:
        async def read(self, length: int) -> bytes:
            raise asyncio.CancelledError

    with pytest.raises(asyncio.CancelledError):
        asyncio.run(FrameCodec.read_authenticated(CancelledReader(), KEY, SequenceTracker()))


def test_exhausted_sequence_rejects_before_another_stream_read() -> None:
    maximum = (1 << 64) - 1
    tracker = SequenceTracker(maximum)
    tracker.decode_authenticated(
        FrameCodec.encode_authenticated(Frame(MessageType.RELEASE_ALL, maximum, b""), KEY), KEY
    )
    reader = ChunkReader([])

    with pytest.raises(ProtocolError, match="exhausted"):
        asyncio.run(FrameCodec.read_authenticated(reader, KEY, tracker))
    assert reader.requests == []
