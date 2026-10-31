using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace ClipRecorder
{
    // Creates the Desktop and Startup-folder shortcuts that launch the app without any
    // console window (PowerShell started hidden, shortcut set to "minimized").
    internal static class Shortcuts
    {
        private const string LinkName = "ClipRecorder.lnk";

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr fd, uint flags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCmd);
            void SetShowCmd(int showCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int icon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr hwnd, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }

        public static string PowerShellPath
        {
            get { return Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"); }
        }

        public static string LaunchArguments(string baseDir, bool autoStart)
        {
            return "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" +
                   Path.Combine(baseDir, "ClipRecorder.ps1") + "\"" + (autoStart ? " -AutoStart" : "");
        }

        public static string DesktopLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), LinkName); }
        }

        public static string StartupLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), LinkName); }
        }

        public static void Create(string linkPath, string baseDir, bool autoStart)
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                link.SetPath(PowerShellPath);
                link.SetArguments(LaunchArguments(baseDir, autoStart));
                link.SetWorkingDirectory(baseDir);
                link.SetDescription("ClipRecorder - grabador de pantalla");
                link.SetIconLocation(Path.Combine(baseDir, "ClipRecorder.ico"), 0);
                link.SetShowCmd(7); // SW_SHOWMINNOACTIVE: no window flashes while starting
                ((IPersistFile)link).Save(linkPath, true);
            }
            finally
            {
                Marshal.ReleaseComObject(link);
            }
        }

        public static void SetAutoStart(string baseDir, bool enabled)
        {
            if (enabled) Create(StartupLink, baseDir, true);
            else if (File.Exists(StartupLink)) File.Delete(StartupLink);
        }

        public static bool IsAutoStartEnabled()
        {
            return File.Exists(StartupLink);
        }
    }
}
