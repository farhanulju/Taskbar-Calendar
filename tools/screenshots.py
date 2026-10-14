"""Regenerates the README screenshots in docs/ from demo data. Quit the real widget first, then:

    .venv\\Scripts\\python tools\\screenshots.py

A neutral backdrop covers the desktop while capturing, so none of your own windows end up in the images.
"""

import ctypes
import ctypes.wintypes as wt
import sys
import tkinter as tk
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "tools"))

from make_icon import png  # noqa: E402
from taskbar_calendar import winapi  # noqa: E402

DOCS = ROOT / "docs"
user32, gdi32 = ctypes.windll.user32, ctypes.windll.gdi32
for name in ("CreateCompatibleDC", "CreateCompatibleBitmap", "SelectObject"):
    getattr(gdi32, name).restype = wt.HANDLE
gdi32.CreateCompatibleDC.argtypes = [wt.HDC]
gdi32.CreateCompatibleBitmap.argtypes = [wt.HDC, ctypes.c_int, ctypes.c_int]
gdi32.SelectObject.argtypes = [wt.HDC, wt.HANDLE]
gdi32.BitBlt.argtypes = [wt.HDC, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wt.HDC, ctypes.c_int,
                         ctypes.c_int, wt.DWORD]
gdi32.GetDIBits.argtypes = [wt.HDC, wt.HANDLE, wt.UINT, wt.UINT, ctypes.c_void_p, ctypes.c_void_p, wt.UINT]
gdi32.DeleteObject.argtypes = [wt.HANDLE]
gdi32.DeleteDC.argtypes = [wt.HDC]


def capture(left, top, right, bottom, path):
    w, h = right - left, bottom - top
    screen = user32.GetDC(None)
    mem = gdi32.CreateCompatibleDC(screen)
    bmp = gdi32.CreateCompatibleBitmap(screen, w, h)
    gdi32.SelectObject(mem, bmp)
    gdi32.BitBlt(mem, 0, 0, w, h, screen, left, top, 0x00CC0020)  # SRCCOPY
    header = (ctypes.c_uint32 * 10)(40, w, (-h) & 0xFFFFFFFF, 1 | (32 << 16), 0, 0, 0, 0, 0, 0)  # top-down BGRA
    buf = (ctypes.c_ubyte * (w * h * 4))()
    gdi32.GetDIBits(mem, bmp, 0, h, buf, header, 0)
    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mem)
    user32.ReleaseDC(None, screen)
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        row = bytes(buf[y * w * 4:(y + 1) * w * 4])
        for x in range(0, len(row), 4):
            raw += bytes((row[x + 2], row[x + 1], row[x], 255))
    DOCS.mkdir(exist_ok=True)
    path.write_bytes(png(w, h, bytes(raw)))
    print(f"saved {path.relative_to(ROOT)} ({w}x{h})")


def window_rect(widget):
    return winapi.rect_of(winapi.user32.GetParent(widget.winfo_id()) or widget.winfo_id())


def main():
    winapi.set_dpi_awareness()
    from taskbar_calendar.widget import Widget

    app = Widget(demo=True)
    root = app.root

    # Neutral backdrop over the desktop (not the taskbar), below the widget and its popups.
    tb, _, _, _ = winapi.taskbar_layout()
    backdrop = tk.Toplevel(root)
    backdrop.overrideredirect(True)
    backdrop.geometry(f"{tb.right - tb.left}x{tb.top}+{tb.left}+0")
    canvas = tk.Canvas(backdrop, highlightthickness=0, bd=0)
    canvas.pack(fill="both", expand=True)
    for i in range(tb.top):
        shade = 0x1f + int(0x10 * i / tb.top)
        canvas.create_line(0, i, tb.right, i, fill="#%02x%02x%02x" % (shade, shade + 4, shade + 14))
    backdrop.attributes("-topmost", True)

    def shot_taskbar():
        winapi.raise_topmost(app.hwnd)
        w, h, x, y = app.geometry
        capture(max(tb.left, x - 24), tb.top, tb.right, tb.bottom, DOCS / "taskbar.png")
        app.hover = True
        app.show_peek()
        root.after(600, shot_popup)

    def shot_popup():
        r = window_rect(app.peek)
        w, h, x, y = app.geometry
        capture(min(r.left, x) - 24, r.top - 24, max(r.right, x + w) + 24, tb.bottom, DOCS / "popup.png")
        app.hover = False
        app.hide_peek()
        app.open_settings()
        root.after(900, shot_settings)

    def shot_settings(tab=0):
        win = app.settings.win
        win.attributes("-topmost", True)
        notebook = next(c for c in win.winfo_children()[0].winfo_children() if c.winfo_class() == "TNotebook")
        notebook.select(tab)
        win.update()
        root.after(400, lambda: grab_settings(tab))

    def grab_settings(tab):
        r = window_rect(app.settings.win)
        name = ("settings.png", "settings-claude.png")[tab]
        capture(r.left - 16, r.top - 16, r.right + 16, r.bottom + 16, DOCS / name)
        if tab == 0:
            root.after(200, lambda: shot_settings(1))
        else:
            app.quit()

    root.after(2500, shot_taskbar)
    app.run()


if __name__ == "__main__":
    main()
