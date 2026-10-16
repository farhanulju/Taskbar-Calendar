using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TaskbarCalendar
{
    /// <summary>The hover popup above the widget: upcoming meetings and Claude usage details.</summary>
    sealed class PeekForm : Form
    {
        public enum Kind { Heading, Row, Note }

        public sealed class Row : IEquatable<Row>
        {
            public Kind Kind;
            public string Left, Right;
            public Color LeftColor, RightColor;
            public int GapAbove;
            public bool Equals(Row o) => o != null && Kind == o.Kind && Left == o.Left && Right == o.Right &&
                                         LeftColor == o.LeftColor && RightColor == o.RightColor && GapAbove == o.GapAbove;
            public override bool Equals(object o) => Equals(o as Row);
            public override int GetHashCode() => (Left ?? "").GetHashCode() ^ (Right ?? "").GetHashCode();
        }

        static readonly Color Bg = Color.FromArgb(0x2b, 0x2b, 0x2b), Border = Color.FromArgb(0x3d, 0x3d, 0x3d);
        readonly Font font, small, smallBold;
        List<Row> rows = new List<Row>();
        float scale = 1;

        public PeekForm(Font font, Font small, Font smallBold)
        {
            this.font = font;
            this.small = small;
            this.smallBold = smallBold;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            BackColor = Bg;
            AutoScaleMode = AutoScaleMode.None;
        }

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

        /// <summary>What the popup shows, as plain data, so it's only redrawn when something actually changed.</summary>
        public static List<Row> BuildRows(WidgetForm widget, DateTime now)
        {
            var rows = new List<Row>();
            var upcoming = widget.UpcomingMeetings(now);
            DateTime? lastDay = null;
            foreach (var m in upcoming)
            {
                if (m.Start.Date != lastDay)
                {
                    var days = (m.Start.Date - now.Date).Days;
                    var label = days == 0 ? "Today" : days == 1 ? "Tomorrow" : m.Start.ToString("dddd, MMM dd", System.Globalization.CultureInfo.InvariantCulture);
                    rows.Add(new Row { Kind = Kind.Heading, Left = label, LeftColor = WidgetForm.Grey, GapAbove = lastDay == null ? 0 : 4 });
                    lastDay = m.Start.Date;
                }
                rows.Add(new Row
                {
                    Kind = Kind.Row,
                    Left = $"{CalendarFeed.FmtTime(m.Start)}–{CalendarFeed.FmtTime(m.End)}",
                    LeftColor = m.Start <= now ? WidgetForm.Green : WidgetForm.Dim,
                    Right = m.Title,
                    RightColor = Color.White,
                });
            }

            var usage = widget.ShowsClaude ? widget.UsageRows(now) : new List<WidgetForm.UsageRow>();
            if (usage.Count > 0)
            {
                rows.Add(new Row { Kind = Kind.Heading, Left = "Claude usage", LeftColor = WidgetForm.Grey, GapAbove = upcoming.Count > 0 ? 8 : 0 });
                var nowTs = Epoch.From(now);
                foreach (var u in usage)
                {
                    var detail = $"{u.Pct:0}% used" + (u.Resets != null
                        ? $" · resets in {ClaudeUsage.FmtCountdown(u.Resets.Value - nowTs)} ({ClaudeUsage.FmtReset(u.Resets.Value, now)})"
                        : " · starts when you next use Claude");
                    rows.Add(new Row { Kind = Kind.Row, Left = u.Long, LeftColor = WidgetForm.Dim, Right = detail,
                                       RightColor = WidgetForm.UsageColor(u.Pct, u.RunsOut != null) });
                    if (u.RunsOut != null)
                        rows.Add(new Row { Kind = Kind.Note, LeftColor = WidgetForm.Amber,
                                           Left = $"At this rate you'll hit the limit around {ClaudeUsage.FmtReset(u.RunsOut.Value, now)}, " +
                                                  $"{ClaudeUsage.FmtCountdown(u.Resets.Value - u.RunsOut.Value)} before it resets" });
                    else if (u.Elapsed != null && u.Elapsed >= 0.05 && u.Pct > 0)
                        rows.Add(new Row { Kind = Kind.Note, LeftColor = WidgetForm.Grey,
                                           Left = $"At this rate you'll be at ~{Math.Min(100, u.Pct / u.Elapsed.Value):0}% when it resets" });
                }
                var age = (int)(widget.UsageAgeSeconds / 60);
                var ago = age < 1 ? "just now" : age < 60 ? $"{age} min ago" : $"{age / 60} h ago";
                rows.Add(new Row { Kind = Kind.Note, Left = $"Updated {ago}", LeftColor = WidgetForm.Grey });
            }
            return rows;
        }

        int Px(float v) => (int)Math.Round(v * scale);
        Font FontFor(Kind k) => k == Kind.Heading ? smallBold : k == Kind.Note ? small : font;
        int LeftColumn => TextRenderer.MeasureText("12:00 AM–12:00 AM ", font, Size.Empty, TextFormatFlags.NoPadding).Width + Px(8);
        int MaxRight => Px(300);
        const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;

        /// <summary>Show (or update) above <paramref name="anchor"/>; does nothing if the content is unchanged.</summary>
        public void ShowRows(List<Row> newRows, Rectangle anchor, float dpiScale)
        {
            if (Visible && newRows.SequenceEqual(rows)) return;
            rows = newRows;
            scale = dpiScale;

            int width = 0, height = 0;
            foreach (var r in rows)
            {
                var f = FontFor(r.Kind);
                var size = TextRenderer.MeasureText(r.Left ?? "", f, Size.Empty, TextFormatFlags.NoPadding);
                int w = r.Kind == Kind.Row
                    ? LeftColumn + Math.Min(MaxRight, TextRenderer.MeasureText(r.Right ?? "", font, Size.Empty, TextFormatFlags.NoPadding).Width)
                    : size.Width;
                width = Math.Max(width, w);
                height += Px(r.GapAbove) + size.Height + (r.Kind == Kind.Heading ? Px(2) : 0);
            }
            int padX = Px(12), padY = Px(10);
            var bounds = new Rectangle(0, 0, width + 2 * padX, height + 2 * padY);
            var screen = Screen.FromRectangle(anchor).Bounds;
            bounds.X = Math.Max(screen.Left + 8, Math.Min(anchor.Right - bounds.Width, screen.Right - bounds.Width - 8));
            bounds.Y = anchor.Top - bounds.Height - Px(14);
            Bounds = bounds;
            Invalidate();
            if (!Visible) Show();
            WinApi.RaiseTopmost(Handle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Bg);
            using (var pen = new Pen(Border))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            int x = Px(12), y = Px(10);
            foreach (var r in rows)
            {
                y += Px(r.GapAbove);
                var f = FontFor(r.Kind);
                int h = TextRenderer.MeasureText("Ag", f, Size.Empty, TextFormatFlags.NoPadding).Height;
                if (r.Kind == Kind.Row)
                {
                    TextRenderer.DrawText(g, r.Left, f, new Rectangle(x, y, LeftColumn, h), r.LeftColor, Bg, Flags);
                    TextRenderer.DrawText(g, r.Right, f, new Rectangle(x + LeftColumn, y, Width - x - LeftColumn - Px(12), h), r.RightColor, Bg, Flags);
                }
                else
                {
                    TextRenderer.DrawText(g, r.Left, f, new Rectangle(x, y, Width - 2 * x, h), r.LeftColor, Bg, Flags);
                }
                y += h + (r.Kind == Kind.Heading ? Px(2) : 0);
            }
        }
    }
}
