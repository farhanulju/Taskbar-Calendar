"""Win32 plumbing: taskbar geometry, shell notifications, single instance.

Windows 11 doesn't let apps add items to the taskbar, so the widget is a borderless, always-on-top window placed
over empty taskbar space. Everything here exists so that window behaves like part of the taskbar without polling.
"""

import ctypes
import ctypes.wintypes as wt
import logging
import threading

import comtypes
import comtypes.client

log = logging.getLogger(__name__)

user32 = ctypes.windll.user32
dwmapi = ctypes.windll.dwmapi
gdi32 = ctypes.windll.gdi32
kernel32 = ctypes.windll.kernel32
shell32 = ctypes.windll.shell32

LRESULT = ctypes.c_ssize_t
WNDPROC = ctypes.WINFUNCTYPE(LRESULT, wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM)
WINEVENTPROC = ctypes.WINFUNCTYPE(None, wt.HANDLE, wt.DWORD, wt.HWND, wt.LONG, wt.LONG, wt.DWORD, wt.DWORD)


class WNDCLASSW(ctypes.Structure):
    _fields_ = [
        ("style", wt.UINT), ("lpfnWndProc", WNDPROC), ("cbClsExtra", ctypes.c_int), ("cbWndExtra", ctypes.c_int),
        ("hInstance", wt.HINSTANCE), ("hIcon", wt.HICON), ("hCursor", wt.HANDLE), ("hbrBackground", wt.HBRUSH),
        ("lpszMenuName", wt.LPCWSTR), ("lpszClassName", wt.LPCWSTR),
    ]


class APPBARDATA(ctypes.Structure):
    _fields_ = [
        ("cbSize", wt.DWORD), ("hWnd", wt.HWND), ("uCallbackMessage", wt.UINT),
        ("uEdge", wt.UINT), ("rc", wt.RECT), ("lParam", wt.LPARAM),
    ]


class MONITORINFO(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("rcMonitor", wt.RECT), ("rcWork", wt.RECT), ("dwFlags", wt.DWORD)]


user32.GetParent.restype = wt.HWND
user32.FindWindowW.restype = wt.HWND
user32.FindWindowExW.restype = wt.HWND
user32.GetForegroundWindow.restype = wt.HWND
user32.GetDC.restype = wt.HDC
user32.ReleaseDC.argtypes = [wt.HWND, wt.HDC]
user32.DefWindowProcW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
user32.DefWindowProcW.restype = LRESULT
user32.CreateWindowExW.argtypes = [
    wt.DWORD, wt.LPCWSTR, wt.LPCWSTR, wt.DWORD, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
    wt.HWND, wt.HMENU, wt.HINSTANCE, wt.LPVOID,
]
user32.CreateWindowExW.restype = wt.HWND
user32.SetWinEventHook.argtypes = [wt.DWORD, wt.DWORD, wt.HMODULE, WINEVENTPROC, wt.DWORD, wt.DWORD, wt.DWORD]
user32.SetWinEventHook.restype = wt.HANDLE
user32.UnhookWinEvent.argtypes = [wt.HANDLE]
user32.SetWindowPos.argtypes = [wt.HWND, wt.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wt.UINT]
user32.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]
user32.PostMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
user32.MonitorFromWindow.restype = wt.HANDLE
user32.GetMonitorInfoW.argtypes = [wt.HANDLE, ctypes.POINTER(MONITORINFO)]
gdi32.GetPixel.argtypes = [wt.HDC, ctypes.c_int, ctypes.c_int]
gdi32.GetPixel.restype = wt.DWORD
kernel32.GetModuleHandleW.restype = wt.HMODULE
kernel32.CreateMutexW.argtypes = [wt.LPVOID, wt.BOOL, wt.LPCWSTR]
kernel32.CreateMutexW.restype = wt.HANDLE
kernel32.CloseHandle.argtypes = [wt.HANDLE]
kernel32.FindFirstChangeNotificationW.argtypes = [wt.LPCWSTR, wt.BOOL, wt.DWORD]
kernel32.FindFirstChangeNotificationW.restype = wt.HANDLE
kernel32.FindNextChangeNotification.argtypes = [wt.HANDLE]
kernel32.WaitForSingleObject.argtypes = [wt.HANDLE, wt.DWORD]
shell32.SHAppBarMessage.argtypes = [wt.DWORD, ctypes.POINTER(APPBARDATA)]
shell32.SHAppBarMessage.restype = ctypes.c_size_t

HWND_TOPMOST = wt.HWND(-1)
SWP_NOSIZE, SWP_NOMOVE, SWP_NOACTIVATE = 0x0001, 0x0002, 0x0010
GWL_EXSTYLE = -20
WS_POPUP = 0x80000000
WS_EX_TOOLWINDOW, WS_EX_APPWINDOW, WS_EX_NOACTIVATE = 0x80, 0x40000, 0x08000000
MONITOR_DEFAULTTONEAREST = 2

EVENT_SYSTEM_FOREGROUND = 0x0003
EVENT_OBJECT_SHOW, EVENT_OBJECT_HIDE = 0x8002, 0x8003
EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_PARENTCHANGE = 0x800B, 0x800F
WINEVENT_OUTOFCONTEXT, WINEVENT_SKIPOWNPROCESS = 0x0000, 0x0002
OBJID_WINDOW = 0

WM_SETTINGCHANGE, WM_TIMECHANGE, WM_DISPLAYCHANGE = 0x001A, 0x001E, 0x007E
WM_POWERBROADCAST, PBT_APMRESUMEAUTOMATIC = 0x0218, 0x0012
WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320
WM_APP = 0x8000
WM_APPBAR_CALLBACK = WM_APP + 1
WM_OPEN_SETTINGS = WM_APP + 2  # sent by a second launch to the running instance
ABM_NEW, ABM_REMOVE = 0, 1
ABN_POSCHANGED, ABN_FULLSCREENAPP = 1, 2

FILE_NOTIFY_CHANGE_FILE_NAME, FILE_NOTIFY_CHANGE_LAST_WRITE = 0x1, 0x10
INVALID_HANDLE_VALUE = wt.HANDLE(-1).value
EVENTS_WINDOW_CLASS = "TaskbarCalendarEvents"


def set_dpi_awareness():
    try:
        user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))  # per-monitor v2
    except (AttributeError, OSError):
        ctypes.windll.shcore.SetProcessDpiAwareness(2)


def rect_of(hwnd):
    r = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    return r


def taskbar_layout():
    """(taskbar RECT, x where the widget's right edge should stop, taskbar hwnd, neighbour hwnds).
    Neighbours are the tray and anything embedded in the taskbar's right half (e.g. TrafficMonitor)."""
    tray = user32.FindWindowW("Shell_TrayWnd", None)
    if not tray:
        return None, None, None, []
    tb = rect_of(tray)
    limit = tb.right
    neighbours = []
    for cls in ("TrayNotifyWnd", "#32770"):
        child = user32.FindWindowExW(tray, None, cls, None)
        while child:
            r = rect_of(child)
            neighbours.append(child)
            if r.right > r.left and r.top >= tb.top - 1 and r.left > tb.left + (tb.right - tb.left) // 2:
                limit = min(limit, r.left)
            child = user32.FindWindowExW(tray, child, cls, None)
    return tb, limit, tray, neighbours


def foreground_is_fullscreen():
    fg = user32.GetForegroundWindow()
    if not fg:
        return False
    buf = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(fg, buf, 256)
    if buf.value in ("Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"):
        return False
    r = rect_of(fg)
    mi = MONITORINFO(cbSize=ctypes.sizeof(MONITORINFO))
    user32.GetMonitorInfoW(user32.MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST), ctypes.byref(mi))
    m = mi.rcMonitor
    return r.left <= m.left and r.top <= m.top and r.right >= m.right and r.bottom >= m.bottom


def screen_pixel(x, y):
    hdc = user32.GetDC(None)
    try:
        c = gdi32.GetPixel(hdc, x, y)
    finally:
        user32.ReleaseDC(None, hdc)
    if c == 0xFFFFFFFF:
        return None
    return (c & 0xFF, (c >> 8) & 0xFF, (c >> 16) & 0xFF)


def style_as_taskbar_item(hwnd):
    """No taskbar button, never steals focus, rounded corners and no window border."""
    ex = user32.GetWindowLongW(hwnd, GWL_EXSTYLE)
    user32.SetWindowLongW(hwnd, GWL_EXSTYLE, (ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) & ~WS_EX_APPWINDOW)
    corner = ctypes.c_int(3)  # DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUNDSMALL
    dwmapi.DwmSetWindowAttribute(hwnd, 33, ctypes.byref(corner), ctypes.sizeof(corner))
    no_border = wt.DWORD(0xFFFFFFFE)  # DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE
    dwmapi.DwmSetWindowAttribute(hwnd, 34, ctypes.byref(no_border), ctypes.sizeof(no_border))


def raise_topmost(hwnd):
    user32.SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)


UIA_LOCK = threading.Lock()


def weather_widget_span():
    """(left, right) of the taskbar's Widgets (weather) button, or None. It's XAML content inside the taskbar,
    not a window, so this uses UI Automation. Call off the UI thread while holding UIA_LOCK; takes ~10-20 ms."""
    comtypes.CoInitialize()
    try:
        uia_mod = comtypes.client.GetModule("UIAutomationCore.dll")
        uia = comtypes.client.CreateObject("{ff48dba4-60ef-4201-aa87-54103eef594e}", interface=uia_mod.IUIAutomation)
        tray = user32.FindWindowW("Shell_TrayWnd", None)
        if not tray:
            return None
        cond = uia.CreatePropertyCondition(30011, "WidgetsButton")  # UIA_AutomationIdPropertyId
        el = uia.ElementFromHandle(tray).FindFirst(4, cond)  # TreeScope_Descendants
        if not el:
            return None
        r = el.CurrentBoundingRectangle
        return (r.left, r.right) if r.right > r.left else None
    finally:
        comtypes.CoUninitialize()


def watch_directory(path, on_change, subtree=False):
    """Blocks in the kernel until something in `path` changes, then calls on_change(). Run on a daemon thread."""
    path.mkdir(parents=True, exist_ok=True)
    handle = kernel32.FindFirstChangeNotificationW(
        str(path), subtree, FILE_NOTIFY_CHANGE_FILE_NAME | FILE_NOTIFY_CHANGE_LAST_WRITE
    )
    if not handle or handle == INVALID_HANDLE_VALUE:
        log.error("Can't watch %s", path)
        return
    while kernel32.WaitForSingleObject(handle, 0xFFFFFFFF) == 0:  # INFINITE; WAIT_OBJECT_0
        on_change()
        if not kernel32.FindNextChangeNotification(handle):
            break


class ShellWatcher:
    """Turns Windows notifications into event names for the widget, so nothing has to poll.

    Windows delivers these callbacks from inside Tk's message loop, where calling Tk crashes _tkinter, so they
    only hand an event name to `notify` and return.
    """

    def __init__(self, notify):
        self.notify = notify
        self.tray = None
        self.watched = set()  # windows whose movement shifts our position (tray, embedded meters)
        self.location_hooks = {}  # pid -> hook handle
        # Set before creating the window: CreateWindowExW already delivers messages to _on_message.
        self.msg_taskbar_created = user32.RegisterWindowMessageW("TaskbarCreated")
        self._wndproc = WNDPROC(self._on_message)
        self._eventproc = WINEVENTPROC(self._on_winevent)

        hinst = kernel32.GetModuleHandleW(None)
        wc = WNDCLASSW(lpfnWndProc=self._wndproc, hInstance=hinst, lpszClassName=EVENTS_WINDOW_CLASS)
        user32.RegisterClassW(ctypes.byref(wc))
        # Hidden top-level window: receives display/theme/time/power broadcasts and AppBar notifications.
        self.hwnd = user32.CreateWindowExW(
            WS_EX_TOOLWINDOW, EVENTS_WINDOW_CLASS, "", WS_POPUP, 0, 0, 0, 0, None, None, hinst, None
        )
        if not self.hwnd:
            log.error("Couldn't create the event window (error %s)", ctypes.GetLastError())
        self.register_appbar()

        flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS
        self.global_hooks = [
            user32.SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, None, self._eventproc, 0, 0, flags),
            user32.SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_HIDE, None, self._eventproc, 0, 0, flags),
            user32.SetWinEventHook(EVENT_OBJECT_PARENTCHANGE, EVENT_OBJECT_PARENTCHANGE, None, self._eventproc, 0, 0, flags),
        ]

    def _appbar(self, message):
        abd = APPBARDATA(cbSize=ctypes.sizeof(APPBARDATA), hWnd=self.hwnd, uCallbackMessage=WM_APPBAR_CALLBACK)
        shell32.SHAppBarMessage(message, ctypes.byref(abd))

    def register_appbar(self):
        # Registering (without reserving space) makes the shell tell us when a full-screen app opens or closes.
        self._appbar(ABM_REMOVE)
        self._appbar(ABM_NEW)

    def watch(self, tray, neighbours):
        """Track movement of the taskbar pieces we sit next to; location hooks are scoped to their processes."""
        self.tray = tray
        self.watched = set(neighbours)
        pids = set()
        for h in neighbours:
            pid = wt.DWORD()
            user32.GetWindowThreadProcessId(h, ctypes.byref(pid))
            pids.add(pid.value)
        for pid in set(self.location_hooks) - pids:
            user32.UnhookWinEvent(self.location_hooks.pop(pid))
        for pid in pids - set(self.location_hooks):
            self.location_hooks[pid] = user32.SetWinEventHook(
                EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, None, self._eventproc, pid, 0,
                WINEVENT_OUTOFCONTEXT,
            )

    def _on_winevent(self, _hook, event, hwnd, id_object, _id_child, _thread, _time):
        if event == EVENT_SYSTEM_FOREGROUND:
            self.notify("foreground")  # also covers the taskbar being clicked and jumping above us
        elif id_object != OBJID_WINDOW or not hwnd:
            return
        elif event == EVENT_OBJECT_LOCATIONCHANGE:
            if hwnd in self.watched:
                self.notify("layout")
        elif hwnd in self.watched or (self.tray and user32.GetParent(hwnd) == self.tray):
            self.notify("layout")  # something was added to / removed from the taskbar

    def _on_message(self, hwnd, msg, wparam, lparam):
        if msg == WM_APPBAR_CALLBACK:
            if wparam == ABN_FULLSCREENAPP:
                self.notify("fullscreen")
            elif wparam == ABN_POSCHANGED:
                self.notify("layout")
            return 0
        if msg == WM_OPEN_SETTINGS:
            self.notify("open-settings")
            return 0
        if msg == self.msg_taskbar_created:
            self.notify("taskbar-restarted")
        elif msg in (WM_DISPLAYCHANGE, WM_SETTINGCHANGE, WM_DWMCOLORIZATIONCOLORCHANGED):
            self.notify("display")
        elif msg == WM_TIMECHANGE:
            self.notify("time")
        elif msg == WM_POWERBROADCAST and wparam == PBT_APMRESUMEAUTOMATIC:
            self.notify("resume")
        return user32.DefWindowProcW(hwnd, msg, wparam, lparam)

    def close(self):
        for h in self.global_hooks + list(self.location_hooks.values()):
            user32.UnhookWinEvent(h)
        self._appbar(ABM_REMOVE)
        user32.DestroyWindow(self.hwnd)


class SingleInstance:
    def __init__(self, name="Local\\TaskbarCalendarWidget"):
        self.handle = kernel32.CreateMutexW(None, False, name)
        self.already_running = kernel32.GetLastError() == 183  # ERROR_ALREADY_EXISTS

    def release(self):
        if self.handle:
            kernel32.CloseHandle(self.handle)
            self.handle = None


def ask_running_instance_to_open_settings():
    hwnd = user32.FindWindowW(EVENTS_WINDOW_CLASS, None)
    return bool(hwnd and user32.PostMessageW(hwnd, WM_OPEN_SETTINGS, 0, 0))
