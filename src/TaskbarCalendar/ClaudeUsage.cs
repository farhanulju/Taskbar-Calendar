using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;

namespace TaskbarCalendar
{
    class UsageWindow
    {
        public double UsedPercentage;
        public double? ResetsAt;   // epoch seconds
    }

    class NotSignedInException : Exception { public NotSignedInException(string message) : base(message) { } }

    class RateLimitedException : Exception
    {
        public readonly int RetryAfter;
        public RateLimitedException(int retryAfter) : base($"rate limited, retry after {retryAfter}s") { RetryAfter = retryAfter; }
    }

    /// <summary>
    /// Claude plan usage (5-hour and weekly windows). Three sources write the usage file, and the widget redraws
    /// whenever it changes:
    ///  * <see cref="FetchUsage"/>: the usage service behind Claude Code's /usage, called with the Claude Code CLI's
    ///    sign-in. Free: no model request, nothing counts against your limits. The main source.
    ///  * The CLI status line (<see cref="StatusLine"/>), after each CLI reply. Free, CLI only.
    ///  * <see cref="ProbeUsage"/>: a minimal headless `claude -p` request (~900 tokens). Last resort only.
    /// </summary>
    static class ClaudeUsage
    {
        public static readonly string[] Windows = { "five_hour", "seven_day" };
        public static readonly Dictionary<string, double> WindowSeconds = new Dictionary<string, double>
        {
            ["five_hour"] = 5 * 3600,
            ["seven_day"] = 7 * 86400,
        };

        static readonly string ClaudeHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        public static readonly string ProjectsDir = Path.Combine(ClaudeHome, "projects");   // transcripts: activity signal
        public static string Credentials = Path.Combine(ClaudeHome, ".credentials.json");   // the CLI's sign-in (read-only)
        public static string ClaudeSettings = Path.Combine(ClaudeHome, "settings.json");
        const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

        // Renews the CLI's sign-in without using tokens: the CLI refreshes an expired token before sending a request,
        // and the request itself is then rejected because the model doesn't exist.
        static readonly string[] RenewArgs =
        {
            "-p", "ok", "--model", "no-such-model", "--max-turns", "1", "--output-format", "json",
            "--no-session-persistence", "--tools", "", "--strict-mcp-config", "--disable-slash-commands",
        };

        static readonly string[] ProbeArgs =
        {
            "-p", "ok", "--model", "haiku", "--max-turns", "1", "--output-format", "stream-json", "--verbose",
            "--no-session-persistence", "--tools", "", "--strict-mcp-config", "--disable-slash-commands",
            "--system-prompt", "Reply with OK.",
        };

        internal static Func<HttpRequestMessage, HttpResponseMessage> Send = request => Http.SendAsync(request).GetAwaiter().GetResult();
        internal static Func<string, bool> Renew = RenewSignIn;
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        // ----------------------------------------------------------------------------------------- usage file

        public class Snapshot
        {
            public Dictionary<string, UsageWindow> Windows = new Dictionary<string, UsageWindow>();
            public double Updated;
        }

        public static Snapshot Load()
        {
            try
            {
                var d = Json.ParseObject(FileUtil.ReadText(Paths.UsageFile));
                if (d == null) return null;
                var snap = new Snapshot { Updated = Json.Num(d, "updated") ?? 0 };
                foreach (var key in Windows)
                {
                    var w = Json.Obj(d, key);
                    if (w == null) continue;
                    snap.Windows[key] = new UsageWindow
                    {
                        UsedPercentage = Json.Num(w, "used_percentage") ?? 0,
                        ResetsAt = Json.Num(w, "resets_at"),
                    };
                }
                return snap;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidOperationException)
            {
                return null;
            }
        }

        public static void Save(Dictionary<string, UsageWindow> windows)
        {
            var d = new Dictionary<string, object>();
            foreach (var kv in windows)
                d[kv.Key] = new Dictionary<string, object> { ["used_percentage"] = kv.Value.UsedPercentage, ["resets_at"] = kv.Value.ResetsAt };
            d["updated"] = Epoch.Now;
            FileUtil.AtomicWrite(Paths.UsageFile, Json.Serialize(d));
        }

        // ----------------------------------------------------------------------------------------- Claude CLI

        public static string FindClaudeExe()
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim('"'), "claude.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
            var native = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
            return File.Exists(native) ? native : null;
        }

        static (int ExitCode, string Stdout, string Stderr) RunCli(string exe, IEnumerable<string> args, int timeoutMs)
        {
            Directory.CreateDirectory(Path.Combine(Paths.DataDir, "probe"));
            var psi = new ProcessStartInfo(exe, string.Join(" ", args.Select(Quote)))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.Combine(Paths.DataDir, "probe"),
            };
            using (var p = Process.Start(psi))
            {
                p.StandardInput.Close();
                var stderrTask = p.StandardError.ReadToEndAsync();
                var stdout = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } }
                return (p.HasExited ? p.ExitCode : -1, stdout, stderrTask.Result);
            }
        }

        static string Quote(string arg) =>
            arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '"', '\t' }) < 0 ? arg : "\"" + arg.Replace("\"", "\\\"") + "\"";

        /// <summary>(signed in, plan) from `claude auth status`: a local check, no request to the model.</summary>
        public static (bool SignedIn, string Plan) CliSignIn(string exe)
        {
            try
            {
                var status = Json.ParseObject(RunCli(exe, new[] { "auth", "status", "--json" }, 15000).Stdout);
                var signedIn = Json.Bool(status, "loggedIn") == true && Json.Str(status, "authMethod") == "claude.ai";
                return (signedIn, Json.Str(status, "subscriptionType"));
            }
            catch (Exception)
            {
                return (false, null);
            }
        }

        /// <summary>(access token, expiry epoch seconds) from the CLI's credentials. Read-only: the token is never
        /// refreshed, stored, logged or sent anywhere except api.anthropic.com.</summary>
        static (string Token, double? Expires) ReadSignIn()
        {
            try
            {
                var oauth = Json.Obj(Json.Parse(FileUtil.ReadText(Credentials)), "claudeAiOauth");
                var expires = Json.Num(oauth, "expiresAt");
                return (Json.Str(oauth, "accessToken"), expires / 1000);
            }
            catch (Exception)
            {
                return (null, null);
            }
        }

        /// <summary>Have the CLI renew its own sign-in (zero tokens; see RenewArgs). Blocking, ~5 s.</summary>
        static bool RenewSignIn(string exe)
        {
            try { RunCli(exe, RenewArgs, 60000); } catch (Exception ex) { Log.Warn($"Couldn't run the Claude CLI: {ex.Message}"); }
            var (token, expires) = ReadSignIn();
            var renewed = token != null && expires > Epoch.Now;
            Log.Info(renewed ? "Renewed the Claude Code sign-in (no tokens used)" : "Couldn't renew the Claude Code sign-in");
            return renewed;
        }

        public static Dictionary<string, UsageWindow> ParseUsageResponse(Dictionary<string, object> data)
        {
            var windows = new Dictionary<string, UsageWindow>();
            foreach (var key in Windows)
            {
                var w = Json.Obj(data, key);
                var utilization = Json.Num(w, "utilization");
                if (utilization == null) continue;
                double? resets = Json.Num(w, "resets_at");
                if (resets == null && Json.Str(w, "resets_at") is string iso)
                    resets = Epoch.From(DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture).UtcDateTime);
                windows[key] = new UsageWindow { UsedPercentage = utilization.Value, ResetsAt = resets };
            }
            return windows;
        }

        /// <summary>Plan usage from the usage service, renewing the CLI's sign-in first if it has expired.
        /// Throws NotSignedInException or RateLimitedException; network errors propagate. Blocking.</summary>
        public static Dictionary<string, UsageWindow> FetchUsage(string exe)
        {
            var (token, expires) = ReadSignIn();
            if (token == null) throw new NotSignedInException("The Claude Code CLI isn't signed in.");
            bool renewed = false;
            if (expires != null && expires < Epoch.Now + 60 && exe != null)
            {
                renewed = Renew(exe);
                token = ReadSignIn().Token;
            }

            HttpResponseMessage response = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
                request.Headers.TryAddWithoutValidation("User-Agent", "taskbar-calendar/" + Paths.VersionText);
                response = Send(request);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && exe != null && !renewed)
                {
                    renewed = Renew(exe);
                    token = ReadSignIn().Token;
                    continue;
                }
                break;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                throw new NotSignedInException($"The usage service rejected the Claude Code sign-in (HTTP {(int)response.StatusCode}).");
            if ((int)response.StatusCode == 429)
            {
                var retry = response.Headers.RetryAfter?.Delta?.TotalSeconds;
                throw new RateLimitedException(retry == null ? 300 : Math.Max(60, (int)retry));
            }
            response.EnsureSuccessStatusCode();
            var windows = ParseUsageResponse(Json.ParseObject(response.Content.ReadAsStringAsync().GetAwaiter().GetResult()));
            Log.Info($"Usage: {Describe(windows)} (free usage service)");
            return windows;
        }

        static string Describe(Dictionary<string, UsageWindow> windows) =>
            windows.Count == 0 ? "no usage in response" : string.Join(", ", windows.Select(kv => $"{kv.Key} {kv.Value.UsedPercentage:0}%"));

        // ----------------------------------------------------------------------------------------- fallback probe

        public static Dictionary<string, UsageWindow> ParseProbeOutput(string text)
        {
            foreach (var line in text.Split('\n'))
            {
                Dictionary<string, object> msg;
                try { msg = Json.ParseObject(line.Trim()); } catch { continue; }
                if (Json.Str(msg, "type") != "rate_limit_event") continue;
                var unified = Json.Obj(Json.Obj(msg, "rate_limit_info"), "unifiedWindows");
                var windows = new Dictionary<string, UsageWindow>();
                foreach (var key in Windows)
                {
                    var w = Json.Obj(unified, key);
                    var utilization = Json.Num(w, "utilization");
                    if (utilization != null)
                        windows[key] = new UsageWindow { UsedPercentage = Math.Round(utilization.Value * 100, 1), ResetsAt = Json.Num(w, "resetsAt") };
                }
                if (windows.Count > 0) return windows;
            }
            return null;
        }

        public static int? ProbeTokens(string text)
        {
            foreach (var line in text.Split('\n').Reverse())
            {
                Dictionary<string, object> msg;
                try { msg = Json.ParseObject(line.Trim()); } catch { continue; }
                if (Json.Str(msg, "type") != "result") continue;
                var usage = Json.Obj(msg, "usage");
                return (int)new[] { "input_tokens", "output_tokens", "cache_creation_input_tokens", "cache_read_input_tokens" }
                    .Sum(k => Json.Num(usage, k) ?? 0);
            }
            return null;
        }

        /// <summary>Last resort: one minimal Haiku request (~900 tokens). Blocking, ~5 s.</summary>
        public static Dictionary<string, UsageWindow> ProbeUsage(string exe)
        {
            var (code, stdout, stderr) = RunCli(exe, ProbeArgs, 90000);
            var windows = ParseProbeOutput(stdout);
            Log.Info($"Usage probe: {ProbeTokens(stdout)} tokens, {(windows == null ? "no usage in response" : Describe(windows))}");
            if (windows == null) Log.Warn($"Usage probe returned no usage (exit {code}): {Tail(stderr, 300)}");
            return windows;
        }

        static string Tail(string s, int n) => s == null || s.Length <= n ? s : s.Substring(s.Length - n);

        // ----------------------------------------------------------------------------------------- display maths

        public static string FmtCountdown(double seconds)
        {
            var minutes = Math.Max(0, (int)Math.Floor(seconds / 60));
            if (minutes < 60) return $"{minutes}m";
            var hours = minutes / 60;
            minutes %= 60;
            if (hours < 24) return $"{hours}h {minutes:00}m";
            return $"{hours / 24}d {hours % 24}h";
        }

        /// <summary>(fraction of the window elapsed, epoch when you'd reach 100% at the current rate or null).</summary>
        public static (double Elapsed, double? RunsOut) Pace(double pct, double resetsAt, double length, double now)
        {
            var elapsed = Math.Min(1.0, Math.Max(0.0, 1 - (resetsAt - now) / length));
            if (pct <= 0 || elapsed < 0.05) return (elapsed, null);   // too early in the window for a meaningful rate
            var rate = pct / (elapsed * length);                        // percent per second
            var hit = now + (100 - pct) / rate;
            return (elapsed, hit < resetsAt ? hit : (double?)null);
        }

        public static string FmtReset(double epoch, DateTime now)
        {
            var t = Epoch.ToLocal(epoch);
            if (t.Date == now.Date) return CalendarFeed.FmtTime(t);
            if ((t.Date - now.Date).Days == 1) return "tomorrow " + CalendarFeed.FmtTime(t);
            return $"{t.ToString("ddd", CultureInfo.InvariantCulture)} {CalendarFeed.FmtTime(t)}";
        }

        // ----------------------------------------------------------------------------------------- CLI status line

        static Dictionary<string, object> ReadClaudeSettings() =>
            File.Exists(ClaudeSettings) ? Json.ParseObject(FileUtil.ReadText(ClaudeSettings)) ?? new Dictionary<string, object>()
                                        : new Dictionary<string, object>();

        static bool IsOurs(object statusLine)
        {
            var command = (Json.Str(statusLine as Dictionary<string, object>, "command") ?? "").Replace('\\', '/');
            return command.Contains("taskbar_calendar/statusline.py") || command.Contains("claude_statusline.py")
                   || command.IndexOf("TaskbarCalendar.exe\" --statusline", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>"ours", "none", "other" (someone else's status line; left alone) or "error".</summary>
        public static string StatusLineState()
        {
            try
            {
                var line = Json.Get(ReadClaudeSettings(), "statusLine");
                if (line == null) return "none";
                return IsOurs(line) ? "ours" : "other";
            }
            catch (Exception)
            {
                return "error";
            }
        }

        public static void InstallStatusLine(string exePath)
        {
            var settings = ReadClaudeSettings();
            var existing = Json.Get(settings, "statusLine");
            if (existing != null && !IsOurs(existing))
                throw new InvalidOperationException("Claude Code already has a different status line configured.");
            settings["statusLine"] = new Dictionary<string, object>
            {
                ["type"] = "command",
                ["command"] = $"\"{exePath.Replace('\\', '/')}\" --statusline",
            };
            FileUtil.AtomicWrite(ClaudeSettings, Json.Serialize(settings, indent: true) + "\n");
        }

        public static void RemoveStatusLine()
        {
            var settings = ReadClaudeSettings();
            if (!IsOurs(Json.Get(settings, "statusLine"))) return;
            settings.Remove("statusLine");
            FileUtil.AtomicWrite(ClaudeSettings, Json.Serialize(settings, indent: true) + "\n");
        }
    }

    /// <summary>`TaskbarCalendar.exe --statusline`: Claude Code's CLI runs this after each reply with session info
    /// as JSON on stdin (https://code.claude.com/docs/en/statusline). Saves the usage and prints a short summary.</summary>
    static class StatusLine
    {
        public static int Run()
        {
            string input;
            using (var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8))
                input = stdin.ReadToEnd();
            var windows = new Dictionary<string, UsageWindow>();
            try
            {
                var limits = Json.Obj(Json.Parse(string.IsNullOrWhiteSpace(input) ? "{}" : input), "rate_limits");
                foreach (var key in ClaudeUsage.Windows)
                {
                    var w = Json.Obj(limits, key);
                    var pct = Json.Num(w, "used_percentage");
                    if (pct != null) windows[key] = new UsageWindow { UsedPercentage = pct.Value, ResetsAt = Json.Num(w, "resets_at") };
                }
                if (windows.Count > 0) ClaudeUsage.Save(windows);
            }
            catch (Exception)
            {
                // Never break Claude Code's status line.
            }
            var summary = string.Join(" · ", new[] { ("five_hour", "5h"), ("seven_day", "wk") }
                .Where(p => windows.ContainsKey(p.Item1))
                .Select(p => $"{p.Item2} {windows[p.Item1].UsedPercentage:0}%"));
            var bytes = Encoding.UTF8.GetBytes(summary + "\n");
            using (var stdout = Console.OpenStandardOutput())
                stdout.Write(bytes, 0, bytes.Length);
            return 0;
        }
    }
}
