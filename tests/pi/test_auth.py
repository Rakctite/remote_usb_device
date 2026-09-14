import asyncio
import hashlib
import hmac

import pytest

from network_hid_bridge.auth import (
    authenticate_server,
    client_proof,
    derive_session_key,
    server_proof,
    verify_client_proof,
    verify_server_proof,
)
from network_hid_bridge.protocol import Frame, FrameCodec, MessageType, ProtocolError


PSK = bytes(range(32))
CLIENT_NONCE = bytes(range(0xA0, 0xC0))
SERVER_NONCE = bytes(range(0xC0, 0xE0))


class Reader:
    def __init__(self, data: bytes) -> None:
        self.data = data

    async def read(self, length: int) -> bytes:
        result, self.data = self.data[:length], self.data[length:]
        return result


class Writer:
    def __init__(self, *, cancel_drain: bool = False) -> None:
        self.data = bytearray()
        self.cancel_drain = cancel_drain

    def write(self, data: bytes) -> None:
        self.data.extend(data)

    async def drain(self) -> None:
        if self.cancel_drain:
            raise asyncio.CancelledError


def test_derivations_match_frozen_values_and_exact_label_nonce_order() -> None:
    assert server_proof(PSK, CLIENT_NONCE, SERVER_NONCE).hex() == "fab58c4e791978698600671a856766a87bfc6e92817773a11f8b339626370707"
    assert client_proof(PSK, CLIENT_NONCE, SERVER_NONCE).hex() == "1cc0f445f69752c72559bce07d9cd441d3bafd7f9606a34c16d29e4f79310d61"
    assert derive_session_key(PSK, CLIENT_NONCE, SERVER_NONCE).hex() == "ddfd4bcf514438966c64721355bdd8b01a8a808927214daa4e3b3219b4e5d951"
    assert server_proof(PSK, CLIENT_NONCE, SERVER_NONCE) == hmac.new(
        PSK, b"server" + CLIENT_NONCE + SERVER_NONCE, hashlib.sha256
    ).digest()
    assert server_proof(PSK, CLIENT_NONCE, SERVER_NONCE) != hmac.new(
        PSK, b"server" + SERVER_NONCE + CLIENT_NONCE, hashlib.sha256
    ).digest()


@pytest.mark.parametrize(
    ("psk", "client_nonce", "server_nonce"),
    [(b"x" * 31, CLIENT_NONCE, SERVER_NONCE), (PSK, b"x" * 31, SERVER_NONCE), (PSK, CLIENT_NONCE, b"x" * 33)],
)
def test_derivation_rejects_wrong_psk_or_nonce_lengths(psk: bytes, client_nonce: bytes, server_nonce: bytes) -> None:
    with pytest.raises(ValueError):
        server_proof(psk, client_nonce, server_nonce)


def test_proof_verifiers_accept_valid_and_reject_invalid() -> None:
    assert verify_server_proof(server_proof(PSK, CLIENT_NONCE, SERVER_NONCE), PSK, CLIENT_NONCE, SERVER_NONCE)
    assert verify_client_proof(client_proof(PSK, CLIENT_NONCE, SERVER_NONCE), PSK, CLIENT_NONCE, SERVER_NONCE)
    assert not verify_client_proof(b"x" * 32, PSK, CLIENT_NONCE, SERVER_NONCE)


def test_server_handshake_with_deterministic_nonce() -> None:
    incoming = FrameCodec.encode_handshake(Frame(MessageType.CLIENT_HELLO, 0, CLIENT_NONCE))
    incoming += FrameCodec.encode_handshake(
        Frame(MessageType.CLIENT_AUTH, 0, client_proof(PSK, CLIENT_NONCE, SERVER_NONCE))
    )
    writer = Writer()
    session_key = asyncio.run(
        authenticate_server(Reader(incoming), writer, PSK, nonce_factory=lambda length: SERVER_NONCE)
    )
    assert session_key == derive_session_key(PSK, CLIENT_NONCE, SERVER_NONCE)
    assert FrameCodec.decode_handshake(bytes(writer.data), MessageType.SERVER_HELLO) == Frame(
        MessageType.SERVER_HELLO,
        0,
        SERVER_NONCE + server_proof(PSK, CLIENT_NONCE, SERVER_NONCE),
    )


@pytest.mark.parametrize(
    "incoming",
    [
        FrameCodec.encode_handshake(Frame(MessageType.CLIENT_AUTH, 0, b"x" * 32)),
        FrameCodec.encode_handshake(Frame(MessageType.CLIENT_HELLO, 0, CLIENT_NONCE))
        + FrameCodec.encode_handshake(Frame(MessageType.CLIENT_AUTH, 0, b"x" * 32)),
    ],
)
def test_server_handshake_rejects_wrong_type_or_proof(incoming: bytes) -> None:
    with pytest.raises(ProtocolError):
        asyncio.run(authenticate_server(Reader(incoming), Writer(), PSK, nonce_factory=lambda _: SERVER_NONCE))


def test_server_handshake_rejects_nonce_factory_wrong_length() -> None:
    hello = FrameCodec.encode_handshake(Frame(MessageType.CLIENT_HELLO, 0, CLIENT_NONCE))
    with pytest.raises(ValueError, match="server nonce"):
        asyncio.run(authenticate_server(Reader(hello), Writer(), PSK, nonce_factory=lambda _: b"short"))


def test_server_handshake_propagates_cancellation() -> None:
    hello = FrameCodec.encode_handshake(Frame(MessageType.CLIENT_HELLO, 0, CLIENT_NONCE))
    with pytest.raises(asyncio.CancelledError):
        asyncio.run(
            authenticate_server(
                Reader(hello), Writer(cancel_drain=True), PSK, nonce_factory=lambda _: SERVER_NONCE
            )
        )
