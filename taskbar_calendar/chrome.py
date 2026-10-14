"""Opening the calendar in the right Chrome profile."""

import json
import os
import subprocess
import webbrowser
import winreg
from pathlib import Path

USER_DATA = Path(os.environ.get("LOCALAPPDATA") or Path.home()) / "Google" / "Chrome" / "User Data"


def find_chrome():
    for root in (winreg.HKEY_LOCAL_MACHINE, winreg.HKEY_CURRENT_USER):
        try:
            with winreg.OpenKey(root, r"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe") as k:
                return winreg.QueryValue(k, None)
        except OSError:
            pass
    for p in (
        os.path.expandvars(r"%ProgramFiles%\Google\Chrome\Application\chrome.exe"),
        os.path.expandvars(r"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe"),
        os.path.expandvars(r"%LocalAppData%\Google\Chrome\Application\chrome.exe"),
    ):
        if os.path.exists(p):
            return p
    return None


def list_profiles():
    """[(profile folder, display name, signed-in email)] from Chrome's Local State, in Chrome's own order."""
    try:
        state = json.loads((USER_DATA / "Local State").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return []
    cache = (state.get("profile") or {}).get("info_cache") or {}
    order = (state.get("profile") or {}).get("profiles_order") or sorted(cache)
    return [
        (folder, cache[folder].get("name") or folder, cache[folder].get("user_name") or "")
        for folder in order if folder in cache
    ]


def open_url(url, profile=""):
    """Open in Chrome with the given profile folder, or in the default browser when no profile is set."""
    chrome = find_chrome() if profile else None
    if not chrome:
        webbrowser.open(url)
        return
    subprocess.Popen([chrome, f"--profile-directory={profile}", url], creationflags=subprocess.DETACHED_PROCESS)
