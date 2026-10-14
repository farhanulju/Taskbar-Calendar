"""Google Calendar via its secret iCal (ICS) address: no OAuth or Google Cloud project needed."""

import datetime as dt
import math
import re
from dataclasses import dataclass

import icalendar
import recurring_ical_events
import requests

URL_RE = re.compile(
    r"https://(?:meet\.google\.com/[a-z\-]+|[\w.-]*zoom\.us/j/[^\s<>\"']+|teams\.microsoft\.com/l/meetup-join/[^\s<>\"']+)",
    re.I,
)


@dataclass
class Meeting:
    title: str
    start: dt.datetime
    end: dt.datetime
    link: str | None = None


def _as_local(value):
    if value.tzinfo is None:
        value = value.astimezone()  # floating time: treat as local
    return value.astimezone()


def _attendee_status(component, email):
    attendees = component.get("ATTENDEE")
    if attendees is None or not email:
        return None
    if not isinstance(attendees, list):
        attendees = [attendees]
    for a in attendees:
        if str(a).lower().removeprefix("mailto:") == email.lower():
            return str(a.params.get("PARTSTAT", "")).upper()
    return None


def _find_link(component):
    for key in ("X-GOOGLE-CONFERENCE", "LOCATION", "DESCRIPTION", "URL"):
        value = component.get(key)
        if value:
            m = URL_RE.search(str(value))
            if m:
                return m.group(0)
    return None


def parse_meetings(ics_bytes, window_start, window_end, email=""):
    """Timed meetings in [window_start, window_end], with recurrences expanded. All-day, cancelled and
    declined events are skipped. `email` defaults to the calendar's name (Google uses your address)."""
    cal = icalendar.Calendar.from_ical(ics_bytes)
    if not email:
        name = str(cal.get("X-WR-CALNAME", ""))
        email = name if "@" in name else ""
    meetings = []
    for ev in recurring_ical_events.of(cal).between(window_start, window_end):
        start = ev.get("DTSTART").dt
        if not isinstance(start, dt.datetime):
            continue  # all-day event
        if str(ev.get("STATUS", "")).upper() == "CANCELLED":
            continue
        if _attendee_status(ev, email) == "DECLINED":
            continue
        end_prop = ev.get("DTEND")
        end = end_prop.dt if end_prop is not None else start
        if not isinstance(end, dt.datetime):
            continue
        if end <= start:
            end = start + dt.timedelta(minutes=30)  # no end time: treat as a 30-minute meeting
        title = str(ev.get("SUMMARY", "")).strip() or "(No title)"
        meetings.append(Meeting(title, _as_local(start), _as_local(end), _find_link(ev)))
    return meetings


def fetch_meetings(urls, lookahead_days, email=""):
    now = dt.datetime.now().astimezone()
    window_start = now - dt.timedelta(days=1)
    window_end = now + dt.timedelta(days=int(lookahead_days))
    meetings = []
    for url in urls:
        resp = requests.get(url, timeout=30)
        resp.raise_for_status()
        meetings += parse_meetings(resp.content, window_start, window_end, email)
    meetings.sort(key=lambda m: (m.start, m.end))
    return meetings


def pick_next(meetings, now):
    """The meeting worth showing: one starting within 10 minutes beats one in progress, which beats later ones."""
    live = [m for m in meetings if m.end > now]
    soon = [m for m in live if now <= m.start <= now + dt.timedelta(minutes=10)]
    if soon:
        return soon[0]
    ongoing = [m for m in live if m.start <= now]
    if ongoing:
        return ongoing[-1]
    return live[0] if live else None


def fmt_time(t):
    return t.strftime("%I:%M %p").lstrip("0")


def fmt_when(m, now):
    if m.start <= now:
        return f"Now · until {fmt_time(m.end)}"
    mins = math.ceil((m.start - now).total_seconds() / 60)
    if mins < 60:
        return f"in {mins} min · {fmt_time(m.start)}"
    days = (m.start.date() - now.date()).days
    if days == 0:
        return fmt_time(m.start)
    if days == 1:
        return f"Tomorrow {fmt_time(m.start)}"
    return f"{m.start.strftime('%a')} {fmt_time(m.start)}"
