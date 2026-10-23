"""Enumerate top-level windows for a given PID and print their screen rects."""
import ctypes
import json
import sys
from ctypes import wintypes

user32 = ctypes.windll.user32


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


target_pid = int(sys.argv[1])
results = []


def enum_cb(hwnd, _):
    pid = wintypes.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    if pid.value != target_pid:
        return True
    if not user32.IsWindow(hwnd):
        return True
    length = user32.GetWindowTextLengthW(hwnd)
    title = ctypes.create_unicode_buffer(length + 1)
    user32.GetWindowTextW(hwnd, title, length + 1)
    cls = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(hwnd, cls, 256)
    r = RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    results.append({
        "hwnd": hwnd,
        "title": title.value,
        "class": cls.value,
        "visible": bool(user32.IsWindowVisible(hwnd)),
        "minimized": bool(user32.IsIconic(hwnd)),
        "rect": [r.left, r.top, r.right, r.bottom],
        "w": r.right - r.left,
        "h": r.bottom - r.top,
    })
    return True


CBF = ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
user32.EnumWindows(CBF(enum_cb), 0)
print(json.dumps(results, ensure_ascii=False, indent=1))
