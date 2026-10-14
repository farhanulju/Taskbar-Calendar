"""Settings and where things live on disk.

Settings: %APPDATA%\\TaskbarCalendar\\config.json (edited through the Settings window).
Data and logs: %LOCALAPPDATA%\\TaskbarCalendar\\.
"""

import json
import logging
import os
import shutil
from pathlib import Path

APP_NAME = "TaskbarCalendar"
CONFIG_DIR = Path(os.environ.get("APPDATA") or Path.home()) / APP_NAME
DATA_DIR = Path(os.environ.get("LOCALAPPDATA") or Path.home()) / APP_NAME
CONFIG_PATH = CONFIG_DIR / "config.json"
LOG_PATH = DATA_DIR / "taskbar_calendar.log"
PROJECT_DIR = Path(__file__).resolve().parent.parent
LEGACY_CONFIG_PATH = PROJECT_DIR / "config.json"  # pre-1.0 versions kept settings next to the script
ICON_PATH = PROJECT_DIR / "assets" / "icon.ico"

CALENDAR_VIEWS = {
    "agenda": ("Agenda", "https://calendar.google.com/calendar/r/agenda"),
    "day": ("Day", "https://calendar.google.com/calendar/r/day"),
    "week": ("Week", "https://calendar.google.com/calendar/r/week"),
    "month": ("Month", "https://calendar.google.com/calendar/r/month"),
}

DEFAULT_CONFIG = {
    # Google Calendar "Secret address in iCal format" links, one per calendar.
    "ics_urls": [],
    # Used to hide meetings you declined; detected from the feed when empty.
    "email": "",
    # Chrome profile folder ("Default", "Profile 2", ...) to open the calendar in; empty = default browser.
    "chrome_profile": "",
    "calendar_view": "agenda",
    "refresh_minutes": 5,
    "lookahead_days": 7,
    "show_calendar": True,
    "show_claude": True,
    # Refresh Claude usage with a tiny headless CLI request while Claude Code is in use (the desktop app
    # doesn't run status lines, so this is how it gets numbers).
    "claude_probe": True,
    "show_network": True,
    # Width of the meeting text, and the gap to whatever sits to the widget's right (px at 100% scaling).
    "width": 210,
    "right_gap": 6,
    # Pin the widget's left edge to this screen x (physical px) instead of placing it automatically.
    "x": None,
}

log = logging.getLogger(__name__)


def _upgrade(cfg):
    """Accept settings written by older versions."""
    if "ics_url" in cfg and "ics_urls" not in cfg:
        old = cfg.pop("ics_url")
        cfg["ics_urls"] = [old] if isinstance(old, str) else list(old or [])
    url = cfg.pop("calendar_url", None)
    if url and "calendar_view" not in cfg:
        for key, (_, view_url) in CALENDAR_VIEWS.items():
            if url.rstrip("/") == view_url:
                cfg["calendar_view"] = key
    return cfg


def load_config():
    if not CONFIG_PATH.exists() and LEGACY_CONFIG_PATH.exists():
        CONFIG_DIR.mkdir(parents=True, exist_ok=True)
        shutil.move(str(LEGACY_CONFIG_PATH), str(CONFIG_PATH))
        log.info("Moved settings from %s to %s", LEGACY_CONFIG_PATH, CONFIG_PATH)
    try:
        cfg = json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
    except FileNotFoundError:
        cfg = {}
    except ValueError:
        log.exception("%s is not valid JSON; using defaults", CONFIG_PATH)
        cfg = {}
    return {**DEFAULT_CONFIG, **_upgrade(cfg)}


def save_config(cfg):
    CONFIG_DIR.mkdir(parents=True, exist_ok=True)
    data = {key: cfg.get(key, default) for key, default in DEFAULT_CONFIG.items()}
    tmp = CONFIG_PATH.with_suffix(".tmp")
    tmp.write_text(json.dumps(data, indent=2), encoding="utf-8")
    os.replace(tmp, CONFIG_PATH)


def is_first_run():
    return not CONFIG_PATH.exists() and not LEGACY_CONFIG_PATH.exists()


def normalize_ics_url(url):
    url = url.strip()
    if url.lower().startswith("webcal://"):
        url = "https://" + url[len("webcal://"):]
    return url


def ics_urls(cfg):
    urls = cfg.get("ics_urls") or []
    if isinstance(urls, str):
        urls = [urls]
    return [normalize_ics_url(u) for u in urls if u and u.strip()]


def calendar_url(cfg):
    return CALENDAR_VIEWS.get(cfg.get("calendar_view"), CALENDAR_VIEWS["agenda"])[1]
