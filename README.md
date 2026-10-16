<img src="docs/icon.png" width="64" alt="" align="right">

# Taskbar Calendar

**Your next meeting, your Claude plan usage and your network speed, right on the Windows 11 taskbar.**

![Taskbar Calendar on the Windows 11 taskbar](docs/taskbar.png)

Windows 11 removed the ability to add things to the taskbar. Taskbar Calendar brings a small, native-looking widget back. It sits next to the system tray, matches the taskbar's colors, and stays out of the way.

- **Next meeting** from Google Calendar, with a live countdown. The time turns amber when the meeting starts within 10 minutes and green while it's in progress. Click to open Google Calendar in the Chrome profile you choose.
- **Claude usage** for your 5-hour and weekly limits. Each row shows how much you've used, a small bar with a tick marking how much of the window has passed, and a countdown to the reset (↻). It turns amber when you're on pace to run out before the reset.
- **Network speed**, upload and download, across your physical network adapters.

Hover over the widget for the details:

<img src="docs/popup.png" alt="Hover popup with upcoming meetings and Claude usage details" width="489">

## Install

You need **Windows 11** and **[Python 3.10 or newer](https://www.python.org/downloads/)**. When installing Python, tick *Add python.exe to PATH*.

1. Download this repository: **Code → Download ZIP**, then extract it somewhere permanent, such as `Documents\Taskbar-Calendar`. Or clone it:
   ```bash
   git clone https://github.com/farhanulju/Taskbar-Calendar.git
   ```
2. Right-click `install.ps1` → **Run with PowerShell**, or run:
   ```bash
   powershell -ExecutionPolicy Bypass -File install.ps1
   ```

The installer creates a private Python environment inside the folder, adds **Taskbar Calendar** to the Start menu and to startup, and launches it. The first time, the settings window opens so you can paste your calendar link.

## Settings

Everything is configured in a regular Windows settings window. To open it, right-click the widget → **Settings…**, or open **Taskbar Calendar** from the Start menu while it's running.

<img src="docs/settings.png" alt="Settings window, Calendar tab" width="573"> <img src="docs/settings-claude.png" alt="Settings window, Claude usage tab" width="573">

### Connecting Google Calendar

The widget reads your calendar through its **secret iCal address**, which is a private, read-only link. It needs no Google sign-in and no Google Cloud project.

1. Open [Google Calendar](https://calendar.google.com) → ⚙ **Settings**.
2. Under *Settings for my calendars*, pick your calendar → **Integrate calendar**.
3. Copy **Secret address in iCal format**, paste it into Taskbar Calendar's settings, and click **Test connection**.

For several calendars, paste one link per line. All-day events, cancelled events and meetings you've declined are skipped. If your workplace's Google admin has turned secret addresses off, the *Secret address* section won't appear.

### Claude usage

Requires a Claude **Pro or Max** plan and the [Claude Code CLI](https://code.claude.com/docs/en/setup), signed in with the same account. Run `claude` once in a terminal to sign in. The desktop app has its own sign-in, which the widget can't use.

**Automatic refresh is free.** The widget reads the same numbers as Claude Code's `/usage`, from Anthropic's usage service (`api.anthropic.com/api/oauth/usage`), using the Claude Code CLI's sign-in. This sends no message to a model, so no tokens are used and nothing counts against your limits. Most Claude usage trackers work this way.

| When it refreshes | |
|---|---|
| Shortly after you use Claude Code, in the desktop app or the CLI | 20 s after the activity, at most once a minute |
| When you hover over the widget | If the numbers are more than 2 minutes old |
| Every 10 minutes while the widget is visible | Catches claude.ai and desktop Chat usage, which leave no local trace |

If the CLI's sign-in has expired (it lasts 8 hours), the widget has the CLI renew it with a request for a model that doesn't exist. The CLI renews the sign-in first, then the request is rejected, so this also uses no tokens. Only if the usage service still can't be reached, for example because you've signed out of the CLI, does the widget fall back to one minimal Haiku request through the CLI (about 900 tokens), at most every 30 minutes. Every check and fallback is logged.

The usage service is undocumented, so Anthropic could change it. Optionally, Settings can also add a Claude Code [status line](https://code.claude.com/docs/en/statusline) that saves your usage after every CLI reply, an official and free second source.

## Using it

| Action | What happens |
|---|---|
| **Click** | Opens Google Calendar (agenda, day, week or month view) |
| **Hover** | Shows today's and upcoming meetings, plus Claude usage with reset times and a projection ("At this rate you'll be at ~76% when it resets") |
| **Right-click** | Join the video call (Google Meet, Zoom or Teams link found in the event), open the calendar, refresh, open Settings, or quit |

The widget hides itself when a full-screen app or video is in front, follows taskbar changes, and works with left-aligned taskbars by moving left of the weather widget.

## How it works

- **A window over the taskbar.** Windows 11 has no API for taskbar add-ons, so the widget is a borderless, always-on-top window placed in empty taskbar space. It copies the taskbar's color and has no taskbar button of its own.
- **Event-driven, not polling.** It reacts to Windows notifications instead of checking repeatedly: the taskbar being activated, full-screen apps opening (via the AppBar API), taskbar layout changes, display or theme changes, sleep and resume, and file changes. Countdowns update once a minute, on the minute. The only thing read every second is the network counters, because Windows has no notification for traffic, and only while the widget is visible. Typical CPU use is a fraction of a percent of one core.
- **Weather widget aware.** With left-aligned taskbar icons, Windows puts the weather widget next to the tray. The widget finds it through UI Automation and stays clear of it.
- **Network speed** comes from `GetIfEntry2` on physical adapters only, so VPN and virtual adapters don't double-count traffic.

## Privacy

- Your calendar link is stored only on your PC, in `%APPDATA%\TaskbarCalendar\config.json`. Anyone with that link can read your calendar, so don't share that file.
- The calendar is fetched directly from Google, and nothing is sent anywhere else.
- For Claude usage, the widget reads the Claude Code CLI's access token from `~/.claude/.credentials.json` (read-only) and sends it only to `api.anthropic.com`. It never stores, logs, changes or refreshes the token itself. Renewal is left to the CLI.
- Logs are kept in `%LOCALAPPDATA%\TaskbarCalendar\`, limited to about 2 MB.

## Troubleshooting

| Problem | Fix |
|---|---|
| "Set up your calendar" | Right-click → Settings and paste your secret iCal address |
| "Calendar unavailable" / ⚠ | Check your internet connection, and click **Test connection** in Settings. The link may have been reset in Google Calendar. |
| Claude shows "–" | Install the Claude Code CLI and run `claude` once to sign in. Requires a Pro or Max plan. Settings → Claude usage shows whether the CLI is signed in. |
| The widget overlaps something | Settings → Display → increase *Space to the right of the widget* |
| It doesn't start with Windows | Settings → General → *Start Taskbar Calendar when I sign in* |
| Anything else | Settings → General → **Open log folder** and check `taskbar_calendar.log` |

## Uninstall

```bash
powershell -ExecutionPolicy Bypass -File uninstall.ps1
```

This removes the shortcuts and the Claude Code status line, if Taskbar Calendar added one. Add `-DeleteSettings` to also delete your settings. Then delete the folder.

## Development

```bash
.venv\Scripts\python -m unittest discover -s tests -t .
```

- `TaskbarCalendar.pyw`: launcher. `pythonw` runs it without a console window.
- `taskbar_calendar/`: the app, with `widget.py` (UI), `settings_dialog.py`, `calendar_feed.py`, `claude_usage.py`, `netmeter.py`, `winapi.py` (Win32 and shell events), `config.py`, `chrome.py` and `shortcuts.py`.
- `python TaskbarCalendar.pyw --demo` shows made-up data. `tools/screenshots.py` regenerates the images in `docs/`, and `tools/make_icon.py` draws the icon.

Issues and pull requests are welcome.

## License

[MIT](LICENSE)
