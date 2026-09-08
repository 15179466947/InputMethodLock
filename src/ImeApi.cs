using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

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

        [DllImport("user32.dll")]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        // —— SendInput 结构（Shift 模拟切换用） ——
        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public INPUTUNION U; }

        private const ushort VK_SHIFT = 0x10;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint INPUT_KEYBOARD = 1;

        // 是否为输入法布局（HKL 高字 0xE000~0xEFFF，如微软拼音 E0200804、搜狗 E0xx0804）；
        // 非输入法布局（高位=低位，如美式键盘 04090409、中文美式键盘 08040804）打字恒为英文
        public static bool IsImeLayout(IntPtr hkl)
        {
            return ((hkl.ToInt64() >> 16) & 0xF000) == 0xE000;
        }

        private static IntPtr _englishHkl;

        // 美式英文键盘（00000409）：TSF 应用下英文锁定的强制目标
        public static IntPtr GetEnglishLayout()
        {
            if (_englishHkl == IntPtr.Zero)
                _englishHkl = LoadKeyboardLayout("00000409", 0x00000001 /*KLF_ACTIVATE*/);
            return _englishHkl;
        }

        // pid→判断结果缓存：前台布局的输入法是否为系统内置（微软）输入法。
        // 依据：注册表 Keyboard Layouts\<KLID>\Ime File 是否位于 System32——
        // 微软内置 IME 的文件都在 System32，第三方（搜狗等）在各自安装目录
        private static readonly Dictionary<long, bool> _systemImeCache = new Dictionary<long, bool>();

        public static bool IsSystemIme(IntPtr hkl)
        {
            long key = hkl.ToInt64();
            bool cached;
            if (_systemImeCache.TryGetValue(key, out cached)) return cached;
            bool isSystem = true;
            string imeFile = null;
            try
            {
                string klid = key.ToString("X8");
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Keyboard Layouts\" + klid))
                {
                    object v = k != null ? k.GetValue("Ime File") : null;
                    imeFile = v as string;
                    if (!string.IsNullOrEmpty(imeFile))
                    {
                        isSystem = File.Exists(Path.Combine(Environment.SystemDirectory, imeFile));
                    }
                    // Ime File 缺失时默认按系统输入法处理——此默认值存疑，记录现场便于定位
                    Logger.Log("IsSystemIme klid={0}: ImeFile='{1}' -> system={2}",
                        klid, imeFile ?? "(missing)", isSystem);
                }
            }
            catch { }
            _systemImeCache[key] = isSystem;
            return isSystem;
        }

        // 模拟一次 Shift 按下/抬起：搜狗、微软拼音等都把 Shift 作为自带中英切换键，
        // 当外部状态写入被无视时，让输入法"自己切"是最后的兜底（1 秒限速防抖）
        private static int _lastShiftTick;

        private static void TapShiftIfAllowed(IntPtr hwnd)
        {
            int now = Environment.TickCount;
            if (now - _lastShiftTick < 1000) return;
            _lastShiftTick = now;

            INPUT[] inp = new INPUT[2];
            inp[0].type = INPUT_KEYBOARD;
            inp[0].U.ki.wVk = VK_SHIFT;
            inp[1].type = INPUT_KEYBOARD;
            inp[1].U.ki.wVk = VK_SHIFT;
            inp[1].U.ki.dwFlags = KEYEVENTF_KEYUP;
            SendInput(2, inp, Marshal.SizeOf(typeof(INPUT)));
            Logger.Log("IME ignored conversion write, Shift tap sent (proc={0})",
                GetForegroundProcessName(hwnd) ?? "?");
        }

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

        // 英文锁定状态变化日志：只在 (进程|布局|hIMC|开关|转换状态) 元组变化时记一条，
        // 用于定位"锁定无效但日志全空"的静默短路分支
        private static string _lastEngState;

        private static void LogEnglishState(IntPtr hwnd, IntPtr hkl, string state, string result)
        {
            try
            {
                string klid = hkl == IntPtr.Zero ? "-" : hkl.ToInt64().ToString("X8");
                string sig = (GetForegroundProcessName(hwnd) ?? "-") + "|" + klid + "|" + state + "|" + result;
                if (sig == _lastEngState) return;
                _lastEngState = sig;
                Logger.Log("EnglishLock: proc={0} klid={1} state={2} -> {3}",
                    GetForegroundProcessName(hwnd) ?? "-", klid, state, result);
            }
            catch { }
        }

        // 强制前台窗口的输入法进入英文（字母数字）模式。
        // v0.10 关键修正：记事本/浏览器/资源管理器等 TSF 应用拿不到 IMM32 上下文
        // （hIMC=NULL），转换状态读写全部失效——此时改用布局级控制：
        // 前台挂输入法布局（HKL 高字 E0xx）就强制切美式键盘；纯键盘布局打字恒为英文
        public static bool ForceEnglishMode(IntPtr hwnd)
        {
            IntPtr hIMC = ImmGetContext(hwnd);
            if (hIMC == IntPtr.Zero)
            {
                // TSF 应用：IMM32 通道不存在。v0.11：GetKeyboardLayout 也看不到 TSF
                // 输入法（搜狗激活时 HKL 仍是纯键盘布局），必须读 TSF 激活 profile
                TsfProfile active;
                if (TsfProfiles.GetActiveProfile(out active))
                {
                    if (active.IsInputProcessor)
                    {
                        // 激活的是输入法（搜狗/微软拼音）：切到该语言的纯键盘布局 → 英文直通
                        ushort langid = active.langid != 0 ? active.langid : (ushort)0x0804;
                        IntPtr hklDefault = new IntPtr(((long)langid << 16) | langid);
                        bool ok = TsfProfiles.ActivateKeyboardLayoutProfile(langid, hklDefault);
                        LogEnglishState(hwnd, hklDefault, "tsf,tip-active",
                            "activate-keyboard-layout->" + (ok ? "ok" : "failed"));
                        return true; // 异步生效，下个轮询周期复查
                    }
                    LogEnglishState(hwnd, active.hkl, "tsf,keyboard-layout-active", "already-english");
                    return true; // 已是纯键盘布局 = 英文
                }
                // TSF 通道失败（COM 异常已记日志）→ 退回 HKL 判断兜底
                uint tid = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
                IntPtr hkl = GetKeyboardLayout(tid);
                if (IsImeLayout(hkl))
                {
                    IntPtr en = GetEnglishLayout();
                    PostMessage(hwnd, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, en);
                    LogEnglishState(hwnd, hkl, "no-hIMC,ime-layout", "switch-to-english-layout");
                    return true;
                }
                LogEnglishState(hwnd, hkl, "no-hIMC,plain-layout", "already-english");
                return true;
            }
            try
            {
                bool open = ImmGetOpenStatus(hIMC);
                if (!open)
                {
                    LogEnglishState(hwnd, IntPtr.Zero, "ime-closed", "english-passthrough");
                    return true; // IME 已关闭 = 英文直通
                }
                int conversion = 0, sentence = 0;
                if (!ImmGetConversionStatus(hIMC, ref conversion, ref sentence))
                {
                    LogEnglishState(hwnd, IntPtr.Zero, "conv-read-failed", "treated-as-english");
                    return true;
                }
                if (conversion == IME_CMODE_ALPHANUMERIC)
                {
                    LogEnglishState(hwnd, IntPtr.Zero, "already-english", "ok");
                    return true; // 已是英文
                }

                uint tid = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
                IntPtr hkl = GetKeyboardLayout(tid);
                bool sysIme = IsSystemIme(hkl);
                bool ok;
                if (sysIme)
                {
                    ok = ImmSetConversionStatus(hIMC, IME_CMODE_ALPHANUMERIC, sentence);
                    LogEnglishState(hwnd, hkl, "open,conv=" + conversion,
                        "conv-write(system-ime)->" + (ok ? "ok" : "failed"));
                }
                else
                {
                    // 第三方输入法：关闭 IME → 按键直通英文
                    ok = ImmSetOpenStatus(hIMC, false) && !ImmGetOpenStatus(hIMC);
                    LogEnglishState(hwnd, hkl, "open,conv=" + conversion,
                        "close-ime(3rd-party)->" + (ok ? "ok" : "failed"));
                }
                return ok;
            }
            finally
            {
                ImmReleaseContext(hwnd, hIMC);
            }
        }

        // 强制前台窗口的输入法进入中文（母语）模式。
        // 第三方输入法无视转换状态写入时，模拟一次 Shift 让它自己切回中文（v0.9）
        public static bool ForceNativeMode(IntPtr hwnd)
        {
            IntPtr hIMC = ImmGetContext(hwnd);
            if (hIMC == IntPtr.Zero)
            {
                // TSF 应用：中文输入法已激活但中英状态不可读写（v0.11），
                // 盲发 Shift 兜底（1 秒限速），让输入法自己切回中文
                TapShiftIfAllowed(hwnd);
                return true;
            }
            try
            {
                if (!ImmGetOpenStatus(hIMC) && !ImmSetOpenStatus(hIMC, true)) return false;
                int conversion = 0, sentence = 0;
                if (!ImmGetConversionStatus(hIMC, ref conversion, ref sentence)) return false;
                if ((conversion & IME_CMODE_NATIVE) != 0) return true; // 已是中文

                if (!ImmSetConversionStatus(hIMC, conversion | IME_CMODE_NATIVE, sentence))
                {
                    TapShiftIfAllowed(hwnd); // 写入直接失败
                    return true;
                }
                // 写入被接受但读回仍是英文 → 输入法无视外部写入，Shift 让它自己切
                int check = 0, sentence2 = 0;
                if (ImmGetConversionStatus(hIMC, ref check, ref sentence2)
                    && (check & IME_CMODE_NATIVE) == 0)
                {
                    TapShiftIfAllowed(hwnd);
                }
                return true;
            }
            finally
            {
                ImmReleaseContext(hwnd, hIMC);
            }
        }
    }
}
