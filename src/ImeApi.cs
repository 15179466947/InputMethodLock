using System;
using System.Runtime.InteropServices;

namespace InputMethodLock
{
    // Windows 输入法相关 API 封装
    internal static class ImeApi
    {
        public const int IME_CMODE_ALPHANUMERIC = 0x0000;
        public const int IME_CMODE_NATIVE = 0x0001;

        // 请求前台窗口切换键盘布局（对其他进程的布局切换只能走这条消息）
        public const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

        [DllImport("user32.dll")]
        public static extern uint GetKeyboardLayoutList(int nBuff, IntPtr[] lpList);

        [DllImport("user32.dll")]
        public static extern IntPtr GetKeyboardLayout(uint idThread);

        [DllImport("user32.dll")]
        public static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint Flags);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("imm32.dll")]
        public static extern IntPtr ImmGetContext(IntPtr hWnd);

        [DllImport("imm32.dll")]
        public static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

        [DllImport("imm32.dll")]
        public static extern bool ImmGetConversionStatus(IntPtr hIMC, ref int lpfdwConversion, ref int lpfdwSentence);

        [DllImport("imm32.dll")]
        public static extern bool ImmSetConversionStatus(IntPtr hIMC, int fdwConversion, int fdwSentence);

        [DllImport("imm32.dll")]
        public static extern bool ImmGetOpenStatus(IntPtr hIMC);

        [DllImport("imm32.dll")]
        public static extern bool ImmSetOpenStatus(IntPtr hIMC, bool fOpen);

        // pid → 进程名缓存：轮询 100ms + 每次按键都会触发 Enforce，
        // Process.GetProcessById 是重量级调用，绝大多数时候前台进程没变
        private static uint _cachePid;
        private static string _cacheName;
        private static int _cacheTick;

        // 取得前台窗口所属进程名（小写，不含扩展名）；传入 hwnd 避免二次采样竞态
        public static string GetForegroundProcessName(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;
            try
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == 0) return null;
                if (pid == _cachePid && Environment.TickCount - _cacheTick < 1000)
                    return _cacheName;

                string name = null;
                using (System.Diagnostics.Process proc = System.Diagnostics.Process.GetProcessById((int)pid))
                {
                    if (proc != null)
                    {
                        string n = proc.ProcessName;
                        if (!string.IsNullOrEmpty(n)) name = n.ToLowerInvariant();
                    }
                }
                _cachePid = pid;
                _cacheName = name;
                _cacheTick = Environment.TickCount;
                return name;
            }
            catch
            {
                return null; // 进程已退出等场景；不写缓存，避免脏数据
            }
        }

        // 在系统已加载的键盘布局中找指定语言（低字 langId，如 0x0804=简体中文）的布局
        public static IntPtr FindLayoutByLanguage(int langId)
        {
            int count = (int)GetKeyboardLayoutList(0, null);
            if (count <= 0) return IntPtr.Zero;
            IntPtr[] hkls = new IntPtr[count];
            GetKeyboardLayoutList(count, hkls);
            foreach (IntPtr hkl in hkls)
            {
                if ((int)(hkl.ToInt64() & 0xFFFF) == langId) return hkl;
            }
            return IntPtr.Zero;
        }

        // 强制前台窗口的输入法进入英文（字母数字）模式
        public static bool ForceEnglishMode(IntPtr hwnd)
        {
            IntPtr hIMC = ImmGetContext(hwnd);
            if (hIMC == IntPtr.Zero) return true; // 纯英文键盘无 IME 上下文，目标已达成
            try
            {
                int conversion = 0, sentence = 0;
                if (!ImmGetConversionStatus(hIMC, ref conversion, ref sentence)) return false;
                if (conversion == IME_CMODE_ALPHANUMERIC) return true; // 已是英文
                return ImmSetConversionStatus(hIMC, IME_CMODE_ALPHANUMERIC, sentence);
            }
            finally
            {
                ImmReleaseContext(hwnd, hIMC);
            }
        }

        // 强制前台窗口的输入法进入中文（母语）模式
        public static bool ForceNativeMode(IntPtr hwnd)
        {
            IntPtr hIMC = ImmGetContext(hwnd);
            if (hIMC == IntPtr.Zero) return false; // 纯英文键盘无 IME 上下文，无法锁定中文
            try
            {
                if (!ImmGetOpenStatus(hIMC) && !ImmSetOpenStatus(hIMC, true)) return false;
                int conversion = 0, sentence = 0;
                if (!ImmGetConversionStatus(hIMC, ref conversion, ref sentence)) return false;
                if ((conversion & IME_CMODE_NATIVE) != 0) return true; // 已是中文
                conversion |= IME_CMODE_NATIVE;
                return ImmSetConversionStatus(hIMC, conversion, sentence);
            }
            finally
            {
                ImmReleaseContext(hwnd, hIMC);
            }
        }
    }
}
