"""DeskBox UI automation helpers: show settings window, capture regions."""
import ctypes
import json
import sys
import time
from ctypes import wintypes

from PIL import ImageGrab

user32 = ctypes.windll.user32

SW_SHOW = 5
SW_RESTORE = 9
HWND_TOPMOST = -1
HWND_NOTOPMOST = -2
SWP_NOMOVE = 0x0002
SWP_NOSIZE = 0x0001
SWP_SHOWWINDOW = 0x0040

TARGET_PID = 18268
VX = 1920  # left monitor width; virtual screen x offset


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


def find_window(title_part):
    found = []

    def cb(hwnd, _):
        pid = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value != TARGET_PID:
            return True
        n = user32.GetWindowTextLengthW(hwnd)
        buf = ctypes.create_unicode_buffer(n + 1)
        user32.GetWindowTextW(hwnd, buf, n + 1)
        if title_part in buf.value:
            r = RECT()
            user32.GetWindowRect(hwnd, ctypes.byref(r))
            found.append((hwnd, buf.value, r.left, r.top, r.right, r.bottom))
        return True

    CBF = ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
    user32.EnumWindows(CBF(cb), 0)
    return found


def show(hwnd):
    user32.ShowWindow(hwnd, SW_RESTORE)
    user32.ShowWindow(hwnd, SW_SHOW)
    user32.SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW)
    time.sleep(0.3)
    user32.SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW)
    force_foreground(hwnd)


def force_foreground(hwnd):
    """Raise a window above others using the AttachThreadInput trick."""
    fg = user32.GetForegroundWindow()
    if fg == hwnd:
        return
    fg_thread = user32.GetWindowThreadProcessId(fg, None)
    my_thread = ctypes.windll.kernel32.GetCurrentThreadId()
    if fg_thread and fg_thread != my_thread:
        user32.AttachThreadInput(my_thread, fg_thread, True)
    user32.BringWindowToTop(hwnd)
    try:
        user32.SetForegroundWindow(hwnd)
    except Exception:
        pass
    if fg_thread and fg_thread != my_thread:
        user32.AttachThreadInput(my_thread, fg_thread, False)
    time.sleep(0.6)


def move(hwnd, x, y):
    r = RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    user32.SetWindowPos(hwnd, 0, x, y, 0, 0, 0x0001 | 0x0004)  # NOSIZE|NOZORDER
    return (r.left, r.top)


MOUSEEVENTF_MOVE = 0x0001
MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004
MOUSEEVENTF_ABSOLUTE = 0x8000
SM_CXSCREEN = 0
SM_CYSCREEN = 1


def _norm_primary(x, y):
    """Normalize virtual coords to primary-monitor 0..65535 space (mouse_event)."""
    sw = user32.GetSystemMetrics(SM_CXSCREEN)
    sh = user32.GetSystemMetrics(SM_CYSCREEN)
    return int(x * 65535 / sw), int(y * 65535 / sh)


def click(x, y):
    """Click at absolute virtual-screen coords (SetCursorPos + relative click)."""
    user32.SetCursorPos(x, y)
    time.sleep(0.15)
    nx, ny = _norm_primary(x, y)
    user32.mouse_event(MOUSEEVENTF_LEFTDOWN, nx, ny, 0, 0)
    time.sleep(0.05)
    user32.mouse_event(MOUSEEVENTF_LEFTUP, nx, ny, 0, 0)


def drag(x1, y1, x2, y2, steps=25):
    user32.SetCursorPos(x1, y1)
    time.sleep(0.15)
    nx1, ny1 = _norm_primary(x1, y1)
    nx2, ny2 = _norm_primary(x2, y2)
    user32.mouse_event(MOUSEEVENTF_LEFTDOWN, nx1, ny1, 0, 0)
    for i in range(1, steps + 1):
        t = i / steps
        user32.SetCursorPos(int(x1 + (x2 - x1) * t), int(y1 + (y2 - y1) * t))
        time.sleep(0.01)
    time.sleep(0.1)
    user32.mouse_event(MOUSEEVENTF_LEFTUP, nx2, ny2, 0, 0)


def grab_full():
    return ImageGrab.grab(all_screens=True).convert("RGB")


def crop(img, left, top, right, bottom):
    return img.crop((left + VX, top, right + VX, bottom))


if __name__ == "__main__":
    action = sys.argv[1]
    if action == "show":
        wins = find_window("DeskBox 设置")
        if not wins:
            print("settings window not found")
            sys.exit(1)
        hwnd, title, l, t, r, b = wins[0]
        show(hwnd)
        time.sleep(2.0)
        wins = find_window("DeskBox 设置")
        hwnd, title, l, t, r, b = wins[0]
        img = grab_full()
        crop(img, l, t, r, b).save("D:/project/wingezi/store-assets/raw/_settings.png")
        print(f"shown and captured: {r-l}x{b-t} at ({l},{t})")
    else:
        wins = find_window("DeskBox 设置")
        if not wins:
            print("settings window not found")
            sys.exit(1)
        hwnd, title, l, t, r, b = wins[0]
        if action == "capture":
            out = sys.argv[2]
            img = grab_full()
            crop(img, l, t, r, b).save(out)
            print(f"captured {out} {r-l}x{b-t} at ({l},{t})")
        elif action == "rect":
            print(json.dumps({"hwnd": hwnd, "rect": [l, t, r, b], "w": r - l, "h": b - t}))
        elif action == "click":
            # click x y — coords relative to the settings window origin
            cx, cy = int(sys.argv[2]), int(sys.argv[3])
            show(hwnd)
            click(l + cx, t + cy)
            print(f"clicked window-rel ({cx},{cy}) abs ({l+cx},{t+cy})")
        elif action == "clickabs":
            cx, cy = int(sys.argv[2]), int(sys.argv[3])
            click(cx, cy)
            print(f"clicked abs ({cx},{cy})")
        elif action == "nav":
            # click nav item by name and capture result
            nav = sys.argv[2]
            show(hwnd)
            time.sleep(0.5)
            img = grab_full()
            crop(img, l, t, r, b).save("D:/project/wingezi/store-assets/raw/_nav_before.png")
            print(f"nav {nav}: before captured")
