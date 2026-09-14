"""Authentication transcript operations for Network HID protocol v1."""

from __future__ import annotations

import hashlib
import hmac
import secrets
from collections.abc import Callable
from typing import Protocol

from .protocol import AsyncReader, Frame, FrameCodec, MessageType, ProtocolError


_VALUE_LENGTH = 32


class AsyncWriter(Protocol):
    def write(self, data: bytes) -> None: ...

    async def drain(self) -> None: ...


def _validate_inputs(psk: bytes, client_nonce: bytes, server_nonce: bytes) -> None:
    if not isinstance(psk, bytes) or len(psk) != _VALUE_LENGTH:
        raise ValueError("PSK must be exactly 32 bytes")
    if not isinstance(client_nonce, bytes) or len(client_nonce) != _VALUE_LENGTH:
        raise ValueError("client nonce must be exactly 32 bytes")
    if not isinstance(server_nonce, bytes) or len(server_nonce) != _VALUE_LENGTH:
        raise ValueError("server nonce must be exactly 32 bytes")


def _derive(label: bytes, psk: bytes, client_nonce: bytes, server_nonce: bytes) -> bytes:
    _validate_inputs(psk, client_nonce, server_nonce)
    return hmac.new(psk, label + client_nonce + server_nonce, hashlib.sha256).digest()


def server_proof(psk: bytes, client_nonce: bytes, server_nonce: bytes) -> bytes:
    return _derive(b"server", psk, client_nonce, server_nonce)


def client_proof(psk: bytes, client_nonce: bytes, server_nonce: bytes) -> bytes:
    return _derive(b"client", psk, client_nonce, server_nonce)


def derive_session_key(psk: bytes, client_nonce: bytes, server_nonce: bytes) -> bytes:
    return _derive(b"session", psk, client_nonce, server_nonce)


def verify_server_proof(
    proof: bytes, psk: bytes, client_nonce: bytes, server_nonce: bytes
) -> bool:
    return hmac.compare_digest(proof, server_proof(psk, client_nonce, server_nonce))


def verify_client_proof(
    proof: bytes, psk: bytes, client_nonce: bytes, server_nonce: bytes
) -> bool:
    return hmac.compare_digest(proof, client_proof(psk, client_nonce, server_nonce))


async def authenticate_server(
    reader: AsyncReader,
    writer: AsyncWriter,
    psk: bytes,
    nonce_factory: Callable[[int], bytes] = secrets.token_bytes,
) -> bytes:
    """Run the server side of the v1 handshake and return its session key."""
    if not isinstance(psk, bytes) or len(psk) != _VALUE_LENGTH:
        raise ValueError("PSK must be exactly 32 bytes")

    hello = await FrameCodec.read_handshake(reader, MessageType.CLIENT_HELLO)
    client_nonce = hello.payload
    server_nonce = nonce_factory(_VALUE_LENGTH)
    if not isinstance(server_nonce, bytes) or len(server_nonce) != _VALUE_LENGTH:
        raise ValueError("server nonce must be exactly 32 bytes")

    response = Frame(
        MessageType.SERVER_HELLO,
        0,
        server_nonce + server_proof(psk, client_nonce, server_nonce),
    )
    writer.write(FrameCodec.encode_handshake(response))
    await writer.drain()

    auth = await FrameCodec.read_handshake(reader, MessageType.CLIENT_AUTH)
    if not verify_client_proof(auth.payload, psk, client_nonce, server_nonce):
        raise ProtocolError("client authentication proof mismatch")
    return derive_session_key(psk, client_nonce, server_nonce)
