using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace TaskbarCalendar
{
    static class Program
    {
        static Mutex instanceMutex;

        public static Icon AppIcon { get; } = LoadIcon();

        static Icon LoadIcon()
        {
            try
            {
                using (var stream = typeof(Program).Assembly.GetManifestResourceStream("icon.ico"))
                    return stream != null ? new Icon(stream) : SystemIcons.Application;
            }
            catch
            {
                return SystemIcons.Application;
            }
        }

        /// <summary>
        ///   TaskbarCalendar.exe                 run (installs itself on first run)
        ///   --settings                          open the settings window (also: launching it again while it runs)
        ///   --demo                              made-up data, nothing saved
        ///   --screenshots &lt;folder&gt;            regenerate the README screenshots from demo data
        ///   --statusline                        Claude Code status line command (reads JSON on stdin)
        ///   --uninstall                         remove it (used by Settings > Apps > Installed apps)
        /// </summary>
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Contains("--statusline")) return StatusLine.Run();   // fast path: no UI, no logging
            if (args.Contains("--list-meetings")) return ListMeetings();

            var demo = args.Contains("--demo") || args.Contains("--screenshots");
            if (args.Contains("--uninstall"))
            {
                Installer.Uninstall();
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => Log.Error("Unhandled UI error", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("Fatal error", e.ExceptionObject as Exception);

            if (!demo)
            {
                Updater.CleanUpAfterUpdate();
                if (Installer.ShouldInstall && Installer.InstallAndRelaunch(args)) return 0;

                instanceMutex = new Mutex(false, @"Local\TaskbarCalendarWidget", out var createdNew);
                if (!createdNew)
                {
                    // Launching it again (e.g. from the Start menu) opens the running copy's settings.
                    WinApi.AskRunningInstanceToOpenSettings();
                    return 0;
                }
                if (Installer.IsInstalledCopy)
                {
                    try { Installer.Register(); } catch (Exception ex) { Log.Warn($"Couldn't refresh the install registration: {ex.Message}"); }
                    if (Config.IsFirstRun) { try { Installer.SetStartup(true); } catch { } }
                    else try { Installer.RepairStartupShortcut(); } catch { }
                }
            }

            Log.Info($"Starting Taskbar Calendar {Paths.VersionText}{(demo ? " (demo)" : "")} from {Paths.CurrentExe}");
            var form = new WidgetForm(demo, Restart, openSettings: !demo && (Config.IsFirstRun || args.Contains("--settings")));
            var context = new ApplicationContext();
            form.FormClosed += (s, e) => context.ExitThread();
            form.Start();

            var shotsIndex = Array.IndexOf(args, "--screenshots");
            if (shotsIndex >= 0) Demo.CaptureScreenshots(form, shotsIndex + 1 < args.Length ? args[shotsIndex + 1] : "docs");

            Application.Run(context);
            return 0;
        }

        /// <summary>`--list-meetings`: print the upcoming meetings as the widget sees them (troubleshooting).</summary>
        static int ListMeetings()
        {
            var cfg = Config.Load();
            var lines = new System.Text.StringBuilder();
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var meetings = CalendarFeed.Fetch(cfg.CalendarUrls(), cfg.LookaheadDays, cfg.Email);
                foreach (var m in meetings.Where(m => m.End > DateTime.Now))
                    lines.AppendLine($"{m.Start:yyyy-MM-dd HH:mm}  {m.End:HH:mm}  {m.Title}{(m.Link != null ? "  [link]" : "")}");
                lines.AppendLine($"{meetings.Count(m => m.End > DateTime.Now)} upcoming meetings, fetched and parsed in {watch.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                lines.AppendLine("Error: " + ex.GetBaseException().Message);
            }
            var bytes = System.Text.Encoding.UTF8.GetBytes(lines.ToString());
            using (var stdout = Console.OpenStandardOutput()) stdout.Write(bytes, 0, bytes.Length);
            return 0;
        }

        /// <summary>Start a fresh copy (after settings changes or an update) once this one has let go of the
        /// single-instance lock.</summary>
        static void Restart()
        {
            instanceMutex?.Dispose();
            instanceMutex = null;
            Updater.Restart();
        }
    }
}
