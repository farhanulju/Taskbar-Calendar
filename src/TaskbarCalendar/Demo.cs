using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace TaskbarCalendar
{
    /// <summary>Made-up data for --demo and the README screenshots, so nothing personal is ever shown.</summary>
    static class Demo
    {
        public static Config Config() => new Config
        {
            IcsUrls = new List<string> { "https://calendar.google.com/calendar/ical/you%40example.com/private-0123456789abcdef/basic.ics" },
        };

        public static List<(string, string, string)> ChromeProfiles() => new List<(string, string, string)>
        {
            ("Default", "Personal", "you@example.com"),
            ("Profile 1", "Work", "you@work.example.com"),
        };

        public static List<Meeting> Meetings(DateTime now)
        {
            var b = now.Date.AddHours(now.Hour).AddMinutes(now.Minute);
            Meeting M(string title, int start, int minutes, string link = null) =>
                new Meeting { Title = title, Start = b.AddMinutes(start), End = b.AddMinutes(start + minutes), Link = link };
            return new List<Meeting>
            {
                M("Design review: onboarding flow", 7, 30, "https://meet.google.com/abc-defg-hij"),
                M("1:1 with Alex", 95, 30),
                M("Sprint planning", 180, 60),
                M("Customer call: Northwind", 24 * 60 + 60, 45),
                M("Team standup", 24 * 60 + 150, 15),
            };
        }

        public static ClaudeUsage.Snapshot Usage()
        {
            var now = Epoch.Now;
            return new ClaudeUsage.Snapshot
            {
                Updated = now - 120,
                Windows =
                {
                    ["five_hour"] = new UsageWindow { UsedPercentage = 42, ResetsAt = now + 2 * 3600 + 14 * 60 },
                    ["seven_day"] = new UsageWindow { UsedPercentage = 61, ResetsAt = now + 3 * 86400 + 4 * 3600 },
                },
            };
        }

        /// <summary>Regenerate the README screenshots: a neutral backdrop covers the desktop (not the taskbar), so
        /// none of your own windows end up in the images.</summary>
        public static void CaptureScreenshots(WidgetForm widget, string folder)
        {
            Directory.CreateDirectory(folder);
            var tb = WinApi.Taskbar().Bounds;
            var backdrop = new Form
            {
                FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, TopMost = true, StartPosition = FormStartPosition.Manual,
                Bounds = new Rectangle(tb.Left, 0, tb.Width, tb.Top),
            };
            backdrop.Paint += (s, e) =>
            {
                using (var brush = new LinearGradientBrush(backdrop.ClientRectangle, Color.FromArgb(0x1f, 0x23, 0x2d), Color.FromArgb(0x2f, 0x33, 0x3d), 90f))
                    e.Graphics.FillRectangle(brush, backdrop.ClientRectangle);
            };
            backdrop.Show();

            var steps = new Queue<Action>();
            void Next(int ms) { var t = new Timer { Interval = ms }; t.Tick += (s, e) => { t.Stop(); t.Dispose(); steps.Dequeue()(); }; t.Start(); }

            steps.Enqueue(() =>
            {
                WinApi.RaiseTopmost(widget.Handle);
                Capture(new Rectangle(widget.Left - 24, tb.Top, tb.Right - (widget.Left - 24), tb.Height), Path.Combine(folder, "taskbar.png"));
                widget.SetHover(true);
                Next(700);
            });
            steps.Enqueue(() =>
            {
                var p = widget.Peek.Bounds;
                var left = Math.Min(p.Left, widget.Left) - 24;
                Capture(Rectangle.FromLTRB(left, p.Top - 24, Math.Max(p.Right, widget.Right) + 24, tb.Bottom), Path.Combine(folder, "popup.png"));
                widget.SetHover(false);
                widget.OpenSettings();
                var s = widget.Settings;
                var area = Screen.PrimaryScreen.WorkingArea;   // it normally centers on the screen with the cursor
                s.Location = new Point(area.Left + (area.Width - s.Width) / 2, area.Top + (area.Height - s.Height) / 3);
                s.TopMost = true;
                s.BringToFront();
                Next(1200);
            });
            foreach (var (tab, name) in new[] { (0, "settings.png"), (1, "settings-claude.png") })
            {
                steps.Enqueue(() => { widget.Settings.Tabs.SelectedIndex = tab; widget.Settings.Refresh(); Next(500); });
                steps.Enqueue(() =>
                {
                    var r = widget.Settings.Bounds;
                    r.Inflate(16, 16);
                    Capture(r, Path.Combine(folder, name));
                    Next(100);
                });
            }
            steps.Enqueue(() => { widget.Settings.Close(); backdrop.Close(); widget.Close(); });
            Next(2500);
        }

        static void Capture(Rectangle area, string path)
        {
            using (var bmp = new Bitmap(area.Width, area.Height))
            {
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(area.Location, Point.Empty, area.Size);
                bmp.Save(path, ImageFormat.Png);
            }
            Log.Info($"Saved screenshot {path} ({area.Width}x{area.Height})");
        }
    }
}
