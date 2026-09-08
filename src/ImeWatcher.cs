using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace InputMethodLock
{
    // 输入法状态变化监听器（即时响应主力，100ms 轮询为兜底）：
    //  1. 低级键盘钩子：只观察不拦截，捕获会触发 IME 状态切换的按键
    //     （Shift 松开=微软拼音中英切换、Ctrl+Space、CapsLock），立即通知恢复
    //  2. SetWinEventHook：系统 IME 状态窗口变化事件（含 Win+Space 布局切换）
    // 回调都跑在安装线程（UI 线程）的消息循环上，直接抛事件是线程安全的
    public class ImeWatcher : IDisposable
    {
        public event Action ChangeDetected;

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_SPACE = 0x20;
        private const int VK_CAPITAL = 0x14;

        private const uint EVENT_OBJECT_IME_SHOW = 0x8027;
        private const uint EVENT_OBJECT_IME_HIDE = 0x8028;
        private const uint EVENT_OBJECT_IME_CHANGE = 0x8029;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private delegate void WinEventProc(IntPtr hHook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwflags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private readonly LowLevelKeyboardProc _kbdProc;   // 防 GC 回收
        private readonly WinEventProc _winEventProc;
        private IntPtr _kbdHook = IntPtr.Zero;
        private IntPtr _eventHook = IntPtr.Zero;
        private readonly Timer _deferred; // 40ms 后补一次恢复：IME 可能在按键处理完才切换状态
        private bool _firing;

        public ImeWatcher()
        {
            _kbdProc = KeyboardProc;
            _winEventProc = OnWinEvent;
            _deferred = new Timer { Interval = 40 };
            _deferred.Tick += delegate { _deferred.Stop(); Fire(); };
        }

        public void Start()
        {
            if (_kbdHook == IntPtr.Zero)
                _kbdHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbdProc, GetModuleHandle(null), 0);
            if (_eventHook == IntPtr.Zero)
                _eventHook = SetWinEventHook(EVENT_OBJECT_IME_SHOW, EVENT_OBJECT_IME_CHANGE,
                    IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        }

        public void Stop()
        {
            if (_kbdHook != IntPtr.Zero) { UnhookWindowsHookEx(_kbdHook); _kbdHook = IntPtr.Zero; }
            if (_eventHook != IntPtr.Zero) { UnhookWinEvent(_eventHook); _eventHook = IntPtr.Zero; }
            _deferred.Stop();
        }

        private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // 低级钩子回调来自 native 层，异常无法跨边界传播会直接终止进程；
            // 且 CallNextHookEx 必须执行，否则全局键盘输入被卡死
            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    int vk = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode 首字段
                    bool relevant = false;
                    if (vk == VK_SHIFT || vk == VK_CAPITAL)
                    {
                        relevant = (msg == WM_KEYUP || msg == WM_SYSKEYUP); // 松开时 IME 才切换
                    }
                    else if (vk == VK_SPACE)
                    {
                        relevant = (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                            && (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0; // Ctrl+Space
                    }
                    if (relevant)
                    {
                        Fire();
                        _deferred.Stop();
                        _deferred.Start(); // 延迟补一次，避开 IME 处理时序
                    }
                }
            }
            catch { }
            return CallNextHookEx(_kbdHook, nCode, wParam, lParam); // 永不吞键
        }

        private void OnWinEvent(IntPtr hHook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            try { Fire(); } catch { }
        }

        private void Fire()
        {
            if (_firing) return; // 防重入（Enforce 可能触发 IME 事件）
            _firing = true;
            try
            {
                var handler = ChangeDetected;
                if (handler != null) handler();
            }
            finally
            {
                _firing = false;
            }
        }

        public void Dispose()
        {
            Stop();
            _deferred.Dispose();
        }
    }
}
