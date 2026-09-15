from __future__ import annotations

from pathlib import Path

import pytest

from network_hid_bridge.hid import HidOutput, HidOutputError


@pytest.fixture
def hid_paths(tmp_path: Path) -> tuple[Path, Path, Path]:
    paths = (tmp_path / "keyboard", tmp_path / "mouse", tmp_path / "consumer")
    for path in paths:
        path.touch()
    return paths


def test_writes_fixed_keyboard_mouse_and_consumer_reports(
    hid_paths: tuple[Path, Path, Path],
) -> None:
    keyboard_path, mouse_path, consumer_path = hid_paths
    output = HidOutput(keyboard_path, mouse_path, consumer_path)

    output.open()
    output.write_keyboard(bytes.fromhex("0200040000000000"))
    output.write_mouse(0x05, 12, -7, 1, -1)
    output.write_consumer(0x00E9)
    output.close(release=False)

    assert keyboard_path.read_bytes() == bytes.fromhex("0200040000000000")
    assert mouse_path.read_bytes() == bytes((0x05, 12, 249, 1))
    assert consumer_path.read_bytes() == bytes.fromhex("0000ffe90000")


@pytest.mark.parametrize("value", [300, -300, 32767, -32768])
@pytest.mark.parametrize("axis_offset", [1, 2, 3])
def test_large_relative_values_are_split_without_losing_motion(
    hid_paths: tuple[Path, Path, Path], value: int, axis_offset: int
) -> None:
    _, mouse_path, _ = hid_paths
    axes = [0, 0, 0, 0]
    axes[axis_offset - 1] = value

    output = HidOutput(*hid_paths)
    output.open()
    output.write_mouse(0x03, *axes)
    output.close(release=False)

    reports = _reports(mouse_path.read_bytes(), 4)
    emitted = [int.from_bytes(report[axis_offset : axis_offset + 1], "big", signed=True) for report in reports]
    assert all(-127 <= part <= 127 for part in emitted)
    assert sum(emitted) == value
    assert all(report[0] == 0x03 for report in reports)


def test_release_all_and_context_shutdown_emit_empty_reports(
    hid_paths: tuple[Path, Path, Path],
) -> None:
    keyboard_path, mouse_path, consumer_path = hid_paths

    with HidOutput(*hid_paths) as output:
        output.write_keyboard(bytes.fromhex("0100050000000000"))
        output.write_mouse(1, 2, 3, 4, 5)
        output.write_consumer(0x00EA)
        output.release_all()

    assert _reports(keyboard_path.read_bytes(), 8)[-2:] == [bytes(8), bytes(8)]
    assert _reports(mouse_path.read_bytes(), 4)[-2:] == [bytes(4), bytes(4)]
    assert _reports(consumer_path.read_bytes(), 3)[-2:] == [bytes(3), bytes(3)]


def test_large_pan_is_split_on_consumer_hid_and_preserves_pressed_usage(
    hid_paths: tuple[Path, Path, Path],
) -> None:
    _, _, consumer_path = hid_paths

    output = HidOutput(*hid_paths)
    output.open()
    output.write_consumer(0x00E9)
    output.write_mouse(0, 0, 0, 0, 300)
    output.close(release=False)

    reports = _reports(consumer_path.read_bytes(), 3)
    assert reports[0] == bytes.fromhex("e90000")
    assert all(report[:2] == bytes.fromhex("e900") for report in reports[1:])
    assert sum(int.from_bytes(report[2:3], "big", signed=True) for report in reports[1:]) == 300


@pytest.mark.parametrize(
    ("method", "argument"),
    [
        ("write_keyboard", bytes(7)),
        ("write_keyboard", bytes(9)),
        ("write_mouse", (0x20, 0, 0, 0, 0)),
        ("write_consumer", 0x1_0000),
    ],
)
def test_rejects_invalid_report_values(
    hid_paths: tuple[Path, Path, Path], method: str, argument: object
) -> None:
    with HidOutput(*hid_paths) as output:
        with pytest.raises(ValueError):
            if isinstance(argument, tuple):
                getattr(output, method)(*argument)
            else:
                getattr(output, method)(argument)


def test_missing_device_node_is_a_fatal_output_error(tmp_path: Path) -> None:
    existing = tmp_path / "existing"
    existing.touch()

    with pytest.raises(HidOutputError):
        HidOutput(existing, tmp_path / "missing", existing).open()


def test_short_write_is_a_fatal_output_error(hid_paths: tuple[Path, Path, Path]) -> None:
    class ShortWriter:
        def write(self, data: bytes) -> int:
            return len(data) - 1

        def close(self) -> None:
            pass

    output = HidOutput(*hid_paths, opener=lambda _: ShortWriter())
    output.open()

    with pytest.raises(HidOutputError, match="short HID write"):
        output.write_keyboard(bytes(8))

    output.close(release=False)


def _reports(data: bytes, size: int) -> list[bytes]:
    assert len(data) % size == 0
    return [data[offset : offset + size] for offset in range(0, len(data), size)]
