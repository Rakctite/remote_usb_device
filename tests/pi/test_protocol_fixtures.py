import importlib
import inspect
import json
from pathlib import Path
from typing import Any

import pytest


FIXTURE_PATH = Path(__file__).parents[1] / "fixtures" / "protocol-v1.json"
HEX_DIGITS = frozenset("0123456789abcdef")


def load_fixture() -> dict[str, Any]:
    with FIXTURE_PATH.open(encoding="utf-8") as fixture_file:
        return json.load(fixture_file)


def assert_lowercase_hex(value: str, byte_length: int | None = None) -> None:
    assert len(value) % 2 == 0
    assert all(character in HEX_DIGITS for character in value)
    if byte_length is not None:
        assert len(value) == byte_length * 2


def test_fixture_schema_and_literal_hex_are_valid() -> None:
    fixture = load_fixture()
    constants = fixture["constants"]

    assert fixture["contract"] == "network-hid-bridge/protocol-v1"
    assert fixture["encoding"] == "lowercase-hex"
    assert constants["header_length"] == 20
    assert constants["tag_length"] == 16
    assert constants["maximum_payload_length"] == 4096
    assert constants["magic"] == "4e484944"
    assert constants["version"] == "01"
    assert constants["flags"] == "0000"
    for name in ("psk", "client_nonce", "server_nonce", "server_proof", "client_proof", "session_key"):
        assert_lowercase_hex(constants[name], 32)

    handshake_lengths = {"01": 32, "02": 64, "03": 32}
    for vector in fixture["handshake"]:
        assert_lowercase_hex(vector["type"], 1)
        assert_lowercase_hex(vector["header"], 20)
        assert_lowercase_hex(vector["payload"])
        assert_lowercase_hex(vector["frame"], 20 + len(vector["payload"]) // 2)
        assert vector["sequence"] == 0
        assert len(vector["payload"]) // 2 == handshake_lengths[vector["type"]]
        assert vector["frame"] == vector["header"] + vector["payload"]

    assert {vector["type"] for vector in fixture["messages"]} == {
        "10", "11", "12", "13", "14", "20", "21"
    }
    message_lengths = {"10": 8, "11": 13, "12": 2, "13": 11, "14": 0, "20": 8, "21": 8}
    for vector in fixture["messages"]:
        assert_lowercase_hex(vector["header"], 20)
        assert vector["payload_length"] == message_lengths[vector["type"]]
        assert_lowercase_hex(vector["payload"], vector["payload_length"])

    for name in ("valid", "invalid_tag", "invalid_sequence"):
        vector = fixture["authenticated_frames"][name]
        assert_lowercase_hex(vector["header"], 20)
        assert_lowercase_hex(vector["tag"], 16)
        assert vector["frame"] == vector["header"] + vector["payload"] + vector["tag"]
        assert_lowercase_hex(vector["frame"])
    assert_lowercase_hex(fixture["authenticated_frames"]["oversize"]["header"], 20)


def test_fixture_headers_encode_declared_fields() -> None:
    fixture = load_fixture()
    vectors = fixture["handshake"] + fixture["messages"]

    for vector in vectors:
        header = bytes.fromhex(vector["header"])
        payload = bytes.fromhex(vector["payload"])
        name = vector["name"]
        assert header[0:4] == b"NHID", f"{name}: magic"
        assert header[4] == 1, f"{name}: version"
        assert header[5] == int(vector["type"], 16), f"{name}: message type"
        assert int.from_bytes(header[6:8], "big") == 0, f"{name}: flags"
        assert int.from_bytes(header[8:16], "big") == vector["sequence"], f"{name}: sequence"
        assert int.from_bytes(header[16:20], "big") == len(payload), f"{name}: payload length"


def test_fixture_payload_layouts_and_invalid_cases_are_unambiguous() -> None:
    fixture = load_fixture()
    message_vectors = {vector["name"]: vector for vector in fixture["messages"]}
    messages = {name: bytes.fromhex(vector["payload"]) for name, vector in message_vectors.items()}
    mouse = messages["mouse_state"]
    full = messages["full_state"]

    assert mouse[0] == 5
    assert int.from_bytes(mouse[1:5], "big", signed=True) == -123456
    assert int.from_bytes(mouse[5:9], "big", signed=True) == 654321
    assert int.from_bytes(mouse[9:11], "big", signed=True) == -120
    assert int.from_bytes(mouse[11:13], "big", signed=True) == 120
    assert full[0:8] == messages["keyboard_state"]
    assert full[8] == mouse[0]
    assert full[9:11] == messages["consumer_state"]
    assert messages["release_all"] == b""
    assert messages["heartbeat_ack"] == messages["heartbeat"]

    authenticated = fixture["authenticated_frames"]
    valid = authenticated["valid"]
    invalid_tag = authenticated["invalid_tag"]
    invalid_sequence = authenticated["invalid_sequence"]
    oversize = authenticated["oversize"]
    handshake = {vector["name"]: vector for vector in fixture["handshake"]}
    constants = fixture["constants"]
    assert handshake["client_hello"]["payload"] == constants["client_nonce"], "client_hello.payload"
    assert handshake["server_hello"]["payload"] == constants["server_nonce"] + constants["server_proof"], (
        "server_hello.payload"
    )
    assert handshake["client_auth"]["payload"] == constants["client_proof"], "client_auth.payload"

    referenced_message = message_vectors[valid["message"]]
    assert valid["header"] == referenced_message["header"], "authenticated_frames.valid.header"
    assert valid["payload"] == referenced_message["payload"], "authenticated_frames.valid.payload"
    assert valid["tag"] == "4f8aa829f97fc236254a3d176abc6c63", "authenticated_frames.valid.tag"
    assert valid["frame"] == valid["header"] + valid["payload"] + valid["tag"], (
        "authenticated_frames.valid.frame"
    )
    assert invalid_tag["expected_error"] == "authentication", "authenticated_frames.invalid_tag.expected_error"
    assert invalid_tag["message"] == valid["message"], "authenticated_frames.invalid_tag.message"
    assert invalid_tag["header"] == valid["header"], "authenticated_frames.invalid_tag.header"
    assert invalid_tag["payload"] == valid["payload"], "authenticated_frames.invalid_tag.payload"
    assert invalid_tag["tag"] == "0f8aa829f97fc236254a3d176abc6c63", "authenticated_frames.invalid_tag.tag"
    assert bytes.fromhex(invalid_tag["frame"])[:-16] == bytes.fromhex(valid["frame"])[:-16], (
        "authenticated_frames.invalid_tag.frame body"
    )
    assert bytes.fromhex(invalid_tag["frame"])[-16:] != bytes.fromhex(valid["frame"])[-16:], (
        "authenticated_frames.invalid_tag.frame tag"
    )
    assert int.from_bytes(bytes.fromhex(valid["header"])[8:16], "big") == 1
    assert invalid_sequence["expected_error"] == "sequence", "authenticated_frames.invalid_sequence.expected_error"
    assert invalid_sequence["message"] == valid["message"], "authenticated_frames.invalid_sequence.message"
    assert invalid_sequence["expected_sequence"] == 2
    assert invalid_sequence["actual_sequence"] == 3
    valid_header = bytes.fromhex(valid["header"])
    invalid_sequence_header = bytes.fromhex(invalid_sequence["header"])
    assert invalid_sequence_header[:8] == valid_header[:8], "authenticated_frames.invalid_sequence.header prefix"
    assert invalid_sequence_header[16:] == valid_header[16:], "authenticated_frames.invalid_sequence.header length"
    assert int.from_bytes(invalid_sequence_header[8:16], "big") == 3
    assert invalid_sequence["payload"] == valid["payload"], "authenticated_frames.invalid_sequence.payload"
    assert invalid_sequence["tag"] == "b8042bd2e9aa2886ae840a83476687e2", (
        "authenticated_frames.invalid_sequence.tag"
    )
    oversize_header = bytes.fromhex(oversize["header"])
    assert oversize["expected_error"] == "payload-too-large", "authenticated_frames.oversize.expected_error"
    assert oversize["message"] == valid["message"], "authenticated_frames.oversize.message"
    assert oversize_header[:8] == valid_header[:8], "authenticated_frames.oversize.header prefix"
    assert int.from_bytes(oversize_header[8:16], "big") == 2, "authenticated_frames.oversize.header sequence"
    declared_oversize = int.from_bytes(oversize_header[16:20], "big")
    assert declared_oversize == oversize["declared_payload_length"], "authenticated_frames.oversize.header length"
    assert declared_oversize == constants["maximum_payload_length"] + 1, (
        "authenticated_frames.oversize.declared_payload_length"
    )


def test_python_frame_codec_decode_authenticated_consumes_shared_vector() -> None:
    fixture = load_fixture()
    try:
        protocol = importlib.import_module("network_hid_bridge.protocol")
    except ModuleNotFoundError as error:
        raise AssertionError(
            "Missing planned API network_hid_bridge.protocol.FrameCodec.decode_authenticated"
        ) from error
    assert hasattr(protocol, "FrameCodec"), (
        "Missing planned API network_hid_bridge.protocol.FrameCodec.decode_authenticated"
    )
    descriptor = vars(protocol.FrameCodec).get("decode_authenticated")
    assert isinstance(descriptor, staticmethod), (
        "FrameCodec.decode_authenticated must be a static method"
    )
    decoder = descriptor.__func__
    signature = inspect.signature(decoder)
    assert list(signature.parameters) == ["encoded", "session_key", "expected_sequence"], (
        "FrameCodec.decode_authenticated must accept exactly encoded, session_key, expected_sequence"
    )
    assert all(
        parameter.kind is inspect.Parameter.POSITIONAL_OR_KEYWORD
        for parameter in signature.parameters.values()
    ), "FrameCodec.decode_authenticated parameters must be positional or keyword"
    valid = fixture["authenticated_frames"]["valid"]

    frame = decoder(
        encoded=bytes.fromhex(valid["frame"]),
        session_key=bytes.fromhex(fixture["constants"]["session_key"]),
        expected_sequence=1,
    )
    assert isinstance(frame, protocol.Frame), "FrameCodec.decode_authenticated must return Frame"


def test_python_frame_codec_consumes_all_frozen_handshake_vectors() -> None:
    fixture = load_fixture()
    protocol = importlib.import_module("network_hid_bridge.protocol")

    for vector in fixture["handshake"]:
        encoded = bytes.fromhex(vector["frame"])
        frame = protocol.FrameCodec.decode_handshake(encoded)
        assert frame.message_type == protocol.MessageType(int(vector["type"], 16))
        assert frame.sequence == vector["sequence"]
        assert frame.payload == bytes.fromhex(vector["payload"])
        assert protocol.FrameCodec.encode_handshake(frame) == encoded


def test_python_frame_codec_rejects_frozen_invalid_authenticated_vectors() -> None:
    fixture = load_fixture()
    protocol = importlib.import_module("network_hid_bridge.protocol")
    authenticated = fixture["authenticated_frames"]
    session_key = bytes.fromhex(fixture["constants"]["session_key"])

    with pytest.raises(protocol.ProtocolError, match="authentication"):
        protocol.FrameCodec.decode_authenticated(
            bytes.fromhex(authenticated["invalid_tag"]["frame"]), session_key, 1
        )
    with pytest.raises(protocol.ProtocolError, match="sequence"):
        protocol.FrameCodec.decode_authenticated(
            bytes.fromhex(authenticated["invalid_sequence"]["frame"]),
            session_key,
            authenticated["invalid_sequence"]["expected_sequence"],
        )
    with pytest.raises(protocol.ProtocolError, match="too large"):
        protocol.FrameCodec.decode_authenticated(
            bytes.fromhex(authenticated["oversize"]["header"]), session_key, 2
        )
