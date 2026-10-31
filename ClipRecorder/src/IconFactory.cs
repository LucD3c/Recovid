using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace ClipRecorder
{
    // Icons are drawn in code so the app ships no binary assets besides the recording library.
    internal static class IconFactory
    {
        public static Bitmap Draw(int size, bool recording)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float pad = size * 0.06f;
                float d = size - 2 * pad;
                using (var bg = new SolidBrush(recording ? Color.FromArgb(255, 200, 30, 30) : Color.FromArgb(255, 40, 40, 46)))
                    g.FillEllipse(bg, pad, pad, d, d);
                using (var ring = new Pen(Color.White, Math.Max(1.2f, size * 0.07f)))
                    g.DrawEllipse(ring, pad + size * 0.05f, pad + size * 0.05f, d - size * 0.10f, d - size * 0.10f);
                float dot = size * (recording ? 0.30f : 0.40f);
                using (var fg = new SolidBrush(recording ? Color.White : Color.FromArgb(255, 235, 45, 45)))
                {
                    if (recording) g.FillRectangle(fg, (size - dot) / 2f, (size - dot) / 2f, dot, dot);
                    else g.FillEllipse(fg, (size - dot) / 2f, (size - dot) / 2f, dot, dot);
                }
            }
            return bmp;
        }

        public static Icon CreateIcon(int size, bool recording)
        {
            using (var bmp = Draw(size, recording))
            {
                IntPtr handle = bmp.GetHicon();
                try
                {
                    using (var temp = Icon.FromHandle(handle)) return (Icon)temp.Clone();
                }
                finally
                {
                    Native.DestroyIcon(handle);
                }
            }
        }

        // Multi-size .ico (PNG frames) for the desktop/startup shortcuts.
        public static void WriteIcoFile(string path)
        {
            int[] sizes = { 16, 24, 32, 48, 64, 256 };
            var frames = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
            {
                using (var bmp = Draw(sizes[i], false))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    frames[i] = ms.ToArray();
                }
            }
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((short)0);
                w.Write((short)1);
                w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((short)1);
                    w.Write((short)32);
                    w.Write(frames[i].Length);
                    w.Write(offset);
                    offset += frames[i].Length;
                }
                for (int i = 0; i < sizes.Length; i++) w.Write(frames[i]);
            }
        }
    }
}
