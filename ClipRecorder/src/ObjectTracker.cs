using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;

namespace ClipRecorder
{
    // Follows a user-marked object on screen (e.g. something moving in a video).
    // It learns the object's appearance from the frozen frame, then ~25 times per second
    // grabs the screen around the last known position and finds the best match with
    // normalized cross-correlation (robust to brightness changes). The template slowly
    // adapts to the object's changing look; if the match is lost, the search widens.
    internal sealed class ObjectTracker : IDisposable
    {
        private const int TemplateMaxSide = 32;
        private const int PeriodMs = 40;
        // Frame-to-frame tracking near the predicted position can accept a moderate match,
        // but (re)acquiring the object anywhere in a wide area demands a strong one: random
        // background texture can reach ~0.6, the real object scores 0.9+.
        private const float TrackScore = 0.50f;
        private const float AcquireScore = 0.75f;
        private const float AdaptScore = 0.80f;
        private const int Candidates = 6;

        private readonly Rectangle _bounds;
        private readonly int _scale, _tw, _th, _n;
        private readonly float[] _tmpl;
        private readonly float[] _tmpl0;
        private float _tnorm;

        private readonly object _sync = new object();
        private Point _center;
        private volatile bool _running;
        private volatile bool _lost;
        private Thread _thread;
        private double _vx, _vy;            // estimated velocity, px per second
        private long _lastFoundMs = -1;
        private int _missedSteps;
        private readonly Stopwatch _clock = new Stopwatch();

        private struct Candidate { public float Score; public int X, Y; }
        private readonly Candidate[] _top = new Candidate[Candidates];

        private Bitmap _buf;
        private Graphics _bufGraphics;
        private float[] _img = new float[0];
        private double[] _sum = new double[0];
        private double[] _sq = new double[0];

        // Lets an offline test feed synthetic frames instead of grabbing the real screen.
        internal Action<Rectangle, Bitmap> CaptureOverride;

        public float Confidence { get; private set; }
        public bool IsLost { get { return _lost; } }
        public int Steps { get; private set; }

        // Where the object is now: last match projected forward with the measured velocity,
        // which removes most of the lag between searches when it moves fast.
        public Point Center
        {
            get
            {
                lock (_sync)
                {
                    if (_lost || _lastFoundMs < 0) return _center;
                    double dt = Math.Min(0.1, (_clock.ElapsedMilliseconds - _lastFoundMs) / 1000.0);
                    return new Point((int)(_center.X + _vx * dt), (int)(_center.Y + _vy * dt));
                }
            }
        }

        private Point RawCenter
        {
            get { lock (_sync) return _center; }
        }

        public ObjectTracker(Bitmap snapshot, Rectangle snapshotBounds, Rectangle objectRect)
        {
            _bounds = snapshotBounds;
            Rectangle obj = objectRect;
            obj.Intersect(snapshotBounds);
            if (obj.Width < 8 || obj.Height < 8) throw new ArgumentException("El objeto marcado es demasiado chico.");

            int maxSide = Math.Max(obj.Width, obj.Height);
            _scale = Math.Max(1, (maxSide + TemplateMaxSide - 1) / TemplateMaxSide);
            _tw = Math.Max(4, obj.Width / _scale);
            _th = Math.Max(4, obj.Height / _scale);
            _n = _tw * _th;

            var rel = new Rectangle(obj.X - snapshotBounds.X, obj.Y - snapshotBounds.Y, _tw * _scale, _th * _scale);
            _tmpl = new float[_n];
            int w, h;
            Downsample(snapshot, rel, _tmpl, out w, out h);
            ZeroMean(_tmpl, out _tnorm);
            _tmpl0 = (float[])_tmpl.Clone();
            _center = new Point(obj.X + rel.Width / 2, obj.Y + rel.Height / 2);
            Log.Write(string.Format("Tracker: objeto {0}, escala 1/{1}, plantilla {2}x{3}", obj, _scale, _tw, _th));
        }

        public void Start()
        {
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "ObjectTracker", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        private void Loop()
        {
            _clock.Start();
            int margin = Math.Max(_tw, _th) + 6;
            // The frozen frame is older than the live screen (the object may have moved while
            // the user was selecting), so the very first search is already a wide one.
            _lost = true;
            _missedSteps = 1;
            while (_running)
            {
                long t0 = _clock.ElapsedMilliseconds;
                try
                {
                    if (!_lost) Step(margin, 1, TrackScore);
                    else Step(margin * (_missedSteps >= 2 ? 8 : 3), 2, AcquireScore);
                }
                catch (Exception ex)
                {
                    Log.Write("Tracker error: " + ex.Message);
                    Thread.Sleep(200);
                }
                int wait = PeriodMs - (int)(_clock.ElapsedMilliseconds - t0);
                Thread.Sleep(wait > 1 ? wait : 1);
            }
        }

        private void Step(int margin, int stride, float accept)
        {
            Point c = RawCenter;
            long now = _clock.ElapsedMilliseconds;
            if (!_lost && _lastFoundMs >= 0)
            {
                // Look where the object is heading, not where it was.
                double ahead = Math.Min(0.15, (now - _lastFoundMs + PeriodMs) / 1000.0);
                c = new Point((int)(c.X + _vx * ahead), (int)(c.Y + _vy * ahead));
            }
            int aw = (_tw + 2 * margin) * _scale;
            int ah = (_th + 2 * margin) * _scale;
            var area = new Rectangle(c.X - aw / 2, c.Y - ah / 2, aw, ah);
            area.Intersect(_bounds);
            int iw = area.Width / _scale, ih = area.Height / _scale;
            if (iw < _tw || ih < _th) return;
            area.Width = iw * _scale;
            area.Height = ih * _scale;

            if (_buf == null || _buf.Width < area.Width || _buf.Height < area.Height)
            {
                if (_bufGraphics != null) _bufGraphics.Dispose();
                if (_buf != null) _buf.Dispose();
                _buf = new Bitmap(Math.Max(area.Width, 64), Math.Max(area.Height, 64), PixelFormat.Format32bppArgb);
                _bufGraphics = Graphics.FromImage(_buf);
            }
            var capture = CaptureOverride;
            if (capture != null) capture(area, _buf);
            else _bufGraphics.CopyFromScreen(area.X, area.Y, 0, 0, area.Size, CopyPixelOperation.SourceCopy);

            if (_img.Length < iw * ih) _img = new float[iw * ih];
            int dw, dh;
            Downsample(_buf, new Rectangle(0, 0, area.Width, area.Height), _img, out dw, out dh);
            BuildIntegrals(iw, ih);

            int bx, by;
            float best;
            if (stride == 1)
            {
                best = Search(iw, ih, 0, iw - _tw, 0, ih - _th, 1, out bx, out by);
            }
            else
            {
                // Coarse pass keeps several candidates; each is refined at full resolution,
                // so a lucky background patch can't hide the real object.
                Search(iw, ih, 0, iw - _tw, 0, ih - _th, stride, out bx, out by);
                best = -2f;
                bx = by = 0;
                for (int k = 0; k < Candidates; k++)
                {
                    if (_top[k].Score <= -1f) continue;
                    int rx, ry;
                    float refined = Search(iw, ih, Math.Max(0, _top[k].X - stride), Math.Min(iw - _tw, _top[k].X + stride),
                                           Math.Max(0, _top[k].Y - stride), Math.Min(ih - _th, _top[k].Y + stride), 1, out rx, out ry);
                    if (refined > best) { best = refined; bx = rx; by = ry; }
                }
            }

            Confidence = best;
            Steps++;
            if (best >= accept)
            {
                var found = new Point(area.X + bx * _scale + _tw * _scale / 2, area.Y + by * _scale + _th * _scale / 2);
                lock (_sync)
                {
                    if (!_lost && _lastFoundMs >= 0 && now > _lastFoundMs)
                    {
                        double dt = (now - _lastFoundMs) / 1000.0;
                        _vx = 0.6 * _vx + 0.4 * (found.X - _center.X) / dt;
                        _vy = 0.6 * _vy + 0.4 * (found.Y - _center.Y) / dt;
                    }
                    else
                    {
                        _vx = _vy = 0;
                    }
                    _center = found;
                    _lastFoundMs = now;
                }
                if (_lost) Log.Write("Tracker: objeto encontrado en (" + found.X + "," + found.Y + "), confianza " + best.ToString("0.00"));
                _lost = false;
                _missedSteps = 0;
                if (best >= AdaptScore) Adapt(bx, by, iw);
            }
            else
            {
                _missedSteps++;
                if (!_lost)
                {
                    lock (_sync) { _lost = true; _vx = _vy = 0; }
                    Log.Write("Tracker: objeto perdido (" + best.ToString("0.00") + "), ampliando búsqueda");
                }
            }
        }

        private unsafe void Downsample(Bitmap bmp, Rectangle r, float[] dst, out int w, out int h)
        {
            int s = _scale;
            w = r.Width / s;
            h = r.Height / s;
            BitmapData data = bmp.LockBits(new Rectangle(r.X, r.Y, w * s, h * s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte* basePtr = (byte*)data.Scan0;
                int stride = data.Stride;
                float inv = 1f / (s * s * 256f);
                for (int oy = 0; oy < h; oy++)
                {
                    for (int ox = 0; ox < w; ox++)
                    {
                        int acc = 0;
                        for (int dy = 0; dy < s; dy++)
                        {
                            byte* p = basePtr + (oy * s + dy) * stride + ox * s * 4;
                            for (int dx = 0; dx < s; dx++, p += 4)
                                acc += p[2] * 77 + p[1] * 150 + p[0] * 29;
                        }
                        dst[oy * w + ox] = acc * inv;
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        private void BuildIntegrals(int iw, int ih)
        {
            int stride = iw + 1;
            int size = stride * (ih + 1);
            if (_sum.Length < size) { _sum = new double[size]; _sq = new double[size]; }
            for (int x = 0; x <= iw; x++) { _sum[x] = 0; _sq[x] = 0; }
            for (int y = 1; y <= ih; y++)
            {
                double rowSum = 0, rowSq = 0;
                _sum[y * stride] = 0;
                _sq[y * stride] = 0;
                int src = (y - 1) * iw;
                for (int x = 1; x <= iw; x++)
                {
                    double v = _img[src + x - 1];
                    rowSum += v;
                    rowSq += v * v;
                    _sum[y * stride + x] = _sum[(y - 1) * stride + x] + rowSum;
                    _sq[y * stride + x] = _sq[(y - 1) * stride + x] + rowSq;
                }
            }
        }

        private unsafe float Search(int iw, int ih, int x0, int x1, int y0, int y1, int step, out int bestX, out int bestY)
        {
            bestX = x0;
            bestY = y0;
            float best = -2f;
            int stride = iw + 1;
            double n = _n;
            bool collect = step > 1;
            if (collect) for (int k = 0; k < Candidates; k++) _top[k].Score = -2f;
            fixed (float* T = _tmpl)
            fixed (float* I = _img)
            {
                for (int py = y0; py <= y1; py += step)
                {
                    for (int px = x0; px <= x1; px += step)
                    {
                        int a = py * stride + px, b = a + _tw, c = (py + _th) * stride + px, d = c + _tw;
                        double s = _sum[d] - _sum[b] - _sum[c] + _sum[a];
                        double q = _sq[d] - _sq[b] - _sq[c] + _sq[a];
                        double varN = q - s * s / n;
                        if (varN < 1e-3) continue;

                        float cross = 0f;
                        for (int ty = 0; ty < _th; ty++)
                        {
                            float* tRow = T + ty * _tw;
                            float* iRow = I + (py + ty) * iw + px;
                            for (int tx = 0; tx < _tw; tx++) cross += tRow[tx] * iRow[tx];
                        }
                        float score = (float)(cross / (_tnorm * Math.Sqrt(varN)));
                        if (score > best) { best = score; bestX = px; bestY = py; }
                        if (collect) Offer(score, px, py);
                    }
                }
            }
            return best;
        }

        // Keeps the best few distinct peaks (positions closer than half the template are the same peak).
        private void Offer(float score, int x, int y)
        {
            int worst = 0;
            for (int k = 0; k < Candidates; k++)
            {
                if (_top[k].Score > -1f && Math.Abs(_top[k].X - x) < _tw / 2 + 1 && Math.Abs(_top[k].Y - y) < _th / 2 + 1)
                {
                    if (score > _top[k].Score) { _top[k].Score = score; _top[k].X = x; _top[k].Y = y; }
                    return;
                }
                if (_top[k].Score < _top[worst].Score) worst = k;
            }
            if (score > _top[worst].Score) { _top[worst].Score = score; _top[worst].X = x; _top[worst].Y = y; }
        }

        // Blend the current look into the template so gradual changes (rotation, lighting)
        // are followed, while a small share of the original look prevents drifting away.
        private void Adapt(int bx, int by, int iw)
        {
            float mean = 0f;
            for (int ty = 0; ty < _th; ty++)
                for (int tx = 0; tx < _tw; tx++)
                    mean += _img[(by + ty) * iw + bx + tx];
            mean /= _n;
            for (int ty = 0; ty < _th; ty++)
            {
                for (int tx = 0; tx < _tw; tx++)
                {
                    int i = ty * _tw + tx;
                    float patch = _img[(by + ty) * iw + bx + tx] - mean;
                    _tmpl[i] = 0.85f * _tmpl[i] + 0.10f * patch + 0.05f * _tmpl0[i];
                }
            }
            ZeroMean(_tmpl, out _tnorm);
        }

        private static void ZeroMean(float[] values, out float norm)
        {
            double mean = 0;
            for (int i = 0; i < values.Length; i++) mean += values[i];
            mean /= values.Length;
            double sq = 0;
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = (float)(values[i] - mean);
                sq += values[i] * values[i];
            }
            norm = (float)Math.Max(Math.Sqrt(sq), 1e-3);
        }

        public void Dispose()
        {
            _running = false;
            if (_thread != null) _thread.Join(1000);
            if (_bufGraphics != null) _bufGraphics.Dispose();
            if (_buf != null) _buf.Dispose();
        }
    }
}
