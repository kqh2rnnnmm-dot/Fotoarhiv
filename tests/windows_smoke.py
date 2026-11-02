"""CI-only Windows startup check. Not a launcher distributed with the application."""
import ctypes
from ctypes import wintypes
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import time

if sys.platform != "win32":
    raise SystemExit("Windows smoke check requires Windows; NOT RUN")

user32 = ctypes.WinDLL("user32", use_last_error=True)
callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
user32.EnumWindows.argtypes = [callback_type, wintypes.LPARAM]
user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user32.IsWindowVisible.argtypes = [wintypes.HWND]
user32.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
exe = Path(sys.argv[1]).resolve()
process = subprocess.Popen([str(exe)], cwd=exe.parent)
try:
    window = None
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError(f"Application exited before showing a window: {process.returncode}")
        found = []

        @callback_type
        def inspect(hwnd, _):
            pid = wintypes.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
            if pid.value == process.pid and user32.IsWindowVisible(hwnd):
                title = ctypes.create_unicode_buffer(256)
                user32.GetWindowTextW(hwnd, title, len(title))
                if title.value == "Фотоархив":
                    found.append(hwnd)
            return True

        user32.EnumWindows(inspect, 0)
        if found:
            window = found[0]
            break
        time.sleep(0.2)
    if window is None:
        raise RuntimeError("Main window not observed within 30 seconds")
    db = Path(os.environ["LOCALAPPDATA"]) / "Fotoarhiv" / "catalog.sqlite"
    with sqlite3.connect(f"{db.as_uri()}?mode=ro", uri=True) as connection:
        assert {"photos", "sources"} <= {row[0] for row in connection.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    user32.PostMessageW(window, 0x0010, 0, 0)  # WM_CLOSE
    assert process.wait(timeout=15) == 0, "Nonzero exit after closing the main window"
    print("PASS: published application shows main window, initializes SQLite, closes normally")
finally:
    if process.poll() is None:
        process.kill()
        process.wait(timeout=10)
