using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace TaskbarCalendar
{
    /// <summary>The taskbar widget: next meeting, Claude usage and network speed, drawn in one borderless,
    /// always-on-top window over empty taskbar space. Left-click opens Google Calendar, hover shows details,
    /// right-click shows the menu.</summary>
    sealed class WidgetForm : Form
    {
        // Claude usage checks (seconds). The usage service is free, but stay polite with it.
        const int UsageMinInterval = 60, UsageAfterActivity = 20, UsageOnHover = 120, UsagePeriodic = 600;
        const int ProbeFallbackInterval = 1800;
        const int UpdateCheckDelay = 60, UpdateCheckInterval = 6 * 3600;

        public static readonly Color Grey = Color.FromArgb(0x9a, 0x9a, 0x9a), Dim = Color.FromArgb(0xc5, 0xc5, 0xc5),
                                     Green = Color.FromArgb(0x6c, 0xcb, 0x5f), Amber = Color.FromArgb(0xff, 0xb9, 0x00),
                                     Red = Color.FromArgb(0xff, 0x99, 0xa4);

        readonly bool demo;
        readonly Action restartApp;
        Config cfg;

        // Data
        List<Meeting> meetings = new List<Meeting>();
        Meeting current;
        string calendarError;
        bool calendarLoaded;
        ClaudeUsage.Snapshot usage;
        readonly NetMeter net;
        string upText = "", downText = "";

        // Appearance
        readonly Font font;
        public readonly Font SmallFont, SmallBoldFont;
        Color baseBg = Color.FromArgb(0x1c, 0x1c, 0x1c), hoverBg = Color.FromArgb(0x2e, 0x2e, 0x2e);
        bool lightTaskbar;
        int calWidth, claudeWidth, netWidth;

        // State
        bool hover, shown, menuOpen;
        (int Left, int Right)? weatherSpan;
        int weatherBusy;
        PeekForm peek;
        SettingsForm settings;
        ShellWatcher shell;
        readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        readonly Timer minuteTimer = new Timer(), netTimer = new Timer { Interval = 1000 }, refreshTimer = new Timer(),
                       layoutTimer = new Timer { Interval = 30 }, usageTimer = new Timer(), peekTimer = new Timer { Interval = 450 },
                       leaveTimer = new Timer { Interval = 80 }, updateTimer = new Timer();
        string claudeExe;
        bool autoUsage, usageRunning;
        double lastUsageCheck, lastProbe, usagePausedUntil;
        Release pendingUpdate;
        string downloadedUpdate;
        bool updateBusy;

        public WidgetForm(bool demo, Action restartApp, bool openSettings)
        {
            this.demo = demo;
            this.restartApp = restartApp;
            cfg = demo ? Demo.Config() : Config.Load();

            Text = "Taskbar Calendar";
            Icon = Program.AppIcon;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            AutoScaleMode = AutoScaleMode.None;

            var family = FontFamily.Families.Any(f => f.Name == "Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
            font = new Font(family, 9f);
            SmallFont = new Font(family, 8f);
            SmallBoldFont = new Font(family, 8f, FontStyle.Bold);

            ComputeSectionWidths();
            net = cfg.ShowNetwork && !demo ? new NetMeter() : null;
            usage = demo ? Demo.Usage() : ClaudeUsage.Load();
            autoUsage = cfg.ShowClaude && cfg.ClaudeProbe && !demo;
            claudeExe = autoUsage ? ClaudeUsage.FindClaudeExe() : null;

            minuteTimer.Tick += (s, e) => MinuteTick();
            netTimer.Tick += (s, e) => NetTick();
            refreshTimer.Tick += (s, e) => RefreshCalendar();
            layoutTimer.Tick += (s, e) => { layoutTimer.Stop(); ApplyLayout(); };
            usageTimer.Tick += (s, e) => { usageTimer.Stop(); CheckUsage(); };
            peekTimer.Tick += (s, e) => { peekTimer.Stop(); ShowPeek(); };
            leaveTimer.Tick += (s, e) => { leaveTimer.Stop(); ConfirmLeave(); };
            updateTimer.Tick += (s, e) => { updateTimer.Interval = UpdateCheckInterval * 1000; CheckForUpdate(); };

            if (openSettings) BeginInvoke(new Action(() => OpenSettings(firstRun: Config.IsFirstRun)));
        }

        // Never steal focus or show a taskbar button.
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WinApi.WS_EX_TOOLWINDOW | WinApi.WS_EX_NOACTIVATE | WinApi.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            WinApi.RoundCorners(Handle);
        }

        /// <summary>Called once by Program. The window isn't shown automatically: the first layout pass decides
        /// whether and where it appears, so it never flashes in the wrong place.</summary>
        public void Start()
        {
            var _ = Handle;   // create the window so BeginInvoke works before it's ever shown
            shell = new ShellWatcher();
            shell.Event += name => BeginInvoke(new Action(() => OnShellEvent(name)));

            ApplyLayout();
            RefreshCalendar();
            refreshTimer.Interval = Math.Max(1, cfg.RefreshMinutes) * 60_000;
            refreshTimer.Start();
            FindWeatherWidget();
            MinuteTick();

            if (cfg.ShowClaude && !demo) Watch(Paths.DataDir, "claude_usage.json", false, () => ReloadUsage());
            if (autoUsage)
            {
                if (Directory.Exists(ClaudeUsage.ProjectsDir))
                    Watch(ClaudeUsage.ProjectsDir, "*.jsonl", true, () => ScheduleUsageCheck(UsageAfterActivity));
                ScheduleUsageCheck(2);
            }
            if (!demo && (cfg.AutoUpdate || !Updater.CanSelfUpdate))
            {
                updateTimer.Interval = UpdateCheckDelay * 1000;
                updateTimer.Start();
            }
        }

        void Watch(string dir, string filter, bool subdirs, Action onChange)
        {
            Directory.CreateDirectory(dir);
            var w = new FileSystemWatcher(dir, filter)
            {
                IncludeSubdirectories = subdirs,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler handler = (s, e) => { try { BeginInvoke(onChange); } catch (InvalidOperationException) { } };
            w.Changed += handler;
            w.Created += handler;
            w.Renamed += (s, e) => handler(s, e);
            watchers.Add(w);
        }

        // ------------------------------------------------------------------------------------------ sizes

        float S => DeviceDpi / 96f;
        int Px(float v) => (int)Math.Round(v * S);
        int Measure(string text, Font f) => TextRenderer.MeasureText(text, f, Size.Empty, TextFormatFlags.NoPadding).Width;
        int LineHeight => TextRenderer.MeasureText("Ag", font, Size.Empty, TextFormatFlags.NoPadding).Height;
        int BarWidth => Px(30);
        int BarHeight => Math.Max(4, Px(4));

        void ComputeSectionWidths()
        {
            calWidth = cfg.ShowCalendar ? Px(cfg.Width) : 0;
            claudeWidth = cfg.ShowClaude
                ? Measure("wk", font) + Px(9) + BarWidth + Measure("100%", font) + Px(6) + Measure("↻ 6d 23h", font) + Px(16)
                : 0;
            netWidth = cfg.ShowNetwork ? Measure("↓ 999.99 KB/s", font) + Px(10) : 0;
        }

        // ------------------------------------------------------------------------------------------ calendar

        void RefreshCalendar()
        {
            if (!cfg.ShowCalendar) return;
            Task.Run(() =>
            {
                try
                {
                    if (demo) { meetings = Demo.Meetings(DateTime.Now); calendarLoaded = true; calendarError = null; }
                    else if (cfg.CalendarUrls().Count == 0) calendarError = "setup";
                    else
                    {
                        meetings = CalendarFeed.Fetch(cfg.CalendarUrls(), cfg.LookaheadDays, cfg.Email);
                        calendarLoaded = true;
                        calendarError = null;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"Calendar refresh failed: {ex.GetBaseException().Message}");
                    calendarError = ex.Message;
                }
                Post(Render);
            });
        }

        void Post(Action action)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(action); } catch (InvalidOperationException) { }
        }

        // ------------------------------------------------------------------------------------------ rendering

        void Render()
        {
            current = CalendarFeed.PickNext(meetings, DateTime.Now);
            Invalidate();
            if (peek != null) ShowPeek();
        }

        (string Title, string When, Color WhenColor) CalendarText(DateTime now)
        {
            if (calendarError == "setup") return ("Set up your calendar", "Right-click › Settings", Dim);
            if (current != null)
            {
                var color = current.Start <= now ? Green : current.Start - now <= TimeSpan.FromMinutes(10) ? Amber : Dim;
                var when = CalendarFeed.FmtWhen(current, now);
                return (current.Title, calendarError != null ? "⚠ " + when : when, color);
            }
            if (!calendarLoaded && calendarError != null) return ("Calendar unavailable", "Will retry…", Dim);
            if (!calendarLoaded) return ("Loading calendar…", "", Dim);
            return ("No upcoming meetings", $"Next {cfg.LookaheadDays} days", Dim);
        }

        public class UsageRow { public string Key, Long; public double Pct; public double? Resets, Elapsed, RunsOut; }

        /// <summary>Rows for both windows. A passed reset means 0%; a window missing from fresh data means it reset
        /// and restarts when you next use Claude.</summary>
        public List<UsageRow> UsageRows(DateTime now)
        {
            var rows = new List<UsageRow>();
            if (usage == null) return rows;
            var nowTs = Epoch.From(now);
            foreach (var (key, label) in new[] { ("five_hour", "5-hour"), ("seven_day", "Weekly") })
            {
                var row = new UsageRow { Key = key, Long = label };
                if (usage.Windows.TryGetValue(key, out var w))
                {
                    row.Pct = w.UsedPercentage;
                    row.Resets = w.ResetsAt;
                    if (row.Resets <= nowTs) { row.Pct = 0; row.Resets = null; }
                    if (row.Resets != null)
                    {
                        var (elapsed, runsOut) = ClaudeUsage.Pace(row.Pct, row.Resets.Value, ClaudeUsage.WindowSeconds[key], nowTs);
                        row.Elapsed = elapsed;
                        row.RunsOut = runsOut;
                    }
                }
                rows.Add(row);
            }
            return rows;
        }

        public static Color UsageColor(double pct, bool runsOut) => pct >= 95 ? Red : pct >= 80 || runsOut ? Amber : Color.White;

        double UsageAge => usage == null ? double.MaxValue : Epoch.Now - usage.Updated;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var bg = hover ? hoverBg : baseBg;
            g.Clear(bg);
            var fg = lightTaskbar ? Color.Black : Color.White;
            var now = DateTime.Now;
            int line = LineHeight, h = ClientSize.Height;
            int top1 = (h - 2 * line) / 2, top2 = top1 + line;
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

            if (calWidth > 0)
            {
                var (title, when, whenColor) = CalendarText(now);
                var r1 = new Rectangle(Px(8), top1, calWidth - Px(16), line);
                var r2 = new Rectangle(Px(8), top2, calWidth - Px(16), line);
                TextRenderer.DrawText(g, title, font, r1, fg, bg, flags | TextFormatFlags.Right);
                TextRenderer.DrawText(g, when, font, r2, whenColor == Dim && lightTaskbar ? Color.DimGray : whenColor, bg, flags | TextFormatFlags.Right);
            }

            if (claudeWidth > 0)
            {
                var rows = UsageRows(now).ToDictionary(r => r.Key);
                var stale = UsageAge > 3 * 3600;
                int x = calWidth + Px(4);
                int nameW = Measure("wk", font), pctW = Measure("100%", font);
                foreach (var (key, label, top) in new[] { ("five_hour", "5h", top1), ("seven_day", "wk", top2) })
                {
                    var labelColor = lightTaskbar ? Color.DimGray : Grey;
                    TextRenderer.DrawText(g, label, font, new Point(x, top), labelColor, bg, flags);
                    int barX = x + nameW + Px(5), barY = top + (line - BarHeight) / 2;
                    using (var track = new SolidBrush(lightTaskbar ? Color.FromArgb(0xc8, 0xc8, 0xc8) : Color.FromArgb(0x4a, 0x4a, 0x4a)))
                        g.FillRectangle(track, barX, barY, BarWidth, BarHeight);
                    int pctX = barX + BarWidth + Px(4);
                    if (!rows.TryGetValue(key, out var row))
                    {
                        TextRenderer.DrawText(g, "–", font, new Rectangle(pctX, top, pctW, line), labelColor, bg, flags | TextFormatFlags.Right);
                        continue;
                    }
                    var color = stale ? Grey : UsageColor(row.Pct, row.RunsOut != null);
                    if (color == Color.White && lightTaskbar) color = Color.Black;
                    using (var fill = new SolidBrush(color))
                        g.FillRectangle(fill, barX, barY, (int)Math.Round(BarWidth * Math.Min(row.Pct, 100) / 100), BarHeight);
                    if (row.Elapsed != null)
                    {
                        int tickX = Math.Min(barX + BarWidth - 1, barX + (int)Math.Round(BarWidth * row.Elapsed.Value));
                        using (var pen = new Pen(fg, Math.Max(1, (int)S)))
                            g.DrawLine(pen, tickX, barY, tickX, barY + BarHeight - 1);
                    }
                    TextRenderer.DrawText(g, $"{row.Pct:0}%", font, new Rectangle(pctX, top, pctW, line), color, bg, flags | TextFormatFlags.Right);
                    var reset = row.Resets != null ? "↻ " + ClaudeUsage.FmtCountdown(row.Resets.Value - Epoch.From(now)) : "";
                    TextRenderer.DrawText(g, reset, font, new Point(pctX + pctW + Px(6), top), labelColor, bg, flags);
                }
            }

            if (netWidth > 0)
            {
                int x = calWidth + claudeWidth + Px(2);
                TextRenderer.DrawText(g, upText, font, new Point(x, top1), fg, bg, flags);
                TextRenderer.DrawText(g, downText, font, new Point(x, top2), fg, bg, flags);
            }
        }

        /// <summary>Copy the taskbar colour from both sides of the widget (the tinted taskbar is a gentle gradient).</summary>
        void MatchTaskbarColors()
        {
            var tb = WinApi.Taskbar();
            if (tb == null || !shown) return;
            int mid = Top + Height / 2;
            var xs = new[] { Math.Max(tb.Bounds.Left, Left - Px(4)), Math.Min(tb.Bounds.Right - 1, Right + Px(3)) };
            var samples = (from sx in xs from dy in new[] { -Px(8), 0, Px(8) } select WinApi.ScreenPixel(sx, mid + dy))
                          .Where(c => c != null).Select(c => c.Value).ToList();
            if (samples.Count == 0) return;
            var avg = Color.FromArgb((int)samples.Average(c => c.R), (int)samples.Average(c => c.G), (int)samples.Average(c => c.B));
            var light = (avg.R + avg.G + avg.B) / 3 > 140;
            int delta = light ? -14 : 18;
            baseBg = avg;
            hoverBg = Color.FromArgb(Clamp(avg.R + delta), Clamp(avg.G + delta), Clamp(avg.B + delta));
            lightTaskbar = light;
            Invalidate();
        }

        static int Clamp(int v) => Math.Max(0, Math.Min(255, v));

        // ------------------------------------------------------------------------------------------ events (no polling)

        void OnShellEvent(string name)
        {
            try
            {
                switch (name)
                {
                    case "foreground":
                        if (shown) WinApi.RaiseTopmost(Handle);
                        ScheduleLayout();
                        break;
                    case "layout":
                    case "fullscreen":
                        ScheduleLayout();
                        break;
                    case "taskbar-restarted":
                        shell.ReRegisterAppBar();
                        ScheduleLayout();
                        Delay(1000, () => { MatchTaskbarColors(); FindWeatherWidget(); });
                        break;
                    case "display":
                        ScheduleLayout();
                        Delay(500, () => { if (!hover) MatchTaskbarColors(); });
                        Delay(700, FindWeatherWidget);   // taskbar alignment / Widgets toggles arrive as setting changes
                        break;
                    case "time":
                        MinuteTick();
                        break;
                    case "resume":
                        RefreshCalendar();
                        MinuteTick();
                        ScheduleUsageCheck(10);
                        break;
                    case "open-settings":
                        OpenSettings();
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"event {name} failed", ex);
            }
        }

        void Delay(int ms, Action action)
        {
            var t = new Timer { Interval = ms };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); action(); };
            t.Start();
        }

        /// <summary>Runs on each minute boundary, the resolution of the countdowns.</summary>
        void MinuteTick()
        {
            minuteTimer.Stop();
            Render();
            ApplyLayout();   // cheap safety net for anything Windows didn't tell us about
            if (!hover) MatchTaskbarColors();
            FindWeatherWidget();   // the forecast text (so its width) changes; adapters come and go
            net?.FindAdapters();
            if (shown && UsageAge > UsagePeriodic) ScheduleUsageCheck(0);   // claude.ai chats leave no local trace
            var now = DateTime.Now;
            minuteTimer.Interval = Math.Max(1000, (int)((60 - now.Second) * 1000 - now.Millisecond + 200));
            minuteTimer.Start();
        }

        // ------------------------------------------------------------------------------------------ Claude usage

        void ReloadUsage()
        {
            var fresh = ClaudeUsage.Load();
            if (fresh == null) return;
            usage = fresh;
            Invalidate();
            if (peek != null) ShowPeek();
        }

        /// <summary>One check after <paramref name="delay"/> s, at most once per UsageMinInterval and never while the
        /// service has asked us to back off; extra triggers in the meantime merge into the pending one.</summary>
        void ScheduleUsageCheck(double delay)
        {
            if (!autoUsage || usageTimer.Enabled || usageRunning) return;
            delay = Math.Max(delay, Math.Max(lastUsageCheck + UsageMinInterval - Epoch.Now, usagePausedUntil - Epoch.Now));
            usageTimer.Interval = Math.Max(1, (int)(delay * 1000));
            usageTimer.Start();
        }

        void CheckUsage()
        {
            usageRunning = true;
            var exe = claudeExe;
            var probeAllowed = exe != null && Epoch.Now - lastProbe > ProbeFallbackInterval;
            Task.Run(() =>
            {
                int pause = 0;
                bool probed = false;
                try
                {
                    ClaudeUsage.Save(ClaudeUsage.FetchUsage(exe));   // the file watcher then updates the display
                }
                catch (RateLimitedException ex)
                {
                    pause = ex.RetryAfter;
                    Log.Info($"Usage service asked to slow down; next check in {pause} s");
                }
                catch (NotSignedInException ex)
                {
                    Log.Info(ex.Message);
                    if (probeAllowed)   // last resort, at most every half hour
                    {
                        probed = true;
                        var windows = ClaudeUsage.ProbeUsage(exe);
                        if (windows != null) ClaudeUsage.Save(windows);
                    }
                }
                catch (Exception ex)   // offline or a service hiccup: keep the last numbers, try again later
                {
                    Log.Warn($"Usage check failed: {ex.GetBaseException().Message}");
                }
                Post(() =>
                {
                    usageRunning = false;
                    lastUsageCheck = Epoch.Now;
                    if (pause > 0) usagePausedUntil = Epoch.Now + pause;
                    if (probed) lastProbe = Epoch.Now;
                });
            });
        }

        // ------------------------------------------------------------------------------------------ network speed

        /// <summary>Network speed is the one thing Windows can't push to us: sampled once a second, only while visible.</summary>
        void NetTick()
        {
            if (netWidth == 0 || !shown) { netTimer.Stop(); net?.Reset(); return; }
            var speeds = demo ? (1.2 * 1024 * 1024, 18.6 * 1024 * 1024) : net?.Sample();
            string up = speeds == null ? "↑ –" : "↑ " + NetMeter.Format(speeds.Value.Item1);
            string down = speeds == null ? "↓ –" : "↓ " + NetMeter.Format(speeds.Value.Item2);
            if (up != upText || down != downText)
            {
                if (speeds != null || upText == "") { upText = up; downText = down; }
                Invalidate(new Rectangle(calWidth + claudeWidth, 0, netWidth, ClientSize.Height));
            }
            if (!netTimer.Enabled) netTimer.Start();
        }

        // ------------------------------------------------------------------------------------------ positioning

        void FindWeatherWidget()
        {
            if (Interlocked.Exchange(ref weatherBusy, 1) == 1) return;   // a lookup is already in flight
            var thread = new Thread(() =>
            {
                (int, int)? span = null;
                try { span = WinApi.WeatherWidgetSpan(); }
                catch (Exception ex) { Log.Warn($"Weather widget lookup failed: {ex.Message}"); }
                finally { Interlocked.Exchange(ref weatherBusy, 0); }
                Post(() => { if (!Equals(span, weatherSpan)) { weatherSpan = span; ScheduleLayout(); } });
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        void ScheduleLayout()
        {
            if (!layoutTimer.Enabled) layoutTimer.Start();   // coalesce bursts into one pass
        }

        void ApplyLayout()
        {
            try
            {
                var tb = WinApi.Taskbar();
                if (tb != null) shell?.Watch(tb.Hwnd, tb.Neighbours);
                int width = calWidth + claudeWidth + netWidth;
                if (tb == null || width == 0 || WinApi.ForegroundIsFullscreen())
                {
                    if (shown)
                    {
                        HidePeek();
                        Hide();
                        shown = false;
                        NetTick();   // stops sampling while hidden
                    }
                    return;
                }
                int limit = tb.Limit;
                // A left-aligned taskbar puts the weather widget on the right, next to the tray.
                if (weatherSpan != null && weatherSpan.Value.Left > tb.Bounds.Left + tb.Bounds.Width / 2)
                    limit = Math.Min(limit, weatherSpan.Value.Left);
                int h = tb.Bounds.Height - Px(6);
                int x = cfg.X ?? limit - Px(cfg.RightGap) - width;
                int y = tb.Bounds.Top + (tb.Bounds.Height - h) / 2;
                // Compare with where the window really is: other programs (Win+D, screen capture tools) can move,
                // resize or minimize it behind our back.
                if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
                var actual = WinApi.RectOf(Handle);
                if (actual.Left != x || actual.Top != y || actual.Width != width || actual.Height != h)
                    SetBounds(x, y, width, h);
                if (!shown)
                {
                    shown = true;
                    MatchTaskbarColors();
                    Show();
                    NetTick();
                }
                WinApi.RaiseTopmost(Handle);
            }
            catch (Exception ex)
            {
                Log.Error("layout failed", ex);
            }
        }

        // ------------------------------------------------------------------------------------------ interaction

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            leaveTimer.Stop();
            if (hover) return;
            hover = true;
            if (UsageAge > UsageOnHover) ScheduleUsageCheck(0);
            Invalidate();
            peekTimer.Start();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            leaveTimer.Stop();
            leaveTimer.Start();
        }

        void ConfirmLeave()
        {
            if (Bounds.Contains(Cursor.Position)) return;
            hover = false;
            peekTimer.Stop();
            Invalidate();
            HidePeek();
        }

        /// <summary>Hover programmatically (screenshots).</summary>
        internal void SetHover(bool on)
        {
            hover = on;
            Invalidate();
            if (on) ShowPeek(); else HidePeek();
        }

        void ShowPeek()
        {
            if (!hover || menuOpen) return;   // never cover (and so close) the right-click menu
            var rows = PeekForm.BuildRows(this, DateTime.Now);
            if (rows.Count == 0) { HidePeek(); return; }
            if (peek == null) peek = new PeekForm(font, SmallFont, SmallBoldFont);
            peek.ShowRows(rows, new Rectangle(Left, Top, Width, Height), S);
        }

        void HidePeek()
        {
            peek?.Close();
            peek = null;
        }

        public IReadOnlyList<Meeting> UpcomingMeetings(DateTime now) =>
            calWidth == 0 ? new List<Meeting>() : meetings.Where(m => m.End > now).Take(6).ToList();

        public bool ShowsClaude => claudeWidth > 0;
        public double UsageAgeSeconds => UsageAge;
        public int EllipsizeWidth => Px(260);

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            // A click means the user is acting, not peeking: drop the popup and any pending one.
            peekTimer.Stop();
            HidePeek();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            HidePeek();
            if (e.Button == MouseButtons.Left) OpenCalendar();
            else if (e.Button == MouseButtons.Right) ShowMenu(e.Location);
        }

        void OpenCalendar()
        {
            try { Chrome.OpenUrl(cfg.CalendarPageUrl(), cfg.ChromeProfile); }
            catch (Exception ex) { Log.Error("Couldn't open the calendar", ex); }
        }

        void ShowMenu(Point at)
        {
            var menu = new ContextMenuStrip { ShowImageMargin = false };
            if (current?.Link != null)
            {
                var link = current.Link;
                menu.Items.Add("Join video call", null, (s, e) => Chrome.OpenUrl(link, cfg.ChromeProfile));
                menu.Items.Add(new ToolStripSeparator());
            }
            if (calWidth > 0)
            {
                menu.Items.Add("Open Google Calendar", null, (s, e) => OpenCalendar());
                menu.Items.Add("Refresh calendar", null, (s, e) => RefreshCalendar());
            }
            menu.Items.Add("Settings…", null, (s, e) => OpenSettings());
            if (pendingUpdate != null)
            {
                menu.Items.Add(new ToolStripSeparator());
                if (downloadedUpdate != null)
                    menu.Items.Add($"Restart to update to {pendingUpdate.Tag}", null, (s, e) => ApplyUpdate());
                else
                    menu.Items.Add($"Update available: {pendingUpdate.Tag}…", null, (s, e) => Chrome.OpenUrl(pendingUpdate.PageUrl ?? Paths.RepoUrl + "/releases"));
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit", null, (s, e) => Close());
            menu.Closed += (s, e) =>
            {
                menuOpen = false;
                leaveTimer.Start();   // the menu captured the mouse; re-check whether we're still hovered
                BeginInvoke(new Action(menu.Dispose));
            };
            menuOpen = true;
            // The widget never takes focus, but a menu only closes on outside clicks when its app loses focus,
            // so take it now (the same thing Windows asks of tray icon menus).
            WinApi.SetForegroundWindow(Handle);
            menu.Show(this, at);
        }

        public void OpenSettings(bool firstRun = false)
        {
            HidePeek();
            if (settings != null && !settings.IsDisposed) { settings.BringToFront(); settings.Activate(); return; }
            settings = new SettingsForm(cfg, firstRun, demo, onSaved: Restart);
            settings.FormClosed += (s, e) => settings = null;
            settings.Show();
            settings.Activate();
        }

        public SettingsForm Settings => settings;
        public PeekForm Peek => peek;

        /// <summary>Settings changed: start a fresh copy with them and exit.</summary>
        void Restart()
        {
            if (demo) return;
            restartApp?.Invoke();
            Close();
        }

        // ------------------------------------------------------------------------------------------ updates

        void CheckForUpdate()
        {
            if (updateBusy || demo) return;
            updateBusy = true;
            var install = cfg.AutoUpdate && Updater.CanSelfUpdate;
            Task.Run(() =>
            {
                Release release = null;
                string file = null;
                try
                {
                    release = Updater.CheckForUpdate();
                    if (release != null && install) file = Updater.Download(release);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Update check failed: {ex.GetBaseException().Message}");
                }
                Post(() =>
                {
                    updateBusy = false;
                    if (release == null) return;
                    pendingUpdate = release;
                    downloadedUpdate = file;
                    if (file != null) ApplyUpdateWhenIdle();
                });
            });
        }

        /// <summary>Install the downloaded update, but not while the user is looking at the popup or settings.</summary>
        void ApplyUpdateWhenIdle()
        {
            if (hover || settings != null) { Delay(60_000, ApplyUpdateWhenIdle); return; }
            ApplyUpdate();
        }

        void ApplyUpdate()
        {
            try
            {
                Updater.Apply(downloadedUpdate);
                Log.Info($"Updated to {pendingUpdate.Tag}; restarting");
                Restart();
            }
            catch (Exception ex)
            {
                Log.Error("Couldn't apply the update", ex);
                downloadedUpdate = null;
            }
        }

        // ------------------------------------------------------------------------------------------ teardown

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            foreach (var t in new[] { minuteTimer, netTimer, refreshTimer, layoutTimer, usageTimer, peekTimer, leaveTimer, updateTimer })
                t.Stop();
            foreach (var w in watchers) w.Dispose();
            HidePeek();
            shell?.Dispose();
            base.OnFormClosed(e);
        }
    }
}
