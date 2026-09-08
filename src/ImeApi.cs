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

        private const uint KLF_ACTIVATE = 0x00000001;

        // 系统已加载的键盘布局列表（用户很少增删语言，缓存 60 秒即可）
        private static IntPtr[] _layoutCache;
        private static int _layoutCacheTick;

        public static IntPtr[] GetLayoutList()
        {
            if (_layoutCache != null && Environment.TickCount - _layoutCacheTick < 60000)
                return _layoutCache;
            int count = (int)GetKeyboardLayoutList(0, null);
            IntPtr[] list = count > 0 ? new IntPtr[count] : new IntPtr[0];
            if (count > 0) GetKeyboardLayoutList(count, list);
            _layoutCache = list;
            _layoutCacheTick = Environment.TickCount;
            return list;
        }

        // 用户已启用的输入法（TIP）条目
        internal struct TipInfo
        {
            public ushort LangId;
            public Guid Clsid;
            public Guid GuidProfile;
        }

        // 从注册表枚举用户已启用的输入法：
        // HKCU\Control Panel\International\User Profile\<语言tag> 下的值名形如
        // "0804:{81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E}{FA550B04-5AD7-411F-A5AC-CA038EC515D7}"
        // 这是"中文锁定"要激活微软拼音/搜狗等具体输入法的唯一信息来源——
        // 系统已加载布局里通常只有 08040804 这种 substitute layout，拿不到输入法本体。
        public static TipInfo[] GetEnabledInputProcessors(ushort langId)
        {
            var list = new List<TipInfo>();
            try
            {
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(
                    @"Control Panel\International\User Profile"))
                {
                    if (root == null) return list.ToArray();
                    foreach (string lang in root.GetSubKeyNames())
                    {
                        using (RegistryKey k = root.OpenSubKey(lang))
                        {
                            if (k == null) continue;
                            foreach (string name in k.GetValueNames())
                            {
                                TipInfo t;
                                if (TryParseTipName(name, langId, out t) && !list.Contains(t))
                                    list.Add(t);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Logger.Throttle("tip-enum", 10000))
                    Logger.Log("Enumerate input processors failed: {0}", ex.Message);
            }
            return list.ToArray();
        }

        private static bool TryParseTipName(string name, ushort langId, out TipInfo info)
        {
            info = new TipInfo();
            if (name == null || name.Length < 6 || name[4] != ':') return false;
            ushort l;
            if (!ushort.TryParse(name.Substring(0, 4),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out l)) return false;
            if (l != langId) return false;

            string rest = name.Substring(5);
            int split = rest.IndexOf('}');
            if (split < 0) return false;
            Guid clsid, guid;
            if (!Guid.TryParse(rest.Substring(0, split + 1), out clsid)) return false;
            if (!Guid.TryParse(rest.Substring(split + 1), out guid)) return false;

            info.LangId = l;
            info.Clsid = clsid;
            info.GuidProfile = guid;
            return true;
        }

        // 英文锁定的候选目标布局（按优先级）：
        //  1. 系统已加载的英文键盘布局（低字 0x0409）—— 唯一可靠目标。
        //     实测：微软拼音处于中文输入模式时，前台线程 HKL 依旧是 08040804
        //     （输入法的 substitute layout），只看 HKL 无法区分中英，
        //     但切到 0409 布局后没有 IME，打字必然是英文。
        //  2. LoadKeyboardLayout 加载美式英文（系统没预装英文键盘时）
        //  3. preferLangId 语言的纯键盘布局（如 08040804）—— 最后的退路：
        //     不能保证切走 IME，但至少把输入法状态拉回该语言的直通模式
        public static IntPtr[] EnglishLayoutCandidates(int preferLangId)
        {
            var list = new List<IntPtr>();

            IntPtr en = FindLayoutByLanguage(0x0409);
            if (en == IntPtr.Zero) en = LoadKeyboardLayout("00000409", KLF_ACTIVATE);
            if (en != IntPtr.Zero) list.Add(en);

            int lang = preferLangId & 0xFFFF;
            if (lang != 0 && lang != 0x0409)
            {
                IntPtr plain = new IntPtr(((long)lang << 16) | (uint)lang);
                if (!list.Contains(plain)) list.Add(plain);
            }
            return list.ToArray();
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
            foreach (IntPtr hkl in GetLayoutList())
            {
                if ((int)(hkl.ToInt64() & 0xFFFF) == langId) return hkl;
            }
            return IntPtr.Zero;
        }

        // 在系统已加载的布局中找某语言的"输入法"布局（HKL 高字 E0xx，如微软拼音/搜狗）。
        // 优先返回真输入法；找不到时退回该语言任意布局（如中文美式键盘），
        // 避免"中文锁定"落到纯键盘布局（打字恒为英文）导致切过去仍是英文
        public static IntPtr FindImeLayoutByLanguage(int langId)
        {
            IntPtr fallback = IntPtr.Zero;
            foreach (IntPtr hkl in GetLayoutList())
            {
                if ((int)(hkl.ToInt64() & 0xFFFF) != langId) continue;
                if (IsImeLayout(hkl)) return hkl;      // 真输入法优先
                if (fallback == IntPtr.Zero) fallback = hkl;
            }
            return fallback;
        }

        // 英文锁定的执行动作：把前台窗口从输入法切到纯键盘布局（打字恒为英文）。
        // hkl 由 ImeLocker 依据 TSF 激活 profile 选出，并在长时间未生效时轮转候选。
        // 三条通道同时走，互为补充：
        //   TSF   —— 对记事本/浏览器/Eclipse 等无 IMM32 上下文的窗口是唯一有效路径
        //   消息  —— "每个应用单独输入法"模式下，TSF 全局激活可能不覆盖该窗口
        //   IMM32 —— 老应用还可能停在中文模式，直接写转换状态
        public static bool ForceEnglishByLayout(IntPtr hwnd, IntPtr hkl)
        {
            if (hkl == IntPtr.Zero) return false;

            TsfProfiles.ActivateKeyboardLayout(hkl);

            uint tid = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
            if (GetKeyboardLayout(tid) != hkl)
                PostMessage(hwnd, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);

            ForceImmEnglish(hwnd);
            return true;
        }

        // IMM32 通道：注意 ImmGetContext 只对**自身进程**的窗口有效，
        // 跨进程调用前台窗口恒返回 0（实测），因此这里通常直接跳过，
        // 真正的切换由 ForceEnglishByLayout 的消息通道完成。保留它是为老应用兜底。
        public static bool ForceImmEnglish(IntPtr hwnd)
        {
            IntPtr hIMC = ImmGetContext(hwnd);
            if (hIMC == IntPtr.Zero) return false;
            try
            {
                if (!ImmGetOpenStatus(hIMC)) return true;           // IME 已关 = 英文直通
                int conversion = 0, sentence = 0;
                if (!ImmGetConversionStatus(hIMC, ref conversion, ref sentence)) return true;
                if (conversion == IME_CMODE_ALPHANUMERIC) return true; // 已是英文模式

                uint tid = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
                IntPtr hkl = GetKeyboardLayout(tid);
                if (IsSystemIme(hkl))
                    ImmSetConversionStatus(hIMC, IME_CMODE_ALPHANUMERIC, sentence);
                else
                    ImmSetOpenStatus(hIMC, false); // 第三方输入法：关闭 IME 让按键直通
                return true;
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
