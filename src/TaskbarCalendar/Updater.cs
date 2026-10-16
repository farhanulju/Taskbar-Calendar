using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace TaskbarCalendar
{
    class Release
    {
        public Version Version;
        public string Tag;
        public string ExeUrl;
        public string ChecksumUrl;
        public string PageUrl;
    }

    /// <summary>
    /// Updates from GitHub Releases: checks the latest release, downloads TaskbarCalendar.exe, verifies it against
    /// the SHA-256 published with the release, and swaps it in. Windows lets a running EXE be renamed (not deleted),
    /// so the old one is moved aside, the new one put in its place, and the app restarts.
    /// </summary>
    static class Updater
    {
        const string ExeAsset = "TaskbarCalendar.exe";
        const string ChecksumAsset = "TaskbarCalendar.exe.sha256";
        static readonly HttpClient Http = CreateClient();

        static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("taskbar-calendar/" + Paths.VersionText);   // GitHub requires one
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        // Overridable only for testing the update flow end to end against a local server.
        static string ReleasesUrl => Environment.GetEnvironmentVariable("TASKBAR_CALENDAR_RELEASES_URL")
                                     ?? $"https://api.github.com/repos/{Paths.Repo}/releases/latest";

        /// <summary>Self-updating only makes sense for the installed copy; winget-managed copies update via winget.</summary>
        public static bool CanSelfUpdate => Installer.IsInstalledCopy && !Installer.IsWingetManaged;

        public static Version ParseVersion(string tag)
        {
            var m = Regex.Match(tag ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?");
            if (!m.Success) return null;
            return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                               m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        }

        static Version Current => new Version(Paths.Version.Major, Paths.Version.Minor, Paths.Version.Build);

        public static Release ParseRelease(Dictionary<string, object> json)
        {
            if (json == null || Json.Bool(json, "draft") == true || Json.Bool(json, "prerelease") == true) return null;
            var tag = Json.Str(json, "tag_name");
            var release = new Release { Tag = tag, Version = ParseVersion(tag), PageUrl = Json.Str(json, "html_url") };
            foreach (var asset in Json.List(json, "assets").OfType<Dictionary<string, object>>())
            {
                var name = Json.Str(asset, "name");
                var url = Json.Str(asset, "browser_download_url");
                if (string.Equals(name, ExeAsset, StringComparison.OrdinalIgnoreCase)) release.ExeUrl = url;
                if (string.Equals(name, ChecksumAsset, StringComparison.OrdinalIgnoreCase)) release.ChecksumUrl = url;
            }
            return release.Version != null && release.ExeUrl != null && release.ChecksumUrl != null ? release : null;
        }

        /// <summary>The latest release if it's newer than this build, else null. Blocking.</summary>
        public static Release CheckForUpdate()
        {
            string text;
            using (var response = Http.GetAsync(ReleasesUrl).GetAwaiter().GetResult())
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;   // no release published yet
                response.EnsureSuccessStatusCode();
                text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            var release = ParseRelease(Json.ParseObject(text));
            if (release == null || release.Version <= Current) return null;
            Log.Info($"Update available: {release.Tag}");
            return release;
        }

        public static string ExpectedHash(string checksumFile) =>
            Regex.Match(checksumFile ?? "", @"\b[0-9a-fA-F]{64}\b").Value.ToLowerInvariant();

        public static string Sha256Of(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Download and verify the release next to the running EXE (as .new). Blocking.</summary>
        public static string Download(Release release)
        {
            var expected = ExpectedHash(Http.GetStringAsync(release.ChecksumUrl).GetAwaiter().GetResult());
            if (expected.Length != 64) throw new InvalidDataException("The release's checksum file is malformed.");
            var target = Paths.CurrentExe + ".new";
            using (var response = Http.GetAsync(release.ExeUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                using (var input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (var output = File.Create(target))
                    input.CopyTo(output);
            }
            var actual = Sha256Of(target);
            if (actual != expected)
            {
                File.Delete(target);
                throw new InvalidDataException($"Downloaded update failed verification (SHA-256 {actual}, expected {expected}).");
            }
            Log.Info($"Downloaded and verified {release.Tag}");
            return target;
        }

        /// <summary>Swap the downloaded EXE in. The caller then restarts.</summary>
        public static void Apply(string downloaded)
        {
            var exe = Paths.CurrentExe;
            var old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);            // allowed while running
            try
            {
                File.Move(downloaded, exe);
            }
            catch
            {
                File.Move(old, exe);        // put the working version back
                throw;
            }
        }

        /// <summary>After an update, the previous EXE is left as .old. The old process may still be exiting when the
        /// new one starts, so keep trying for a little while.</summary>
        public static void CleanUpAfterUpdate()
        {
            new System.Threading.Thread(() =>
            {
                for (int attempt = 0; attempt < 30; attempt++)
                {
                    var left = new[] { ".old", ".new" }.Select(s => Paths.CurrentExe + s).Where(File.Exists).ToList();
                    if (left.Count == 0) return;
                    foreach (var path in left) { try { File.Delete(path); } catch { } }
                    System.Threading.Thread.Sleep(2000);
                }
            }) { IsBackground = true }.Start();
        }

        public static void Restart(string args = "")
        {
            Process.Start(new ProcessStartInfo(Paths.CurrentExe, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Paths.CurrentExe) });
        }
    }
}
