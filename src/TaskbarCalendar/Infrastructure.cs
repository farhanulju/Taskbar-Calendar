using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace TaskbarCalendar
{
    /// <summary>Where things live. Same locations as the Python version, so settings carry over.</summary>
    static class Paths
    {
        public const string AppName = "TaskbarCalendar";
        public const string RepoUrl = "https://github.com/farhanulju/Taskbar-Calendar";
        public const string Repo = "farhanulju/Taskbar-Calendar";

        public static readonly string ConfigDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
        public static readonly string DataDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
        public static readonly string ConfigFile = Path.Combine(ConfigDir, "config.json");
        public static readonly string LogFile = Path.Combine(DataDir, "taskbar_calendar.log");
        public static readonly string UsageFile = Path.Combine(DataDir, "claude_usage.json");
        public static readonly string InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
        public static readonly string InstalledExe = Path.Combine(InstallDir, "TaskbarCalendar.exe");

        public static string CurrentExe => Assembly.GetEntryAssembly()?.Location ?? Path.Combine(AppContext.BaseDirectory, "TaskbarCalendar.exe");

        public static Version Version => Assembly.GetExecutingAssembly().GetName().Version;
        public static string VersionText => $"{Version.Major}.{Version.Minor}.{Version.Build}";
    }

    /// <summary>Small rotating log file (about 2 MB at most).</summary>
    static class Log
    {
        static readonly object Lock = new object();
        internal static string FilePath = Paths.LogFile;   // tests point this elsewhere

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARNING", message);
        public static void Error(string message, Exception ex = null) => Write("ERROR", ex == null ? message : $"{message}\n{ex}");

        static void Write(string level, string message)
        {
            try
            {
                lock (Lock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    var file = new FileInfo(FilePath);
                    if (file.Exists && file.Length > 1_000_000)
                    {
                        File.Copy(file.FullName, file.FullName + ".1", true);
                        File.WriteAllText(file.FullName, "");
                    }
                    File.AppendAllText(FilePath,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss,fff} {level} {message}{Environment.NewLine}", Encoding.UTF8);
                }
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }

    /// <summary>JSON via the serializer built into .NET Framework, so there's no extra dependency.</summary>
    static class Json
    {
        static JavaScriptSerializer Serializer() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };

        public static object Parse(string text) => Serializer().DeserializeObject(text);

        public static Dictionary<string, object> ParseObject(string text) => Parse(text) as Dictionary<string, object>;

        public static string Serialize(object value, bool indent = false)
        {
            var json = Serializer().Serialize(value);
            return indent ? Indent(json) : json;
        }

        static string Indent(string json)
        {
            var sb = new StringBuilder();
            int depth = 0;
            bool inString = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\')) inString = !inString;
                if (inString) { sb.Append(c); continue; }
                switch (c)
                {
                    case '{': case '[':
                        sb.Append(c);
                        if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']')) { sb.Append(json[++i]); break; }
                        sb.Append('\n').Append(' ', 2 * ++depth);
                        break;
                    case '}': case ']':
                        sb.Append('\n').Append(' ', 2 * --depth).Append(c);
                        break;
                    case ',':
                        sb.Append(",\n").Append(' ', 2 * depth);
                        break;
                    case ':':
                        sb.Append(": ");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        // Helpers for reading loosely typed JSON.
        public static Dictionary<string, object> Obj(object o, string key) =>
            (o as Dictionary<string, object>) != null && ((Dictionary<string, object>)o).TryGetValue(key, out var v)
                ? v as Dictionary<string, object> : null;

        public static object Get(Dictionary<string, object> o, string key) =>
            o != null && o.TryGetValue(key, out var v) ? v : null;

        public static string Str(Dictionary<string, object> o, string key) => Get(o, key) as string;

        public static double? Num(Dictionary<string, object> o, string key)
        {
            var v = Get(o, key);
            if (v == null || v is string || v is bool) return null;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return null; }
        }

        public static bool? Bool(Dictionary<string, object> o, string key) => Get(o, key) as bool?;

        public static IEnumerable<object> List(Dictionary<string, object> o, string key) =>
            Get(o, key) as IEnumerable<object> ?? Array.Empty<object>();
    }

    static class FileUtil
    {
        /// <summary>Write through a uniquely named temp file and an atomic replace, so readers never see half a
        /// file and two writers can't trip over each other. Windows refuses to replace a file someone has open,
        /// so retry briefly.</summary>
        public static void AtomicWrite(string path, string text, int attempts = 40)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = Path.Combine(Path.GetDirectoryName(path),
                $"{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(tmp, path, null, true);
                    else File.Move(tmp, path);
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < attempts - 1)
                {
                    Thread.Sleep(50);
                }
                catch
                {
                    try { File.Delete(tmp); } catch { }
                    throw;
                }
            }
        }

        public static string ReadText(string path)
        {
            // Allow others to write/replace the file while we read it.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs, Encoding.UTF8))
                return reader.ReadToEnd();
        }
    }

    /// <summary>Unix time in seconds, the format the usage file uses (shared with the Python version).</summary>
    static class Epoch
    {
        static readonly DateTime Zero = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public static double Now => (DateTime.UtcNow - Zero).TotalSeconds;
        public static DateTime ToLocal(double epoch) => Zero.AddSeconds(epoch).ToLocalTime();
        public static double From(DateTime t) => (t.ToUniversalTime() - Zero).TotalSeconds;
    }
}
