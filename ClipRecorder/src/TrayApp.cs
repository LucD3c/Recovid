using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace ClipRecorder
{
    internal sealed class TrayApp : ApplicationContext
    {
        private readonly string _baseDir;
        private readonly string _settingsPath;
        private readonly EventWaitHandle _trigger;
        private readonly ManualResetEvent _quit = new ManualResetEvent(false);
        private readonly SynchronizationContext _ui;
        private Settings _settings;

        private readonly NotifyIcon _tray;
        private readonly Icon _idleIcon;
        private readonly Icon _recordingIcon;
        private readonly ToolStripMenuItem _recordItem;
        private readonly ToolStripMenuItem _autoStartItem;
        private readonly HotkeyWindow _hotkey;
        private Thread _triggerThread;

        private RecordingSession _session;
        private bool _selecting;
        private Action _balloonClick;
        private readonly ManualResetEvent _warmupDone = new ManualResetEvent(false);

        public TrayApp(string baseDir, EventWaitHandle trigger, bool autoStarted)
        {
            _baseDir = baseDir;
            _trigger = trigger;
            _ui = SynchronizationContext.Current;
            _settingsPath = Path.Combine(baseDir, "settings.json");
            _settings = Settings.Load(_settingsPath);
            string outputFolder = _settings.ResolveOutputFolder(baseDir);

            int iconSize = SystemInformation.SmallIconSize.Width;
            _idleIcon = IconFactory.CreateIcon(iconSize, false);
            _recordingIcon = IconFactory.CreateIcon(iconSize, true);

            var menu = new ContextMenuStrip();
            _recordItem = new ToolStripMenuItem("Grabar ahora", null, delegate { StartCaptureSoon(); });
            _recordItem.Font = new Font(_recordItem.Font, FontStyle.Bold);
            menu.Items.Add(_recordItem);
            menu.Items.Add("Abrir carpeta de videos", null, delegate { OpenFolder(null); });
            menu.Items.Add("Configuración...", null, delegate { OpenSettings(); });
            menu.Items.Add(new ToolStripSeparator());
            _autoStartItem = new ToolStripMenuItem("Iniciar con Windows", null, delegate { ToggleAutoStart(); });
            menu.Items.Add(_autoStartItem);
            menu.Items.Add("Ver registro (log)", null, delegate { OpenLog(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Salir", null, delegate { ExitApp(); });

            _tray = new NotifyIcon { Icon = _idleIcon, ContextMenuStrip = menu, Visible = true };
            _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) StartCaptureSoon(); };
            _tray.BalloonTipClicked += delegate { var a = _balloonClick; if (a != null) a(); };

            _hotkey = new HotkeyWindow();
            _hotkey.Pressed += delegate { StartCapture(); };

            EnsureFirstRunSetup();
            RegisterHotkey(true);
            UpdateMenu();

            _triggerThread = new Thread(WatchTrigger) { IsBackground = true, Name = "ClipRecorderTrigger" };
            _triggerThread.Start();

            Log.Write("Listo. Atajo " + _settings.Hotkey + ", videos en " + outputFolder);
            if (!autoStarted)
                ShowBalloon("ClipRecorder está listo",
                    "Presioná " + _settings.Hotkey + " para grabar.\nEl ícono queda junto al reloj (puede estar dentro de la flechita ^).",
                    ToolTipIcon.Info, null);

            var warmup = new Thread(Warmup) { IsBackground = true, Name = "ClipRecorderWarmup" };
            warmup.Start();
        }

        private void EnsureFirstRunSetup()
        {
            try
            {
                string ico = Path.Combine(_baseDir, "ClipRecorder.ico");
                if (!File.Exists(ico)) IconFactory.WriteIcoFile(ico);

                string projectLink = Path.Combine(_baseDir, "ClipRecorder.lnk");
                Shortcuts.Create(projectLink, _baseDir, false);

                if (!_settings.FirstRunDone)
                {
                    Shortcuts.Create(Shortcuts.DesktopLink, _baseDir, false);
                    Log.Write("Acceso directo creado en el escritorio: " + Shortcuts.DesktopLink);
                    _settings.FirstRunDone = true;
                    _settings.Save(_settingsPath);
                }
                // Keep the startup shortcut in sync with the setting (and with the project's location).
                Shortcuts.SetAutoStart(_baseDir, _settings.AutoStart);
            }
            catch (Exception ex)
            {
                Log.Write("No se pudieron crear los accesos directos: " + ex.Message);
            }
        }

        // Runs a tiny throwaway recording so Media Foundation and the encoder are already
        // loaded when the user presses the hotkey for the first time.
        private void Warmup()
        {
            try
            {
                Thread.Sleep(1500);
                string temp = Path.Combine(Path.GetTempPath(), "cliprecorder_warmup_" + Guid.NewGuid().ToString("N") + ".mp4");
                long ms = Warmer.Run(temp, _settings.HardwareEncoder);
                Log.Write("Precalentamiento del grabador: " + ms + " ms");
                try { File.Delete(temp); } catch { }
            }
            catch (Exception ex)
            {
                Log.Write("Precalentamiento omitido: " + ex.Message);
            }
            finally
            {
                _warmupDone.Set();
            }
        }

        private void WatchTrigger()
        {
            var handles = new WaitHandle[] { _trigger, _quit };
            while (WaitHandle.WaitAny(handles) == 0)
            {
                Log.Write("Se abrió ClipRecorder de nuevo: iniciando captura.");
                _ui.Post(_ => StartCapture(), null);
            }
        }

        private void RegisterHotkey(bool notifyOnError)
        {
            string error;
            if (_hotkey.Register(_settings.Hotkey, out error))
            {
                Log.Write("Atajo registrado: " + _settings.Hotkey);
                return;
            }
            Log.Write("Atajo NO registrado: " + error);
            if (notifyOnError)
                MessageBox.Show(error + "\n\nPodés elegir otro desde Configuración (clic derecho en el ícono rojo junto al reloj).",
                    "ClipRecorder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void UpdateMenu()
        {
            _recordItem.Text = "Grabar ahora    (" + _settings.Hotkey + ")";
            _autoStartItem.Checked = Shortcuts.IsAutoStartEnabled();
            bool busy = _session != null;
            _recordItem.Enabled = !busy;
            _tray.Icon = busy ? _recordingIcon : _idleIcon;
            string tip = busy ? "ClipRecorder - grabando..." : "ClipRecorder - " + _settings.Hotkey + " para grabar";
            _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        // From the tray menu: wait for the menu to disappear so it isn't in the frozen frame.
        private void StartCaptureSoon()
        {
            var t = new System.Windows.Forms.Timer { Interval = 200 };
            t.Tick += delegate { t.Stop(); t.Dispose(); StartCapture(); };
            t.Start();
        }

        private void StartCapture()
        {
            if (_selecting) return;
            if (_session != null)
            {
                ShowBalloon("ClipRecorder", "Ya hay una grabación en curso.", ToolTipIcon.Info, null);
                return;
            }
            _selecting = true;
            SelectionResult selection = null;
            try
            {
                Screen screen = Screen.FromPoint(Cursor.Position);
                Log.Write("Atajo: abriendo selector en " + screen.DeviceName + " " + screen.Bounds);
                using (var overlay = new SelectionOverlay(screen, _settings.TrackingValue))
                {
                    overlay.ShowDialog();
                    selection = overlay.Result;
                }
                if (selection == null)
                {
                    Log.Write("Selección cancelada.");
                    return;
                }
                _warmupDone.WaitOne(4000);
                var session = new RecordingSession(_settings, selection, _settings.ResolveOutputFolder(_baseDir));
                selection = null; // owned by the session now
                session.Completed += OnCompleted;
                session.Failed += OnFailed;
                _session = session;
                UpdateMenu();
                session.Start();
            }
            catch (Exception ex)
            {
                Log.Write("Error al iniciar la grabación: " + ex);
                if (_session != null) { _session.Dispose(); _session = null; }
                UpdateMenu();
                MessageBox.Show("No se pudo iniciar la grabación:\n\n" + ex.Message, "ClipRecorder", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (selection != null) selection.Dispose();
                _selecting = false;
            }
        }

        private void OnCompleted(RecordingSession session, string path)
        {
            EndSession(session);
            ShowBalloon("Video guardado", Path.GetFileName(path) + "\nHacé clic acá para verlo en la carpeta.", ToolTipIcon.Info,
                () => OpenFolder(path));
        }

        private void OnFailed(RecordingSession session, string error)
        {
            EndSession(session);
            ShowBalloon("No se pudo grabar", error, ToolTipIcon.Error, () => OpenLog());
        }

        private void EndSession(RecordingSession session)
        {
            if (_session == session) _session = null;
            session.Dispose();
            UpdateMenu();
        }

        private void ShowBalloon(string title, string text, ToolTipIcon icon, Action onClick)
        {
            _balloonClick = onClick;
            _tray.ShowBalloonTip(5000, title, text, icon);
        }

        private void OpenFolder(string selectFile)
        {
            if (selectFile != null && File.Exists(selectFile))
                Process.Start("explorer.exe", "/select,\"" + selectFile + "\"");
            else
                Process.Start("explorer.exe", "\"" + _settings.ResolveOutputFolder(_baseDir) + "\"");
        }

        private void OpenLog()
        {
            if (Log.FilePath != null && File.Exists(Log.FilePath)) Process.Start("notepad.exe", "\"" + Log.FilePath + "\"");
        }

        private void ToggleAutoStart()
        {
            try
            {
                _settings.AutoStart = !Shortcuts.IsAutoStartEnabled();
                Shortcuts.SetAutoStart(_baseDir, _settings.AutoStart);
                _settings.Save(_settingsPath);
                Log.Write("Iniciar con Windows: " + _settings.AutoStart);
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo cambiar el inicio automático:\n\n" + ex.Message, "ClipRecorder");
            }
            UpdateMenu();
        }

        private void OpenSettings()
        {
            _hotkey.Unregister(); // so the current hotkey can be typed into the settings box
            try
            {
                using (var form = new SettingsForm(_settings, _baseDir))
                {
                    if (form.ShowDialog() == DialogResult.OK)
                    {
                        _settings.Save(_settingsPath);
                        Shortcuts.SetAutoStart(_baseDir, _settings.AutoStart);
                        Log.Write("Configuración guardada.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Error en configuración: " + ex);
                MessageBox.Show("No se pudo guardar la configuración:\n\n" + ex.Message, "ClipRecorder");
            }
            finally
            {
                RegisterHotkey(true);
                UpdateMenu();
            }
        }

        private void ExitApp()
        {
            Log.Write("Cerrando ClipRecorder.");
            if (_session != null) _session.Cancel();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _quit.Set();
                _hotkey.Dispose();
                _tray.Visible = false;
                _tray.Dispose();
                _idleIcon.Dispose();
                _recordingIcon.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
