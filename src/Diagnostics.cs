using System;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace InputMethodLock
{
    // 诊断模式：InputMethodLock.exe --diag
    // 输入法行为无法离线验证，这个入口在真机上把每条通道都跑一遍
    // （能否读状态、能否切换），结果写进 debug.log 并弹窗显示。
    internal static class Diagnostics
    {
        public static void Run()
        {
            var sb = new StringBuilder();
            Log(sb, "=== 输入法诊断 ===");
            try
            {
                // 等系统焦点稳定，否则测到的是漂移中的前台窗口
                Thread.Sleep(1500);

                IntPtr hwnd = ImeApi.GetForegroundWindow();
                uint tid = ImeApi.GetWindowThreadProcessId(hwnd, IntPtr.Zero);
                Log(sb, "前台窗口 hwnd=" + hwnd.ToInt64().ToString("X")
                    + " tid=" + tid + " proc=" + (ImeApi.GetForegroundProcessName(hwnd) ?? "-"));
                Log(sb, "HKL(前台线程)=" + Hex(ImeApi.GetKeyboardLayout(tid))
                    + " HKL(本进程)=" + Hex(ImeApi.GetKeyboardLayout(0)));

                string[] ks = new string[ImeApi.GetLayoutList().Length];
                for (int i = 0; i < ImeApi.GetLayoutList().Length; i++)
                    ks[i] = Hex(ImeApi.GetLayoutList()[i]);
                Log(sb, "已加载布局=" + string.Join(",", ks));

                ImeApi.TipInfo[] tips = ImeApi.GetEnabledInputProcessors();
                Log(sb, "已启用输入法=" + tips.Length + " 个");
                foreach (ImeApi.TipInfo t in tips)
                    Log(sb, "   " + t.LangId.ToString("X4") + " " + t.Clsid.ToString("B")
                        + " " + t.GuidProfile.ToString("B") + " " + t.Name);

                // —— 通道探测 ——
                TsfProfile p;
                Log(sb, "TSF GetActiveProfile="
                    + (TsfProfiles.GetActiveProfile(out p) ? p.Describe() : "FAILED"));

                ushort lang;
                Guid gp;
                Guid mspy = new Guid("81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E");
                int hr = TsfProfiles.GetActiveLanguageProfileOf(mspy, out lang, out gp);
                Log(sb, "TSF GetActiveLanguageProfile(MSPY) hr=" + Hr(hr)
                    + " lang=" + lang.ToString("X4"));

                IntPtr hIMC = ImeApi.ImmGetContext(hwnd);
                Log(sb, "ImmGetContext(跨进程)=" + Hex(hIMC));
                if (hIMC != IntPtr.Zero) ImeApi.ImmReleaseContext(hwnd, hIMC);

                // —— WM_IME_CONTROL：跨进程读写中英模式 ——
                // 这是"保留当前输入法、只锁中英状态"能否实现的技术前提：
                // IMM32 的 ImmGetContext 跨进程恒为 0，但 WM_IME_CONTROL 是窗口消息，
                // 由目标进程侧的 IME 处理，理论上可以跨进程读写。
                Log(sb, "--- IME 模式通道 (WM_IME_CONTROL) ---");
                ImeApi.PostMessage(ImeApi.GetForegroundWindow(),
                    ImeApi.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, new IntPtr(0x08040804));
                Thread.Sleep(500);
                IntPtr h = ImeApi.GetForegroundWindow();
                Log(sb, "切到中文布局 HKL=" + Hex(CurrentHkl())
                    + " proc=" + (ImeApi.GetForegroundProcessName(h) ?? "-"));
                int open = ImeApi.QueryIme(h, ImeApi.IMC_GETOPENSTATUS);
                int mode = ImeApi.QueryIme(h, ImeApi.IMC_GETCONVERSIONMODE);
                Log(sb, "GETOPENSTATUS=" + open + " GETCONVERSIONMODE=" + mode
                    + "  (1=中文 / 0=英文 / -1=通道不可用)");

                bool setEn = ImeApi.SetIme(h, ImeApi.IMC_SETCONVERSIONMODE,
                    ImeApi.IME_CMODE_ALPHANUMERIC);
                Thread.Sleep(300);
                Log(sb, "设为英文 -> " + setEn
                    + "，读回 mode=" + ImeApi.QueryIme(h, ImeApi.IMC_GETCONVERSIONMODE));

                bool setCn = ImeApi.SetIme(h, ImeApi.IMC_SETCONVERSIONMODE,
                    ImeApi.IME_CMODE_NATIVE);
                Thread.Sleep(300);
                Log(sb, "设为中文 -> " + setCn
                    + "，读回 mode=" + ImeApi.QueryIme(h, ImeApi.IMC_GETCONVERSIONMODE));

                // —— 英文锁定闭环 ——
                Log(sb, "--- 英文锁定 ---");
                IntPtr original = CurrentHkl();
                ImeApi.PostMessage(hwnd, ImeApi.WM_INPUTLANGCHANGEREQUEST,
                    IntPtr.Zero, new IntPtr(0x08040804));
                Thread.Sleep(400);
                Log(sb, "置为中文布局后 HKL=" + Hex(CurrentHkl()));

                ImeLocker en = new ImeLocker();
                en.SetMode(LockMode.English);
                en.Enabled = true;
                en.Enforce();
                Thread.Sleep(500);
                Log(sb, "Enforce 一次后 HKL=" + Hex(CurrentHkl()) + " IsLockedNow=" + en.IsLockedNow);
                en.Enforce();
                Thread.Sleep(300);
                Log(sb, "Enforce 两次后 HKL=" + Hex(CurrentHkl()) + " IsLockedNow=" + en.IsLockedNow);
                en.Dispose();

                // —— 中文锁定闭环 ——
                Log(sb, "--- 中文锁定 ---");
                ImeApi.PostMessage(hwnd, ImeApi.WM_INPUTLANGCHANGEREQUEST,
                    IntPtr.Zero, new IntPtr(0x04090409));
                Thread.Sleep(400);
                Log(sb, "置为英文布局后 HKL=" + Hex(CurrentHkl()));

                ImeLocker cn = new ImeLocker();
                cn.SetMode(LockMode.Chinese);
                if (tips.Length > 0) cn.SetChineseTarget(tips[0], true);
                else cn.SetChineseTarget(default(ImeApi.TipInfo), false);
                cn.SetChineseFallbackLayout(new IntPtr(0x08040804));
                cn.Enabled = true;
                cn.Enforce();
                Thread.Sleep(600);
                Log(sb, "Enforce 一次后 HKL=" + Hex(CurrentHkl()) + " IsLockedNow=" + cn.IsLockedNow);
                cn.Enforce();
                Thread.Sleep(400);
                Log(sb, "Enforce 两次后 HKL=" + Hex(CurrentHkl()) + " IsLockedNow=" + cn.IsLockedNow);
                cn.Dispose();

                // —— 布局锁定闭环 ——
                Log(sb, "--- 布局锁定 ---");
                ImeApi.PostMessage(hwnd, ImeApi.WM_INPUTLANGCHANGEREQUEST,
                    IntPtr.Zero, new IntPtr(0x08040804));
                Thread.Sleep(400);
                Log(sb, "置为中文布局后 HKL=" + Hex(CurrentHkl()));

                ImeLocker ly = new ImeLocker();
                ly.SetMode(LockMode.Layout);
                ly.SetTargetLayout(new IntPtr(0x04090409));
                ly.Enabled = true;
                ly.Enforce();
                Thread.Sleep(500);
                Log(sb, "Enforce 后 HKL=" + Hex(CurrentHkl()) + " IsLockedNow=" + ly.IsLockedNow);
                ly.Dispose();

                // 还原，避免把用户输入法留在测试状态
                if (original != IntPtr.Zero)
                {
                    ImeApi.PostMessage(ImeApi.GetForegroundWindow(),
                        ImeApi.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, original);
                    Thread.Sleep(400);
                }
                Log(sb, "还原 HKL=" + Hex(original) + "，实际 HKL=" + Hex(CurrentHkl()));
            }
            catch (Exception ex)
            {
                Log(sb, "EXCEPTION: " + ex);
            }
            Log(sb, "=== 诊断结束 ===");
            MessageBox.Show(sb.ToString(), "输入法锁定 - 诊断",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // 每次现取前台窗口：测试期间焦点可能漂移，缓存的 tid 会读到过期的 HKL
        private static IntPtr CurrentHkl()
        {
            IntPtr hwnd = ImeApi.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;
            uint tid = ImeApi.GetWindowThreadProcessId(hwnd, IntPtr.Zero);
            return ImeApi.GetKeyboardLayout(tid);
        }

        private static string Hex(IntPtr v)
        {
            return v.ToInt64().ToString("X8");
        }

        private static string Hr(int hr)
        {
            return "0x" + hr.ToString("X8");
        }

        private static void Log(StringBuilder sb, string line)
        {
            sb.AppendLine(line);
            Logger.Log("[diag] " + line);
        }
    }
}
