"""The taskbar widget: next meeting, Claude usage and network speed in one borderless window over the taskbar.

Left-click  : open Google Calendar
Hover       : upcoming meetings and Claude usage details
Right-click : join video call / settings / refresh / quit
"""

import datetime as dt
import logging
import queue
import threading
import time
import tkinter as tk
import tkinter.font as tkfont
import webbrowser

from . import claude_usage as cu
from . import winapi
from .calendar_feed import Meeting, fetch_meetings, fmt_time, fmt_when, pick_next
from .chrome import open_url
from .config import DEFAULT_CONFIG, ICON_PATH, calendar_url, ics_urls, load_config
from .netmeter import NetMeter, fmt_speed

log = logging.getLogger(__name__)

POPUP_BG, POPUP_BORDER = "#2b2b2b", "#3d3d3d"

# Claude usage checks (seconds). The usage service is free, but stay polite with it.
USAGE_MIN_INTERVAL = 60  # never more often than this
USAGE_AFTER_ACTIVITY = 20  # after Claude Code writes to a session, let the reply finish first
USAGE_ON_HOVER = 120  # hovering refreshes numbers older than this
USAGE_PERIODIC = 600  # otherwise refresh every 10 minutes while the widget is visible
PROBE_FALLBACK_INTERVAL = 1800  # the paid fallback probe runs at most every 30 minutes
GREY, DIM, GREEN, AMBER = "#9a9a9a", "#c5c5c5", "#6ccb5f", "#ffb900"


DEMO_CONFIG = {
    **DEFAULT_CONFIG,
    "ics_urls": ["https://calendar.google.com/calendar/ical/you%40example.com/private-0123456789abcdef/basic.ics"],
}
DEMO_PROFILES = [("Default", "Personal", "you@example.com"), ("Profile 1", "Work", "you@work.example.com")]


def demo_data(now):
    """Made-up meetings and usage for --demo (screenshots), so nothing personal is shown."""
    base = now.replace(second=0, microsecond=0)

    def meeting(title, start, minutes, link=None):
        return Meeting(title, base + dt.timedelta(minutes=start), base + dt.timedelta(minutes=start + minutes), link)

    meetings = [
        meeting("Design review: onboarding flow", 7, 30, "https://meet.google.com/abc-defg-hij"),
        meeting("1:1 with Alex", 95, 30),
        meeting("Sprint planning", 180, 60),
        meeting("Customer call: Northwind", 24 * 60 + 60, 45),
        meeting("Team standup", 24 * 60 + 150, 15),
    ]
    ts = now.timestamp()
    usage = {
        "five_hour": {"used_percentage": 42, "resets_at": ts + 2 * 3600 + 14 * 60},
        "seven_day": {"used_percentage": 61, "resets_at": ts + 3 * 86400 + 4 * 3600},
        "updated": ts - 120,
    }
    return meetings, usage


class Widget:
    def __init__(self, demo=False, restart=None, first_run=False):
        self.cfg = dict(DEMO_CONFIG) if demo else load_config()
        self.demo = demo
        self.restart_app = restart
        self.meetings = []
        self.error = None
        self.loaded = False
        self.current = None
        self.hover = False
        self.peek = None
        self.peek_rows = None
        self.peek_timer = None
        self.leave_timer = None
        self.settings = None
        self.bg = "#1c1c1c"

        self.root = tk.Tk()
        self.root.withdraw()
        self.root.overrideredirect(True)
        self.root.attributes("-topmost", True)
        self.root.title("Taskbar Calendar")
        if ICON_PATH.exists():
            self.root.iconbitmap(default=str(ICON_PATH))  # also used by the settings window
        self.scale = self.root.winfo_fpixels("1i") / 96.0

        family = "Segoe UI Variable Text" if "Segoe UI Variable Text" in tkfont.families() else "Segoe UI"
        self.font = tkfont.Font(family=family, size=9)
        self.font_small = (family, 8)
        self.font_small_bold = (family, 8, "bold")

        self._build()
        self.menu = tk.Menu(self.root, tearoff=0)

        self.root.update_idletasks()
        self.hwnd = winapi.user32.GetParent(self.root.winfo_id()) or self.root.winfo_id()
        winapi.style_as_taskbar_item(self.hwnd)

        self.geometry = None
        self.visible = False
        self.layout_pending = False
        self.minute_timer = None
        self.net_timer = None
        self.weather_span = None
        self.light_taskbar = False
        self.netmeter = NetMeter() if self.net_width and not demo else None
        self.claude_usage = None if demo else cu.load_usage()
        self.auto_usage = bool(self.claude_width and self.cfg.get("claude_probe", True) and not demo)
        self.claude_exe = cu.find_claude_exe() if self.auto_usage else None
        self.usage_timer = None
        self.usage_running = False
        self.last_usage_check = 0.0
        self.usage_paused_until = 0.0  # set when the usage service asks us to slow down
        self.last_probe = 0.0

        self.events = queue.SimpleQueue()
        self.shell = winapi.ShellWatcher(self.events.put)
        self.apply_layout()
        self.refresh()
        self.find_weather_widget()
        self.root.after(0, self.minute_tick)
        self.root.after(0, lambda: threading.Thread(target=self.pump_events, daemon=True).start())
        if self.claude_width and not demo:
            self._watch(cu.USAGE_FILE.parent, "claude-usage")
        if self.auto_usage:
            self._watch(cu.PROJECTS_DIR, "claude-activity", subtree=True)
            self.schedule_usage_check(2)
        if first_run:
            self.root.after(800, lambda: self.open_settings(first_run=True))

    def _watch(self, path, event, subtree=False):
        threading.Thread(
            target=winapi.watch_directory, args=(path, lambda: self.events.put(event), subtree), daemon=True
        ).start()

    # ----------------------------------------------------------------------------- layout of the window

    def _label(self, parent, anchor, fg="#ffffff", **kw):
        return tk.Label(parent, font=self.font, anchor=anchor, bg=self.bg, fg=fg, bd=0, pady=0, **kw)

    def _build(self):
        s = self.scale
        self.frame = tk.Frame(self.root, bg=self.bg, cursor="hand2")
        self.frame.pack(fill="both", expand=True)

        # Network speed (right), fixed width so the numbers don't jiggle.
        self.net_width = 0
        self.net = tk.Frame(self.frame, bg=self.bg, cursor="hand2")
        self.net_inner = tk.Frame(self.net, bg=self.bg, cursor="hand2")
        self.lbl_up = self._label(self.net_inner, "w")
        self.lbl_down = self._label(self.net_inner, "w")
        if self.cfg.get("show_network", True):
            self.net_width = self.font.measure("↓ 999.99 KB/s") + int(10 * s)
            self.net.config(width=self.net_width)
            self.net.pack(side="right", fill="y")
            self.net.pack_propagate(False)
            self.net_inner.place(x=int(2 * s), rely=0.5, anchor="w")
            self.lbl_up.pack(anchor="w")
            self.lbl_down.pack(anchor="w")

        # Claude usage (middle). Each row: "5h ▬▬▬|▭▭ 17% ↻ 1h 26m"; the tick marks how much of the window is gone.
        self.claude_width = 0
        self.claude = tk.Frame(self.frame, bg=self.bg, cursor="hand2")
        self.claude_inner = tk.Frame(self.claude, bg=self.bg, cursor="hand2")
        self.bar_w, self.bar_h = int(30 * s), max(4, int(4 * s))
        self.claude_rows = {}
        for row, (key, short) in enumerate((("five_hour", "5h"), ("seven_day", "wk"))):
            cells = {
                "name": self._label(self.claude_inner, "w", fg=GREY, text=short),
                "bar": tk.Canvas(self.claude_inner, width=self.bar_w, height=self.bar_h, bg=self.bg,
                                 highlightthickness=0, bd=0, cursor="hand2"),
                "pct": self._label(self.claude_inner, "e"),
                "reset": self._label(self.claude_inner, "w", fg=GREY),
            }
            cells["name"].grid(row=row, column=0, sticky="w")
            cells["bar"].grid(row=row, column=1, padx=(int(5 * s), int(4 * s)))
            cells["pct"].grid(row=row, column=2, sticky="e")
            cells["reset"].grid(row=row, column=3, sticky="w", padx=(int(6 * s), 0))
            self.claude_rows[key] = cells
        if self.cfg.get("show_claude", True):
            self.claude_inner.grid_columnconfigure(2, minsize=self.font.measure("100%"))
            self.claude_inner.grid_columnconfigure(3, minsize=self.font.measure("↻ 6d 23h"))
            self.claude_width = (self.font.measure("wk") + int(9 * s) + self.bar_w + self.font.measure("100%")
                                 + int(6 * s) + self.font.measure("↻ 6d 23h") + int(16 * s))
            self.claude.config(width=self.claude_width)
            self.claude.pack(side="right", fill="y")
            self.claude.pack_propagate(False)
            self.claude_inner.place(x=int(4 * s), rely=0.5, anchor="w")

        # Next meeting (left), right-aligned like the clock.
        self.cal_width = 0
        self.cal = tk.Frame(self.frame, bg=self.bg, cursor="hand2")
        self.inner = tk.Frame(self.cal, bg=self.bg, cursor="hand2")
        self.lbl_title = self._label(self.inner, "e")
        self.lbl_when = self._label(self.inner, "e", fg=DIM)
        if self.cfg.get("show_calendar", True):
            self.cal_width = int(self.cfg["width"] * s)
            self.cal.pack(side="left", fill="both", expand=True)
            self.inner.place(relx=1.0, rely=0.5, x=-int(8 * s), anchor="e")
            self.lbl_title.pack(anchor="e")
            self.lbl_when.pack(anchor="e")

        for w in self.widgets():
            w.bind("<Button-1>", lambda e: self.open_calendar())
            w.bind("<Button-3>", self.show_menu)
            w.bind("<Enter>", self.on_enter)
            w.bind("<Leave>", self.on_leave)

    def widgets(self):
        return (self.frame, self.cal, self.inner, self.lbl_title, self.lbl_when,
                self.claude, self.claude_inner, *[w for cells in self.claude_rows.values() for w in cells.values()],
                self.net, self.net_inner, self.lbl_up, self.lbl_down)

    # ----------------------------------------------------------------------------- calendar data

    def refresh(self):
        self.refresh_now()
        self.root.after(int(self.cfg["refresh_minutes"]) * 60_000, self.refresh)

    def refresh_now(self):
        if not self.cal_width:
            return

        def work():
            try:
                if self.demo:
                    self.meetings, self.claude_usage = demo_data(dt.datetime.now().astimezone())
                    self.loaded, self.error = True, None
                elif not ics_urls(self.cfg):
                    self.error = "setup"
                else:
                    self.meetings = fetch_meetings(ics_urls(self.cfg), self.cfg["lookahead_days"], self.cfg["email"])
                    self.loaded, self.error = True, None
            except Exception as exc:
                log.warning("Calendar refresh failed: %s", exc)
                self.error = str(exc)
            try:
                self.root.after(0, self.render)
            except RuntimeError:
                pass  # Tk has shut down

        threading.Thread(target=work, daemon=True).start()

    # ----------------------------------------------------------------------------- rendering

    def render(self):
        now = dt.datetime.now().astimezone()
        self.current = pick_next(self.meetings, now)
        when_color = DIM
        if self.error == "setup":
            title, when = "Set up your calendar", "Right-click › Settings"
        elif self.current:
            title = self.current.title
            when = fmt_when(self.current, now)
            if self.current.start <= now:
                when_color = GREEN
            elif self.current.start - now <= dt.timedelta(minutes=10):
                when_color = AMBER
        elif not self.loaded and self.error:
            title, when = "Calendar unavailable", "Will retry…"
        elif not self.loaded:
            title, when = "Loading calendar…", ""
        else:
            title, when = "No upcoming meetings", f"Next {self.cfg['lookahead_days']} days"
        if self.error and self.loaded and self.error != "setup":
            when = "⚠ " + when

        maxw = self.cal_width - int(16 * self.scale)
        self.lbl_title.config(text=self.ellipsize(title, maxw))
        self.lbl_when.config(text=self.ellipsize(when, maxw), fg=when_color)
        self.render_claude()
        if self.peek:
            self.show_peek()

    def ellipsize(self, text, maxw):
        if self.font.measure(text) <= maxw:
            return text
        while text and self.font.measure(text + "…") > maxw:
            text = text[:-1]
        return text.rstrip() + "…"

    def claude_windows(self, now):
        """{key: (long label, pct, resets_at, elapsed fraction, runs-out epoch)}; a passed reset means 0%."""
        rows = {}
        usage = self.claude_usage or {}
        now_ts = now.timestamp()
        for key, long in (("five_hour", "5-hour"), ("seven_day", "Weekly")):
            w = usage.get(key)
            if not isinstance(w, dict):
                if usage:  # Claude Code drops a window once it resets; it restarts when you next use Claude
                    rows[key] = (long, 0.0, None, None, None)
                continue
            pct, resets = float(w.get("used_percentage") or 0), w.get("resets_at")
            if resets and resets <= now_ts:
                pct, resets = 0.0, None  # window rolled over since usage was last reported
            elapsed, runs_out = cu.usage_pace(pct, resets, cu.WINDOW_SECONDS[key], now_ts) if resets else (None, None)
            rows[key] = (long, pct, resets, elapsed, runs_out)
        return rows

    def render_claude(self):
        if not self.claude_width:
            return
        now = dt.datetime.now().astimezone()
        windows = self.claude_windows(now)
        stale = self.claude_usage_age() > 3 * 3600
        track = "#c8c8c8" if self.light_taskbar else "#4a4a4a"
        tick = "#000000" if self.light_taskbar else "#ffffff"
        for key, cells in self.claude_rows.items():
            bar = cells["bar"]
            bar.delete("all")
            bar.create_rectangle(0, 0, self.bar_w, self.bar_h, fill=track, width=0)
            if key not in windows:
                cells["pct"].config(text="–", fg=GREY)
                cells["reset"].config(text="")
                continue
            _, pct, resets, elapsed, runs_out = windows[key]
            color = GREY if stale else cu.usage_color(pct, runs_out is not None)
            if color == "#ffffff" and self.light_taskbar:
                color = "#000000"
            bar.create_rectangle(0, 0, round(self.bar_w * min(pct, 100) / 100), self.bar_h, fill=color, width=0)
            if elapsed is not None:
                x = min(self.bar_w - 1, round(self.bar_w * elapsed))
                bar.create_line(x, 0, x, self.bar_h, fill=tick, width=max(1, int(self.scale)))
            cells["pct"].config(text=f"{pct:.0f}%", fg=color)
            cells["reset"].config(text=f"↻ {cu.fmt_countdown(resets - now.timestamp())}" if resets else "")

    def set_bg(self, color):
        self.bg = color
        for w in self.widgets():
            w.config(bg=color)

    def match_taskbar_colors(self):
        """Copy the taskbar colour from both sides of the widget (the tinted taskbar is a gentle gradient)."""
        tb, _, _, _ = winapi.taskbar_layout()
        if tb is None or not self.geometry:
            return
        w, h, x, y = self.geometry
        mid = y + h // 2
        xs = (max(tb.left, x - int(4 * self.scale)), min(tb.right - 1, x + w + int(3 * self.scale)))
        samples = [winapi.screen_pixel(sx, mid + dy) for sx in xs for dy in (-int(8 * self.scale), 0, int(8 * self.scale))]
        samples = [c for c in samples if c]
        if not samples:
            return
        rgb = tuple(round(sum(c[i] for c in samples) / len(samples)) for i in range(3))
        light = sum(rgb) / 3 > 140
        delta = -14 if light else 18
        self.base_bg = "#%02x%02x%02x" % rgb
        self.hover_bg = "#%02x%02x%02x" % tuple(max(0, min(255, c + delta)) for c in rgb)
        self.set_bg(self.hover_bg if self.hover else self.base_bg)
        for label in (self.lbl_title, self.lbl_up, self.lbl_down):
            label.config(fg="#000000" if light else "#ffffff")
        if light != self.light_taskbar:
            self.light_taskbar = light
            self.render_claude()

    # ----------------------------------------------------------------------------- events (no polling)

    def pump_events(self):
        # Runs on a helper thread; root.after from here is marshalled safely onto Tk's thread.
        while True:
            event = self.events.get()
            try:
                self.root.after(0, self.on_shell_event, event)
            except RuntimeError:
                return  # Tk has shut down

    def on_shell_event(self, event):
        try:
            if event == "foreground":
                winapi.raise_topmost(self.hwnd) if self.visible else None
                self.schedule_layout()
            elif event in ("layout", "fullscreen"):
                self.schedule_layout()
            elif event == "taskbar-restarted":
                self.shell.register_appbar()
                self.schedule_layout()
                self.root.after(1000, self.match_taskbar_colors)
                self.root.after(1000, self.find_weather_widget)
            elif event == "display":
                self.schedule_layout()
                self.root.after(500, lambda: self.hover or self.match_taskbar_colors())
                # Taskbar alignment / Widgets toggles arrive as setting changes; give the taskbar time to re-flow.
                self.root.after(700, self.find_weather_widget)
            elif event == "time":
                self.minute_tick()
            elif event == "claude-activity":
                # A Claude Code session wrote its transcript, so usage moved; check once things settle.
                self.schedule_usage_check(USAGE_AFTER_ACTIVITY)
            elif event == "claude-usage":
                usage = cu.load_usage()
                if usage:
                    self.claude_usage = usage
                    self.render_claude()
                    if self.peek:
                        self.show_peek()
            elif event == "resume":
                self.refresh_now()
                self.minute_tick()
                self.schedule_usage_check(10)
            elif event == "open-settings":
                self.open_settings()
        except Exception:
            log.exception("event %s failed", event)

    def minute_tick(self):
        """Runs on each minute boundary, the resolution of the countdowns."""
        if self.minute_timer:
            self.root.after_cancel(self.minute_timer)
        self.render()
        self.apply_layout()  # cheap safety net for anything Windows didn't tell us about
        if not self.hover:
            self.match_taskbar_colors()
        # The weather text (so its width) changes with the forecast; adapters come and go (USB Wi-Fi, docks).
        self.find_weather_widget()
        if self.netmeter:
            self.netmeter.find_adapters()
        # Chats on claude.ai or in the desktop app's Chat tab leave no local trace, so also check periodically.
        if self.visible and self.claude_usage_age() > USAGE_PERIODIC:
            self.schedule_usage_check(0)
        delay = 60 - (time.time() % 60) + 0.2
        self.minute_timer = self.root.after(int(delay * 1000), self.minute_tick)

    # ----------------------------------------------------------------------------- Claude usage

    def claude_usage_age(self):
        return time.time() - (self.claude_usage or {}).get("updated", 0)

    def schedule_usage_check(self, delay):
        """One check after `delay` s, at most once per USAGE_MIN_INTERVAL and never while the usage service has
        asked us to back off; extra triggers in the meantime merge into the pending one."""
        if not self.auto_usage or self.usage_timer or self.usage_running:
            return
        delay = max(delay, self.last_usage_check + USAGE_MIN_INTERVAL - time.time(),
                    self.usage_paused_until - time.time())
        self.usage_timer = self.root.after(int(delay * 1000), self.check_usage)

    def check_usage(self):
        self.usage_timer = None
        self.usage_running = True
        exe = self.claude_exe
        probe_allowed = exe and time.time() - self.last_probe > PROBE_FALLBACK_INTERVAL

        def work():
            pause = 0
            probed = False
            try:
                cu.save_usage(cu.fetch_usage(exe))  # the file watcher then updates the display
            except cu.RateLimited as exc:
                pause = exc.retry_after
                log.info("Usage service asked to slow down; next check in %s s", pause)
            except cu.NotSignedIn as exc:
                # Last resort, at most every half hour: the paid probe works whenever the CLI can run at all.
                log.info("%s", exc)
                if probe_allowed:
                    probed = True
                    windows = cu.probe_usage(exe)
                    if windows:
                        cu.save_usage(windows)
            except Exception as exc:  # offline, service hiccup: keep the last numbers and try again later
                log.warning("Usage check failed: %s", exc)
            finally:
                try:
                    self.root.after(0, self.usage_check_done, pause, probed)
                except RuntimeError:
                    pass

        threading.Thread(target=work, daemon=True).start()

    def usage_check_done(self, pause, probed):
        self.usage_running = False
        self.last_usage_check = time.time()
        if pause:
            self.usage_paused_until = time.time() + pause
        if probed:
            self.last_probe = time.time()

    # ----------------------------------------------------------------------------- network speed

    def net_tick(self):
        """Network speed is the one thing Windows can't push to us: sampled once a second, only while visible."""
        if self.net_timer:
            self.root.after_cancel(self.net_timer)
            self.net_timer = None
        if not self.net_width or not self.visible:
            if self.netmeter:
                self.netmeter.last = None  # don't average over the hidden period
            return
        try:
            speeds = (1.2 * 1024 * 1024, 18.6 * 1024 * 1024) if self.demo else self.netmeter.sample()
            if speeds:
                up, down = f"↑ {fmt_speed(speeds[0])}", f"↓ {fmt_speed(speeds[1])}"
                if self.lbl_up.cget("text") != up:
                    self.lbl_up.config(text=up)
                if self.lbl_down.cget("text") != down:
                    self.lbl_down.config(text=down)
            elif not self.lbl_up.cget("text"):
                self.lbl_up.config(text="↑ –")
                self.lbl_down.config(text="↓ –")
        except Exception:
            log.exception("network sample failed")
        self.net_timer = self.root.after(1000, self.net_tick)

    # ----------------------------------------------------------------------------- positioning

    def find_weather_widget(self):
        if winapi.UIA_LOCK.locked():
            return  # a lookup is already in flight

        def work():
            if not winapi.UIA_LOCK.acquire(blocking=False):
                return
            try:
                span = winapi.weather_widget_span()
            except Exception:
                log.exception("weather widget lookup failed")
                return
            finally:
                winapi.UIA_LOCK.release()
            try:
                self.root.after(0, self.set_weather_span, span)
            except RuntimeError:
                pass

        threading.Thread(target=work, daemon=True).start()

    def set_weather_span(self, span):
        if span != self.weather_span:
            self.weather_span = span
            self.schedule_layout()

    def schedule_layout(self):
        # Coalesce bursts (e.g. a neighbour sliding over) into one layout pass.
        if not self.layout_pending:
            self.layout_pending = True
            self.root.after(30, self.apply_layout)

    def apply_layout(self):
        self.layout_pending = False
        try:
            tb, limit, tray, neighbours = winapi.taskbar_layout()
            self.shell.watch(tray, neighbours)
            width = self.cal_width + self.claude_width + self.net_width
            if tb is None or not width or winapi.foreground_is_fullscreen():
                if self.visible:
                    self.hide_peek()
                    self.root.withdraw()
                    self.visible = False
                    self.net_tick()  # stops sampling while hidden
                return
            # A left-aligned taskbar puts the weather widget on the right, next to the tray.
            if self.weather_span and self.weather_span[0] > tb.left + (tb.right - tb.left) // 2:
                limit = min(limit, self.weather_span[0])
            h = (tb.bottom - tb.top) - int(6 * self.scale)
            if self.cfg.get("x") is not None:
                x = int(self.cfg["x"])
            else:
                x = limit - int(self.cfg["right_gap"] * self.scale) - width
            y = tb.top + ((tb.bottom - tb.top) - h) // 2
            geo = (width, h, x, y)
            # Compare with where the window really is, not where we last put it: other programs (Win+D, screen
            # capture tools, display changes) can minimize, move or resize it behind our back.
            actual = winapi.rect_of(self.hwnd)
            displaced = (actual.right - actual.left, actual.bottom - actual.top, actual.left, actual.top) != geo
            if self.visible and winapi.user32.IsIconic(self.hwnd):
                self.root.deiconify()
                displaced = True
            if geo != self.geometry or displaced:
                if displaced and self.geometry == geo:
                    log.info("Widget window was moved or resized by something else; putting it back")
                self.geometry = geo
                self.root.geometry("%dx%d+%d+%d" % geo)
            if not self.visible:
                self.match_taskbar_colors()
                self.root.deiconify()
                self.visible = True
                self.net_tick()
            winapi.raise_topmost(self.hwnd)
        except Exception:
            log.exception("layout failed")

    # ----------------------------------------------------------------------------- interaction

    def on_enter(self, _):
        # Every label in the widget sends its own Enter/Leave; treat the whole widget as one hover target.
        self._cancel("leave_timer")
        if self.hover:
            return
        self.hover = True
        if self.claude_usage_age() > USAGE_ON_HOVER:
            self.schedule_usage_check(0)
        self.set_bg(getattr(self, "hover_bg", self.bg))
        self.peek_timer = self.root.after(450, self.show_peek)

    def on_leave(self, _):
        # Moving between the widget's own labels also fires Leave, so confirm the pointer really left.
        self._cancel("leave_timer")
        self.leave_timer = self.root.after(80, self._confirm_leave)

    def _confirm_leave(self):
        self.leave_timer = None
        if self._pointer_inside():
            return
        self.hover = False
        self._cancel("peek_timer")
        self.set_bg(getattr(self, "base_bg", self.bg))
        self.hide_peek()

    def _pointer_inside(self):
        if not self.geometry:
            return False
        x, y = self.root.winfo_pointerxy()
        w, h, left, top = self.geometry
        return left <= x < left + w and top <= y < top + h

    def _cancel(self, name):
        timer = getattr(self, name, None)
        if timer:
            self.root.after_cancel(timer)
        setattr(self, name, None)

    def _peek_rows(self, now):
        """What the popup shows, as plain data, so it's only rebuilt when something actually changed."""
        rows = []
        upcoming = [m for m in self.meetings if m.end > now][:6] if self.cal_width else []
        last_day = None
        for m in upcoming:
            day = m.start.date()
            if day != last_day:
                days = (day - now.date()).days
                label = "Today" if days == 0 else "Tomorrow" if days == 1 else m.start.strftime("%A, %b %d")
                rows.append(("heading", label, 4 if last_day else 0))
                last_day = day
            rows.append(("row", f"{fmt_time(m.start)}–{fmt_time(m.end)}", GREEN if m.start <= now else DIM,
                         self.ellipsize(m.title, int(260 * self.scale)), "#ffffff"))

        claude_rows = list(self.claude_windows(now).values()) if self.claude_width else []
        if claude_rows:
            rows.append(("heading", "Claude usage", 8 if upcoming else 0))
            for long, pct, resets, elapsed, runs_out in claude_rows:
                detail = f"{pct:.0f}% used"
                if resets:
                    detail += f" · resets in {cu.fmt_countdown(resets - now.timestamp())} ({cu.fmt_reset(resets, now)})"
                else:
                    detail += " · starts when you next use Claude"
                rows.append(("row", long, DIM, detail, cu.usage_color(pct, runs_out is not None)))
                if runs_out:
                    rows.append(("note", f"At this rate you'll hit the limit around {cu.fmt_reset(runs_out, now)}, "
                                         f"{cu.fmt_countdown(resets - runs_out)} before it resets", AMBER))
                elif elapsed is not None and elapsed >= 0.05 and pct > 0:
                    rows.append(("note", f"At this rate you'll be at ~{min(100, pct / elapsed):.0f}% when it resets",
                                 GREY))
            age = int(self.claude_usage_age() // 60)
            ago = "just now" if age < 1 else f"{age} min ago" if age < 60 else f"{age // 60} h ago"
            rows.append(("note", f"Updated {ago}", GREY))
        return rows

    def show_peek(self):
        self.peek_timer = None
        if not self.hover:
            return
        rows = self._peek_rows(dt.datetime.now().astimezone())
        if not rows:
            self.hide_peek()
            return
        if self.peek is not None and rows == self.peek_rows:
            return  # nothing changed: leave it alone (rebuilding is what made it blink)
        self.peek_rows = rows

        first = self.peek is None
        if first:
            self.peek = tk.Toplevel(self.root, bg=POPUP_BG)
            self.peek.withdraw()  # build and place it before it's ever visible
            self.peek.overrideredirect(True)
            self.peek.attributes("-topmost", True)

        box = tk.Frame(self.peek, bg=POPUP_BG, padx=12, pady=10, highlightthickness=1, highlightbackground=POPUP_BORDER)
        for row in rows:
            if row[0] == "heading":
                tk.Label(box, text=row[1], bg=POPUP_BG, fg=GREY, font=self.font_small_bold,
                         anchor="w").pack(fill="x", pady=(row[2], 2))
            elif row[0] == "row":
                line = tk.Frame(box, bg=POPUP_BG)
                line.pack(fill="x")
                tk.Label(line, text=row[1], width=17, anchor="w", bg=POPUP_BG, fg=row[2],
                         font=self.font).pack(side="left")
                tk.Label(line, text=row[3], anchor="w", bg=POPUP_BG, fg=row[4], font=self.font).pack(side="left")
            else:
                tk.Label(box, text=row[1], bg=POPUP_BG, fg=row[2], font=self.font_small, anchor="w").pack(fill="x")
        # Swap the new content in only once it's complete, so an open popup never shows a half-built state.
        old = [c for c in self.peek.winfo_children() if c is not box]
        box.pack(fill="both", expand=True)
        for child in old:
            child.destroy()

        self.peek.update_idletasks()
        pw, ph = self.peek.winfo_reqwidth(), self.peek.winfo_reqheight()
        if self.geometry:
            w, h, x, y = self.geometry
            px = max(8, min(x + w - pw, self.root.winfo_screenwidth() - pw - 8))
            self.peek.geometry(f"{pw}x{ph}+{px}+{y - ph - int(14 * self.scale)}")
        if first:
            self.peek.update_idletasks()
            winapi.style_as_taskbar_item(winapi.user32.GetParent(self.peek.winfo_id()) or self.peek.winfo_id())
            self.peek.deiconify()

    def hide_peek(self):
        self.peek_rows = None
        if self.peek is not None:
            self.peek.destroy()
            self.peek = None

    def open_calendar(self):
        self.hide_peek()
        open_url(calendar_url(self.cfg), self.cfg.get("chrome_profile", ""))

    def open_settings(self, first_run=False):
        from .settings_dialog import SettingsDialog  # only loaded when needed

        self.hide_peek()
        if self.settings:
            self.settings.bring_to_front()
            return
        self.settings = SettingsDialog(self.root, self.cfg, on_saved=self.restart, on_close=self._settings_closed,
                                       first_run=first_run, demo_profiles=DEMO_PROFILES if self.demo else None)

    def _settings_closed(self):
        self.settings = None

    def show_menu(self, e):
        self.hide_peek()
        self.menu.delete(0, "end")
        if self.current and self.current.link:
            self.menu.add_command(label="Join video call", command=lambda link=self.current.link: webbrowser.open(link))
            self.menu.add_separator()
        if self.cal_width:
            self.menu.add_command(label="Open Google Calendar", command=self.open_calendar)
            self.menu.add_command(label="Refresh calendar", command=self.refresh_now)
        self.menu.add_command(label="Settings…", command=self.open_settings)
        self.menu.add_separator()
        self.menu.add_command(label="Quit", command=self.quit)
        self.menu.tk_popup(e.x_root, e.y_root)

    def restart(self):
        """Settings changed: start a fresh copy with them and exit."""
        if self.restart_app and not self.demo:
            self.quit(restart=True)

    def quit(self, restart=False):
        self.shell.close()
        self.root.destroy()
        if restart and self.restart_app:
            self.restart_app()

    def run(self):
        self.root.mainloop()
