using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace InputMethodLock
{
    // 嵌入资源读取工具（icon.png 编译时以 /res 嵌入）
    internal static class AppResources
    {
        private static Image _image;

        // 设计稿缩放图（256px），托盘/窗口图标统一从这里实时绘制
        public static Image LoadImage()
        {
            if (_image != null) return _image;
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream("InputMethodLock.icon.png"))
                {
                    if (s != null) _image = Image.FromStream(s);
                }
            }
            catch { }
            return _image;
        }

        public static Icon CreateIcon(int size)
        {
            Image img = LoadImage();
            if (img == null) return null;
            using (var bmp = new Bitmap(size, size))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.DrawImage(img, new Rectangle(0, 0, size, size));
                return CopyAsIcon(bmp);
            }
        }

        // 从位图复制出独立 Icon：单一 GetHicon + 立即销毁原始句柄，避免 GDI 泄漏
        public static Icon CopyAsIcon(Bitmap bmp)
        {
            IntPtr hIcon = bmp.GetHicon();
            try
            {
                using (Icon tmp = Icon.FromHandle(hIcon))
                    return (Icon)tmp.Clone();
            }
            finally
            {
                ImeApi.DestroyIcon(hIcon);
            }
        }
    }

    // 应用主上下文：托盘 + 菜单 + 锁定器 + 热键
    public class AppContext : ApplicationContext
    {
        private readonly ImeLocker _locker;
        private readonly HotkeyWindow _hotkeyWindow;
        private readonly NotifyIcon _tray;
        private readonly Config _config;
        private SettingsForm _settingsForm;
        private MenuItem _menuToggle;
        private readonly ImeWatcher _watcher = new ImeWatcher();
        private bool _hotkeyFailed; // 热键注册失败（被占用等），启动/保存后气泡提示
        private IntPtr _userHklSnapshot; // 启用锁定时用户正在用的键盘布局 HKL

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private const int SM_CXSMICON = 49; // 小图标标准尺寸（随系统 DPI 缩放：96dpi=16, 144dpi=24…）

        public AppContext()
        {
            bool initOk = true;
            _config = Config.Load();

            _locker = new ImeLocker();
            _locker.LockedStateChanged += delegate { SyncLockStateUi(); };

            try
            {
                _hotkeyWindow = new HotkeyWindow();
                _hotkeyWindow.HotkeyPressed += OnHotkey;
                if (!ApplyHotkey()) _hotkeyFailed = true;
                ApplyConfigToLocker();
            }
            catch (Exception ex)
            {
                initOk = false;
                Logger.Log("Init failure: {0}", ex);
            }

            // 变化监听：Shift/Ctrl+Space/CapsLock 及系统 IME 事件 → 立即强制恢复
            _watcher.ChangeDetected += OnImeChanged;
            _watcher.Start();

            // DPI / 显示设置变化 → 按新尺寸重绘托盘图标
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += delegate { SyncLockStateUi(); };

            _tray = new NotifyIcon();
            _tray.Text = "输入法锁定";
            _tray.Icon = MakeIcon(_config.LockEnabled);
            _tray.Visible = true;
            _tray.ContextMenu = BuildMenu();
            // 左键单击托盘 = 快速开/关锁定（热键冲突时的替代操作入口）
            _tray.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ToggleLock();
            };

            if (_config.LockEnabled) EnableLock();
            SyncLockStateUi();
            Logger.Log("Started. mode={0}, enabled={1}, hotkeyFailed={2}",
                _config.Mode, _config.LockEnabled, _hotkeyFailed);

            // 启动结果提示
            if (!initOk)
            {
                _tray.ShowBalloonTip(3000, "输入法锁定",
                    "程序启动成功，但锁定初始化失败，请打开设置检查配置",
                    ToolTipIcon.Warning);
            }
            else if (_hotkeyFailed)
            {
                _tray.ShowBalloonTip(3000, "输入法锁定",
                    "启动成功，但临时开关热键注册失败（可能被其他程序占用），请打开设置更换热键",
                    ToolTipIcon.Warning);
            }
            else
            {
                _tray.ShowBalloonTip(2000, "输入法锁定",
                    "程序启动成功" + (_config.LockEnabled ? "，锁定已启用" : "，锁定未启用"),
                    ToolTipIcon.Info);
            }
        }

        private ContextMenu BuildMenu()
        {
            var menu = new ContextMenu();

            _menuToggle = new MenuItem("启用锁定");
            _menuToggle.Checked = _locker.Enabled;
            _menuToggle.Click += delegate { ToggleLock(); };
            menu.MenuItems.Add(_menuToggle);

            var englishMode = new MenuItem("模式：英文锁定");
            englishMode.RadioCheck = true;
            englishMode.Checked = _config.Mode == "english";
            englishMode.Click += delegate
            {
                _config.Mode = "english";
                _config.Save();
                ApplyConfigToLocker();
                RefreshModeChecks();
            };
            menu.MenuItems.Add(englishMode);

            var chineseMode = new MenuItem("模式：中文锁定");
            chineseMode.RadioCheck = true;
            chineseMode.Checked = _config.Mode == "chinese";
            chineseMode.Click += delegate
            {
                _config.Mode = "chinese";
                _config.Save();
                ApplyConfigToLocker();
                RefreshModeChecks();
            };
            menu.MenuItems.Add(chineseMode);

            var layoutMode = new MenuItem("模式：布局锁定");
            layoutMode.RadioCheck = true;
            layoutMode.Checked = _config.Mode == "layout";
            layoutMode.Click += delegate
            {
                _config.Mode = "layout";
                _config.Save();
                ApplyConfigToLocker();
                RefreshModeChecks();
            };
            menu.MenuItems.Add(layoutMode);

            menu.MenuItems.Add(new MenuItem("-"));

            var settings = new MenuItem("设置(&S)...");
            settings.Click += delegate { ShowSettings(); };
            menu.MenuItems.Add(settings);

            var exit = new MenuItem("退出(&X)");
            exit.Click += delegate { ExitApp(); };
            menu.MenuItems.Add(exit);

            return menu;
        }

        private void RefreshModeChecks()
        {
            if (_menuToggle == null || _menuToggle.GetContextMenu() == null) return;
            ContextMenu menu = _menuToggle.GetContextMenu();
            ((MenuItem)menu.MenuItems[1]).Checked = _config.Mode == "english";
            ((MenuItem)menu.MenuItems[2]).Checked = _config.Mode == "chinese";
            ((MenuItem)menu.MenuItems[3]).Checked = _config.Mode == "layout";
        }

        private void ApplyConfigToLocker()
        {
            if (_config.Mode == "layout") _locker.SetMode(LockMode.Layout);
            else if (_config.Mode == "chinese") _locker.SetMode(LockMode.Chinese);
            else _locker.SetMode(LockMode.English);
            _locker.SetTargetLayout(ParseLayoutStorage(_config.TargetLayout));

            // 中文锁定目标：设置指定了 tip: 就解析它，否则交给 ImeLocker 退回到
            // 第一个已启用输入法（换机器也能用，不写死语言）。兜底布局语言也据此取。
            ushort fallbackLang = 0x0804;
            ImeApi.TipInfo tip;
            if (!string.IsNullOrEmpty(_config.ChineseIme)
                && ImeApi.StorageToTip(_config.ChineseIme, out tip))
            {
                _locker.SetChineseTarget(tip, true);
                fallbackLang = tip.LangId;
            }
            else
            {
                ImeApi.TipInfo[] all = ImeApi.GetEnabledInputProcessors();
                if (all.Length > 0) fallbackLang = all[0].LangId;
                _locker.SetChineseTarget(default(ImeApi.TipInfo), false);
            }
            IntPtr chinese = ImeApi.FindImeLayoutByLanguage(fallbackLang);
            if (chinese == IntPtr.Zero)
                chinese = ImeApi.LoadKeyboardLayout(fallbackLang.ToString("X8"), 0x00000001 /*KLF_ACTIVATE*/);
            _locker.SetChineseFallbackLayout(chinese);

            _locker.SetExceptions(_config.Exceptions);
        }

        // 布局存储格式："hkl:XXXXXXXX"=完整 HKL（精确到微软拼音等 IME），纯 hex=旧版 KLID，none=不切换
        private static IntPtr ParseLayoutStorage(string val)
        {
            try
            {
                if (string.IsNullOrEmpty(val) || val == "none") return IntPtr.Zero;
                if (val.StartsWith("hkl:"))
                    return new IntPtr(Convert.ToInt64(val.Substring(4), 16));
                return ImeApi.LoadKeyboardLayout(val, 0x00000001);
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private void OnImeChanged()
        {
            if (_locker.Enabled) _locker.Enforce();
        }

        // DPI 缩放变化（WindowMetrics 类别）时按新的小图标尺寸重绘托盘图标
        private void OnPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            if (e.Category == Microsoft.Win32.UserPreferenceCategory.Window) SyncLockStateUi();
        }

        // 注册成功返回 false（被占用等），供气泡提示
        private bool ApplyHotkey()
        {
            Keys key = (Keys)(int)HotkeyHelper.ParseKey(_config.HotkeyKey);
            if (key == Keys.None)
            {
                _hotkeyWindow.Unregister(); // 未配置热键
                return true;
            }
            return _hotkeyWindow.Register(
                HotkeyHelper.ParseModifiers(_config.HotkeyModifiers),
                (uint)key);
        }

        // —— 锁定开关的唯一入口，保证托盘/菜单/设置/热键四处状态一致 ——
        // 注意：运行时开关只改运行状态；"下次启动是否启用"由设置里的独立选项控制
        private void ToggleLock()
        {
            if (_locker.Enabled) DisableLock(); else EnableLock();
        }

        private void EnableLock()
        {
            if (_locker.Enabled) return;
            TakeSnapshot();
            _locker.Start();
            _locker.Enforce();
            SyncLockStateUi();
        }

        // 记住用户当前输入法（HKL），解锁时自动恢复（auto 模式）。
        // 说明：TSF profile 快照已移除——新接口 GetActiveProfile 在本机恒返回
        // E_INVALIDARG，HKL 是跨进程唯一可靠的输入法标识。
        private void TakeSnapshot()
        {
            try
            {
                IntPtr hwnd = ImeApi.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return;
                uint tid = ImeApi.GetWindowThreadProcessId(hwnd, IntPtr.Zero);
                _userHklSnapshot = ImeApi.GetKeyboardLayout(tid);
                Logger.Log("Snapshot user layout: {0}", _userHklSnapshot.ToInt64().ToString("X8"));
            }
            catch (Exception ex)
            {
                Logger.Log("Snapshot failed: {0}", ex.Message);
            }
        }

        private void DisableLock()
        {
            if (!_locker.Enabled) return;
            _locker.Stop();
            RestoreUnlockLayout();
            SyncLockStateUi();
        }

        // 停用锁定时切换输入法：auto=恢复锁定前的快照；none=不动；hkl:xx=指定输入法。
        // 通道统一走 WM_INPUTLANGCHANGEREQUEST（实测可靠的跨进程切换方式）
        // 解析"停用恢复"的目标 HKL：auto=恢复快照（由调用方处理）；none=不切换（Zero）；
        // tip:xxx=指定输入法的 HKL；其余当键盘布局解析。
        private IntPtr ResolveRestoreHkl(string val)
        {
            if (val == "none") return IntPtr.Zero;
            if (val.StartsWith("tip:"))
            {
                ImeApi.TipInfo tip;
                if (ImeApi.StorageToTip(val, out tip) && tip.Hkl != IntPtr.Zero) return tip.Hkl;
                return IntPtr.Zero;
            }
            return ParseLayoutStorage(val);
        }

        private void RestoreUnlockLayout()
        {
            string val = string.IsNullOrEmpty(_config.UnlockLayout) ? "auto" : _config.UnlockLayout;
            if (val == "none") return;

            IntPtr hkl = val == "auto" ? _userHklSnapshot : ResolveRestoreHkl(val);
            if (hkl == IntPtr.Zero) return;

            // 发给用户真正在用的窗口：停用时当前前台可能是托盘/资源管理器，
            // 发过去恢复就无效。LastLockedHwnd 是锁定期间最后强制过的前台窗口。
            IntPtr hwnd = _locker.LastLockedHwnd;
            if (hwnd == IntPtr.Zero || !ImeApi.IsWindow(hwnd)) hwnd = ImeApi.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;
            bool posted = ImeApi.PostMessage(hwnd, ImeApi.WM_INPUTLANGCHANGEREQUEST,
                IntPtr.Zero, hkl);
            Logger.Log("Unlock: restore layout {0} posted={1} hwnd={2}",
                hkl.ToInt64().ToString("X8"), posted, hwnd.ToInt64().ToString("X"));
        }

        // 刷新托盘图标、菜单勾选、提示文字与已打开的设置窗口
        private void SyncLockStateUi()
        {
            if (_menuToggle != null) _menuToggle.Checked = _locker.Enabled;
            Icon oldTray = _tray.Icon;
            _tray.Icon = MakeIcon(_locker.Enabled);
            if (oldTray != null) oldTray.Dispose(); // 旧图标由调用方释放，避免 GDI 泄漏
            UpdateTooltip();
            if (_settingsForm != null && !_settingsForm.IsDisposed)
                _settingsForm.UpdateLockState(_locker.Enabled);
        }

        private void OnHotkey()
        {
            ToggleLock();
        }

        private void UpdateTooltip()
        {
            string mode;
            if (_config.Mode == "layout") mode = "布局锁定";
            else if (_config.Mode == "chinese") mode = "中文锁定";
            else mode = "英文锁定";
            string state = _locker.Enabled ? "已启用" : "已停用";
            _tray.Text = "输入法锁定 - " + state + "（" + mode + "）\r\n左键点击快速开关";
        }

        private void ShowSettings()
        {
            if (_settingsForm != null && !_settingsForm.IsDisposed)
            {
                // 已打开时重新加载当前状态，避免显示过期配置
                _settingsForm.ReloadValues();
                _settingsForm.UpdateLockState(_locker.Enabled);
                _settingsForm.Activate();
                return;
            }
            _settingsForm = new SettingsForm(_config);
            _settingsForm.ConfigSaved += delegate
            {
                ApplyConfigToLocker();
                _hotkeyFailed = !ApplyHotkey();
                if (_settingsForm.LockNowRequested) EnableLock();
                else DisableLock();
                SyncLockStateUi();
                if (_hotkeyFailed && _tray != null)
                {
                    _tray.ShowBalloonTip(3000, "输入法锁定",
                        "临时开关热键注册失败（可能被其他程序占用），请更换热键或按 Delete 清除",
                        ToolTipIcon.Warning);
                }
            };
            _settingsForm.UpdateLockState(_locker.Enabled);
            _settingsForm.Show();
        }

        private void ExitApp()
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
            _watcher.Dispose();
            if (_hotkeyWindow != null) _hotkeyWindow.Dispose();
            _locker.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            Application.Exit();
        }

        // 当前 DPI 下的托盘小图标尺寸（16~64px 钳制）
        private static int TrayIconSize()
        {
            int size = GetSystemMetrics(SM_CXSMICON);
            if (size < 16) size = 16;
            if (size > 64) size = 64;
            return size;
        }

        // 托盘图标：从设计稿实时缩放绘制（按系统 DPI 取小图标尺寸）；停用时半透明灰化。
        // 每次返回新 Icon 实例，调用方负责释放旧值
        private Icon MakeIcon(bool enabled)
        {
            int size = TrayIconSize();
            using (var bmp = new Bitmap(size, size))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.Clear(Color.Transparent);
                Image img = AppResources.LoadImage();
                if (img != null)
                {
                    g.DrawImage(img, new Rectangle(0, 0, size, size));
                    if (!enabled)
                    {
                        using (Bitmap frame = new Bitmap(bmp))
                        {
                            g.Clear(Color.Transparent);
                            ControlPaint.DrawImageDisabled(g, frame, 0, 0, Color.Transparent);
                        }
                    }
                }
                else
                {
                    using (var brush = new SolidBrush(enabled ? Color.FromArgb(46, 160, 67) : Color.Gray))
                    {
                        g.FillEllipse(brush, 0, 0, size - 1, size - 1);
                    }
                    using (var font = new Font("Microsoft YaHei", size * 9f / 16f, FontStyle.Bold, GraphicsUnit.Pixel))
                    {
                        TextRenderer.DrawText(g, "锁", font, new Rectangle(0, 0, size, size),
                            Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    }
                }
                return AppResources.CopyAsIcon(bmp);
            }
        }
    }
}
