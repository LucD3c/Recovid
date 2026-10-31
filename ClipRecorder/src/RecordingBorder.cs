using System;
using System.Drawing;
using System.Windows.Forms;

namespace ClipRecorder
{
    // Thin frame drawn just OUTSIDE the recorded area: yellow while the encoder starts,
    // red the exact moment frames are being captured. It is click-through and excluded
    // from screen capture, so it never shows up in the video.
    internal sealed class RecordingBorder : Form
    {
        private const int Thickness = 3;
        private Size _innerSize = Size.Empty;

        public RecordingBorder()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.Gold;
            Opacity = 0.95; // makes the window layered, required for click-through
            Text = "ClipRecorder - Grabando";
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_DPICHANGED) return;
            base.WndProc(ref m);
        }

        public void Place(Rectangle recorded)
        {
            Rectangle outer = Rectangle.Inflate(recorded, Thickness, Thickness);
            if (recorded.Size != _innerSize)
            {
                _innerSize = recorded.Size;
                var region = new Region(new Rectangle(0, 0, outer.Width, outer.Height));
                region.Exclude(new Rectangle(Thickness, Thickness, recorded.Width, recorded.Height));
                Region old = Region;
                Bounds = outer;
                Region = region;
                if (old != null) old.Dispose();
            }
            else if (Location != outer.Location)
            {
                Location = outer.Location;
            }
        }

        public void SetRecording(bool recording)
        {
            BackColor = recording ? Color.Red : Color.Gold;
        }
    }
}
