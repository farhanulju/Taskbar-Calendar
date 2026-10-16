using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TaskbarCalendar
{
    /// <summary>The settings window: regular Windows controls, so it looks and behaves like any Windows dialog.</summary>
    sealed class SettingsForm : Form
    {
        const string IcsHelpUrl = "https://support.google.com/calendar/answer/37648#zippy=%2Cget-your-calendar-view-only";
        const string ClaudeInstallUrl = "https://code.claude.com/docs/en/setup";
        const string DefaultBrowser = "Default browser";
        const int ContentWidth = 470;
        static readonly Color HintColor = Color.FromArgb(0x5f, 0x5f, 0x5f), GoodColor = Color.FromArgb(0x0f, 0x7b, 0x0f),
                              BadColor = Color.FromArgb(0xc4, 0x2b, 0x1c);

        readonly Config cfg;
        readonly bool demo;
        readonly Action onSaved;
        readonly Font headingFont = new Font("Segoe UI Semibold", 10f);

        public readonly TabControl Tabs = new TabControl();
        TextBox icsBox;
        Label testResult, cliStatus, updateStatus;
        ComboBox profileBox, viewBox;
        NumericUpDown lookahead, refresh, width, gap;
        CheckBox showClaude, probe, statusLine, showCalendar, showNetwork, startup, autoUpdate;
        readonly Dictionary<string, string> profiles = new Dictionary<string, string>();
        bool statusLineInitial;

        public SettingsForm(Config cfg, bool firstRun, bool demo, Action onSaved)
        {
            this.cfg = cfg.Clone();
            this.demo = demo;
            this.onSaved = onSaved;

            Text = "Taskbar Calendar settings";
            Icon = Program.AppIcon;
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);
            ShowInTaskbar = true;

            var outer = Column(this, padding: 0);
            if (firstRun)
                Add(outer, new Label { Text = "Welcome! Paste your Google Calendar link below to get started.", Font = headingFont, AutoSize = true, Margin = new Padding(0, 0, 0, 12) });

            Tabs.Width = ContentWidth + 40;
            foreach (var (title, build) in new (string, Action<FlowLayoutPanel>)[]
                     { ("Calendar", CalendarTab), ("Claude usage", ClaudeTab), ("Display", DisplayTab), ("General", GeneralTab) })
            {
                var page = new TabPage(title) { Padding = new Padding(12), UseVisualStyleBackColor = true };
                var col = Column(page, padding: 0);
                build(col);
                Tabs.TabPages.Add(page);
            }
            outer.Controls.Add(Tabs);
            // Size the tab control to its tallest page.
            Tabs.Height = Tabs.TabPages.Cast<TabPage>().Max(p => p.Controls[0].PreferredSize.Height) + 60;

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = false, Width = Tabs.Width, Height = 36, Margin = new Padding(0, 12, 0, 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var save = new Button { Text = "Save", AutoSize = true };
            save.Click += (s, e) => Save();
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(save);
            outer.Controls.Add(buttons);
            AcceptButton = save;
            CancelButton = cancel;
        }

        // ------------------------------------------------------------------------------------------ layout helpers

        static FlowLayoutPanel Column(Control parent, int padding)
        {
            var panel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(padding), Dock = parent is Form ? DockStyle.None : DockStyle.Fill,
            };
            parent.Controls.Add(panel);
            return panel;
        }

        static T Add<T>(Control parent, T control) where T : Control { parent.Controls.Add(control); return control; }

        Label Heading(Control parent, string text, int top = 0) =>
            Add(parent, new Label { Text = text, Font = headingFont, AutoSize = true, Margin = new Padding(0, top, 0, 2) });

        Label Hint(Control parent, string text) =>
            Add(parent, new Label { Text = text, ForeColor = HintColor, AutoSize = true, MaximumSize = new Size(ContentWidth, 0), Margin = new Padding(0, 2, 0, 6) });

        LinkLabel Link(Control parent, string text, string url)
        {
            var link = Add(parent, new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
            link.LinkClicked += (s, e) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return link;
        }

        CheckBox Check(Control parent, string text, bool value) =>
            Add(parent, new CheckBox { Text = text, Checked = value, AutoSize = true, Margin = new Padding(0, 4, 0, 0) });

        static void Separator(Control parent) =>
            Add(parent, new Label { BorderStyle = BorderStyle.Fixed3D, Height = 2, Width = ContentWidth, Margin = new Padding(0, 12, 0, 12) });

        static FlowLayoutPanel Row(Control parent) =>
            Add(parent, new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) });

        static Label Caption(Control parent, string text) =>
            Add(parent, new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 4, 0) });

        NumericUpDown Spin(Control parent, string label, int value, int min, int max, string suffix)
        {
            var row = Row(parent);
            Caption(row, label);
            var spin = Add(row, new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), Width = 60, TextAlign = HorizontalAlignment.Right });
            Caption(row, suffix);
            return spin;
        }

        // ------------------------------------------------------------------------------------------ tabs

        void CalendarTab(FlowLayoutPanel tab)
        {
            Heading(tab, "Google Calendar secret address (iCal format)");
            Hint(tab, "In Google Calendar, open Settings, pick your calendar, then Integrate calendar and copy \"Secret address in " +
                      "iCal format\". For more than one calendar, put each link on its own line.");
            Link(tab, "Show me where to find it", IcsHelpUrl);
            icsBox = Add(tab, new TextBox
            {
                Multiline = true, WordWrap = false, ScrollBars = ScrollBars.Horizontal, Width = ContentWidth, Height = 64,
                Font = new Font("Consolas", 9f), Text = string.Join(Environment.NewLine, cfg.IcsUrls),
            });
            Hint(tab, "Anyone with this link can see your calendar, so it's stored only on this PC.");
            var test = Row(tab);
            var testButton = Add(test, new Button { Text = "Test connection", AutoSize = true });
            testResult = Caption(test, "");
            testButton.Click += (s, e) => TestCalendar();

            Separator(tab);
            Heading(tab, "When you click the widget");
            profiles[DefaultBrowser] = "";
            var chromeProfiles = demo ? Demo.ChromeProfiles() : Chrome.FindChrome() != null ? Chrome.ListProfiles() : new List<(string, string, string)>();
            foreach (var (folder, name, email) in chromeProfiles)
                profiles[$"Chrome: {name}" + (string.IsNullOrEmpty(email) ? "" : $" ({email})")] = folder;
            var row = Row(tab);
            Caption(row, "Open Google Calendar in");
            profileBox = Add(row, new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 });
            profileBox.Items.AddRange(profiles.Keys.ToArray<object>());
            profileBox.SelectedItem = profiles.FirstOrDefault(p => p.Value == cfg.ChromeProfile).Key ?? DefaultBrowser;
            row = Row(tab);
            Caption(row, "Showing the");
            viewBox = Add(row, new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 });
            viewBox.Items.AddRange(Config.CalendarViews.Select(v => (object)v.Label).ToArray());
            viewBox.SelectedItem = Config.CalendarViews.FirstOrDefault(v => v.Key == cfg.CalendarView).Label ?? "Agenda";
            Caption(row, "view");

            Separator(tab);
            lookahead = Spin(tab, "Show meetings for the next", cfg.LookaheadDays, 1, 30, "days");
            refresh = Spin(tab, "Check for calendar changes every", cfg.RefreshMinutes, 1, 60, "minutes");
        }

        void ClaudeTab(FlowLayoutPanel tab)
        {
            showClaude = Check(tab, "Show my Claude plan usage (5-hour and weekly limits)", cfg.ShowClaude);
            Hint(tab, "Shows how much of each limit you've used, when it resets, and whether you're on pace to run out early. " +
                      "Needs a Claude Pro or Max plan.");

            Separator(tab);
            Heading(tab, "Keeping the numbers current");
            var statusRow = Row(tab);
            cliStatus = Caption(statusRow, "Checking the Claude Code CLI…");
            var exe = ClaudeUsage.FindClaudeExe();
            if (demo) SetCliStatus(true, "max", exe ?? "claude.exe", statusRow);
            else if (exe == null) SetCliStatus(false, null, null, statusRow);
            else Task.Run(() => ClaudeUsage.CliSignIn(exe)).ContinueWith(t =>
                     BeginInvoke(new Action(() => SetCliStatus(t.Result.SignedIn, t.Result.Plan, exe, statusRow))));

            probe = Check(tab, "Refresh automatically (free, recommended)", cfg.ClaudeProbe);
            Hint(tab, "Reads the same numbers as Claude Code's /usage from Anthropic's usage service, using the Claude Code CLI's " +
                      "sign-in. No tokens are used and nothing counts against your limits. Checks shortly after you use Claude, " +
                      "when you hover over the widget, and every 10 minutes.");

            var state = ClaudeUsage.StatusLineState();
            statusLineInitial = state == "ours";
            statusLine = Check(tab, "Also update from the Claude Code CLI status line (free)", statusLineInitial);
            if (state == "other") { statusLine.Enabled = false; Hint(tab, "You already have your own Claude Code status line, so this is left alone."); }
            else if (state == "error") { statusLine.Enabled = false; Hint(tab, "Couldn't read your Claude Code settings (~/.claude/settings.json)."); }
            else Hint(tab, "Adds a status line to ~/.claude/settings.json that saves your usage after each CLI reply. The desktop app doesn't run status lines.");
        }

        void SetCliStatus(bool signedIn, string plan, string exe, FlowLayoutPanel row)
        {
            if (IsDisposed) return;
            if (signedIn)
            {
                cliStatus.Text = "✓ Claude Code CLI is signed in" + (string.IsNullOrEmpty(plan) ? "" : $" ({char.ToUpper(plan[0]) + plan.Substring(1)} plan)");
                cliStatus.ForeColor = GoodColor;
            }
            else if (exe != null)
            {
                cliStatus.Text = "✗ The Claude Code CLI isn't signed in. Open a terminal and run  claude  once.";
                cliStatus.ForeColor = BadColor;
            }
            else
            {
                cliStatus.Text = "✗ Claude Code CLI not found";
                cliStatus.ForeColor = BadColor;
                Link(row, "How to install it", ClaudeInstallUrl);
            }
        }

        void DisplayTab(FlowLayoutPanel tab)
        {
            Heading(tab, "Show on the taskbar");
            showCalendar = Check(tab, "Next meeting", cfg.ShowCalendar);
            Add(tab, new Label { Text = "Claude usage: see the Claude usage tab", ForeColor = HintColor, AutoSize = true, Margin = new Padding(18, 2, 0, 0) });
            showNetwork = Check(tab, "Network upload and download speed", cfg.ShowNetwork);

            Separator(tab);
            Heading(tab, "Size and position");
            width = Spin(tab, "Meeting text width", cfg.Width, 120, 500, "px");
            gap = Spin(tab, "Space to the right of the widget", cfg.RightGap, 0, 400, "px");
            Hint(tab, "The widget sits just left of the system tray, or of the weather widget when your taskbar icons are " +
                      "left-aligned. Increase the space to move it further left.");
        }

        void GeneralTab(FlowLayoutPanel tab)
        {
            startup = Check(tab, "Start Taskbar Calendar when I sign in to Windows", demo || Installer.StartupEnabled);

            Separator(tab);
            Heading(tab, "Updates");
            autoUpdate = Check(tab, "Install updates automatically", cfg.AutoUpdate);
            if (Installer.IsWingetManaged)
                Hint(tab, "Installed with winget, so updates come through winget:  winget upgrade TaskbarCalendar");
            else if (!Updater.CanSelfUpdate && !demo)
                Hint(tab, "This is a portable or development copy, so it only tells you about new releases.");
            else
                Hint(tab, "New releases are downloaded from GitHub, checked against their published SHA-256 checksum, and " +
                          "installed when you're not using the widget.");
            var row = Row(tab);
            var check = Add(row, new Button { Text = "Check for updates", AutoSize = true });
            updateStatus = Caption(row, $"You have version {Paths.VersionText}.");
            check.Click += (s, e) => CheckForUpdates();

            Separator(tab);
            Heading(tab, "Files");
            row = Row(tab);
            var openSettings = Add(row, new Button { Text = "Open settings folder", AutoSize = true });
            openSettings.Click += (s, e) => OpenFolder(Paths.ConfigDir);
            var openLogs = Add(row, new Button { Text = "Open log folder", AutoSize = true });
            openLogs.Click += (s, e) => OpenFolder(Paths.DataDir);

            Separator(tab);
            Heading(tab, $"Taskbar Calendar {Paths.VersionText}");
            Link(tab, Paths.RepoUrl.Replace("https://", ""), Paths.RepoUrl);
        }

        static void OpenFolder(string path)
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        // ------------------------------------------------------------------------------------------ actions

        List<string> Urls() => icsBox.Lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(Config.NormalizeIcsUrl).ToList();

        void TestCalendar()
        {
            var urls = Urls();
            if (urls.Count == 0) { SetResult(testResult, "Paste your calendar link first.", BadColor); return; }
            SetResult(testResult, "Checking…", SystemColors.ControlText);
            var days = (int)lookahead.Value;
            Task.Run(() =>
            {
                try { return (true, CalendarFeed.Fetch(urls, days).Count); }
                catch (Exception ex) { Log.Info($"Calendar test failed: {ex.GetBaseException().Message}"); return (false, 0); }
            }).ContinueWith(t => BeginInvoke(new Action(() => SetResult(testResult,
                t.Result.Item1 ? $"✓ Connected. {t.Result.Item2} meetings in the next {days} days."
                               : "✗ Couldn't read that link. Check it's the secret iCal address.",
                t.Result.Item1 ? GoodColor : BadColor))));
        }

        void CheckForUpdates()
        {
            SetResult(updateStatus, "Checking…", SystemColors.ControlText);
            Task.Run(() =>
            {
                try { return (Updater.CheckForUpdate(), (string)null); }
                catch (Exception ex) { return ((Release)null, ex.GetBaseException().Message); }
            }).ContinueWith(t => BeginInvoke(new Action(() =>
            {
                var (release, error) = t.Result;
                if (error != null) SetResult(updateStatus, "Couldn't reach GitHub. Try again later.", BadColor);
                else if (release == null) SetResult(updateStatus, $"You're up to date ({Paths.VersionText}).", GoodColor);
                else SetResult(updateStatus, $"{release.Tag} is available. " + (Updater.CanSelfUpdate && autoUpdate.Checked
                    ? "It will install in the background shortly." : "Download it from GitHub."), GoodColor);
            })));
        }

        void SetResult(Label label, string text, Color color)
        {
            if (IsDisposed) return;
            label.Text = text;
            label.ForeColor = color;
        }

        void Save()
        {
            if (demo) { Close(); return; }
            var urls = Urls();
            var bad = urls.FirstOrDefault(u => !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                                               !u.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
            if (bad != null)
            {
                MessageBox.Show(this, $"This doesn't look like a calendar link:\n\n{bad}", "Taskbar Calendar", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            cfg.IcsUrls = urls;
            cfg.ChromeProfile = profiles.TryGetValue(profileBox.SelectedItem as string ?? DefaultBrowser, out var folder) ? folder : "";
            cfg.CalendarView = Config.CalendarViews.FirstOrDefault(v => v.Label == viewBox.SelectedItem as string).Key ?? "agenda";
            cfg.LookaheadDays = (int)lookahead.Value;
            cfg.RefreshMinutes = (int)refresh.Value;
            cfg.ShowCalendar = showCalendar.Checked;
            cfg.ShowClaude = showClaude.Checked;
            cfg.ClaudeProbe = probe.Checked;
            cfg.ShowNetwork = showNetwork.Checked;
            cfg.AutoUpdate = autoUpdate.Checked;
            cfg.Width = (int)width.Value;
            cfg.RightGap = (int)gap.Value;
            cfg.Save();

            var problems = new List<string>();
            try { if (startup.Checked != Installer.StartupEnabled) Installer.SetStartup(startup.Checked); }
            catch (Exception ex) { Log.Error("Startup shortcut", ex); problems.Add($"Couldn't change the startup setting: {ex.Message}"); }
            try
            {
                if (statusLine.Checked != statusLineInitial)
                {
                    if (statusLine.Checked) ClaudeUsage.InstallStatusLine(Paths.CurrentExe);
                    else ClaudeUsage.RemoveStatusLine();
                }
            }
            catch (Exception ex) { Log.Error("Claude status line", ex); problems.Add($"Couldn't update the Claude Code status line: {ex.Message}"); }
            if (problems.Count > 0)
                MessageBox.Show(this, "Settings saved, but:\n\n" + string.Join("\n", problems), "Taskbar Calendar", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            Close();
            onSaved?.Invoke();
        }
    }
}
