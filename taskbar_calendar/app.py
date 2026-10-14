"""Entry point: logging, single instance, and restarting after settings change."""

import argparse
import logging
import logging.handlers
import subprocess
import sys
from pathlib import Path

from . import __version__, winapi
from .config import DATA_DIR, LOG_PATH, is_first_run


def setup_logging():
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    handler = logging.handlers.RotatingFileHandler(LOG_PATH, maxBytes=1_000_000, backupCount=1, encoding="utf-8")
    handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(name)s: %(message)s"))
    logging.basicConfig(level=logging.INFO, handlers=[handler])


def main(argv=None):
    parser = argparse.ArgumentParser(prog="TaskbarCalendar", description="Next meeting, Claude usage and network "
                                                                          "speed on the Windows 11 taskbar.")
    parser.add_argument("--settings", action="store_true", help="open the settings window")
    parser.add_argument("--demo", action="store_true", help="show made-up data (for screenshots)")
    parser.add_argument("--version", action="version", version=__version__)
    args = parser.parse_args(argv)

    setup_logging()
    log = logging.getLogger("taskbar_calendar")
    winapi.set_dpi_awareness()

    instance = None
    if not args.demo:
        instance = winapi.SingleInstance()
        if instance.already_running:
            # Launching it again (e.g. from the Start menu) opens the running copy's settings.
            winapi.ask_running_instance_to_open_settings()
            return 0

    def restart():
        if instance:
            instance.release()
        launcher = Path(sys.argv[0]).resolve()
        subprocess.Popen([sys.executable, str(launcher)], creationflags=subprocess.DETACHED_PROCESS)

    from .widget import Widget  # after DPI awareness, which must be set before any window exists

    log.info("Starting Taskbar Calendar %s", __version__)
    try:
        Widget(demo=args.demo, restart=restart, first_run=is_first_run() or args.settings).run()
    except Exception:
        log.exception("Fatal error")
        raise
    return 0
