using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace ClipRecorder
{
    // Entry point, called by ClipRecorder.ps1 after compiling these sources in memory.
    // Running inside Windows PowerShell (signed by Microsoft) means no new executable is
    // ever written to disk, so Smart App Control has nothing to block.
    public static class Program
    {
        private const string InstanceName = @"Local\ClipRecorder.Instance";
        public const string TriggerName = @"Local\ClipRecorder.Trigger";

        public static int Run(string baseDir, string libPath, bool autoStarted)
        {
            Log.Init(Path.Combine(baseDir, "cliprecorder.log"));
            Log.Write("=== Iniciando ClipRecorder" + (autoStarted ? " (inicio con Windows)" : "") + " ===");

            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                if (new AssemblyName(e.Name).Name != "ScreenRecorderLib") return null;
                var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "ScreenRecorderLib");
                return loaded ?? Assembly.LoadFrom(libPath);
            };

            Native.HideConsole();
            Log.Write("Escalado de pantalla: " + Native.EnableDpiAwareness());

            bool createdNew;
            using (var instance = new Mutex(true, InstanceName, out createdNew))
            {
                if (!createdNew)
                {
                    Log.Write("Ya estaba abierto: se le pide que empiece una captura.");
                    EventWaitHandle existing;
                    if (EventWaitHandle.TryOpenExisting(TriggerName, out existing))
                        using (existing) existing.Set();
                    return 0;
                }

                using (var trigger = new EventWaitHandle(false, EventResetMode.AutoReset, TriggerName))
                {
                    try { Application.EnableVisualStyles(); } catch { }
                    try { Application.SetCompatibleTextRenderingDefault(false); } catch { }
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                    Application.ThreadException += (s, e) =>
                    {
                        Log.Write("ERROR no controlado: " + e.Exception);
                        MessageBox.Show("ClipRecorder tuvo un error inesperado:\n\n" + e.Exception.Message + "\n\nDetalle en cliprecorder.log",
                            "ClipRecorder", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    };
                    AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("ERROR fatal: " + e.ExceptionObject);
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

                    Log.Write("Pantalla principal: " + Screen.PrimaryScreen.Bounds + " (" + Screen.AllScreens.Length + " monitor/es)");
                    using (var app = new TrayApp(baseDir, trigger, autoStarted))
                        Application.Run(app);
                }
            }
            Log.Write("=== ClipRecorder cerrado ===");
            return 0;
        }
    }
}
