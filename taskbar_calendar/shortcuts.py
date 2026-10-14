"""Start-with-Windows and Start menu shortcuts."""

import os
import sys
from pathlib import Path

import comtypes.client

from .config import ICON_PATH, PROJECT_DIR

SHORTCUT_NAME = "Taskbar Calendar.lnk"
STARTUP_DIR = Path(os.environ["APPDATA"]) / "Microsoft" / "Windows" / "Start Menu" / "Programs" / "Startup"
START_MENU_DIR = STARTUP_DIR.parent
LAUNCHER = PROJECT_DIR / "TaskbarCalendar.pyw"


def _pythonw():
    exe = Path(sys.executable)
    windowed = exe.with_name("pythonw.exe")
    return windowed if windowed.exists() else exe


def create_shortcut(path):
    shell = comtypes.client.CreateObject("WScript.Shell", dynamic=True)
    link = shell.CreateShortcut(str(path))
    link.TargetPath = str(_pythonw())
    link.Arguments = f'"{LAUNCHER}"'
    link.WorkingDirectory = str(PROJECT_DIR)
    link.Description = "Next meeting, Claude usage and network speed on the taskbar"
    link.IconLocation = str(ICON_PATH)
    link.Save()


def startup_enabled():
    return (STARTUP_DIR / SHORTCUT_NAME).exists()


def set_startup(enabled):
    path = STARTUP_DIR / SHORTCUT_NAME
    if enabled:
        create_shortcut(path)
    elif path.exists():
        path.unlink()
