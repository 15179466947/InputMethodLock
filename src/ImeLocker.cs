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
        private Func<bool> _chineseImeActivator;  // TSF 通道：激活快照中的中文输入法 profile
        private HashSet<string> _exceptions = new HashSet<string>();
        private bool _lastLockedState;

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

        public void SetChineseImeActivator(Func<bool> activator) { _chineseImeActivator = activator; }

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
                // v0.11：TSF 应用的真实输入法状态在激活 profile 里（HKL 看不到）。
                // 激活的是中文输入法 → 锁其中英状态；不是 → 先激活中文输入法 profile
                TsfProfile active;
                bool tsf = TsfProfiles.GetActiveProfile(out active);
                bool tipChinese = tsf && active.IsInputProcessor && active.langid == 0x0804;
                if (tsf && !tipChinese)
                {
                    bool activated = _chineseImeActivator != null && _chineseImeActivator();
                    if (!activated && _chineseHkl != IntPtr.Zero)
                    {
                        ImeApi.PostMessage(hwnd, ImeApi.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, _chineseHkl);
                        activated = true;
                    }
                    ok = activated; // 激活后输入法以默认中文模式启动，下个周期复查
                }
                else
                {
                    ok = ImeApi.ForceNativeMode(hwnd);
                }
            }
            else
            {
                ok = ImeApi.ForceEnglishMode(hwnd);
            }
            UpdateState(ok);
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
