using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace ClipRecorder
{
    public enum TrackingMode { Ninguno, Mouse, Objeto }

    // Public so JavaScriptSerializer can populate it.
    public sealed class Settings
    {
        public static readonly string[] QualityNames = { "Baja", "Media", "Alta", "Maxima" };
        private static readonly int[] QualityValues = { 50, 70, 85, 95 };

        public int DurationSeconds { get; set; }
        public int Fps { get; set; }
        public string Quality { get; set; }
        public int OutputWidth { get; set; }
        public int OutputHeight { get; set; }
        public bool RecordSystemAudio { get; set; }
        public bool RecordMicrophone { get; set; }
        public string Tracking { get; set; }
        public string Hotkey { get; set; }
        public string OutputFolder { get; set; }
        public bool HardwareEncoder { get; set; }
        public bool AutoStart { get; set; }
        public bool FirstRunDone { get; set; }

        public Settings()
        {
            DurationSeconds = 5;
            Fps = 30;
            Quality = "Alta";
            OutputWidth = 0;
            OutputHeight = 0;
            RecordSystemAudio = true;
            RecordMicrophone = false;
            Tracking = "Ninguno";
            Hotkey = "Ctrl+Alt+Shift+R";
            OutputFolder = "Recordings";
            HardwareEncoder = false;
            AutoStart = true;
            FirstRunDone = false;
        }

        public TrackingMode TrackingValue
        {
            get
            {
                TrackingMode mode;
                return Enum.TryParse(Tracking, true, out mode) ? mode : TrackingMode.Ninguno;
            }
        }

        public int QualityValue
        {
            get
            {
                int i = Array.FindIndex(QualityNames, q => string.Equals(q, Quality, StringComparison.OrdinalIgnoreCase));
                return QualityValues[i < 0 ? 2 : i];
            }
        }

        public string ResolveOutputFolder(string baseDir)
        {
            string folder = string.IsNullOrWhiteSpace(OutputFolder) ? "Recordings" : OutputFolder.Trim();
            if (!Path.IsPathRooted(folder)) folder = Path.Combine(baseDir, folder);
            Directory.CreateDirectory(folder);
            return folder;
        }

        public static Settings Load(string path)
        {
            Settings s = null;
            try
            {
                if (File.Exists(path))
                    s = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                Log.Write("settings.json invalido, se usan valores por defecto: " + ex.Message);
            }
            if (s == null) s = new Settings();
            s.Sanitize();
            return s;
        }

        private void Sanitize()
        {
            DurationSeconds = Math.Max(1, Math.Min(3600, DurationSeconds));
            Fps = Math.Max(5, Math.Min(120, Fps));
            OutputWidth = Math.Max(0, OutputWidth) & ~1;
            OutputHeight = Math.Max(0, OutputHeight) & ~1;
            if (OutputWidth == 0 || OutputHeight == 0) { OutputWidth = 0; OutputHeight = 0; }
            if (Array.FindIndex(QualityNames, q => string.Equals(q, Quality, StringComparison.OrdinalIgnoreCase)) < 0) Quality = "Alta";
            Tracking = TrackingValue.ToString();
            if (string.IsNullOrWhiteSpace(Hotkey)) Hotkey = "Ctrl+Alt+Shift+R";
        }

        public void Save(string path)
        {
            var json = new JavaScriptSerializer();
            var fields = new List<KeyValuePair<string, object>>
            {
                new KeyValuePair<string, object>("DurationSeconds", DurationSeconds),
                new KeyValuePair<string, object>("Fps", Fps),
                new KeyValuePair<string, object>("Quality", Quality),
                new KeyValuePair<string, object>("OutputWidth", OutputWidth),
                new KeyValuePair<string, object>("OutputHeight", OutputHeight),
                new KeyValuePair<string, object>("RecordSystemAudio", RecordSystemAudio),
                new KeyValuePair<string, object>("RecordMicrophone", RecordMicrophone),
                new KeyValuePair<string, object>("Tracking", Tracking),
                new KeyValuePair<string, object>("Hotkey", Hotkey),
                new KeyValuePair<string, object>("OutputFolder", OutputFolder),
                new KeyValuePair<string, object>("HardwareEncoder", HardwareEncoder),
                new KeyValuePair<string, object>("AutoStart", AutoStart),
                new KeyValuePair<string, object>("FirstRunDone", FirstRunDone)
            };
            var sb = new StringBuilder("{" + Environment.NewLine);
            for (int i = 0; i < fields.Count; i++)
            {
                sb.Append("  \"").Append(fields[i].Key).Append("\": ").Append(json.Serialize(fields[i].Value));
                sb.Append(i < fields.Count - 1 ? "," : "").Append(Environment.NewLine);
            }
            sb.Append("}").Append(Environment.NewLine);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
