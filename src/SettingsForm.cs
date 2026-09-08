using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace InputMethodLock
{
    // 设置窗口：启用开关、锁定模式、目标布局、热键、解锁恢复、例外程序、自启
    public class SettingsForm : Form
    {
        private readonly Config _config;
        public event Action ConfigSaved;

        private CheckBox _chkLockNow;     // 启用锁定（当前状态，立即生效）
        private CheckBox _chkEnabled;     // 启动时自动启用
        private CheckBox _chkAutoStart;
        private RadioButton _rbEnglish;
        private RadioButton _rbChinese;
        private RadioButton _rbLayout;
        private ComboBox _cmbLayouts;
        private ComboBox _cmbUnlock;      // 停用锁定时切换到
        private TextBox _txtHotkey;
        private bool _capturing;
        private bool _clearedHotkey;
        private TextBox _txtExceptions;
        private Button _btnSave;
        private Button _btnCancel;

        public SettingsForm(Config config)
        {
            _config = config;
            Icon formIcon = AppResources.CreateIcon(32);
            if (formIcon != null) Icon = formIcon;
            InitUi();
            LoadValues();
        }

        // 保存时的实时锁定开关状态（区别于"下次启动自动启用"）
        public bool LockNowRequested
        {
            get { return _chkLockNow != null && _chkLockNow.Checked; }
        }

        private void InitUi()
        {
            Text = "输入法锁定 - 设置";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            // 用 Pixel 单位（12px@96DPI 等同原 9pt）：AutoScaleMode=Dpi + PerMonitorV2
            // 会把 point 字号的逻辑像素再乘一次 DPI 系数，导致字号二次放大、标签被截断
            Font = new Font("Microsoft YaHei UI", 12f, GraphicsUnit.Pixel);
            // 硬编码坐标在 125%/150% DPI 下会错位，按 DPI 基准 96 缩放全部 Bounds
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            ClientSize = new Size(420, 420);

            _chkLockNow = new CheckBox();
            _chkLockNow.Text = "启用锁定（立即生效）";
            _chkLockNow.Bounds = new Rectangle(16, 10, 250, 20);
            Controls.Add(_chkLockNow);

            var groupMode = new GroupBox();
            groupMode.Text = "锁定模式";
            groupMode.Bounds = new Rectangle(12, 34, 396, 122);

            _rbEnglish = new RadioButton();
            _rbEnglish.Text = "英文锁定（保留当前输入法，锁英文状态）";
            _rbEnglish.Bounds = new Rectangle(10, 20, 370, 20);
            _rbEnglish.CheckedChanged += delegate { UpdateLayoutComboEnabled(); };
            groupMode.Controls.Add(_rbEnglish);

            _rbChinese = new RadioButton();
            _rbChinese.Text = "中文锁定（保留当前输入法，锁定为中文状态）";
            _rbChinese.Bounds = new Rectangle(10, 45, 370, 20);
            _rbChinese.CheckedChanged += delegate { UpdateLayoutComboEnabled(); };
            groupMode.Controls.Add(_rbChinese);

            _rbLayout = new RadioButton();
            _rbLayout.Text = "布局锁定（强制指定键盘布局）：";
            _rbLayout.Bounds = new Rectangle(10, 70, 200, 20);
            groupMode.Controls.Add(_rbLayout);

            _cmbLayouts = new ComboBox();
            _cmbLayouts.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbLayouts.Bounds = new Rectangle(30, 93, 350, 24);
            groupMode.Controls.Add(_cmbLayouts);

            Controls.Add(groupMode);

            var lblHotkey = new Label();
            lblHotkey.Text = "临时开关热键：";
            lblHotkey.Bounds = new Rectangle(16, 168, 100, 20);
            Controls.Add(lblHotkey);

            _txtHotkey = new TextBox();
            _txtHotkey.ReadOnly = true;
            _txtHotkey.BackColor = SystemColors.Window;
            _txtHotkey.Bounds = new Rectangle(120, 165, 180, 24);
            _txtHotkey.KeyDown += OnHotkeyKeyDown;
            _txtHotkey.Enter += delegate { _capturing = true; };
            _txtHotkey.Leave += delegate { _capturing = false; }; // 只进不出的捕获态会吞掉后续所有按键
            Controls.Add(_txtHotkey);

            var lblHotkeyHint = new Label();
            lblHotkeyHint.Text = "点击后按下快捷键（需含 Ctrl/Alt/Shift），按 Delete 清除";
            lblHotkeyHint.ForeColor = Color.Gray;
            lblHotkeyHint.Bounds = new Rectangle(120, 190, 300, 16);
            Controls.Add(lblHotkeyHint);

            var lblUnlock = new Label();
            lblUnlock.Text = "停用锁定时切到：";
            lblUnlock.Bounds = new Rectangle(16, 216, 100, 20);
            Controls.Add(lblUnlock);

            _cmbUnlock = new ComboBox();
            _cmbUnlock.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbUnlock.Bounds = new Rectangle(122, 213, 178, 24);
            Controls.Add(_cmbUnlock);

            var lblExceptions = new Label();
            lblExceptions.Text = "例外程序（这些进程不锁定，进程名用英文逗号分隔）：";
            lblExceptions.Bounds = new Rectangle(16, 244, 390, 20);
            Controls.Add(lblExceptions);

            _txtExceptions = new TextBox();
            _txtExceptions.Multiline = true;
            _txtExceptions.ScrollBars = ScrollBars.Vertical;
            _txtExceptions.Bounds = new Rectangle(16, 266, 392, 50);
            Controls.Add(_txtExceptions);

            _chkEnabled = new CheckBox();
            _chkEnabled.Text = "下次启动时自动启用锁定";
            _chkEnabled.Bounds = new Rectangle(16, 324, 220, 20);
            Controls.Add(_chkEnabled);

            _chkAutoStart = new CheckBox();
            _chkAutoStart.Text = "开机自动运行";
            _chkAutoStart.Bounds = new Rectangle(16, 348, 200, 20);
            Controls.Add(_chkAutoStart);

            _btnSave = new Button();
            _btnSave.Text = "保存";
            _btnSave.Bounds = new Rectangle(230, 380, 80, 28);
            _btnSave.Click += delegate { Save(); };
            Controls.Add(_btnSave);

            _btnCancel = new Button();
            _btnCancel.Text = "取消";
            _btnCancel.Bounds = new Rectangle(322, 380, 80, 28);
            _btnCancel.Click += delegate { Close(); };
            Controls.Add(_btnCancel);

            AcceptButton = _btnSave;
            CancelButton = _btnCancel;
        }

        public void ReloadValues()
        {
            LoadValues();
        }

        // 锁定状态在别处（托盘/热键）变化时同步勾选框
        public void UpdateLockState(bool enabled)
        {
            if (_chkLockNow != null) _chkLockNow.Checked = enabled;
        }

        private void LoadValues()
        {
            _chkEnabled.Checked = _config.LockEnabled; // 下次启动偏好
            _rbEnglish.Checked = _config.Mode == "english";
            _rbChinese.Checked = _config.Mode == "chinese";
            _rbLayout.Checked = _config.Mode == "layout";
            if (!_rbEnglish.Checked && !_rbChinese.Checked && !_rbLayout.Checked) _rbEnglish.Checked = true;

            // 列出系统已加载的键盘布局（目标布局 + 解锁恢复共用一份列表）
            int count = (int)ImeApi.GetKeyboardLayoutList(0, null);
            var hkls = new IntPtr[count > 0 ? count : 0];
            if (count > 0) ImeApi.GetKeyboardLayoutList(count, hkls);

            // 目标布局下拉：存完整 HKL（hkl:XXXXXXXX），锁定可精确到微软拼音等 IME，
            // 而不是只按语言码锁到"中文美式键盘"
            string saved = _config.TargetLayout;
            int savedIndex = -1;
            int index = 0;
            _cmbLayouts.Items.Clear();
            foreach (IntPtr hkl in hkls)
            {
                string storage = "hkl:" + hkl.ToInt64().ToString("X8", CultureInfo.InvariantCulture);
                string name = LayoutItemDisplayName(hkl);
                _cmbLayouts.Items.Add(new LayoutItem(storage, name));
                if (string.Equals(storage, saved, StringComparison.OrdinalIgnoreCase)) savedIndex = index;
                index++;
            }
            if (_cmbLayouts.Items.Count == 0)
            {
                _cmbLayouts.Items.Add(new LayoutItem("hkl:04090409", "English (United States)"));
                savedIndex = 0;
            }
            _cmbLayouts.SelectedIndex = savedIndex >= 0 ? savedIndex : 0;
            UpdateLayoutComboEnabled();

            // 解锁恢复下拉：自动（默认）| 不切换 | 全部布局（存完整 HKL）
            string savedUnlock = string.IsNullOrEmpty(_config.UnlockLayout) ? "auto" : _config.UnlockLayout;
            int unlockIndex = 0;
            _cmbUnlock.Items.Clear();
            _cmbUnlock.Items.Add(new LayoutItem("auto", "（自动：恢复锁定前的输入法）"));
            _cmbUnlock.Items.Add(new LayoutItem("none", "（不切换）"));
            index = 2;
            foreach (IntPtr hkl in hkls)
            {
                string storage = "hkl:" + hkl.ToInt64().ToString("X8", CultureInfo.InvariantCulture);
                _cmbUnlock.Items.Add(new LayoutItem(storage, LayoutItemDisplayName(hkl)));
                if (string.Equals(storage, savedUnlock, StringComparison.OrdinalIgnoreCase)) unlockIndex = index;
                index++;
            }
            if (savedUnlock == "auto") unlockIndex = 0;
            else if (savedUnlock == "none") unlockIndex = 1;
            _cmbUnlock.SelectedIndex = unlockIndex;

            Keys hotkeyKey = (Keys)(int)HotkeyHelper.ParseKey(_config.HotkeyKey);
            if (hotkeyKey == Keys.None)
                _txtHotkey.Text = "无";
            else
                _txtHotkey.Text = HotkeyHelper.ModifiersText(HotkeyHelper.ParseModifiers(_config.HotkeyModifiers))
                    + "+" + HotkeyHelper.KeyToText(hotkeyKey);
            _txtHotkey.Tag = null;
            _clearedHotkey = false;

            _txtExceptions.Text = string.Join(", ", _config.Exceptions.ToArray());
            _chkAutoStart.Checked = AutoStart.IsEnabled();
        }

        // 布局显示名：优先读注册表 Layout Text（能显示"微软拼音"等 IME 名），
        // 再退回语言原生名（HKLL 低字语言 ID），最后退回 HKL 十六进制
        private static string LayoutItemDisplayName(IntPtr hkl)
        {
            long v = hkl.ToInt64();
            string klidFull = v.ToString("X8", CultureInfo.InvariantCulture);
            string klidLang = "0000" + (v & 0xFFFF).ToString("X4", CultureInfo.InvariantCulture);
            string regName = ReadLayoutText(klidFull);
            if (regName == null) regName = ReadLayoutText(klidLang);
            if (regName != null) return regName;
            try
            {
                return new CultureInfo((int)(v & 0xFFFF)).NativeName;
            }
            catch
            {
                return klidFull;
            }
        }

        private static string ReadLayoutText(string klid)
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Keyboard Layouts\" + klid))
                {
                    if (key == null) return null;
                    object val = key.GetValue("Layout Text");
                    string s = val as string;
                    return string.IsNullOrEmpty(s) ? null : s;
                }
            }
            catch
            {
                return null;
            }
        }

        private void UpdateLayoutComboEnabled()
        {
            _cmbLayouts.Enabled = _rbLayout.Checked;
        }

        private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
        {
            if (!_capturing) return; // 非捕获态不吞键
            e.SuppressKeyPress = true;
            if (e.KeyCode == Keys.Delete || e.KeyCode == Keys.Back)
            {
                // 清除热键 = 停用热键功能
                _txtHotkey.Text = "无";
                _txtHotkey.Tag = null;
                _clearedHotkey = true;
                return;
            }
            Keys key = e.KeyCode;
            if (key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu
                || key == Keys.LWin || key == Keys.RWin) return;
            if (key == Keys.None) return;
            var parts = new List<string>();
            if ((e.Modifiers & Keys.Control) != 0) parts.Add("Ctrl");
            if ((e.Modifiers & Keys.Alt) != 0) parts.Add("Alt");
            if ((e.Modifiers & Keys.Shift) != 0) parts.Add("Shift");
            if (parts.Count == 0) return; // 必须带修饰键，避免干扰正常打字
            parts.Add(HotkeyHelper.KeyToText(key));
            _txtHotkey.Text = string.Join("+", parts.ToArray());
            _txtHotkey.Tag = key; // 保存时用 Keys 枚举名写回配置
            _clearedHotkey = false;
        }

        private void Save()
        {
            // "下次启动自动启用"是持久偏好；实时开关走 LockNowRequested 由主程序应用
            _config.LockEnabled = _chkEnabled.Checked;
            _config.Mode = _rbLayout.Checked ? "layout" : (_rbChinese.Checked ? "chinese" : "english");

            LayoutItem target = _cmbLayouts.SelectedItem as LayoutItem;
            if (target != null) _config.TargetLayout = target.Klid;
            LayoutItem unlock = _cmbUnlock.SelectedItem as LayoutItem;
            if (unlock != null) _config.UnlockLayout = unlock.Klid;

            // 热键：捕获过就用新值；清除过就停用；否则保持不变
            Keys? captured = _txtHotkey.Tag as Keys?;
            if (captured.HasValue)
            {
                string text = _txtHotkey.Text;
                int lastPlus = text.LastIndexOf('+');
                if (lastPlus > 0)
                {
                    string modPart = text.Substring(0, lastPlus).Replace("Ctrl", "Control");
                    _config.HotkeyModifiers = modPart;
                    _config.HotkeyKey = captured.Value.ToString();
                }
            }
            else if (_clearedHotkey)
            {
                _config.HotkeyModifiers = "None";
                _config.HotkeyKey = "None";
            }

            _config.Exceptions = new List<string>();
            foreach (string item in _txtExceptions.Text.Split(new[] { ',', '，' }))
            {
                string t = item.Trim();
                if (t.Length > 0) _config.Exceptions.Add(t);
            }

            _config.StartWithWindows = _chkAutoStart.Checked;
            AutoStart.Set(_chkAutoStart.Checked, System.Reflection.Assembly.GetExecutingAssembly().Location);
            _config.Save();

            var handler = ConfigSaved;
            if (handler != null) handler();
            Close();
        }

        private class LayoutItem
        {
            public readonly string Klid;
            private readonly string _name;
            public LayoutItem(string klid, string name) { Klid = klid; _name = name; }
            public override string ToString() { return _name; }
        }
    }
}
