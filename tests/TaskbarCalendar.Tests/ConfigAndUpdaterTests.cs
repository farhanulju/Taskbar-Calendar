using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace TaskbarCalendar.Tests
{
    public class ConfigTests : IDisposable
    {
        readonly string dir = Path.Combine(Path.GetTempPath(), "tbc-tests-" + Guid.NewGuid().ToString("N"));
        string File_ => Path.Combine(dir, "config.json");

        public ConfigTests() { TestLog.Use(); Directory.CreateDirectory(dir); }
        public void Dispose() => Directory.Delete(dir, true);

        [Fact]
        public void DefaultsWhenNothingSaved()
        {
            var cfg = Config.Load(File_);
            Assert.Empty(cfg.IcsUrls);
            Assert.True(cfg.ShowClaude && cfg.ClaudeProbe && cfg.AutoUpdate);
            Assert.Equal(210, cfg.Width);
        }

        [Fact]
        public void SaveAndLoadRoundTrip()
        {
            var cfg = new Config { IcsUrls = { "https://example.com/a.ics" }, Width = 300, ChromeProfile = "Profile 2", X = 1200, AutoUpdate = false };
            cfg.Save(File_);
            var back = Config.Load(File_);
            Assert.Equal(new[] { "https://example.com/a.ics" }, back.IcsUrls);
            Assert.Equal((300, "Profile 2", 1200, false), (back.Width, back.ChromeProfile, back.X, back.AutoUpdate));
        }

        [Fact]
        public void ReadsSettingsWrittenByThePythonVersion()
        {
            File.WriteAllText(File_, @"{
  ""ics_urls"": [""https://example.com/cal.ics""],
  ""email"": """",
  ""chrome_profile"": ""Profile 2"",
  ""calendar_view"": ""week"",
  ""refresh_minutes"": 5, ""lookahead_days"": 7,
  ""show_calendar"": true, ""show_claude"": true, ""claude_probe"": true, ""show_network"": false,
  ""width"": 210, ""right_gap"": 6, ""x"": null
}");
            var cfg = Config.Load(File_);
            Assert.Equal("Profile 2", cfg.ChromeProfile);
            Assert.Equal("week", cfg.CalendarView);
            Assert.False(cfg.ShowNetwork);
            Assert.Null(cfg.X);
            Assert.True(cfg.AutoUpdate);   // new setting, defaulted
        }

        [Fact]
        public void UpgradesTheOldestFormat()
        {
            File.WriteAllText(File_, @"{""ics_url"": ""https://example.com/old.ics"", ""calendar_url"": ""https://calendar.google.com/calendar/r/day""}");
            var cfg = Config.Load(File_);
            Assert.Equal(new[] { "https://example.com/old.ics" }, cfg.IcsUrls);
            Assert.Equal("day", cfg.CalendarView);
        }

        [Fact]
        public void InvalidJsonFallsBackToDefaults()
        {
            File.WriteAllText(File_, "{not json");
            Assert.Empty(Config.Load(File_).IcsUrls);
        }

        [Fact]
        public void IcsUrlsAreNormalised()
        {
            var cfg = new Config { IcsUrls = { " webcal://example.com/a.ics ", "", "https://example.com/b.ics" } };
            Assert.Equal(new[] { "https://example.com/a.ics", "https://example.com/b.ics" }, cfg.CalendarUrls());
        }
    }

    public class UpdaterTests
    {
        [Theory]
        [InlineData("v2.0.1", 2, 0, 1)]
        [InlineData("2.1", 2, 1, 0)]
        [InlineData("release-v10.4.7", 10, 4, 7)]
        public void ParsesVersionTags(string tag, int major, int minor, int build) =>
            Assert.Equal(new Version(major, minor, build), Updater.ParseVersion(tag));

        [Fact] public void RejectsTagsWithoutAVersion() => Assert.Null(Updater.ParseVersion("latest"));

        static Dictionary<string, object> ReleaseJson(bool draft = false, bool prerelease = false, bool withChecksum = true) =>
            Json.ParseObject(Json.Serialize(new Dictionary<string, object>
            {
                ["tag_name"] = "v2.1.0",
                ["draft"] = draft,
                ["prerelease"] = prerelease,
                ["html_url"] = "https://github.com/farhanulju/Taskbar-Calendar/releases/tag/v2.1.0",
                ["assets"] = withChecksum
                    ? new object[]
                    {
                        new Dictionary<string, object> { ["name"] = "TaskbarCalendar.exe", ["browser_download_url"] = "https://example.com/TaskbarCalendar.exe" },
                        new Dictionary<string, object> { ["name"] = "TaskbarCalendar.exe.sha256", ["browser_download_url"] = "https://example.com/TaskbarCalendar.exe.sha256" },
                    }
                    : new object[] { new Dictionary<string, object> { ["name"] = "TaskbarCalendar.exe", ["browser_download_url"] = "https://example.com/x.exe" } },
            }));

        [Fact]
        public void ParsesAReleaseWithItsAssets()
        {
            var r = Updater.ParseRelease(ReleaseJson());
            Assert.Equal(new Version(2, 1, 0), r.Version);
            Assert.EndsWith("TaskbarCalendar.exe", r.ExeUrl);
            Assert.EndsWith(".sha256", r.ChecksumUrl);
        }

        [Fact] public void IgnoresDrafts() => Assert.Null(Updater.ParseRelease(ReleaseJson(draft: true)));
        [Fact] public void IgnoresPrereleases() => Assert.Null(Updater.ParseRelease(ReleaseJson(prerelease: true)));
        [Fact] public void RequiresAChecksum() => Assert.Null(Updater.ParseRelease(ReleaseJson(withChecksum: false)));

        [Fact]
        public void ReadsChecksumFilesInCommonFormats()
        {
            var hash = new string('a', 64);
            Assert.Equal(hash, Updater.ExpectedHash($"{hash}  TaskbarCalendar.exe\n"));
            Assert.Equal(hash, Updater.ExpectedHash(hash.ToUpperInvariant()));
            Assert.Equal("", Updater.ExpectedHash("not a hash"));
        }

        [Fact]
        public void HashesFiles()
        {
            var path = Path.GetTempFileName();
            File.WriteAllText(path, "abc");
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Updater.Sha256Of(path));
            File.Delete(path);
        }
    }

    public class FileUtilTests
    {
        [Fact]
        public void AtomicWriteWaitsForAReaderToLetGo()
        {
            var dir = Path.Combine(Path.GetTempPath(), "tbc-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, "claude_usage.json");
            File.WriteAllText(target, "old");
            var reader = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);   // blocks replacing
            Task.Delay(300).ContinueWith(_ => reader.Dispose());
            FileUtil.AtomicWrite(target, "new");
            Assert.Equal("new", File.ReadAllText(target));
            Assert.Single(Directory.GetFiles(dir));   // no temp files left behind
            Directory.Delete(dir, true);
        }
    }
}
