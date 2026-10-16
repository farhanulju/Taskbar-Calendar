using System;
using System.Linq;
using Xunit;

namespace TaskbarCalendar.Tests
{
    public class CalendarFeedTests
    {
        static readonly DateTime Now = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Local);

        static string Ics(string events, string name = "me@example.com") =>
            $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nX-WR-CALNAME:{name}\r\n{events}END:VCALENDAR\r\n";

        static string Stamp(DateTime t) => t.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");

        static string Event(string uid, DateTime start, DateTime? end, string summary, string extra = "") =>
            $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTART:{Stamp(start)}\r\n" + (end != null ? $"DTEND:{Stamp(end.Value)}\r\n" : "") +
            $"SUMMARY:{summary}\r\n{extra}END:VEVENT\r\n";

        static System.Collections.Generic.List<Meeting> Parse(string ics, string email = "") =>
            CalendarFeed.Parse(ics, Now.AddDays(-1), Now.AddDays(7), email);

        [Fact]
        public void RecurringEventIsExpanded()
        {
            var start = Now.AddDays(-3).AddHours(1);
            var ics = Ics(Event("a", start, start.AddMinutes(30), "Standup", "RRULE:FREQ=DAILY\r\n"));
            Assert.Equal(8, Parse(ics).Count);   // yesterday through 7 days ahead
        }

        [Fact]
        public void AllDayCancelledAndDeclinedAreSkipped()
        {
            var start = Now.AddHours(1);
            var ics = Ics(
                $"BEGIN:VEVENT\r\nUID:allday\r\nDTSTART;VALUE=DATE:{Now:yyyyMMdd}\r\nSUMMARY:Holiday\r\nEND:VEVENT\r\n" +
                Event("c", start, start.AddHours(1), "Cancelled", "STATUS:CANCELLED\r\n") +
                Event("d", start, start.AddHours(1), "Declined", "ATTENDEE;PARTSTAT=DECLINED:mailto:me@example.com\r\n") +
                Event("k", start, start.AddHours(1), "Kept", "ATTENDEE;PARTSTAT=ACCEPTED:mailto:me@example.com\r\n"));
            Assert.Equal(new[] { "Kept" }, Parse(ics).Select(m => m.Title));
        }

        [Fact]
        public void ExplicitEmailIsUsedForDeclinedWhenCalendarNameIsNotAnAddress()
        {
            var start = Now.AddHours(1);
            var ics = Ics(Event("d", start, start.AddHours(1), "X", "ATTENDEE;PARTSTAT=DECLINED:mailto:other@example.com\r\n"), name: "Work");
            Assert.Single(Parse(ics));
            Assert.Empty(Parse(ics, "other@example.com"));
        }

        [Fact]
        public void VideoLinkAndDefaultDuration()
        {
            var start = Now.AddHours(1);
            var m = Assert.Single(Parse(Ics(Event("m", start, null, "Sync", "DESCRIPTION:Join: https://meet.google.com/abc-defg-hij\r\n"))));
            Assert.Equal("https://meet.google.com/abc-defg-hij", m.Link);
            Assert.Equal(TimeSpan.FromMinutes(30), m.End - m.Start);
            Assert.Equal(start, m.Start);
        }

        static Meeting M(string title, int startMin, int minutes = 30) =>
            new Meeting { Title = title, Start = Now.AddMinutes(startMin), End = Now.AddMinutes(startMin + minutes) };

        [Fact]
        public void MeetingStartingSoonBeatsOneInProgress() =>
            Assert.Equal("soon", CalendarFeed.PickNext(new[] { M("ongoing", -20, 60), M("soon", 5) }, Now).Title);

        [Fact]
        public void InProgressBeatsLater() =>
            Assert.Equal("ongoing", CalendarFeed.PickNext(new[] { M("ongoing", -20, 60), M("later", 45) }, Now).Title);

        [Fact]
        public void FinishedMeetingsAreIgnored() => Assert.Null(CalendarFeed.PickNext(new[] { M("done", -60) }, Now));

        [Fact]
        public void WhenText()
        {
            Assert.StartsWith("Now · until", CalendarFeed.FmtWhen(M("x", -5), Now));
            Assert.Equal("in 7 min · 12:07 PM", CalendarFeed.FmtWhen(M("x", 7), Now));
            Assert.StartsWith("Tomorrow", CalendarFeed.FmtWhen(M("x", 24 * 60), Now));
        }
    }
}
