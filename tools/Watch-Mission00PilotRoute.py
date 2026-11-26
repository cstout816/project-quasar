"""Drive the saved-pilot menu on one already-running guarded diagnostic."""

import argparse
import ctypes as ct
import json
import struct
import time
from pathlib import Path


PILOT_ROWS = (140, 164, 187, 210, 233, 256, 279, 301)


def highlighted(r, g, b):
    return b > 120 and b > r + 42 and b > g + 37


def light_text(r, g, b):
    return min(r, g, b) > 115 and max(r, g, b) - min(r, g, b) < 38


def muted_text(r, g, b):
    return min(r, g, b) > 95 and max(r, g, b) - min(r, g, b) < 38


def screen(frame):
    if len(frame) != 921654 or frame[:2] != b"BM":
        return None
    offset, width, height = struct.unpack_from("<I4xii", frame, 10)
    if width != 640 or abs(height) != 480 or len(frame) - offset != 1920 * 480:
        return None

    def score(rect, predicate):
        x0, y0, x1, y1 = rect
        count = 0
        for y in range(y0, y1):
            start = offset + 1920 * (479 - y if height > 0 else y) + 3 * x0
            for i in range(start, start + 3 * (x1 - x0), 3):
                b, g, r = frame[i:i + 3]
                count += predicate(r, g, b)
        return count

    if score((54, 235, 310, 249), highlighted) >= 100:
        return "title"
    menu_header = score((56, 43, 230, 71), light_text) >= 55
    right_header = score((435, 103, 577, 125), muted_text) >= 65
    if menu_header and right_header:
        profile = score((257, 321, 306, 342), muted_text) >= 95
        if profile and score((412, 373, 584, 399), highlighted) >= 140:
            return "complete"
        selected = [
            index for index, row in enumerate(PILOT_ROWS)
            if score((471, row - 8, 580, row + 9), highlighted) >= 90
        ]
        if len(selected) == 1:
            row = selected[0]
            if row != 7 or profile:
                return f"pilot-{row}"
        return None
    if menu_header and score((210, 414, 435, 440), light_text) >= 110:
        if score((434, 321, 585, 341), highlighted) >= 110:
            return "listed"
        if score((437, 294, 586, 319), highlighted) >= 110:
            return "list-as-new"
    if not menu_header and score((462, 306, 585, 324), highlighted) >= 110:
        if score((255, 413, 390, 440), light_text) >= 95:
            return "campaign"
    return None


def route(pid, output, expected_image, max_seconds, confirm_ms=550):
    kernel = ct.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.argtypes = (ct.c_uint, ct.c_int, ct.c_uint)
    kernel.OpenProcess.restype = ct.c_void_p
    kernel.QueryFullProcessImageNameW.argtypes = (
        ct.c_void_p, ct.c_uint, ct.c_wchar_p, ct.POINTER(ct.c_uint)
    )
    kernel.WaitForSingleObject.argtypes = (ct.c_void_p, ct.c_uint)
    kernel.WaitForSingleObject.restype = ct.c_uint
    kernel.CloseHandle.argtypes = (ct.c_void_p,)
    handle = kernel.OpenProcess(0x101000, 0, pid)
    if not handle:
        raise ct.WinError(ct.get_last_error())
    try:
        buffer = ct.create_unicode_buffer(32768)
        length = ct.c_uint(len(buffer))
        if not kernel.QueryFullProcessImageNameW(handle, 0, buffer, ct.byref(length)):
            raise ct.WinError(ct.get_last_error())
        manifest = json.loads((output / "run-manifest.json").read_text(encoding="utf-8-sig"))
        if (Path(buffer.value).resolve() != expected_image.resolve()
                or Path(manifest["ExecutablePath"]).resolve() != expected_image.resolve()
                or Path(manifest["RunDirectory"]).resolve() != output.resolve()):
            raise RuntimeError("PID, executable and guarded run directory do not match")

        until = time.monotonic() + max_seconds
        last_action = 0.0

        def observe():
            if kernel.WaitForSingleObject(handle, 0) != 0x102:
                raise RuntimeError("Guarded game process has exited")
            try:
                return screen((output / "frame.bmp").read_bytes())
            except (FileNotFoundError, PermissionError):
                return None

        def log(event, **fields):
            row = json.dumps(dict(event=event, **fields))
            with (output / "pilot-route.jsonl").open("a", encoding="utf-8") as evidence:
                evidence.write(row + "\n")
            print(row, flush=True)

        def wait_for(expected, seconds, stable=1.4):
            end = min(until, time.monotonic() + seconds)
            first = None
            while time.monotonic() < end:
                if observe() == expected:
                    first = time.monotonic() if first is None else first
                    if time.monotonic() - first >= stable:
                        return True
                else:
                    first = None
                time.sleep(.18)
            return False

        def move(current, action, expected):
            nonlocal last_action
            for attempt in (1, 2):
                # Retrying requires the exact prior screen. An unexpected
                # highlight or fade receives no input.
                if not wait_for(current, 5, stable=.25 if current == "title" else 1.4):
                    raise RuntimeError(f"Expected {current} after the prior action")
                if time.monotonic() - last_action < 1.5:
                    time.sleep(1.5 - (time.monotonic() - last_action))
                if time.monotonic() >= until or observe() != current:
                    raise RuntimeError(f"Lost the {current} input gate")
                with (output / "pad-live.txt").open("a", encoding="ascii", newline="") as pad:
                    pad.write(action + "\r\n")
                last_action = time.monotonic()
                log("input", screen=current, action=action, attempt=attempt)
                if expected is None:
                    return
                if wait_for(expected, 14 if attempt == 1 else 22):
                    log("confirmed", screen=expected)
                    return
            raise TimeoutError(expected)

        if not wait_for("title", 65, stable=.4):
            raise TimeoutError("Title prompt")
        flow = [
            ("title", "a:550", "campaign"),
            ("campaign", "a:550", "list-as-new"),
            ("list-as-new", "ldown:350", "listed"),
            ("listed", "a:550", "pilot-0"),
            *((f"pilot-{i}", "ldown:350", f"pilot-{i + 1}") for i in range(7)),
            ("pilot-7", "a:550", "complete"),
            ("complete", "a:550", None),
        ]
        for current, action, expected in flow:
            if current != "title" and action.startswith("a:"):
                action = f"a:{confirm_ms}"
            move(current, action, expected)
    finally:
        kernel.CloseHandle(handle)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("pid", type=int)
    parser.add_argument("run_directory", type=Path)
    parser.add_argument("expected_image", type=Path)
    parser.add_argument("--seconds", type=int, default=190, choices=range(1, 241), metavar="1-240")
    parser.add_argument("--confirm-ms", type=int, default=550, choices=range(350, 2501), metavar="350-2500")
    opts = parser.parse_args()
    route(opts.pid, opts.run_directory, opts.expected_image, opts.seconds, opts.confirm_ms)
