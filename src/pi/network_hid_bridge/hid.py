"""Synchronous, report-sized writes to the three Linux HID Gadget nodes."""

from __future__ import annotations

from collections.abc import Callable
from pathlib import Path
from threading import RLock
from typing import BinaryIO, Protocol


EMPTY_KEYBOARD_REPORT = bytes(8)
EMPTY_MOUSE_REPORT = bytes(5)
EMPTY_CONSUMER_REPORT = bytes(2)


class HidOutputError(OSError):
    """A fatal HID node open or write failure."""


class _Writer(Protocol):
    def write(self, data: bytes) -> int: ...

    def close(self) -> None: ...


def _open_device(path: Path) -> BinaryIO:
    return path.open("wb", buffering=0)


class HidOutput:
    """Own and serialize keyboard, mouse, and consumer-control HID reports."""

    def __init__(
        self,
        keyboard_path: str | Path,
        mouse_path: str | Path,
        consumer_path: str | Path,
        *,
        opener: Callable[[Path], _Writer] = _open_device,
    ) -> None:
        self._paths = tuple(Path(path) for path in (keyboard_path, mouse_path, consumer_path))
        self._opener = opener
        self._handles: tuple[_Writer, _Writer, _Writer] | None = None
        self._lock = RLock()

    def __enter__(self) -> HidOutput:
        self.open()
        return self

    def __exit__(self, _type: object, _value: object, _traceback: object) -> None:
        self.close()

    def open(self) -> None:
        with self._lock:
            if self._handles is not None:
                raise HidOutputError("HID output is already open")
            opened: list[_Writer] = []
            try:
                for path in self._paths:
                    if not path.exists():
                        raise FileNotFoundError(path)
                    opened.append(self._opener(path))
            except OSError as error:
                for handle in opened:
                    handle.close()
                raise HidOutputError("unable to open all HID device nodes") from error
            self._handles = (opened[0], opened[1], opened[2])

    def close(self, *, release: bool = True) -> None:
        with self._lock:
            handles = self._handles
            if handles is None:
                return
            release_error: HidOutputError | None = None
            if release:
                try:
                    self.release_all()
                except HidOutputError as error:
                    release_error = error
            self._handles = None
            for handle in handles:
                try:
                    handle.close()
                except OSError:
                    pass
            if release_error is not None:
                raise release_error

    def write_keyboard(self, report: bytes) -> None:
        if not isinstance(report, bytes) or len(report) != 8:
            raise ValueError("keyboard report must be exactly 8 bytes")
        with self._lock:
            self._write(0, report)

    def write_mouse(self, buttons: int, delta_x: int, delta_y: int, wheel: int, pan: int) -> None:
        if not 0 <= buttons <= 0x1F:
            raise ValueError("mouse buttons must fit the five-button report")
        values = [delta_x, delta_y, wheel, pan]
        if not all(isinstance(value, int) for value in values):
            raise TypeError("mouse relative values must be integers")

        with self._lock:
            while True:
                parts = [_clamp_relative(value) for value in values]
                report = bytes((buttons,)) + b"".join(
                    part.to_bytes(1, "little", signed=True) for part in parts
                )
                self._write(1, report)
                values = [value - part for value, part in zip(values, parts, strict=True)]
                if not any(values):
                    return

    def write_consumer(self, usage: int) -> None:
        if not isinstance(usage, int) or not 0 <= usage <= 0xFFFF:
            raise ValueError("consumer usage must fit uint16")
        with self._lock:
            self._write(2, usage.to_bytes(2, "little"))

    def release_all(self) -> None:
        with self._lock:
            self._write(0, EMPTY_KEYBOARD_REPORT)
            self._write(1, EMPTY_MOUSE_REPORT)
            self._write(2, EMPTY_CONSUMER_REPORT)

    def _write(self, index: int, report: bytes) -> None:
        if self._handles is None:
            raise HidOutputError("HID output is not open")
        try:
            written = self._handles[index].write(report)
        except OSError as error:
            raise HidOutputError("HID device write failed") from error
        if written != len(report):
            raise HidOutputError("short HID write")


def _clamp_relative(value: int) -> int:
    return max(-127, min(127, value))
