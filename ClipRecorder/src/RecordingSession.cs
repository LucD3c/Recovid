using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using ScreenRecorderLib;

namespace ClipRecorder
{
    internal sealed class RecordingSession : IDisposable
    {
        private const int FollowIntervalMs = 33;

        private readonly Settings _settings;
        private readonly SelectionResult _selection;
        private readonly string _outputFolder;
        private SynchronizationContext _ui;

        private Recorder _recorder;
        private DisplayRecordingSource _source;
        private RecordingBorder _border;
        private ObjectTracker _tracker;
        private System.Windows.Forms.Timer _stopTimer;
        private System.Windows.Forms.Timer _followTimer;
        private System.Windows.Forms.Timer _watchdog;
        private readonly Stopwatch _clock = new Stopwatch();

        private Rectangle _monitor;
        private int _width, _height;
        private double _x, _y;
        private int _appliedX = int.MinValue, _appliedY = int.MinValue;
        private TrackingMode _mode;
        private bool _started, _finished;
        private long _lastPositionLog;

        public string FilePath { get; private set; }
        public event Action<RecordingSession> Started;
        public event Action<RecordingSession, string> Completed;
        public event Action<RecordingSession, string> Failed;

        public RecordingSession(Settings settings, SelectionResult selection, string outputFolder)
        {
            _settings = settings;
            _selection = selection;
            _outputFolder = outputFolder;
        }

        public void Start()
        {
            _ui = SynchronizationContext.Current;
            _monitor = _selection.Screen.Bounds;

            Rectangle region = _selection.Region;
            region.Intersect(_monitor);
            // H.264 needs even dimensions.
            _width = Math.Max(16, region.Width & ~1);
            _height = Math.Max(16, region.Height & ~1);
            _x = region.X - _monitor.X;
            _y = region.Y - _monitor.Y;

            bool fullScreen = _width >= _monitor.Width - 2 && _height >= _monitor.Height - 2;
            _mode = fullScreen ? TrackingMode.Ninguno : _selection.Mode;

            _source = CreateSource();
            _source.SourceRect = new ScreenRect(_x, _y, _width, _height);
            _source.IsCursorCaptureEnabled = true;

            var options = new RecorderOptions
            {
                SourceOptions = new SourceOptions(),
                OutputOptions = new OutputOptions(),
                AudioOptions = BuildAudio(),
                MouseOptions = new MouseOptions { IsMousePointerEnabled = true },
                VideoEncoderOptions = new VideoEncoderOptions
                {
                    Encoder = new H264VideoEncoder { BitrateMode = H264BitrateControlMode.Quality, EncoderProfile = H264Profile.High },
                    Quality = _settings.QualityValue,
                    Framerate = _settings.Fps,
                    IsFixedFramerate = true,
                    IsHardwareEncodingEnabled = _settings.HardwareEncoder,
                    IsMp4FastStartEnabled = true
                }
            };
            options.SourceOptions.RecordingSources.Add(_source);
            if (_settings.OutputWidth > 0 && _settings.OutputHeight > 0)
            {
                options.OutputOptions.OutputFrameSize = new ScreenSize(_settings.OutputWidth, _settings.OutputHeight);
                options.OutputOptions.Stretch = StretchMode.Uniform;
            }

            FilePath = NextFilePath();
            Log.Write(string.Format("Grabación: monitor {0} {1}, área {2}x{3} en ({4},{5}), seguimiento {6}, {7}s, {8}fps, calidad {9}, encoder {10}",
                _selection.Screen.DeviceName, _monitor, _width, _height, _x, _y, _mode, _settings.DurationSeconds, _settings.Fps,
                _settings.Quality, _settings.HardwareEncoder ? "GPU" : "software"));

            _border = new RecordingBorder();
            _border.Place(AbsoluteRect());
            _border.Show();

            if (_mode == TrackingMode.Objeto)
            {
                _tracker = new ObjectTracker(_selection.Snapshot, _monitor, _selection.ObjectRect);
                _tracker.Start();
            }
            if (_mode != TrackingMode.Ninguno)
            {
                // Following starts right away so the frame is already on target when capture begins.
                _followTimer = new System.Windows.Forms.Timer { Interval = FollowIntervalMs };
                _followTimer.Tick += delegate { Follow(); };
                _followTimer.Start();
            }

            _recorder = Recorder.CreateRecorder(options);
            // The library raises these on its own capture thread; everything that touches
            // timers or windows must run on the UI thread.
            _recorder.OnStatusChanged += (s, e) => _ui.Post(_ => OnStatus(e.Status), null);
            _recorder.OnRecordingComplete += (s, e) => _ui.Post(_ => Finish(null, e.FilePath), null);
            _recorder.OnRecordingFailed += (s, e) => _ui.Post(_ => Finish(e.Error, null), null);

            _watchdog = new System.Windows.Forms.Timer { Interval = 15000 };
            _watchdog.Tick += delegate { OnWatchdog(); };
            _watchdog.Start();

            _clock.Start();
            _recorder.Record(FilePath);
        }

        private DisplayRecordingSource CreateSource()
        {
            string device = _selection.Screen.DeviceName;
            bool known = Recorder.GetDisplays().Any(d => string.Equals(d.DeviceName, device, StringComparison.OrdinalIgnoreCase));
            if (known) return new DisplayRecordingSource(device);
            Log.Write("Monitor '" + device + "' no reconocido por el grabador; se usa el principal.");
            _monitor = Screen.PrimaryScreen.Bounds;
            return DisplayRecordingSource.MainMonitor;
        }

        private AudioOptions BuildAudio()
        {
            var audio = new AudioOptions { IsAudioEnabled = false, Bitrate = AudioBitrate.bitrate_192kbps };
            if (_settings.RecordSystemAudio)
            {
                var outs = Recorder.GetSystemAudioLoopbackDevices();
                var dev = outs.FirstOrDefault(d => d.IsDefaultDevice) ?? outs.FirstOrDefault();
                if (dev != null)
                {
                    dev.IsAudioCaptureEnabled = true;
                    audio.AudioSources.Add(dev);
                    Log.Write("Audio del sistema: " + dev.FriendlyName);
                }
                else Log.Write("No hay dispositivo de salida de audio para grabar.");
            }
            if (_settings.RecordMicrophone)
            {
                var ins = Recorder.GetSystemAudioCaptureDevices();
                var mic = ins.FirstOrDefault(d => d.IsDefaultDevice) ?? ins.FirstOrDefault();
                if (mic != null)
                {
                    mic.IsAudioCaptureEnabled = true;
                    audio.AudioSources.Add(mic);
                    Log.Write("Micrófono: " + mic.FriendlyName);
                }
                else Log.Write("No hay micrófono para grabar.");
            }
            audio.IsAudioEnabled = audio.AudioSources.Count > 0;
            return audio;
        }

        private string NextFilePath()
        {
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string path = Path.Combine(_outputFolder, "Grabacion_" + stamp + ".mp4");
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(_outputFolder, "Grabacion_" + stamp + "_" + i + ".mp4");
            return path;
        }

        private Rectangle AbsoluteRect()
        {
            return new Rectangle(_monitor.X + (int)Math.Round(_x), _monitor.Y + (int)Math.Round(_y), _width, _height);
        }

        private void OnStatus(RecorderStatus status)
        {
            if (_finished) return;
            if (status != RecorderStatus.Recording || _started) return;
            _started = true;
            Log.Write("Captura real iniciada a los " + _clock.ElapsedMilliseconds + " ms de soltar el mouse.");
            if (_border != null) _border.SetRecording(true);

            // The requested duration is counted from the moment frames are really being
            // captured, never from when Record() was called (the encoder needs time to start).
            _stopTimer = new System.Windows.Forms.Timer { Interval = _settings.DurationSeconds * 1000 };
            _stopTimer.Tick += delegate
            {
                _stopTimer.Stop();
                Log.Write("Duración cumplida, deteniendo.");
                if (_border != null) _border.Hide();
                _recorder.Stop();
            };
            _stopTimer.Start();

            var handler = Started;
            if (handler != null) handler(this);
        }

        private void OnWatchdog()
        {
            _watchdog.Stop();
            if (_started || _finished) return;
            Log.Write("La captura no arrancó en 15 s; se cancela.");
            try { _recorder.Stop(); } catch { }
            Finish("La grabación no pudo iniciar (el grabador no respondió en 15 segundos).", null);
        }

        private void Follow()
        {
            if (_finished) return;
            Point target;
            double easing;
            if (_mode == TrackingMode.Mouse)
            {
                Native.POINT p;
                if (!Native.GetCursorPos(out p)) return;
                target = new Point(p.X, p.Y);
                easing = 0.25;
            }
            else
            {
                if (_tracker == null) return;
                target = _tracker.Center;
                easing = 0.30;
            }

            double maxX = Math.Max(0, _monitor.Width - _width);
            double maxY = Math.Max(0, _monitor.Height - _height);
            double tx = Math.Max(0, Math.Min(maxX, target.X - _monitor.X - _width / 2.0));
            double ty = Math.Max(0, Math.Min(maxY, target.Y - _monitor.Y - _height / 2.0));
            _x += (tx - _x) * easing;
            _y += (ty - _y) * easing;

            int nx = (int)Math.Round(_x), ny = (int)Math.Round(_y);
            if (nx != _appliedX || ny != _appliedY)
            {
                _appliedX = nx;
                _appliedY = ny;
                _source.SourceRect = new ScreenRect(nx, ny, _width, _height);
                if (_border != null && _border.Visible) _border.Place(AbsoluteRect());
            }

            if (_clock.ElapsedMilliseconds - _lastPositionLog >= 500)
            {
                _lastPositionLog = _clock.ElapsedMilliseconds;
                string extra = _tracker != null ? string.Format(", objeto en ({0},{1}) confianza {2:0.00}{3}", target.X, target.Y, _tracker.Confidence, _tracker.IsLost ? " PERDIDO" : "") : "";
                Log.Write(string.Format("Seguimiento: centro del recorte ({0},{1}){2}", _monitor.X + nx + _width / 2, _monitor.Y + ny + _height / 2, extra));
            }
        }

        public void Cancel()
        {
            if (_finished || _recorder == null) return;
            Log.Write("Grabación cancelada.");
            try { _recorder.Stop(); } catch { }
        }

        private void Finish(string error, string path)
        {
            if (_finished) return;
            _finished = true;
            StopHelpers();

            if (error == null)
            {
                Log.Write("Video guardado: " + path);
                var handler = Completed;
                if (handler != null) handler(this, path);
            }
            else
            {
                Log.Write("Error de grabación: " + error);
                try
                {
                    if (File.Exists(FilePath) && new FileInfo(FilePath).Length < 1024) File.Delete(FilePath);
                }
                catch { }
                var handler = Failed;
                if (handler != null) handler(this, error);
            }
        }

        private void StopHelpers()
        {
            if (_stopTimer != null) { _stopTimer.Stop(); _stopTimer.Dispose(); _stopTimer = null; }
            if (_followTimer != null) { _followTimer.Stop(); _followTimer.Dispose(); _followTimer = null; }
            if (_watchdog != null) { _watchdog.Stop(); _watchdog.Dispose(); _watchdog = null; }
            if (_tracker != null)
            {
                Log.Write("Tracker: " + _tracker.Steps + " búsquedas realizadas.");
                _tracker.Dispose();
                _tracker = null;
            }
            if (_border != null) { _border.Close(); _border.Dispose(); _border = null; }
        }

        public void Dispose()
        {
            StopHelpers();
            if (_recorder != null) { _recorder.Dispose(); _recorder = null; }
            _selection.Dispose();
        }
    }
}
