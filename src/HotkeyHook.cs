using System;
using System.Windows.Forms;

namespace InputMethodLock
{
    // 隐藏消息窗口：负责接收全局热键 WM_HOTKEY
    public class HotkeyWindow : NativeWindow, IDisposable
    {
        public const int WM_HOTKEY = 0x0312;
        public const int HOTKEY_ID = 0xB00B;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public event Action HotkeyPressed;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams());
        }

        public bool Register(uint modifiers, uint vk)
        {
            Unregister();
            return RegisterHotKey(Handle, HOTKEY_ID, modifiers, vk);
        }

        public void Unregister()
        {
            if (Handle != IntPtr.Zero)
                UnregisterHotKey(Handle, HOTKEY_ID);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
            {
                var handler = HotkeyPressed;
                if (handler != null) handler();
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            Unregister();
            DestroyHandle();
        }
    }

    // 修饰键/键值解析工具
    public static class HotkeyHelper
    {
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_NOREPEAT = 0x4000;

        public static uint ParseModifiers(string text)
        {
            uint mod = MOD_NOREPEAT;
            if (string.IsNullOrEmpty(text)) return mod;
            foreach (string part in text.Split('+'))
            {
                string p = part.Trim();
                if (string.Equals(p, "Control", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p, "Ctrl", StringComparison.OrdinalIgnoreCase))
                    mod |= MOD_CONTROL;
                else if (string.Equals(p, "Alt", StringComparison.OrdinalIgnoreCase))
                    mod |= MOD_ALT;
                else if (string.Equals(p, "Shift", StringComparison.OrdinalIgnoreCase))
                    mod |= MOD_SHIFT;
            }
            return mod;
        }

        public static uint ParseKey(string name)
        {
            try
            {
                return (uint)(int)Enum.Parse(typeof(Keys), name, true);
            }
            catch
            {
                // 解析失败返回 None（不注册热键）；兜底成具体键会静默注册错误热键
                return (uint)Keys.None;
            }
        }

        public static string ModifiersText(uint mod)
        {
            var parts = new System.Collections.Generic.List<string>();
            if ((mod & MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((mod & MOD_ALT) != 0) parts.Add("Alt");
            if ((mod & MOD_SHIFT) != 0) parts.Add("Shift");
            return parts.Count > 0 ? string.Join("+", parts.ToArray()) : "无";
        }

        // Keys 枚举 → 人类可读按键名（如 Oemtilde → `）
        public static string KeyToText(Keys key)
        {
            if (key >= Keys.A && key <= Keys.Z) return key.ToString();
            if (key >= Keys.D0 && key <= Keys.D9) return ((int)(key - Keys.D0)).ToString();
            if (key >= Keys.NumPad0 && key <= Keys.NumPad9) return "Num" + ((int)(key - Keys.NumPad0));
            if (key >= Keys.F1 && key <= Keys.F24) return key.ToString();
            switch (key)
            {
                case Keys.Oemtilde: return "`";
                case Keys.OemMinus: return "-";
                case Keys.Oemplus: return "=";
                case Keys.OemOpenBrackets: return "[";
                case Keys.OemCloseBrackets: return "]";
                case Keys.OemSemicolon: return ";";
                case Keys.OemQuotes: return "'";
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.OemQuestion: return "/";
                case Keys.OemPipe: return "\\";
                case Keys.Space: return "Space";
                case Keys.Insert: return "Insert";
                case Keys.Delete: return "Delete";
                case Keys.Home: return "Home";
                case Keys.End: return "End";
                case Keys.PageUp: return "PgUp";
                case Keys.PageDown: return "PgDn";
                case Keys.Scroll: return "Scroll";
                case Keys.Pause: return "Pause";
                default: return key.ToString();
            }
        }
    }
}
