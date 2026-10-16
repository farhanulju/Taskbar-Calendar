using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TaskbarCalendar
{
    /// <summary>Settings, stored in %APPDATA%\TaskbarCalendar\config.json with the same keys the Python version
    /// used, so existing settings carry over.</summary>
    class Config
    {
        public static readonly (string Key, string Label, string Url)[] CalendarViews =
        {
            ("agenda", "Agenda", "https://calendar.google.com/calendar/r/agenda"),
            ("day", "Day", "https://calendar.google.com/calendar/r/day"),
            ("week", "Week", "https://calendar.google.com/calendar/r/week"),
            ("month", "Month", "https://calendar.google.com/calendar/r/month"),
        };

        public List<string> IcsUrls = new List<string>();
        public string Email = "";            // used to hide declined meetings; detected from the feed when empty
        public string ChromeProfile = "";    // Chrome profile folder; empty = default browser
        public string CalendarView = "agenda";
        public int RefreshMinutes = 5;
        public int LookaheadDays = 7;
        public bool ShowCalendar = true;
        public bool ShowClaude = true;
        public bool ClaudeProbe = true;      // automatic (free) Claude usage refresh
        public bool ShowNetwork = true;
        public bool AutoUpdate = true;
        public int Width = 210;              // meeting text width, px at 100% scaling
        public int RightGap = 6;             // gap to whatever sits right of the widget
        public int? X;                       // pin the widget's left edge (physical px) instead of auto placement

        public static bool IsFirstRun => !File.Exists(Paths.ConfigFile);

        public static Config Load() => Load(Paths.ConfigFile);

        internal static Config Load(string path)
        {
            var cfg = new Config();
            Dictionary<string, object> d = null;
            try
            {
                if (File.Exists(path)) d = Json.ParseObject(FileUtil.ReadText(path));
            }
            catch (Exception ex)
            {
                Log.Warn($"{path} is not valid JSON; using defaults ({ex.Message})");
            }
            if (d == null) return cfg;

            // Older versions stored a single "ics_url" and a full "calendar_url".
            var urls = Json.Get(d, "ics_urls") ?? Json.Get(d, "ics_url");
            if (urls is string single) cfg.IcsUrls = new List<string> { single };
            else if (urls is IEnumerable<object> many) cfg.IcsUrls = many.OfType<string>().ToList();
            var legacyUrl = Json.Str(d, "calendar_url");
            var view = Json.Str(d, "calendar_view")
                       ?? CalendarViews.FirstOrDefault(v => v.Url == legacyUrl?.TrimEnd('/')).Key;

            cfg.Email = Json.Str(d, "email") ?? cfg.Email;
            cfg.ChromeProfile = Json.Str(d, "chrome_profile") ?? cfg.ChromeProfile;
            cfg.CalendarView = view ?? cfg.CalendarView;
            cfg.RefreshMinutes = (int?)Json.Num(d, "refresh_minutes") ?? cfg.RefreshMinutes;
            cfg.LookaheadDays = (int?)Json.Num(d, "lookahead_days") ?? cfg.LookaheadDays;
            cfg.ShowCalendar = Json.Bool(d, "show_calendar") ?? cfg.ShowCalendar;
            cfg.ShowClaude = Json.Bool(d, "show_claude") ?? cfg.ShowClaude;
            cfg.ClaudeProbe = Json.Bool(d, "claude_probe") ?? cfg.ClaudeProbe;
            cfg.ShowNetwork = Json.Bool(d, "show_network") ?? cfg.ShowNetwork;
            cfg.AutoUpdate = Json.Bool(d, "auto_update") ?? cfg.AutoUpdate;
            cfg.Width = (int?)Json.Num(d, "width") ?? cfg.Width;
            cfg.RightGap = (int?)Json.Num(d, "right_gap") ?? cfg.RightGap;
            cfg.X = (int?)Json.Num(d, "x");
            return cfg;
        }

        public void Save() => Save(Paths.ConfigFile);

        internal void Save(string path)
        {
            var d = new Dictionary<string, object>
            {
                ["ics_urls"] = IcsUrls.ToArray(),
                ["email"] = Email,
                ["chrome_profile"] = ChromeProfile,
                ["calendar_view"] = CalendarView,
                ["refresh_minutes"] = RefreshMinutes,
                ["lookahead_days"] = LookaheadDays,
                ["show_calendar"] = ShowCalendar,
                ["show_claude"] = ShowClaude,
                ["claude_probe"] = ClaudeProbe,
                ["show_network"] = ShowNetwork,
                ["auto_update"] = AutoUpdate,
                ["width"] = Width,
                ["right_gap"] = RightGap,
                ["x"] = X,
            };
            FileUtil.AtomicWrite(path, Json.Serialize(d, indent: true));
        }

        public Config Clone() => (Config)MemberwiseClone();

        public static string NormalizeIcsUrl(string url)
        {
            url = url.Trim();
            return url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase) ? "https://" + url.Substring(9) : url;
        }

        public List<string> CalendarUrls() =>
            IcsUrls.Where(u => !string.IsNullOrWhiteSpace(u)).Select(NormalizeIcsUrl).ToList();

        public string CalendarPageUrl() =>
            (CalendarViews.FirstOrDefault(v => v.Key == CalendarView).Url) ?? CalendarViews[0].Url;
    }
}
