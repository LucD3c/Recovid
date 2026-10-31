using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ClipRecorder
{
    internal sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private const int HotkeyId = 0xC11;
        private bool _registered;

        public event EventHandler Pressed;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams());
        }

        public bool Register(string hotkey, out string error)
        {
            Unregister();
            uint mods, vk;
            if (!HotkeyText.TryParse(hotkey, out mods, out vk))
            {
                error = "El atajo '" + hotkey + "' no es válido.";
                return false;
            }
            _registered = Native.RegisterHotKey(Handle, HotkeyId, mods | Native.MOD_NOREPEAT, vk);
            error = _registered ? null : "Otra aplicación ya usa el atajo " + hotkey + " (código " + Marshal.GetLastWin32Error() + ").";
            return _registered;
        }

        public void Unregister()
        {
            if (!_registered) return;
            Native.UnregisterHotKey(Handle, HotkeyId);
            _registered = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            {
                var handler = Pressed;
                if (handler != null) handler(this, EventArgs.Empty);
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            Unregister();
            DestroyHandle();
        }
    }

    internal static class HotkeyText
    {
        public static bool TryParse(string text, out uint mods, out uint vk)
        {
            mods = 0;
            vk = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] parts = text.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim().ToLowerInvariant();
                bool last = i == parts.Length - 1;
                if (p == "ctrl" || p == "control") mods |= Native.MOD_CONTROL;
                else if (p == "alt") mods |= Native.MOD_ALT;
                else if (p == "shift" || p == "mayus") mods |= Native.MOD_SHIFT;
                else if (p == "win" || p == "windows") mods |= Native.MOD_WIN;
                else if (last)
                {
                    Keys key;
                    if (!Enum.TryParse(parts[i].Trim(), true, out key)) return false;
                    vk = (uint)key;
                }
                else return false;
            }
            return vk != 0;
        }

        public static string FromKeys(Keys modifiers, Keys key)
        {
            var parts = new List<string>();
            if ((modifiers & Keys.Control) != 0) parts.Add("Ctrl");
            if ((modifiers & Keys.Alt) != 0) parts.Add("Alt");
            if ((modifiers & Keys.Shift) != 0) parts.Add("Shift");
            parts.Add(key.ToString());
            return string.Join("+", parts.ToArray());
        }
    }
}
