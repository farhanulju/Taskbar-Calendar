using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace TaskbarCalendar
{
    class Meeting
    {
        public string Title;
        public DateTime Start;   // local time
        public DateTime End;
        public string Link;      // Meet / Zoom / Teams link found in the event, if any
    }

    /// <summary>Google Calendar via its secret iCal (ICS) address: no OAuth or Google Cloud project needed.</summary>
    static class CalendarFeed
    {
        static readonly Regex LinkPattern = new Regex(
            @"https://(?:meet\.google\.com/[a-z\-]+|[\w.-]*zoom\.us/j/[^\s<>""']+|teams\.microsoft\.com/l/meetup-join/[^\s<>""']+)",
            RegexOptions.IgnoreCase);

        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        public static List<Meeting> Fetch(IEnumerable<string> urls, int lookaheadDays, string email = "")
        {
            var now = DateTime.Now;
            var meetings = new List<Meeting>();
            foreach (var url in urls)
            {
                var text = Http.GetStringAsync(url).GetAwaiter().GetResult();
                meetings.AddRange(Parse(text, now.AddDays(-1), now.AddDays(lookaheadDays), email));
            }
            return meetings.OrderBy(m => m.Start).ThenBy(m => m.End).ToList();
        }

        /// <summary>Timed meetings in the window, recurrences expanded. All-day, cancelled and declined events are
        /// skipped. <paramref name="email"/> defaults to the calendar's name (Google uses your address).</summary>
        public static List<Meeting> Parse(string ics, DateTime windowStart, DateTime windowEnd, string email = "")
        {
            var cal = Ical.Net.Calendar.Load(ics);
            if (string.IsNullOrEmpty(email))
            {
                var name = cal.Properties["X-WR-CALNAME"]?.Value?.ToString() ?? "";
                email = name.Contains("@") ? name : "";
            }

            var result = new List<Meeting>();
            foreach (var occurrence in cal.GetOccurrences(new CalDateTime(windowStart.ToUniversalTime()), new CalDateTime(windowEnd.ToUniversalTime())))
            {
                if (!(occurrence.Source is CalendarEvent ev) || ev.IsAllDay) continue;
                if (string.Equals(ev.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;
                if (Declined(ev, email)) continue;

                var start = occurrence.Period.StartTime.AsUtc.ToLocalTime();
                var end = (occurrence.Period.EndTime?.AsUtc ?? occurrence.Period.StartTime.AsUtc).ToLocalTime();
                if (end <= start) end = start.AddMinutes(30);   // no end time: treat as a 30-minute meeting
                if (end < windowStart || start > windowEnd) continue;

                var title = (ev.Summary ?? "").Trim();
                result.Add(new Meeting
                {
                    Title = title.Length > 0 ? title : "(No title)",
                    Start = start,
                    End = end,
                    Link = FindLink(ev),
                });
            }
            return result.OrderBy(m => m.Start).ThenBy(m => m.End).ToList();
        }

        static bool Declined(CalendarEvent ev, string email)
        {
            if (string.IsNullOrEmpty(email) || ev.Attendees == null) return false;
            return ev.Attendees.Any(a =>
                a.Value != null &&
                string.Equals(a.Value.ToString().Replace("mailto:", "").Replace("MAILTO:", ""), email, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.ParticipationStatus, "DECLINED", StringComparison.OrdinalIgnoreCase));
        }

        static string FindLink(CalendarEvent ev)
        {
            var candidates = new[]
            {
                ev.Properties["X-GOOGLE-CONFERENCE"]?.Value?.ToString(),
                ev.Location,
                ev.Description,
                ev.Url?.ToString(),
            };
            foreach (var text in candidates)
            {
                if (string.IsNullOrEmpty(text)) continue;
                var m = LinkPattern.Match(text);
                if (m.Success) return m.Value;
            }
            return null;
        }

        /// <summary>The meeting worth showing: one starting within 10 minutes beats one in progress, which beats
        /// later ones.</summary>
        public static Meeting PickNext(IEnumerable<Meeting> meetings, DateTime now)
        {
            var live = meetings.Where(m => m.End > now).ToList();
            var soon = live.FirstOrDefault(m => m.Start >= now && m.Start <= now.AddMinutes(10));
            if (soon != null) return soon;
            var ongoing = live.Where(m => m.Start <= now).ToList();
            if (ongoing.Count > 0) return ongoing.Last();
            return live.FirstOrDefault();
        }

        public static string FmtTime(DateTime t) => t.ToString("h:mm tt", CultureInfo.InvariantCulture);

        public static string FmtWhen(Meeting m, DateTime now)
        {
            if (m.Start <= now) return $"Now · until {FmtTime(m.End)}";
            var minutes = (int)Math.Ceiling((m.Start - now).TotalMinutes);
            if (minutes < 60) return $"in {minutes} min · {FmtTime(m.Start)}";
            var days = (m.Start.Date - now.Date).Days;
            if (days == 0) return FmtTime(m.Start);
            if (days == 1) return $"Tomorrow {FmtTime(m.Start)}";
            return $"{m.Start.ToString("ddd", CultureInfo.InvariantCulture)} {FmtTime(m.Start)}";
        }
    }
}
