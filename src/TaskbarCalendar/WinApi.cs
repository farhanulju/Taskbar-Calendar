using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Forms;

namespace TaskbarCalendar
{
    /// <summary>Win32 plumbing. Windows 11 doesn't let apps add items to the taskbar, so the widget is a borderless,
    /// always-on-top window over empty taskbar space; this is what makes it behave like part of the taskbar.</summary>
    static class WinApi
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

        [StructLayout(LayoutKind.Sequential)]
        struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage, uEdge; public RECT rc; public IntPtr lParam; }

        public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);
        [DllImport("shell32.dll")] static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
        public const uint WM_APP = 0x8000, WM_APPBAR_CALLBACK = WM_APP + 1, WM_OPEN_SETTINGS = WM_APP + 2;
        public const uint WM_SETTINGCHANGE = 0x1A, WM_TIMECHANGE = 0x1E, WM_DISPLAYCHANGE = 0x7E, WM_POWERBROADCAST = 0x218,
                          WM_DWMCOLORIZATIONCOLORCHANGED = 0x320, PBT_APMRESUMEAUTOMATIC = 0x12;
        public const uint EVENT_SYSTEM_FOREGROUND = 0x3, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003,
                          EVENT_OBJECT_LOCATIONCHANGE = 0x800B, EVENT_OBJECT_PARENTCHANGE = 0x800F;
        public const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
        const uint ABM_NEW = 0, ABM_REMOVE = 1;
        public const int ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;
        public const string EventsWindowTitle = "TaskbarCalendarEvents";

        public static RECT RectOf(IntPtr hwnd) { GetWindowRect(hwnd, out var r); return r; }

        public static string ClassOf(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public class TaskbarInfo
        {
            public RECT Bounds;
            public int Limit;              // x where the widget's right edge should stop
            public IntPtr Hwnd;
            public List<IntPtr> Neighbours = new List<IntPtr>();
        }

        /// <summary>The taskbar, and where the widget's right edge should stop: left of the tray and of anything
        /// embedded in the taskbar's right half (e.g. TrafficMonitor).</summary>
        public static TaskbarInfo Taskbar()
        {
            var tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return null;
            var info = new TaskbarInfo { Hwnd = tray, Bounds = RectOf(tray) };
            info.Limit = info.Bounds.Right;
            foreach (var cls in new[] { "TrayNotifyWnd", "#32770" })
            {
                for (var child = FindWindowEx(tray, IntPtr.Zero, cls, null); child != IntPtr.Zero; child = FindWindowEx(tray, child, cls, null))
                {
                    var r = RectOf(child);
                    info.Neighbours.Add(child);
                    if (r.Right > r.Left && r.Top >= info.Bounds.Top - 1 && r.Left > info.Bounds.Left + info.Bounds.Width / 2)
                        info.Limit = Math.Min(info.Limit, r.Left);
                }
            }
            return info;
        }

        public static bool ForegroundIsFullscreen()
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            var cls = ClassOf(fg);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return false;
            var r = RectOf(fg);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            GetMonitorInfo(MonitorFromWindow(fg, 2), ref mi);
            var m = mi.rcMonitor;
            return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
        }

        public static Color? ScreenPixel(int x, int y)
        {
            var dc = GetDC(IntPtr.Zero);
            try
            {
                var c = GetPixel(dc, x, y);
                if (c == 0xFFFFFFFF) return null;
                return Color.FromArgb((int)(c & 0xFF), (int)((c >> 8) & 0xFF), (int)((c >> 16) & 0xFF));
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, dc);
            }
        }

        /// <summary>Rounded corners and no window border (Windows 11).</summary>
        public static void RoundCorners(IntPtr hwnd)
        {
            int corner = 3;           // DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUNDSMALL
            DwmSetWindowAttribute(hwnd, 33, ref corner, 4);
            int noBorder = unchecked((int)0xFFFFFFFE);   // DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE
            DwmSetWindowAttribute(hwnd, 34, ref noBorder, 4);
        }

        public static void RaiseTopmost(IntPtr hwnd) =>
            SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // NOSIZE | NOMOVE | NOACTIVATE

        /// <summary>(left, right) of the taskbar's Widgets (weather) button, or null. It's XAML content inside the
        /// taskbar, not a window, so this uses UI Automation. ~10-20 ms; call off the UI thread.</summary>
        public static (int Left, int Right)? WeatherWidgetSpan()
        {
            var tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return null;
            var element = AutomationElement.FromHandle(tray)?.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "WidgetsButton"));
            if (element == null) return null;
            var r = element.Current.BoundingRectangle;
            return r.IsEmpty || r.Width <= 0 ? null : ((int)r.Left, (int)r.Right);
        }

        public static void AskRunningInstanceToOpenSettings()
        {
            var hwnd = FindWindow(null, EventsWindowTitle);
            if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_OPEN_SETTINGS, IntPtr.Zero, IntPtr.Zero);
        }

        public static void RegisterAppBar(IntPtr hwnd, bool register)
        {
            var abd = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)), hWnd = hwnd, uCallbackMessage = WM_APPBAR_CALLBACK };
            SHAppBarMessage(ABM_REMOVE, ref abd);
            if (register) SHAppBarMessage(ABM_NEW, ref abd);
        }
    }

    /// <summary>Turns Windows notifications into events for the widget, so nothing has to poll. Runs on the UI
    /// thread (hooks and messages arrive through the message loop).</summary>
    sealed class ShellWatcher : NativeWindow, IDisposable
    {
        public event Action<string> Event;

        readonly uint msgTaskbarCreated = WinApi.RegisterWindowMessage("TaskbarCreated");
        readonly WinApi.WinEventProc proc;   // kept alive for as long as the hooks exist
        readonly List<IntPtr> globalHooks = new List<IntPtr>();
        readonly Dictionary<uint, IntPtr> locationHooks = new Dictionary<uint, IntPtr>();
        HashSet<IntPtr> watched = new HashSet<IntPtr>();
        IntPtr tray;

        public ShellWatcher()
        {
            proc = OnWinEvent;
            // Hidden top-level window: receives display/theme/time/power broadcasts and AppBar notifications.
            CreateHandle(new CreateParams { Caption = WinApi.EventsWindowTitle, Style = unchecked((int)0x80000000), ExStyle = WinApi.WS_EX_TOOLWINDOW });
            WinApi.RegisterAppBar(Handle, true);
            const uint flags = WinApi.WINEVENT_OUTOFCONTEXT | WinApi.WINEVENT_SKIPOWNPROCESS;
            globalHooks.Add(WinApi.SetWinEventHook(WinApi.EVENT_SYSTEM_FOREGROUND, WinApi.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, proc, 0, 0, flags));
            globalHooks.Add(WinApi.SetWinEventHook(WinApi.EVENT_OBJECT_SHOW, WinApi.EVENT_OBJECT_HIDE, IntPtr.Zero, proc, 0, 0, flags));
            globalHooks.Add(WinApi.SetWinEventHook(WinApi.EVENT_OBJECT_PARENTCHANGE, WinApi.EVENT_OBJECT_PARENTCHANGE, IntPtr.Zero, proc, 0, 0, flags));
        }

        public void ReRegisterAppBar() => WinApi.RegisterAppBar(Handle, true);

        /// <summary>Track movement of the taskbar pieces we sit next to; location hooks are scoped to their processes.</summary>
        public void Watch(IntPtr trayHwnd, List<IntPtr> neighbours)
        {
            tray = trayHwnd;
            watched = new HashSet<IntPtr>(neighbours);
            var pids = new HashSet<uint>();
            foreach (var h in neighbours) { WinApi.GetWindowThreadProcessId(h, out var pid); pids.Add(pid); }
            foreach (var pid in new List<uint>(locationHooks.Keys))
                if (!pids.Contains(pid)) { WinApi.UnhookWinEvent(locationHooks[pid]); locationHooks.Remove(pid); }
            foreach (var pid in pids)
                if (!locationHooks.ContainsKey(pid))
                    locationHooks[pid] = WinApi.SetWinEventHook(WinApi.EVENT_OBJECT_LOCATIONCHANGE, WinApi.EVENT_OBJECT_LOCATIONCHANGE,
                                                                IntPtr.Zero, proc, pid, 0, WinApi.WINEVENT_OUTOFCONTEXT);
        }

        void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (evt == WinApi.EVENT_SYSTEM_FOREGROUND) { Raise("foreground"); return; }   // includes the taskbar being clicked
            if (idObject != 0 || hwnd == IntPtr.Zero) return;
            if (evt == WinApi.EVENT_OBJECT_LOCATIONCHANGE) { if (watched.Contains(hwnd)) Raise("layout"); return; }
            if (watched.Contains(hwnd) || (tray != IntPtr.Zero && WinApi.GetParent(hwnd) == tray)) Raise("layout");
        }

        protected override void WndProc(ref Message m)
        {
            var msg = (uint)m.Msg;
            if (msg == WinApi.WM_APPBAR_CALLBACK)
            {
                var code = m.WParam.ToInt32();
                if (code == WinApi.ABN_FULLSCREENAPP) Raise("fullscreen");
                else if (code == WinApi.ABN_POSCHANGED) Raise("layout");
                m.Result = IntPtr.Zero;
                return;
            }
            if (msg == WinApi.WM_OPEN_SETTINGS) { Raise("open-settings"); return; }
            if (msg == msgTaskbarCreated) Raise("taskbar-restarted");
            else if (msg == WinApi.WM_DISPLAYCHANGE || msg == WinApi.WM_SETTINGCHANGE || msg == WinApi.WM_DWMCOLORIZATIONCOLORCHANGED) Raise("display");
            else if (msg == WinApi.WM_TIMECHANGE) Raise("time");
            else if (msg == WinApi.WM_POWERBROADCAST && m.WParam.ToInt32() == WinApi.PBT_APMRESUMEAUTOMATIC) Raise("resume");
            base.WndProc(ref m);
        }

        void Raise(string name) => Event?.Invoke(name);

        public void Dispose()
        {
            foreach (var h in globalHooks) WinApi.UnhookWinEvent(h);
            foreach (var h in locationHooks.Values) WinApi.UnhookWinEvent(h);
            WinApi.RegisterAppBar(Handle, false);
            DestroyHandle();
        }
    }
}
