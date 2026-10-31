using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace ClipRecorder
{
    internal sealed class SelectionResult : IDisposable
    {
        public Screen Screen;
        public Rectangle Region;      // absolute screen pixels
        public TrackingMode Mode;
        public Rectangle ObjectRect;  // absolute screen pixels (only for Objeto)
        public Bitmap Snapshot;       // frozen frame of Screen, used to learn the object's look

        public void Dispose()
        {
            if (Snapshot != null) { Snapshot.Dispose(); Snapshot = null; }
        }
    }

    // Snipping-Tool-style picker: freezes the screen, the user drags the area to record,
    // and (in object mode) then drags a box around the thing the camera should follow.
    internal sealed class SelectionOverlay : Form
    {
        private readonly Screen _screen;
        private Bitmap _shot;
        private readonly Bitmap _dim;
        private readonly Font _hintFont;
        private readonly Font _labelFont;

        private TrackingMode _mode;
        private int _phase;               // 0 = area to record, 1 = object to follow
        private bool _dragging;
        private Point _start, _current;
        private Rectangle _region;        // client coordinates
        private Rectangle _lastDirty;

        public SelectionResult Result { get; private set; }

        public SelectionOverlay(Screen screen, TrackingMode defaultMode)
        {
            _screen = screen;
            _mode = defaultMode;
            Rectangle b = screen.Bounds;

            _shot = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(_shot))
                g.CopyFromScreen(b.Location, Point.Empty, b.Size, CopyPixelOperation.SourceCopy);

            _dim = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(_dim))
            {
                g.DrawImageUnscaled(_shot, 0, 0);
                using (var shade = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                    g.FillRectangle(shade, 0, 0, b.Width, b.Height);
            }

            _hintFont = new Font("Segoe UI", 11f, FontStyle.Regular);
            _labelFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = b;
            TopMost = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            Cursor = Cursors.Cross;
            BackColor = Color.Black;
            Text = "ClipRecorder - Seleccion";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            Native.SetForegroundWindow(Handle);
            Focus();
        }

        protected override void WndProc(ref Message m)
        {
            // The overlay is placed in physical pixels; ignore DPI-change auto-resizing.
            if (m.Msg == Native.WM_DPICHANGED) return;
            base.WndProc(ref m);
        }

        private static Rectangle Normalize(Point a, Point b)
        {
            return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { Cancel(); return; }
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            _start = _current = e.Location;
            Capture = true;
            RefreshDirty();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_dragging) return;
            _current = e.Location;
            RefreshDirty();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!_dragging || e.Button != MouseButtons.Left) return;
            _dragging = false;
            Capture = false;
            Rectangle rect = Normalize(_start, e.Location);

            if (_phase == 0)
            {
                // A plain click (no drag) means "record the whole screen".
                _region = (rect.Width < 8 || rect.Height < 8) ? ClientRectangle : rect;
                bool fullScreen = _region.Width >= ClientSize.Width - 2 && _region.Height >= ClientSize.Height - 2;
                if (_mode == TrackingMode.Objeto && !fullScreen)
                {
                    _phase = 1;
                    Invalidate();
                    return;
                }
                Finish(Rectangle.Empty);
            }
            else
            {
                // A plain click on the object picks a box around the click point.
                if (rect.Width < 8 || rect.Height < 8)
                    rect = new Rectangle(e.X - 40, e.Y - 40, 80, 80);
                rect.Intersect(ClientRectangle);
                if (rect.Width < 8 || rect.Height < 8) return;
                Finish(rect);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Escape:
                    Cancel();
                    break;
                case Keys.Enter:
                    if (_phase == 0) { _region = ClientRectangle; Finish(Rectangle.Empty); }
                    else { _mode = TrackingMode.Ninguno; Finish(Rectangle.Empty); }
                    break;
                case Keys.N:
                    if (_phase == 0) { _mode = TrackingMode.Ninguno; Invalidate(); }
                    break;
                case Keys.M:
                    if (_phase == 0) { _mode = TrackingMode.Mouse; Invalidate(); }
                    break;
                case Keys.O:
                    if (_phase == 0) { _mode = TrackingMode.Objeto; Invalidate(); }
                    break;
            }
        }

        private void Cancel()
        {
            Result = null;
            DialogResult = DialogResult.Cancel;
            Close();
        }

        private void Finish(Rectangle objectClient)
        {
            Point origin = _screen.Bounds.Location;
            var result = new SelectionResult
            {
                Screen = _screen,
                Region = new Rectangle(_region.X + origin.X, _region.Y + origin.Y, _region.Width, _region.Height),
                Mode = _mode,
                Snapshot = _shot
            };
            if (_mode == TrackingMode.Objeto && !objectClient.IsEmpty)
                result.ObjectRect = new Rectangle(objectClient.X + origin.X, objectClient.Y + origin.Y, objectClient.Width, objectClient.Height);
            else if (_mode == TrackingMode.Objeto)
                result.Mode = TrackingMode.Ninguno;
            _shot = null; // ownership moves to the result
            Result = result;
            DialogResult = DialogResult.OK;
            Close();
        }

        private Rectangle CurrentDrag()
        {
            return _dragging ? Normalize(_start, _current) : Rectangle.Empty;
        }

        // Repaint only what changed so dragging stays smooth even on 4K screens.
        private void RefreshDirty()
        {
            Rectangle now = CurrentDrag();
            now.Inflate(4, 4);
            now = Rectangle.Union(now, new Rectangle(now.X, now.Bottom, 200, 40));
            Rectangle dirty = _lastDirty.IsEmpty ? now : Rectangle.Union(_lastDirty, now);
            _lastDirty = now;
            Invalidate(dirty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(_dim, e.ClipRectangle, e.ClipRectangle, GraphicsUnit.Pixel);
            g.CompositingMode = CompositingMode.SourceOver;

            if (_phase == 1)
            {
                DrawBright(g, _region, Color.FromArgb(255, 47, 169, 255), false);
                Rectangle obj = CurrentDrag();
                if (!obj.IsEmpty) DrawBright(g, obj, Color.Gold, true);
            }
            else
            {
                Rectangle sel = CurrentDrag();
                if (!sel.IsEmpty) DrawBright(g, sel, Color.FromArgb(255, 47, 169, 255), true);
            }
            DrawHint(g);
        }

        private void DrawBright(Graphics g, Rectangle r, Color color, bool showSize)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            if (_shot != null) g.DrawImage(_shot, r, r, GraphicsUnit.Pixel);
            using (var pen = new Pen(color, 2f)) g.DrawRectangle(pen, r);
            if (!showSize) return;
            string text = r.Width + " x " + r.Height;
            SizeF sz = g.MeasureString(text, _labelFont);
            var box = new RectangleF(r.X, r.Bottom + 4, sz.Width + 8, sz.Height + 2);
            using (var bg = new SolidBrush(Color.FromArgb(200, 20, 20, 20))) g.FillRectangle(bg, box);
            using (var fg = new SolidBrush(Color.White)) g.DrawString(text, _labelFont, fg, box.X + 4, box.Y + 1);
        }

        private void DrawHint(Graphics g)
        {
            string line1, line2;
            if (_phase == 0)
            {
                line1 = "Arrastrá para elegir el área a grabar   ·   Clic sin arrastrar o Enter = pantalla completa   ·   Esc = cancelar";
                line2 = "Seguimiento:  [N] Ninguno    [M] Seguir el mouse    [O] Seguir un objeto          Actual: " + ModeLabel(_mode);
            }
            else
            {
                line1 = "Ahora marcá el OBJETO a seguir: arrastrá un recuadro sobre él (o hacé clic encima)";
                line2 = "Enter = grabar sin seguimiento   ·   Esc = cancelar";
            }
            SizeF s1 = g.MeasureString(line1, _hintFont);
            SizeF s2 = g.MeasureString(line2, _hintFont);
            float w = Math.Max(s1.Width, s2.Width) + 32;
            float h = s1.Height + s2.Height + 20;
            float x = (ClientSize.Width - w) / 2f;
            float y = 24;
            using (var bg = new SolidBrush(Color.FromArgb(215, 18, 18, 22))) g.FillRectangle(bg, x, y, w, h);
            using (var accent = new SolidBrush(_phase == 0 ? Color.FromArgb(255, 47, 169, 255) : Color.Gold)) g.FillRectangle(accent, x, y, 4, h);
            using (var fg = new SolidBrush(Color.White))
            {
                g.DrawString(line1, _hintFont, fg, x + 16, y + 8);
                g.DrawString(line2, _hintFont, fg, x + 16, y + 10 + s1.Height);
            }
        }

        private static string ModeLabel(TrackingMode mode)
        {
            switch (mode)
            {
                case TrackingMode.Mouse: return "seguir el mouse";
                case TrackingMode.Objeto: return "seguir un objeto";
                default: return "ninguno (área fija)";
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_shot != null) _shot.Dispose();
                _dim.Dispose();
                _hintFont.Dispose();
                _labelFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
