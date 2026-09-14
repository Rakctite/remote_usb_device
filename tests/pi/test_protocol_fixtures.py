import importlib
import json
from pathlib import Path
from typing import Any


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
        assert header[0:4] == b"NHID"
        assert header[4] == 1
        assert header[5] == int(vector["type"], 16)
        assert int.from_bytes(header[6:8], "big") == 0
        assert int.from_bytes(header[8:16], "big") == vector["sequence"]
        assert int.from_bytes(header[16:20], "big") == len(payload)


def test_fixture_payload_layouts_and_invalid_cases_are_unambiguous() -> None:
    fixture = load_fixture()
    messages = {vector["name"]: bytes.fromhex(vector["payload"]) for vector in fixture["messages"]}
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
    assert invalid_tag["header"] == valid["header"]
    assert invalid_tag["payload"] == valid["payload"]
    assert invalid_tag["tag"] != valid["tag"]
    assert int.from_bytes(bytes.fromhex(valid["header"])[8:16], "big") == 1
    assert invalid_sequence["expected_sequence"] == 2
    assert invalid_sequence["actual_sequence"] == 3
    assert int.from_bytes(bytes.fromhex(invalid_sequence["header"])[8:16], "big") == 3
    declared_oversize = int.from_bytes(bytes.fromhex(oversize["header"])[16:20], "big")
    assert declared_oversize == oversize["declared_payload_length"] == 4097
    assert declared_oversize > fixture["constants"]["maximum_payload_length"]


def test_python_protocol_api_consumes_shared_vectors() -> None:
    fixture = load_fixture()
    protocol = importlib.import_module("network_hid_bridge.protocol")

    assert protocol.Frame.from_bytes(bytes.fromhex(fixture["authenticated_frames"]["valid"]["frame"]))
