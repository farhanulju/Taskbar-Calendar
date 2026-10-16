using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using Xunit;

namespace TaskbarCalendar.Tests
{
    public class UsageMathTests
    {
        [Fact]
        public void Countdown() => Assert.Equal(
            new[] { "0m", "0m", "1m", "59m", "1h 01m", "23h 59m", "1d 1h", "6d 23h" },
            new double[] { -5, 59, 61, 3599, 3660, 86399, 90000, 6 * 86400 + 23 * 3600 }.Select(ClaudeUsage.FmtCountdown));

        const double FiveHours = 5 * 3600;

        [Fact]
        public void UnderPaceNeverRunsOut()
        {
            var (elapsed, runsOut) = ClaudeUsage.Pace(20, 2.5 * 3600, FiveHours, 0);   // 20% used, halfway through
            Assert.Equal(0.5, elapsed, 6);
            Assert.Null(runsOut);
        }

        [Fact]
        public void OverPacePredictsWhenTheLimitIsHit()
        {
            var (elapsed, runsOut) = ClaudeUsage.Pace(60, 4 * 3600, FiveHours, 0);    // 60% after 1 h: 40% left lasts 40 min
            Assert.Equal(0.2, elapsed, 6);
            Assert.Equal(40 * 60, runsOut.Value, 3);
        }

        [Fact]
        public void TooEarlyToTell() => Assert.Null(ClaudeUsage.Pace(30, FiveHours - 60, FiveHours, 0).RunsOut);
    }

    public class ProbeOutputTests
    {
        const string Stream =
            "{\"type\":\"system\",\"subtype\":\"init\"}\n" +
            "not json\n" +
            "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"unifiedWindows\":{" +
            "\"five_hour\":{\"utilization\":0.17,\"resetsAt\":1792002000},\"seven_day\":{\"utilization\":0.53,\"resetsAt\":1792162800}," +
            "\"other\":{\"utilization\":0.9}}}}\n" +
            "{\"type\":\"result\",\"usage\":{\"input_tokens\":2,\"output_tokens\":4,\"cache_creation_input_tokens\":955,\"cache_read_input_tokens\":0}}\n";

        [Fact]
        public void ParsesBothWindowsAsPercentages()
        {
            var w = ClaudeUsage.ParseProbeOutput(Stream);
            Assert.Equal(17.0, w["five_hour"].UsedPercentage, 6);
            Assert.Equal(1792162800, w["seven_day"].ResetsAt);
            Assert.Equal(2, w.Count);
        }

        [Fact] public void CountsTokens() => Assert.Equal(961, ClaudeUsage.ProbeTokens(Stream));
        [Fact] public void MissingEvent() => Assert.Null(ClaudeUsage.ParseProbeOutput("{\"type\":\"result\"}"));
    }

    [Collection("ClaudeUsage statics")]
    public class FreeUsageServiceTests : IDisposable
    {
        const string Body = "{\"five_hour\":{\"utilization\":27.0,\"resets_at\":\"2026-10-16T14:19:59.649435+00:00\"}," +
                            "\"seven_day\":{\"utilization\":93.0,\"resets_at\":\"2026-10-16T14:59:59+00:00\"},\"seven_day_opus\":null}";
        readonly string dir = Path.Combine(Path.GetTempPath(), "tbc-tests-" + Guid.NewGuid().ToString("N"));
        readonly string originalCreds = ClaudeUsage.Credentials;
        readonly Func<HttpRequestMessage, HttpResponseMessage> originalSend = ClaudeUsage.Send;
        readonly Func<string, bool> originalRenew = ClaudeUsage.Renew;
        readonly List<HttpRequestMessage> requests = new List<HttpRequestMessage>();

        public FreeUsageServiceTests()
        {
            TestLog.Use();
            Directory.CreateDirectory(dir);
            ClaudeUsage.Credentials = Path.Combine(dir, ".credentials.json");
        }

        public void Dispose()
        {
            ClaudeUsage.Credentials = originalCreds;
            ClaudeUsage.Send = originalSend;
            ClaudeUsage.Renew = originalRenew;
            Directory.Delete(dir, true);
        }

        void SignIn(string token = "tok-1", double expiresIn = 3600) =>
            File.WriteAllText(ClaudeUsage.Credentials,
                $"{{\"claudeAiOauth\":{{\"accessToken\":\"{token}\",\"expiresAt\":{(long)((Epoch.Now + expiresIn) * 1000)}}}}}");

        void Respond(HttpStatusCode status, string body = "{}", int? retryAfter = null) => ClaudeUsage.Send = req =>
        {
            requests.Add(req);
            var resp = new HttpResponseMessage(status) { Content = new StringContent(body) };
            if (retryAfter != null) resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfter.Value));
            return resp;
        };

        static string Auth(HttpRequestMessage r) => r.Headers.GetValues("Authorization").Single();

        [Fact]
        public void ParsesPercentagesAndIsoResetTimes()
        {
            var w = ClaudeUsage.ParseUsageResponse(Json.ParseObject(Body));
            Assert.Equal(27.0, w["five_hour"].UsedPercentage);
            Assert.Equal(1792162799, w["seven_day"].ResetsAt.Value, 3);
            Assert.Equal(2, w.Count);
        }

        [Fact]
        public void SendsTheTokenOnlyToAnthropic()
        {
            SignIn();
            Respond(HttpStatusCode.OK, Body);
            ClaudeUsage.FetchUsage(null);
            Assert.Equal("api.anthropic.com", requests.Single().RequestUri.Host);
            Assert.Equal("https", requests.Single().RequestUri.Scheme);
            Assert.Equal("Bearer tok-1", Auth(requests.Single()));
        }

        [Fact]
        public void ExpiredSignInIsRenewedBeforeTheRequest()
        {
            SignIn(expiresIn: -60);
            var renewed = 0;
            ClaudeUsage.Renew = exe => { renewed++; SignIn("tok-2"); return true; };
            Respond(HttpStatusCode.OK, Body);
            ClaudeUsage.FetchUsage("claude.exe");
            Assert.Equal(1, renewed);
            Assert.Equal("Bearer tok-2", Auth(requests.Single()));
        }

        [Fact]
        public void RejectedTokenIsRenewedOnceThenReported()
        {
            SignIn();
            var renewed = 0;
            ClaudeUsage.Renew = exe => { renewed++; return false; };
            Respond(HttpStatusCode.Unauthorized);
            Assert.Throws<NotSignedInException>(() => ClaudeUsage.FetchUsage("claude.exe"));
            Assert.Equal(1, renewed);
            Assert.Equal(2, requests.Count);
        }

        [Fact]
        public void RateLimitReportsWhenToRetry()
        {
            SignIn();
            Respond((HttpStatusCode)429, retryAfter: 120);
            Assert.Equal(120, Assert.Throws<RateLimitedException>(() => ClaudeUsage.FetchUsage(null)).RetryAfter);
        }

        [Fact]
        public void NotSignedIn() => Assert.Throws<NotSignedInException>(() => ClaudeUsage.FetchUsage(null));
    }

    [Collection("ClaudeUsage statics")]
    public class StatusLineSettingsTests : IDisposable
    {
        readonly string dir = Path.Combine(Path.GetTempPath(), "tbc-tests-" + Guid.NewGuid().ToString("N"));
        readonly string original = ClaudeUsage.ClaudeSettings;

        public StatusLineSettingsTests()
        {
            Directory.CreateDirectory(dir);
            ClaudeUsage.ClaudeSettings = Path.Combine(dir, "settings.json");
        }

        public void Dispose()
        {
            ClaudeUsage.ClaudeSettings = original;
            Directory.Delete(dir, true);
        }

        [Fact]
        public void InstallKeepsOtherSettingsAndRemoveUndoesIt()
        {
            File.WriteAllText(ClaudeUsage.ClaudeSettings, "{\"model\":\"opus\"}");
            Assert.Equal("none", ClaudeUsage.StatusLineState());
            ClaudeUsage.InstallStatusLine(@"C:\Users\x\AppData\Local\Programs\TaskbarCalendar\TaskbarCalendar.exe");
            Assert.Equal("ours", ClaudeUsage.StatusLineState());
            ClaudeUsage.RemoveStatusLine();
            var settings = Json.ParseObject(File.ReadAllText(ClaudeUsage.ClaudeSettings));
            Assert.Equal(new[] { "model" }, settings.Keys);
        }

        [Fact]
        public void TheOldPythonStatusLineCountsAsOurs()
        {
            File.WriteAllText(ClaudeUsage.ClaudeSettings,
                "{\"statusLine\":{\"type\":\"command\",\"command\":\"\\\"C:/x/python.exe\\\" \\\"C:/x/taskbar_calendar/statusline.py\\\"\"}}");
            Assert.Equal("ours", ClaudeUsage.StatusLineState());
        }

        [Fact]
        public void SomeoneElsesStatusLineIsLeftAlone()
        {
            File.WriteAllText(ClaudeUsage.ClaudeSettings, "{\"statusLine\":{\"type\":\"command\",\"command\":\"my-prompt.sh\"}}");
            Assert.Equal("other", ClaudeUsage.StatusLineState());
            Assert.Throws<InvalidOperationException>(() => ClaudeUsage.InstallStatusLine(@"C:\x\TaskbarCalendar.exe"));
            ClaudeUsage.RemoveStatusLine();
            Assert.Equal("other", ClaudeUsage.StatusLineState());
        }
    }
}
