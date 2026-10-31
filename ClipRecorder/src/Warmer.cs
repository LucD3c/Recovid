using System.Diagnostics;
using System.Threading;
using ScreenRecorderLib;

namespace ClipRecorder
{
    internal static class Warmer
    {
        public static long Run(string path, bool hardware)
        {
            var clock = Stopwatch.StartNew();
            var done = new ManualResetEvent(false);
            var started = new ManualResetEvent(false);
            var source = DisplayRecordingSource.MainMonitor;
            source.SourceRect = new ScreenRect(0, 0, 64, 64);
            var options = new RecorderOptions
            {
                SourceOptions = new SourceOptions(),
                AudioOptions = new AudioOptions { IsAudioEnabled = false },
                VideoEncoderOptions = new VideoEncoderOptions { IsHardwareEncodingEnabled = hardware, Framerate = 10 }
            };
            options.SourceOptions.RecordingSources.Add(source);
            using (var recorder = Recorder.CreateRecorder(options))
            {
                recorder.OnStatusChanged += (s, e) => { if (e.Status == RecorderStatus.Recording) started.Set(); };
                recorder.OnRecordingComplete += (s, e) => done.Set();
                recorder.OnRecordingFailed += (s, e) => { started.Set(); done.Set(); };
                recorder.Record(path);
                started.WaitOne(10000);
                Thread.Sleep(200);
                recorder.Stop();
                done.WaitOne(10000);
            }
            return clock.ElapsedMilliseconds;
        }
    }
}
