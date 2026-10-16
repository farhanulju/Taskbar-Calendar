using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TaskbarCalendar
{
    /// <summary>
    /// The download is a single EXE. On first run it installs itself for the current user (no admin rights):
    /// copies itself to %LOCALAPPDATA%\Programs\TaskbarCalendar, adds a Start menu shortcut and an entry under
    /// Settings > Apps > Installed apps (with Uninstall). That gives the updater a stable place to write to, wherever
    /// the download was saved.
    /// </summary>
    static class Installer
    {
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TaskbarCalendar";
        const string ShortcutName = "Taskbar Calendar.lnk";
        static readonly string StartMenuDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        static readonly string StartupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);

        static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\');

        public static bool IsInstalledCopy =>
            string.Equals(Normalize(Paths.CurrentExe), Normalize(Paths.InstalledExe), StringComparison.OrdinalIgnoreCase);

        /// <summary>winget installs portable apps itself and updates them; leave that to winget.</summary>
        public static bool IsWingetManaged =>
            Paths.CurrentExe.IndexOf(@"\Microsoft\WinGet\", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Development builds (run from bin\) and portable copies (a "portable" file next to the EXE) stay
        /// where they are.</summary>
        public static bool IsPortable
        {
            get
            {
                var dir = Path.GetDirectoryName(Paths.CurrentExe) ?? "";
                return File.Exists(Path.Combine(dir, "portable")) ||
                       dir.IndexOf(@"\bin\Debug", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       dir.IndexOf(@"\bin\Release", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        public static bool ShouldInstall => !IsInstalledCopy && !IsWingetManaged && !IsPortable;

        /// <summary>Copy this EXE into place, register it and start the installed copy. Returns true when the caller
        /// should exit.</summary>
        public static bool InstallAndRelaunch(string[] args)
        {
            try
            {
                Directory.CreateDirectory(Paths.InstallDir);
                if (InstalledVersion() is Version installed && installed > Paths.Version)
                {
                    Log.Info($"A newer version ({installed}) is already installed; starting that instead");
                }
                else
                {
                    StopOtherInstances();
                    File.Copy(Paths.CurrentExe, Paths.InstalledExe, true);
                    Register();
                }
                Process.Start(new ProcessStartInfo(Paths.InstalledExe, string.Join(" ", args.Select(a => $"\"{a}\"")))
                {
                    UseShellExecute = false,
                    WorkingDirectory = Paths.InstallDir,
                });
                Log.Info($"Installed {Paths.VersionText} to {Paths.InstallDir}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Install failed; running from the download location instead", ex);
                return false;
            }
        }

        static Version InstalledVersion()
        {
            try
            {
                return File.Exists(Paths.InstalledExe) ? System.Reflection.AssemblyName.GetAssemblyName(Paths.InstalledExe).Version : null;
            }
            catch
            {
                return null;
            }
        }

        static void StopOtherInstances()
        {
            var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName("TaskbarCalendar").Where(p => p.Id != me.Id))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
        }

        /// <summary>Start menu shortcut and the Installed apps entry. Refreshed on every start, so the shown version
        /// stays current after updates.</summary>
        public static void Register()
        {
            CreateShortcut(Path.Combine(StartMenuDir, ShortcutName), Paths.InstalledExe);
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", "Taskbar Calendar");
                key.SetValue("DisplayVersion", Paths.VersionText);
                key.SetValue("Publisher", "farhanulju");
                key.SetValue("DisplayIcon", Paths.InstalledExe);
                key.SetValue("InstallLocation", Paths.InstallDir);
                key.SetValue("UninstallString", $"\"{Paths.InstalledExe}\" --uninstall");
                key.SetValue("URLInfoAbout", Paths.RepoUrl);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)(new FileInfo(Paths.InstalledExe).Length / 1024), RegistryValueKind.DWord);
            }
        }

        public static bool StartupEnabled => File.Exists(Path.Combine(StartupDir, ShortcutName));

        public static void SetStartup(bool enabled)
        {
            var path = Path.Combine(StartupDir, ShortcutName);
            if (enabled) CreateShortcut(path, Paths.CurrentExe);
            else if (File.Exists(path)) File.Delete(path);
        }

        /// <summary>Keep the startup shortcut pointing at the running copy (it may have moved into the install folder).</summary>
        public static void RepairStartupShortcut()
        {
            if (StartupEnabled) SetStartup(true);
        }

        static void CreateShortcut(string path, string target)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = target;
            link.Arguments = "";   // an existing shortcut (e.g. from the Python version) may carry old arguments
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.Description = "Next meeting, Claude usage and network speed on the taskbar";
            link.IconLocation = target + ",0";
            link.Save();
        }

        /// <summary>`TaskbarCalendar.exe --uninstall` (from Installed apps): stop it, remove shortcuts, the Installed
        /// apps entry and its Claude Code status line, then delete the program folder once this process has exited.</summary>
        public static void Uninstall()
        {
            var proceed = MessageBox.Show(
                "Uninstall Taskbar Calendar?\n\nYour settings will be kept, in case you install it again.",
                "Taskbar Calendar", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;
            if (!proceed) return;

            StopOtherInstances();
            foreach (var dir in new[] { StartMenuDir, StartupDir })
            {
                try { File.Delete(Path.Combine(dir, ShortcutName)); } catch { }
            }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            try { ClaudeUsage.RemoveStatusLine(); } catch { }
            Log.Info("Uninstalled");

            if (Directory.Exists(Paths.InstallDir))
            {
                // A running EXE can't delete itself: let a short-lived cmd do it after we exit.
                Process.Start(new ProcessStartInfo("cmd.exe",
                    $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{Paths.InstallDir}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
            }
            MessageBox.Show("Taskbar Calendar was uninstalled.", "Taskbar Calendar", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
