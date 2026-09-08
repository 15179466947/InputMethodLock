using System;
using System.Collections.Generic;
using System.Threading;

namespace InputMethodLock
{
    public enum LockMode
    {
        English,   // 英文锁定：保留当前输入法，仅强制中英状态为英文
        Chinese,   // 中文锁定：保留当前输入法，强制为中文状态
        Layout     // 布局锁定：强制指定键盘布局
    }

    // 核心锁定器：定时检查前台窗口，强制目标输入法状态
    public class ImeLocker : IDisposable
    {
        private readonly System.Windows.Forms.Timer _timer;
        private LockMode _mode = LockMode.English;
        private IntPtr _targetHkl = IntPtr.Zero;
        private IntPtr _chineseHkl = IntPtr.Zero; // 中文锁定时，前台无 IME 上下文的兜底布局
        private HashSet<string> _exceptions = new HashSet<string>();
        private bool _lastLockedState;
        private IntPtr _lastLockedHwnd; // 最近一次执行锁定的前台窗口（停用恢复用）

        // 停用锁定后要把输入法切回去的目标窗口（托盘/资源管理器不是用户真正在用的窗口）
        public IntPtr LastLockedHwnd { get { return _lastLockedHwnd; } }

        public bool Enabled { get; set; }
        public bool IsLockedNow { get { return _lastLockedState; } }

        public event Action LockedStateChanged;

        public ImeLocker()
        {
            _timer = new System.Windows.Forms.Timer();
            // 100ms 兜底轮询：即时响应由 ImeWatcher（键盘钩子 + IME 事件）负责，
            // 轮询覆盖事件收不到的场景（全屏游戏、远程会话）
            _timer.Interval = 100;
            _timer.Tick += OnTick;
        }

        public void Start()
        {
            Enabled = true;
            _timer.Start();
            UpdateState(true);
        }

        public void Stop()
        {
            Enabled = false;
            _timer.Stop();
            UpdateState(true);
        }

        public void SetMode(LockMode mode) { _mode = mode; }

        public void SetTargetLayout(IntPtr hkl) { _targetHkl = hkl; }

        public void SetChineseFallbackLayout(IntPtr hkl) { _chineseHkl = hkl; }

        public void SetExceptions(IEnumerable<string> processNames)
        {
            _exceptions = new HashSet<string>();
            if (processNames == null) return;
            foreach (string name in processNames)
            {
                string n = (name ?? "").Trim().ToLowerInvariant();
                if (n.Length > 0) _exceptions.Add(n);
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            Enforce();
        }

        // 对前台窗口执行一次锁定检查
        public void Enforce()
        {
            if (!Enabled) { UpdateState(false); return; }

            IntPtr hwnd = ImeApi.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) { UpdateState(false); return; }

            string proc = ImeApi.GetForegroundProcessName(hwnd);
            if (proc != null && _exceptions.Contains(proc))
            {
                UpdateState(false);
                return;
            }

            // 记住用户实际在用的窗口：停用锁定时要向**这个**窗口发恢复消息。
            // 直接取当时的前台窗口会拿到托盘/资源管理器，恢复就发错了地方。
            _lastLockedHwnd = hwnd;

            bool ok;
            if (_mode == LockMode.Layout)
            {
                // 目标布局解析失败（Zero）时保持布局锁定语义，静默降级成英文锁定
                // 会让用户以为在锁布局；此处报告未锁定
                if (_targetHkl == IntPtr.Zero) { UpdateState(false); return; }

                // 注意：ActivateKeyboardLayout 只影响自己线程；对前台窗口
                // 必须发 WM_INPUTLANGCHANGEREQUEST 才能真正切走它的布局。
                uint tid = ImeApi.GetWindowThreadProcessId(hwnd, IntPtr.Zero);
                IntPtr current = ImeApi.GetKeyboardLayout(tid);
                if (current != _targetHkl)
                    ImeApi.PostMessage(hwnd, ImeApi.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, _targetHkl);
                ok = true; // 异步生效，下个轮询周期复查
            }
            else if (_mode == LockMode.Chinese)
            {
                ok = ForceChinese(hwnd);
            }
            else
            {
                ok = ForceEnglish(hwnd);
            }
            UpdateState(ok);
        }

        // —— 中文锁定 ——
        // 能做的：确保目标输入法（微软拼音/搜狗等）处于激活状态，从别的语言切回来。
        // 做不到的：跨进程读不到输入法自身的"中/英模式"（实测 WM_IME_CONTROL 读回恒为 0、
        // GetActiveLanguageProfile 对已启用输入法恒返回 S_OK），所以输入法激活后
        // 是中文还是英文模式，仍由用户/输入法自行维持。
        private ImeApi.TipInfo _chineseTip;
        private bool _chineseTipValid;
        private string _chnLogSig;

        // 中文锁定的目标输入法（设置界面指定；未指定时自动取已启用列表的第一个，
        // 这样换一台装了别的输入法的机器也能直接用）
        internal void SetChineseTarget(ImeApi.TipInfo tip, bool valid)
        {
            _chineseTip = tip;
            _chineseTipValid = valid;
        }

        private bool ForceChinese(IntPtr hwnd)
        {
            uint tid = ImeApi.GetWindowThreadProcessId(hwnd, IntPtr.Zero);
            IntPtr current = ImeApi.GetKeyboardLayout(tid);

            ImeApi.TipInfo tip = _chineseTip;
            bool valid = _chineseTipValid;
            if (!valid)
            {
                ImeApi.TipInfo[] all = ImeApi.GetEnabledInputProcessors();
                if (all.Length > 0) { tip = all[0]; valid = true; }
            }

            IntPtr target = valid ? tip.Hkl : _chineseHkl;
            if (target == IntPtr.Zero)
            {
                LogChn(hwnd, current, IntPtr.Zero, false);
                return false;
            }

            // 判据：目标有独立 IME 布局（搜狗等，HKL 高字 E0xx）时按完整 HKL 精确比较；
            // 否则（微软拼音这类 substitute 到 08040804 的）按语言比较，避免同语言内抖动
            long cur = current.ToInt64();
            bool locked = ImeApi.IsImeLayout(target)
                ? cur == target.ToInt64()
                : (int)(cur & 0xFFFF) == (valid ? tip.LangId : (int)(target.ToInt64() & 0xFFFF));
            if (locked) return true;

            // 关键：ActivateLanguageProfile 实测返回 S_OK 却并不切换前台输入法
            // （它只作用于调用线程），所以不管它成败，消息通道都必须发——
            // 真正生效的是这一步。早期版本"激活成功就跳过兜底"导致中文锁定失效。
            if (valid) TsfProfiles.ActivateTip(tip.LangId, tip.Clsid, tip.GuidProfile);
            ImeApi.PostMessage(hwnd, ImeApi.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, target);

            LogChn(hwnd, current, target, valid);
            return false; // 异步生效，下个周期复查
        }

        private void LogChn(IntPtr hwnd, IntPtr current, IntPtr target, bool tipMode)
        {
            string sig = current.ToInt64().ToString("X8") + "|"
                + target.ToInt64().ToString("X8") + "|" + tipMode;
            if (sig == _chnLogSig) return;
            _chnLogSig = sig;
            Logger.Log("ChineseLock: proc={0} current={1:X8} -> target={2:X8} (tip={3})",
                ImeApi.GetForegroundProcessName(hwnd) ?? "-",
                current.ToInt64(), target.ToInt64(), tipMode);
        }

        // 真机实测结论（Win10 19045，本机）：
        //  · TSF 新接口 ITfInputProcessorProfileMgr 所有方法恒返回 E_INVALIDARG，不可用
        //  · ImmGetContext 跨进程恒返回 0 —— IMM32 只对自身进程的窗口有效
        //  · 唯一可靠通道是 PostMessage(WM_INPUTLANGCHANGEREQUEST)：前台线程 HKL
        //    实测可从 08040804 切到 04090409，也能再切回来
        // 关键陷阱：微软拼音处于中文输入模式时，前台 HKL 依旧是 08040804
        // （输入法的 substitute layout），所以"HKL 是纯键盘布局就是英文"是错误判据，
        // 必须切到真正的英文键盘布局才算锁定。
        private int _engCandidate;
        private int _engAttempts;
        private string _engLogSig;

        private bool ForceEnglish(IntPtr hwnd)
        {
            uint tid = ImeApi.GetWindowThreadProcessId(hwnd, IntPtr.Zero);
            IntPtr current = ImeApi.GetKeyboardLayout(tid);

            IntPtr[] cands = ImeApi.EnglishLayoutCandidates((int)(current.ToInt64() & 0xFFFF));
            if (cands.Length == 0)
            {
                LogEng(hwnd, current, IntPtr.Zero, false, "no-candidate");
                return false;
            }

            // 已停在首选（真正的英文键盘布局）→ 锁定已生效
            if (current == cands[0])
            {
                _engAttempts = 0;
                LogEng(hwnd, current, current, true, "locked");
                return true;
            }

            if (_engCandidate >= cands.Length) _engCandidate = 0;
            if (_engAttempts >= 10) // 同一候选试满 ~1s 仍未切动 → 换下一个
            {
                _engAttempts = 0;
                _engCandidate = (_engCandidate + 1) % cands.Length;
            }
            IntPtr target = cands[_engCandidate];
            _engAttempts++;

            bool posted = ImeApi.PostMessage(hwnd, ImeApi.WM_INPUTLANGCHANGEREQUEST,
                IntPtr.Zero, target);
            ImeApi.ForceImmEnglish(hwnd);
            LogEng(hwnd, current, target, posted, "switch");
            return false; // 异步生效，下个周期复查 HKL
        }

        // 状态变化才记日志：100ms 轮询下同一状态每秒会打 10 条，首条反而被冲掉
        private void LogEng(IntPtr hwnd, IntPtr from, IntPtr to, bool posted, string action)
        {
            string sig = action + "|" + from.ToInt64().ToString("X8") + "|"
                + to.ToInt64().ToString("X8") + "|" + posted;
            if (sig == _engLogSig) return;
            _engLogSig = sig;
            Logger.Log("EnglishLock: proc={0} {1} {2:X8} -> {3:X8} posted={4}",
                ImeApi.GetForegroundProcessName(hwnd) ?? "-", action,
                from.ToInt64(), to.ToInt64(), posted);
        }

        private void UpdateState(bool enforcedOk)
        {
            // 对外状态 = 已启用且强制成功（或处于例外/未启用时视为未锁定）
            bool display = Enabled && enforcedOk;
            if (display != _lastLockedState)
            {
                _lastLockedState = display;
                var handler = LockedStateChanged;
                if (handler != null) handler();
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}
