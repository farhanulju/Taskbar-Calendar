"""Settings window. Uses ttk's native Windows theme so it looks like a regular Windows dialog."""

import logging
import os
import threading
import tkinter as tk
import webbrowser
from tkinter import messagebox, ttk

from . import REPO_URL, __version__, chrome, claude_usage, shortcuts
from .calendar_feed import fetch_meetings
from .config import CALENDAR_VIEWS, CONFIG_DIR, DATA_DIR, normalize_ics_url, save_config

log = logging.getLogger(__name__)

ICS_HELP_URL = "https://support.google.com/calendar/answer/37648#zippy=%2Cget-your-calendar-view-only"
CLAUDE_INSTALL_URL = "https://code.claude.com/docs/en/setup"
DEFAULT_BROWSER = "Default browser"
PAD = 12


class SettingsDialog:
    def __init__(self, root, cfg, on_saved, on_close=None, first_run=False, demo_profiles=None):
        """`demo_profiles` (for screenshots) replaces the real Chrome profiles, and Save then changes nothing."""
        self.cfg = dict(cfg)
        self.demo = demo_profiles is not None
        self.demo_profiles = demo_profiles
        self.on_saved = on_saved
        self.on_close = on_close
        self.win = win = tk.Toplevel(root)
        win.title("Taskbar Calendar settings")
        win.resizable(False, False)
        win.protocol("WM_DELETE_WINDOW", self.close)
        win.bind("<Escape>", lambda e: self.close())

        style = ttk.Style(win)
        if "vista" in style.theme_names():
            style.theme_use("vista")
        style.configure("Hint.TLabel", foreground="#5f5f5f")
        style.configure("Link.TLabel", foreground="#0067c0")
        style.configure("Good.TLabel", foreground="#0f7b0f")
        style.configure("Bad.TLabel", foreground="#c42b1c")
        style.configure("Heading.TLabel", font=("Segoe UI Semibold", 10))

        outer = ttk.Frame(win, padding=PAD)
        outer.pack(fill="both", expand=True)
        if first_run:
            ttk.Label(outer, text="Welcome! Paste your Google Calendar link below to get started.",
                      style="Heading.TLabel").pack(anchor="w", pady=(0, PAD))
        nb = ttk.Notebook(outer)
        nb.pack(fill="both", expand=True)
        for title, build in (("Calendar", self._calendar_tab), ("Claude usage", self._claude_tab),
                             ("Display", self._display_tab), ("General", self._general_tab)):
            tab = ttk.Frame(nb, padding=PAD)
            build(tab)
            nb.add(tab, text=title)

        buttons = ttk.Frame(outer)
        buttons.pack(fill="x", pady=(PAD, 0))
        ttk.Button(buttons, text="Cancel", command=self.close).pack(side="right")
        ttk.Button(buttons, text="Save", command=self.save, default="active").pack(side="right", padx=(0, 8))

        win.update_idletasks()
        x = (win.winfo_screenwidth() - win.winfo_reqwidth()) // 2
        y = (win.winfo_screenheight() - win.winfo_reqheight()) // 3
        win.geometry(f"+{x}+{y}")
        self.bring_to_front()

    # ----------------------------------------------------------------------------- helpers

    def bring_to_front(self):
        self.win.deiconify()
        self.win.lift()
        self.win.attributes("-topmost", True)
        self.win.after(200, lambda: self.win.attributes("-topmost", False))
        self.win.focus_force()

    def _hint(self, parent, text, **pack):
        label = ttk.Label(parent, text=text, style="Hint.TLabel", wraplength=470, justify="left")
        label.pack(anchor="w", **pack)
        return label

    def _link(self, parent, text, url, **pack):
        label = ttk.Label(parent, text=text, style="Link.TLabel", cursor="hand2", font=("Segoe UI", 9, "underline"))
        label.bind("<Button-1>", lambda e: webbrowser.open(url))
        label.pack(anchor="w", **pack)
        return label

    def _spin(self, parent, label, var, lo, hi, suffix):
        row = ttk.Frame(parent)
        row.pack(anchor="w", pady=2)
        ttk.Label(row, text=label).pack(side="left")
        ttk.Spinbox(row, from_=lo, to=hi, textvariable=var, width=5, justify="right").pack(side="left", padx=6)
        ttk.Label(row, text=suffix).pack(side="left")

    # ----------------------------------------------------------------------------- tabs

    def _calendar_tab(self, tab):
        ttk.Label(tab, text="Google Calendar secret address (iCal format)", style="Heading.TLabel").pack(anchor="w")
        self._hint(tab, "In Google Calendar, open Settings, pick your calendar, then Integrate calendar and copy "
                        "\"Secret address in iCal format\". For more than one calendar, put each link on its own line.")
        self._link(tab, "Show me where to find it", ICS_HELP_URL, pady=(2, 6))
        self.ics_text = tk.Text(tab, height=3, width=64, wrap="none", font=("Consolas", 9), relief="solid",
                                borderwidth=1, highlightthickness=0)
        self.ics_text.insert("1.0", "\n".join(self.cfg.get("ics_urls") or []))
        self.ics_text.pack(anchor="w", fill="x")
        self._hint(tab, "Anyone with this link can see your calendar, so it's stored only on this PC.", pady=(4, 0))

        test = ttk.Frame(tab)
        test.pack(anchor="w", pady=(6, 0), fill="x")
        ttk.Button(test, text="Test connection", command=self.test_calendar).pack(side="left")
        self.test_result = ttk.Label(test, text="")
        self.test_result.pack(side="left", padx=8)

        ttk.Separator(tab).pack(fill="x", pady=PAD)
        ttk.Label(tab, text="When you click the widget", style="Heading.TLabel").pack(anchor="w")
        self.profiles = {DEFAULT_BROWSER: ""}
        real = chrome.list_profiles() if chrome.find_chrome() else []
        for folder, name, email in self.demo_profiles if self.demo else real:
            self.profiles[f"Chrome: {name}" + (f" ({email})" if email else "")] = folder
        current = next((k for k, v in self.profiles.items() if v == self.cfg.get("chrome_profile")), DEFAULT_BROWSER)
        self.profile_var = tk.StringVar(self.win, current)
        row = ttk.Frame(tab)
        row.pack(anchor="w", pady=(4, 2))
        ttk.Label(row, text="Open Google Calendar in").pack(side="left")
        ttk.Combobox(row, textvariable=self.profile_var, values=list(self.profiles), state="readonly",
                     width=46).pack(side="left", padx=6)
        self.view_var = tk.StringVar(self.win, CALENDAR_VIEWS.get(self.cfg.get("calendar_view"), CALENDAR_VIEWS["agenda"])[0])
        row = ttk.Frame(tab)
        row.pack(anchor="w", pady=2)
        ttk.Label(row, text="Showing the").pack(side="left")
        ttk.Combobox(row, textvariable=self.view_var, values=[v[0] for v in CALENDAR_VIEWS.values()],
                     state="readonly", width=10).pack(side="left", padx=6)
        ttk.Label(row, text="view").pack(side="left")

        ttk.Separator(tab).pack(fill="x", pady=PAD)
        self.lookahead_var = tk.IntVar(self.win, self.cfg.get("lookahead_days", 7))
        self.refresh_var = tk.IntVar(self.win, self.cfg.get("refresh_minutes", 5))
        self._spin(tab, "Show meetings for the next", self.lookahead_var, 1, 30, "days")
        self._spin(tab, "Check for calendar changes every", self.refresh_var, 1, 60, "minutes")

    def _claude_tab(self, tab):
        self.show_claude_var = tk.BooleanVar(self.win, self.cfg.get("show_claude", True))
        ttk.Checkbutton(tab, text="Show my Claude plan usage (5-hour and weekly limits)",
                        variable=self.show_claude_var).pack(anchor="w")
        self._hint(tab, "Shows how much of each limit you've used, when it resets, and whether you're on pace to "
                        "run out early. Needs a Claude Pro or Max plan.", pady=(2, 0))

        ttk.Separator(tab).pack(fill="x", pady=PAD)
        ttk.Label(tab, text="Keeping the numbers current", style="Heading.TLabel").pack(anchor="w")
        exe = claude_usage.find_claude_exe()
        status = ttk.Frame(tab)
        status.pack(anchor="w", pady=(4, 6))
        if exe:
            ttk.Label(status, text="✓ Claude Code CLI found", style="Good.TLabel").pack(side="left")
        else:
            ttk.Label(status, text="✗ Claude Code CLI not found", style="Bad.TLabel").pack(side="left")
            self._link(status, "How to install it", CLAUDE_INSTALL_URL).pack(side="left", padx=8)

        self.probe_var = tk.BooleanVar(self.win, self.cfg.get("claude_probe", True))
        ttk.Checkbutton(tab, text="Refresh automatically while I use Claude (recommended)",
                        variable=self.probe_var).pack(anchor="w")
        self._hint(tab, "After you use Claude Code (desktop app or CLI), the widget sends one tiny request through "
                        "the Claude CLI (about 1,000 tokens on Haiku) to read your usage. At most once every "
                        "5 minutes, and never while you're idle.", pady=(2, 8))

        state = claude_usage.statusline_state()
        self.statusline_initial = state == "ours"
        self.statusline_var = tk.BooleanVar(self.win, self.statusline_initial)
        check = ttk.Checkbutton(tab, text="Also update from the Claude Code CLI status line (free)",
                                variable=self.statusline_var)
        check.pack(anchor="w")
        if state == "other":
            check.state(["disabled"])
            self._hint(tab, "You already have your own Claude Code status line, so this is left alone.", pady=(2, 0))
        elif state == "error":
            check.state(["disabled"])
            self._hint(tab, "Couldn't read your Claude Code settings (~/.claude/settings.json).", pady=(2, 0))
        else:
            self._hint(tab, "Adds a status line to ~/.claude/settings.json that saves your usage after each CLI "
                            "reply. The desktop app doesn't run status lines.", pady=(2, 0))

    def _display_tab(self, tab):
        ttk.Label(tab, text="Show on the taskbar", style="Heading.TLabel").pack(anchor="w")
        self.show_calendar_var = tk.BooleanVar(self.win, self.cfg.get("show_calendar", True))
        self.show_network_var = tk.BooleanVar(self.win, self.cfg.get("show_network", True))
        ttk.Checkbutton(tab, text="Next meeting", variable=self.show_calendar_var).pack(anchor="w", pady=(4, 0))
        ttk.Label(tab, text="Claude usage: see the Claude usage tab", style="Hint.TLabel").pack(anchor="w", padx=(22, 0))
        ttk.Checkbutton(tab, text="Network upload and download speed", variable=self.show_network_var).pack(anchor="w")

        ttk.Separator(tab).pack(fill="x", pady=PAD)
        ttk.Label(tab, text="Size and position", style="Heading.TLabel").pack(anchor="w")
        self.width_var = tk.IntVar(self.win, self.cfg.get("width", 210))
        self.gap_var = tk.IntVar(self.win, self.cfg.get("right_gap", 6))
        self._spin(tab, "Meeting text width", self.width_var, 120, 500, "px")
        self._spin(tab, "Space to the right of the widget", self.gap_var, 0, 400, "px")
        self._hint(tab, "The widget sits just left of the system tray, or of the weather widget when your taskbar "
                        "icons are left-aligned. Increase the space to move it further left.", pady=(2, 0))

    def _general_tab(self, tab):
        self.startup_var = tk.BooleanVar(self.win, True if self.demo else shortcuts.startup_enabled())
        ttk.Checkbutton(tab, text="Start Taskbar Calendar when I sign in to Windows",
                        variable=self.startup_var).pack(anchor="w")

        ttk.Separator(tab).pack(fill="x", pady=PAD)
        ttk.Label(tab, text="Files", style="Heading.TLabel").pack(anchor="w")
        row = ttk.Frame(tab)
        row.pack(anchor="w", pady=(4, 0))
        ttk.Button(row, text="Open settings folder", command=lambda: self._open_dir(CONFIG_DIR)).pack(side="left")
        ttk.Button(row, text="Open log folder", command=lambda: self._open_dir(DATA_DIR)).pack(side="left", padx=8)

        ttk.Separator(tab).pack(fill="x", pady=PAD)
        ttk.Label(tab, text=f"Taskbar Calendar {__version__}", style="Heading.TLabel").pack(anchor="w")
        self._link(tab, REPO_URL.removeprefix("https://"), REPO_URL, pady=(2, 0))

    @staticmethod
    def _open_dir(path):
        path.mkdir(parents=True, exist_ok=True)
        os.startfile(path)

    # ----------------------------------------------------------------------------- actions

    def _urls(self):
        return [normalize_ics_url(line) for line in self.ics_text.get("1.0", "end").splitlines() if line.strip()]

    def test_calendar(self):
        urls = self._urls()
        if not urls:
            self.test_result.config(text="Paste your calendar link first.", style="Bad.TLabel")
            return
        self.test_result.config(text="Checking…", style="TLabel")
        days = self._int(self.lookahead_var, 7, 1, 30)

        def work():
            try:
                meetings = fetch_meetings(urls, days)
                text, style = f"✓ Connected. {len(meetings)} meetings in the next {days} days.", "Good.TLabel"
            except Exception as exc:
                log.info("Calendar test failed: %s", exc)
                text, style = "✗ Couldn't read that link. Check it's the secret iCal address.", "Bad.TLabel"
            try:
                self.win.after(0, lambda: self.test_result.config(text=text, style=style))
            except (RuntimeError, tk.TclError):
                pass  # dialog closed meanwhile

        threading.Thread(target=work, daemon=True).start()

    @staticmethod
    def _int(var, default, lo, hi):
        try:
            return max(lo, min(hi, int(var.get())))
        except (tk.TclError, ValueError):
            return default

    def save(self):
        if self.demo:
            self.close()
            return
        urls = self._urls()
        bad = [u for u in urls if not u.lower().startswith(("https://", "http://"))]
        if bad:
            messagebox.showerror("Taskbar Calendar", f"This doesn't look like a calendar link:\n\n{bad[0]}",
                                 parent=self.win)
            return
        view = next((k for k, (label, _) in CALENDAR_VIEWS.items() if label == self.view_var.get()), "agenda")
        self.cfg.update(
            ics_urls=urls,
            chrome_profile=self.profiles.get(self.profile_var.get(), ""),
            calendar_view=view,
            lookahead_days=self._int(self.lookahead_var, 7, 1, 30),
            refresh_minutes=self._int(self.refresh_var, 5, 1, 60),
            show_calendar=self.show_calendar_var.get(),
            show_claude=self.show_claude_var.get(),
            claude_probe=self.probe_var.get(),
            show_network=self.show_network_var.get(),
            width=self._int(self.width_var, 210, 120, 500),
            right_gap=self._int(self.gap_var, 6, 0, 400),
        )
        save_config(self.cfg)

        problems = []
        try:
            if self.startup_var.get() != shortcuts.startup_enabled():
                shortcuts.set_startup(self.startup_var.get())
        except Exception as exc:
            log.exception("Startup shortcut")
            problems.append(f"Couldn't change the startup setting: {exc}")
        try:
            if self.statusline_var.get() != self.statusline_initial:
                if self.statusline_var.get():
                    claude_usage.install_statusline()
                else:
                    claude_usage.remove_statusline()
        except Exception as exc:
            log.exception("Claude status line")
            problems.append(f"Couldn't update the Claude Code status line: {exc}")
        if problems:
            messagebox.showwarning("Taskbar Calendar", "Settings saved, but:\n\n" + "\n".join(problems), parent=self.win)

        self.close()
        self.on_saved()

    def close(self):
        try:
            self.win.destroy()
        finally:
            if self.on_close:
                self.on_close()
