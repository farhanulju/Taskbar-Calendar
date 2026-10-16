"""Claude Code status line command: saves your plan usage (5-hour and weekly) for the taskbar widget.

Claude Code's CLI runs this after each reply and pipes session info as JSON on stdin; `rate_limits` is present for
Pro/Max subscribers (https://code.claude.com/docs/en/statusline). It also prints a short status line.

Standalone on purpose (standard library only, no package imports): it runs on every CLI reply, so it must be fast.
"""

import json
import os
import sys
import tempfile
import time
from pathlib import Path

USAGE_FILE = Path(os.environ.get("LOCALAPPDATA") or Path.home()) / "TaskbarCalendar" / "claude_usage.json"


def save(text):
    """Unique temp file + atomic replace (same as claude_usage.atomic_write); Windows refuses to replace a file
    someone has open, so retry briefly rather than drop the update."""
    USAGE_FILE.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(dir=USAGE_FILE.parent, prefix="claude_usage.", suffix=".tmp")
    with os.fdopen(fd, "w", encoding="utf-8") as f:
        f.write(text)
    for attempt in range(40):
        try:
            os.replace(tmp, USAGE_FILE)
            return
        except PermissionError:
            time.sleep(0.05)
    os.remove(tmp)


def main():
    sys.stdout.reconfigure(encoding="utf-8")  # Windows pipes default to a legacy code page
    try:
        data = json.loads(sys.stdin.buffer.read().decode("utf-8") or "{}")
    except ValueError:
        data = {}
    limits = data.get("rate_limits") or {}
    windows = {k: limits[k] for k in ("five_hour", "seven_day") if isinstance(limits.get(k), dict)}
    if windows:
        save(json.dumps({**windows, "updated": time.time()}))

    parts = [
        f"{label} {windows[key].get('used_percentage', 0):.0f}%"
        for key, label in (("five_hour", "5h"), ("seven_day", "wk"))
        if key in windows
    ]
    print(" · ".join(parts))


if __name__ == "__main__":
    main()
