using System;
using System.IO;

namespace ClipRecorder
{
    internal static class Log
    {
        private static readonly object Sync = new object();
        private static string _path;

        public static string FilePath { get { return _path; } }

        public static void Init(string path)
        {
            _path = path;
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Delete(path);
            }
            catch { }
        }

        public static void Write(string message)
        {
            if (_path == null) return;
            try
            {
                lock (Sync)
                {
                    File.AppendAllText(_path, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + message + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
