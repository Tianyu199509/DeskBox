"""DeskBox store screenshot capture utilities."""
import ctypes
import time
from ctypes import wintypes

from PIL import ImageGrab, Image

user32 = ctypes.windll.user32

VK_F7 = 0x76
VK_D = 0x44
VK_LWIN = 0x5B
KEYEVENTF_KEYUP = 0x0002


def press_f7():
    """Toggle show/hide all DeskBox widgets via the global hotkey."""
    user32.keybd_event(VK_F7, 0, 0, 0)
    time.sleep(0.05)
    user32.keybd_event(VK_F7, 0, KEYEVENTF_KEYUP, 0)


def show_desktop():
    """Win+D — widgets stay visible per keepWidgetsVisibleOnShowDesktop."""
    user32.keybd_event(VK_LWIN, 0, 0, 0)
    time.sleep(0.05)
    user32.keybd_event(VK_D, 0, 0, 0)
    time.sleep(0.05)
    user32.keybd_event(VK_D, 0, KEYEVENTF_KEYUP, 0)
    time.sleep(0.05)
    user32.keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, 0)


def grab_virtual():
    return ImageGrab.grab(all_screens=True).convert("RGB")


def grab_primary():
    return ImageGrab.grab().convert("RGB")


def save(img, path):
    img.save(path, "PNG")
    print(f"saved {path} {img.size}")


if __name__ == "__main__":
    import sys

    action = sys.argv[1] if len(sys.argv) > 1 else "grab"
    out = sys.argv[2] if len(sys.argv) > 2 else "D:/project/wingezi/store-assets-html/source/_probe.png"
    if action == "f7":
        press_f7()
        time.sleep(2.5)
        save(grab_virtual(), out)
    elif action == "f7grab":
        press_f7()
        time.sleep(2.5)
        save(grab_virtual(), out)
    elif action == "desktop":
        show_desktop()
        time.sleep(3.0)
        save(grab_virtual(), out)
    elif action == "restore":
        show_desktop()
        time.sleep(1.5)
        print("desktop shown; press Win+D again or click taskbar to restore windows")
    else:
        save(grab_virtual(), out)
