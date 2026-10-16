using System.IO;

namespace TaskbarCalendar.Tests
{
    /// <summary>Keeps tests out of the real log file.</summary>
    static class TestLog
    {
        static TestLog() => Log.FilePath = Path.Combine(Path.GetTempPath(), "taskbar-calendar-tests.log");
        public static void Use() { }
    }
}
