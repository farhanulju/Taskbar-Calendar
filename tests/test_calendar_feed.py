import datetime as dt
import unittest

from taskbar_calendar.calendar_feed import Meeting, fmt_when, parse_meetings, pick_next

UTC = dt.timezone.utc


def ics(*events, name="me@example.com"):
    return ("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nX-WR-CALNAME:%s\r\n%sEND:VCALENDAR\r\n" % (name, "".join(events))).encode()


def event(uid, start, end=None, summary="Meeting", extra=""):
    def stamp(t):
        return t.strftime("%Y%m%dT%H%M%SZ")

    end_line = f"DTEND:{stamp(end)}\r\n" if end else ""
    return (f"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTART:{stamp(start)}\r\n{end_line}"
            f"SUMMARY:{summary}\r\n{extra}END:VEVENT\r\n")


class ParseMeetingsTest(unittest.TestCase):
    def setUp(self):
        self.now = dt.datetime(2026, 10, 14, 12, 0, tzinfo=UTC)
        self.window = (self.now - dt.timedelta(days=1), self.now + dt.timedelta(days=7))

    def parse(self, data, email=""):
        return parse_meetings(data, *self.window, email=email)

    def test_recurring_event_is_expanded(self):
        start = self.now - dt.timedelta(days=3, hours=-1)
        data = ics(event("a", start, start + dt.timedelta(minutes=30), "Standup", "RRULE:FREQ=DAILY\r\n"))
        self.assertEqual(len(self.parse(data)), 8)  # yesterday through 7 days ahead

    def test_all_day_cancelled_and_declined_are_skipped(self):
        start = self.now + dt.timedelta(hours=1)
        data = ics(
            f"BEGIN:VEVENT\r\nUID:allday\r\nDTSTART;VALUE=DATE:{self.now:%Y%m%d}\r\nSUMMARY:Holiday\r\nEND:VEVENT\r\n",
            event("c", start, start + dt.timedelta(hours=1), "Cancelled", "STATUS:CANCELLED\r\n"),
            event("d", start, start + dt.timedelta(hours=1), "Declined",
                  "ATTENDEE;PARTSTAT=DECLINED:mailto:me@example.com\r\n"),
            event("k", start, start + dt.timedelta(hours=1), "Kept",
                  "ATTENDEE;PARTSTAT=ACCEPTED:mailto:me@example.com\r\n"),
        )
        self.assertEqual([m.title for m in self.parse(data)], ["Kept"])

    def test_declined_uses_explicit_email_over_calendar_name(self):
        start = self.now + dt.timedelta(hours=1)
        data = ics(event("d", start, start + dt.timedelta(hours=1), "X",
                         "ATTENDEE;PARTSTAT=DECLINED:mailto:other@example.com\r\n"), name="Work")
        self.assertEqual(len(self.parse(data)), 1)
        self.assertEqual(len(self.parse(data, email="other@example.com")), 0)

    def test_video_link_and_default_duration(self):
        start = self.now + dt.timedelta(hours=1)
        data = ics(event("m", start, None, "Sync", "DESCRIPTION:Join: https://meet.google.com/abc-defg-hij\r\n"))
        (m,) = self.parse(data)
        self.assertEqual(m.link, "https://meet.google.com/abc-defg-hij")
        self.assertEqual(m.end - m.start, dt.timedelta(minutes=30))


class PickNextTest(unittest.TestCase):
    def setUp(self):
        self.now = dt.datetime(2026, 10, 14, 12, 0, tzinfo=UTC)

    def m(self, title, start_min, minutes=30):
        start = self.now + dt.timedelta(minutes=start_min)
        return Meeting(title, start, start + dt.timedelta(minutes=minutes))

    def test_meeting_starting_soon_beats_one_in_progress(self):
        self.assertEqual(pick_next([self.m("ongoing", -20, 60), self.m("soon", 5)], self.now).title, "soon")

    def test_in_progress_beats_later(self):
        self.assertEqual(pick_next([self.m("ongoing", -20, 60), self.m("later", 45)], self.now).title, "ongoing")

    def test_finished_meetings_are_ignored(self):
        self.assertIsNone(pick_next([self.m("done", -60, 30)], self.now))


class FmtWhenTest(unittest.TestCase):
    def test_formats(self):
        now = dt.datetime(2026, 10, 14, 12, 0).astimezone()

        def at(minutes):
            start = now + dt.timedelta(minutes=minutes)
            return fmt_when(Meeting("x", start, start + dt.timedelta(minutes=30)), now)

        self.assertTrue(at(-5).startswith("Now · until"))
        self.assertTrue(at(7).startswith("in 7 min"))
        self.assertTrue(at(24 * 60).startswith("Tomorrow"))


if __name__ == "__main__":
    unittest.main()
