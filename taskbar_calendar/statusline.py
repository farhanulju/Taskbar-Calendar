"""Claude Code status line command: saves your plan usage (5-hour and weekly) for the taskbar widget.

Claude Code's CLI runs this after each reply and pipes session info as JSON on stdin; `rate_limits` is present for
Pro/Max subscribers (https://code.claude.com/docs/en/statusline). It also prints a short status line.

Standalone on purpose (standard library only, no package imports): it runs on every CLI reply, so it must be fast.
"""

import json
import os
import sys
import time
from pathlib import Path

USAGE_FILE = Path(os.environ.get("LOCALAPPDATA") or Path.home()) / "TaskbarCalendar" / "claude_usage.json"


def main():
    sys.stdout.reconfigure(encoding="utf-8")  # Windows pipes default to a legacy code page
    try:
        data = json.loads(sys.stdin.buffer.read().decode("utf-8") or "{}")
    except ValueError:
        data = {}
    limits = data.get("rate_limits") or {}
    windows = {k: limits[k] for k in ("five_hour", "seven_day") if isinstance(limits.get(k), dict)}
    if windows:
        USAGE_FILE.parent.mkdir(parents=True, exist_ok=True)
        tmp = USAGE_FILE.with_suffix(".tmp")
        tmp.write_text(json.dumps({**windows, "updated": time.time()}), encoding="utf-8")
        os.replace(tmp, USAGE_FILE)  # atomic, so the widget never reads half a file

    parts = [
        f"{label} {windows[key].get('used_percentage', 0):.0f}%"
        for key, label in (("five_hour", "5h"), ("seven_day", "wk"))
        if key in windows
    ]
    print(" · ".join(parts))


if __name__ == "__main__":
    main()
