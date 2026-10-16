using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace TaskbarCalendar
{
    /// <summary>Opening the calendar in the right Chrome profile.</summary>
    static class Chrome
    {
        static readonly string UserData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data");

        public static string FindChrome()
        {
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using (var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe"))
                    if (key?.GetValue(null) is string path && File.Exists(path)) return path;
            }
            foreach (var candidate in new[]
            {
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Google\Chrome\Application\chrome.exe"),
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe"),
                Environment.ExpandEnvironmentVariables(@"%LocalAppData%\Google\Chrome\Application\chrome.exe"),
            })
                if (File.Exists(candidate)) return candidate;
            return null;
        }

        /// <summary>(profile folder, display name, signed-in email) from Chrome's Local State, in Chrome's order.</summary>
        public static List<(string Folder, string Name, string Email)> ListProfiles()
        {
            try
            {
                var state = Json.ParseObject(FileUtil.ReadText(Path.Combine(UserData, "Local State")));
                var profile = Json.Obj(state, "profile");
                var cache = Json.Obj(profile, "info_cache") ?? new Dictionary<string, object>();
                var order = Json.List(profile, "profiles_order").OfType<string>().ToList();
                if (order.Count == 0) order = cache.Keys.OrderBy(k => k).ToList();
                return order.Where(cache.ContainsKey).Select(folder =>
                {
                    var p = cache[folder] as Dictionary<string, object>;
                    return (folder, Json.Str(p, "name") ?? folder, Json.Str(p, "user_name") ?? "");
                }).ToList();
            }
            catch (Exception)
            {
                return new List<(string, string, string)>();
            }
        }

        /// <summary>Open in Chrome with the given profile folder, or in the default browser when none is set.</summary>
        public static void OpenUrl(string url, string profile = "")
        {
            var chrome = string.IsNullOrEmpty(profile) ? null : FindChrome();
            if (chrome == null)
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(chrome, $"--profile-directory=\"{profile}\" \"{url}\"") { UseShellExecute = false });
        }
    }
}
