"""Claude plan usage (5-hour and weekly windows).

Two sources write USAGE_FILE, and the widget redraws whenever it changes:
  * statusline.py, which Claude Code's CLI runs after each reply (free, CLI only), and
  * probe_usage(), a minimal headless `claude -p` request whose output stream carries a rate_limit_event with
    both windows. The desktop app doesn't run status lines, so this is what keeps it current there.
"""

import datetime as dt
import json
import logging
import os
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

from .calendar_feed import fmt_time
from .config import DATA_DIR

log = logging.getLogger(__name__)

USAGE_FILE = DATA_DIR / "claude_usage.json"  # statusline.py computes the same path; keep them in sync
PROJECTS_DIR = Path.home() / ".claude" / "projects"  # Claude Code (desktop and CLI) writes transcripts here
CLAUDE_SETTINGS = Path.home() / ".claude" / "settings.json"
STATUSLINE_SCRIPT = Path(__file__).resolve().parent / "statusline.py"
PROBE_MIN_INTERVAL = 300  # seconds between probes
WINDOW_SECONDS = {"five_hour": 5 * 3600, "seven_day": 7 * 86400}

PROBE_ARGS = [
    "-p", "ok", "--model", "haiku", "--max-turns", "1", "--output-format", "stream-json", "--verbose",
    "--no-session-persistence", "--tools", "", "--strict-mcp-config", "--disable-slash-commands",
    "--system-prompt", "Reply with OK.",
]


def load_usage():
    try:
        return json.loads(USAGE_FILE.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def atomic_write(path, text, attempts=40):
    """Write via a uniquely named temp file and an atomic replace, so readers never see half a file and two
    writers (the probe and the CLI status line) can't trip over each other. Windows refuses to replace a file
    while someone has it open, so retry briefly."""
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(dir=path.parent, prefix=path.stem + ".", suffix=".tmp")
    with os.fdopen(fd, "w", encoding="utf-8") as f:
        f.write(text)
    for attempt in range(attempts):
        try:
            os.replace(tmp, path)
            return
        except PermissionError:
            if attempt == attempts - 1:
                os.remove(tmp)
                raise
            time.sleep(0.05)


def save_usage(windows):
    atomic_write(USAGE_FILE, json.dumps({**windows, "updated": time.time()}))


def find_claude_exe():
    found = shutil.which("claude")
    if found:
        return found
    native = Path.home() / ".local" / "bin" / "claude.exe"
    return str(native) if native.exists() else None


def probe_tokens(text):
    """Total tokens the probe request used, from the stream's final result message (for the log)."""
    for line in reversed(text.splitlines()):
        try:
            msg = json.loads(line)
        except ValueError:
            continue
        if msg.get("type") == "result":
            usage = msg.get("usage") or {}
            return sum(usage.get(k) or 0 for k in
                       ("input_tokens", "output_tokens", "cache_creation_input_tokens", "cache_read_input_tokens"))
    return None


def parse_probe_output(text):
    """{window: {used_percentage, resets_at}} from `claude -p --output-format stream-json` output, or None."""
    for line in text.splitlines():
        try:
            msg = json.loads(line)
        except ValueError:
            continue
        if msg.get("type") != "rate_limit_event":
            continue
        unified = (msg.get("rate_limit_info") or {}).get("unifiedWindows") or {}
        windows = {
            key: {"used_percentage": round(float(w["utilization"]) * 100, 1), "resets_at": w.get("resetsAt")}
            for key, w in unified.items()
            if key in WINDOW_SECONDS and isinstance(w, dict) and "utilization" in w
        }
        if windows:
            return windows
    return None


def probe_usage(exe):
    """One minimal Haiku request (no tools, MCP servers, skills or saved session; ~1k tokens).
    Blocking, ~5 s: run it off the UI thread."""
    workdir = DATA_DIR / "probe"
    workdir.mkdir(parents=True, exist_ok=True)
    out = subprocess.run(
        [exe, *PROBE_ARGS], cwd=workdir, stdin=subprocess.DEVNULL, capture_output=True, timeout=90,
        creationflags=subprocess.CREATE_NO_WINDOW,
    )
    text = out.stdout.decode("utf-8", "replace")
    windows = parse_probe_output(text)
    log.info("Usage probe: %s tokens, %s", probe_tokens(text),
             {k: f"{v['used_percentage']:.0f}%" for k, v in (windows or {}).items()} or "no usage in response")
    if not windows:
        log.warning("Usage probe returned no usage (exit %s): %s", out.returncode,
                    out.stderr.decode("utf-8", "replace")[-300:])
    return windows


def fmt_countdown(seconds):
    minutes = max(0, int(seconds // 60))
    if minutes < 60:
        return f"{minutes}m"
    hours, minutes = divmod(minutes, 60)
    if hours < 24:
        return f"{hours}h {minutes:02d}m"
    days, hours = divmod(hours, 24)
    return f"{days}d {hours}h"


def usage_pace(pct, resets_at, length, now_ts):
    """(fraction of the window elapsed, epoch when you'd reach 100% at the current rate or None)."""
    elapsed = min(1.0, max(0.0, 1 - (resets_at - now_ts) / length))
    if pct <= 0 or elapsed < 0.05:
        return elapsed, None  # too early in the window for a meaningful rate
    rate = pct / (elapsed * length)  # percent per second
    hit = now_ts + (100 - pct) / rate
    return elapsed, (hit if hit < resets_at else None)


def usage_color(pct, runs_out=False):
    return "#ff99a4" if pct >= 95 else "#ffb900" if pct >= 80 or runs_out else "#ffffff"


def fmt_reset(epoch, now):
    t = dt.datetime.fromtimestamp(epoch).astimezone()
    if t.date() == now.date():
        return fmt_time(t)
    if (t.date() - now.date()).days == 1:
        return f"tomorrow {fmt_time(t)}"
    return f"{t.strftime('%a')} {fmt_time(t)}"


# ----------------------------------------------------------------------------- Claude Code status line


def _read_claude_settings():
    try:
        return json.loads(CLAUDE_SETTINGS.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return {}


def _is_ours(status_line):
    command = str((status_line or {}).get("command", "")).replace("\\", "/")
    return "taskbar_calendar/statusline.py" in command or "claude_statusline.py" in command


def statusline_state():
    """'ours', 'none', 'other' (you have your own status line; we leave it alone) or 'error'."""
    try:
        status_line = _read_claude_settings().get("statusLine")
    except (OSError, ValueError):
        return "error"
    if not status_line:
        return "none"
    return "ours" if _is_ours(status_line) else "other"


def _write_claude_settings(settings):
    CLAUDE_SETTINGS.parent.mkdir(parents=True, exist_ok=True)
    tmp = CLAUDE_SETTINGS.with_suffix(".tmp")
    tmp.write_text(json.dumps(settings, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    os.replace(tmp, CLAUDE_SETTINGS)


def install_statusline():
    settings = _read_claude_settings()
    if settings.get("statusLine") and not _is_ours(settings["statusLine"]):
        raise RuntimeError("Claude Code already has a different status line configured.")
    python = Path(sys.executable).with_name("python.exe")  # needs a console interpreter to write stdout
    if not python.exists():
        python = Path(sys.executable)
    command = f'"{python.as_posix()}" "{STATUSLINE_SCRIPT.as_posix()}"'
    settings["statusLine"] = {"type": "command", "command": command}
    _write_claude_settings(settings)


def remove_statusline():
    settings = _read_claude_settings()
    if _is_ours(settings.get("statusLine")):
        del settings["statusLine"]
        _write_claude_settings(settings)
