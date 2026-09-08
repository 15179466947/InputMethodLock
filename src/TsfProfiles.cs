using System;
using System.Runtime.InteropServices;

namespace InputMethodLock
{
    // TF_INPUTPROCESSORPROFILE（msctf.h）——字段与顺序严格对应 Windows SDK 头文件
    internal struct TsfProfile
    {
        public uint dwProfileType;      // 1=输入法(TIP) 2=键盘布局
        public ushort langid;           // 0x0804=简体中文
        public Guid clsid;              // 键盘布局 profile 时为 GUID_EMPTY
        public Guid guidProfile;
        public Guid catid;
        public IntPtr hklSubstitute;
        public uint dwCaps;
        public IntPtr hkl;
        public uint dwFlags;

        public bool IsInputProcessor
        {
            get { return dwProfileType == TsfProfiles.ProfileTypeInputProcessor; }
        }

        public string Describe()
        {
            return string.Format("type={0} lang={1:X4} clsid={2} guid={3} hkl={4}",
                dwProfileType, langid, clsid.ToString("B").ToUpperInvariant(),
                guidProfile.ToString("B").ToUpperInvariant(),
                hkl.ToInt64().ToString("X8"));
        }
    }

    // TSF 输入法 profile 管理接口（ITfInputProcessorProfileMgr，Vista+）。
    // 关键价值：TSF 应用（记事本/浏览器等）里 GetKeyboardLayout 看不到真实输入法、
    // IMM32 读写全部失效，而本接口读写的是系统全局激活状态，跨进程有效——
    // im-select 等输入法切换工具即基于此实现。
    internal static class TsfProfiles
    {
        public const uint ProfileTypeInputProcessor = 1;
        public const uint ProfileTypeKeyboardLayout = 2;

        // CLSID_TF_InputProcessorProfiles —— 取自 msctf.dll 的实际注册值
        // （HKLM\SOFTWARE\Classes\CLSID 下 InprocServer32=msctf.dll、默认名
        //  "TF_InputProcessorProfiles" 的那一项）。
        // 历史 bug：v0.11~v0.12 曾写成 {33C53FB8-4DE9-4B2C-B2B9-3CF6ED2E7030}，
        // 该 GUID 系统内未注册，CoCreate 恒返回 REGDB_E_CLASSNOTREG，
        // 导致整个 TSF 通道静默失效、三个锁定模式全部退化成"什么都不做"。
        // 修改前请先用注册表核对，勿凭记忆改动。
        private static readonly Guid ClsidTfInputProcessorProfiles =
            new Guid("33C53A50-F456-4884-B049-85FD643ECFED");
        private static readonly Guid CatidTipKeyboard =
            new Guid("34745C63-C2AA-4E08-B8F4-DFC60BF4ACB1");

        [ComImport, Guid("33C53A50-F456-4884-B049-85FD643ECFED")]
        private class TFInputProcessorProfilesClass { }

        // 方法声明顺序必须与 msctf.h 的 vtable 一致：
        // ActivateProfile, DeactivateProfile, GetProfile, EnumProfiles,
        // ReleaseInputProcessor, RegisterProfile, UnregisterProfile, GetActiveProfile
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
         Guid("71C6E74C-0F28-11D8-A82A-00065B84435C")]
        private interface ITfInputProcessorProfileMgr
        {
            void ActivateProfile(uint dwProfileType, ushort langid, ref Guid clsid,
                ref Guid guidProfile, IntPtr hkl, uint dwFlags);
            void DeactivateProfile(uint dwProfileType, ushort langid, ref Guid clsid,
                ref Guid guidProfile, IntPtr hkl, uint dwFlags);
            void GetProfile(uint dwProfileType, ushort langid, ref Guid clsid,
                ref Guid guidProfile, IntPtr hkl, out TsfProfile profile);
            void EnumProfiles(ushort langid, out IntPtr ppEnum); // 枚举器接口未使用
            void ReleaseInputProcessor(ref Guid rclsid, uint dwFlags);
            void RegisterProfile(ref Guid rclsid, ushort langid, ref Guid guidProfile,
                IntPtr pchDesc, int cchDesc, IntPtr pchIconFile, int cchFile,
                uint uIconIndex, IntPtr hklSubstitute, uint dwPreferredLayout,
                int bEnabledByDefault, uint dwFlags); // 未调用，仅占 vtable 槽位
            void UnregisterProfile(ref Guid rclsid, ushort langid,
                ref Guid guidProfile, uint dwFlags);
            void GetActiveProfile(ref Guid catid, out TsfProfile profile);
        }

        // COM 实例复用：Enforce 每 100ms 跑一次，每次 new 一个 RCW 既慢又会在
        // 失败时把日志刷爆。创建失败后 10 秒内不再重试（限流）。
        private static ITfInputProcessorProfileMgr _mgr;
        private static bool _mgrFailed;
        private static int _mgrFailTick;
        private static bool _initLogged;

        // 异常 → 带 HRESULT 的描述。COM 失败只看 Message 会丢失最关键的错误码
        private static string Describe(Exception ex)
        {
            try
            {
                int hr = System.Runtime.InteropServices.Marshal.GetHRForException(ex);
                return string.Format("0x{0:X8} {1}: {2}", hr, ex.GetType().Name, ex.Message);
            }
            catch
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static ITfInputProcessorProfileMgr GetMgr()
        {
            if (_mgr != null) return _mgr;
            if (_mgrFailed && Environment.TickCount - _mgrFailTick < 10000) return null;
            try
            {
                _mgr = (ITfInputProcessorProfileMgr)(object)new TFInputProcessorProfilesClass();
                _mgrFailed = false;
                if (!_initLogged)
                {
                    _initLogged = true;
                    Logger.Log("TSF: COM initialized (TF_InputProcessorProfiles)");
                }
                return _mgr;
            }
            catch (Exception ex)
            {
                _mgrFailed = true;
                _mgrFailTick = Environment.TickCount;
                // COM 类级失败只记一条：同样的错每秒重复 10 次没有信息量
                if (Logger.Throttle("tsf-create", 10000))
                    Logger.Log("TSF CoCreate failed: {0}", Describe(ex));
                return null;
            }
        }

        // TSF 通道当前是否可用（供调用方决定走哪条兜底路径）
        public static bool Available { get { return GetMgr() != null; } }

        // 读取当前系统激活的输入法 profile（全局状态，含 TSF 输入法）
        public static bool GetActiveProfile(out TsfProfile profile)
        {
            profile = new TsfProfile();
            ITfInputProcessorProfileMgr mgr = GetMgr();
            if (mgr == null) return false;
            try
            {
                Guid cat = CatidTipKeyboard;
                mgr.GetActiveProfile(ref cat, out profile);
                return true;
            }
            catch (Exception ex)
            {
                _mgr = null; // RCW 可能已失效，下次重建
                if (Logger.Throttle("tsf-getactive", 10000))
                    Logger.Log("TSF GetActiveProfile failed: {0}", Describe(ex));
                return false;
            }
        }

        // 激活指定 profile（TSF 输入法或键盘布局）
        public static bool Activate(TsfProfile profile)
        {
            ITfInputProcessorProfileMgr mgr = GetMgr();
            if (mgr == null) return false;
            try
            {
                // 按 MSDN 契约归一化参数，否则 ActivateProfile 会静默失败：
                // - 键盘布局(KEYBOARDLAYOUT)：clsid / guidProfile 必须为 GUID_EMPTY
                // - 输入法(INPUTPROCESSOR)：hkl 必须为 NULL
                bool isTip = profile.dwProfileType == ProfileTypeInputProcessor;
                Guid clsid = isTip ? profile.clsid : Guid.Empty;
                Guid guid = isTip ? profile.guidProfile : Guid.Empty;
                IntPtr hkl = isTip ? IntPtr.Zero : profile.hkl;
                mgr.ActivateProfile(profile.dwProfileType, profile.langid,
                    ref clsid, ref guid, hkl, 0);
                return true;
            }
            catch (Exception ex)
            {
                _mgr = null;
                if (Logger.Throttle("tsf-activate", 10000))
                    Logger.Log("TSF Activate failed (type={0} lang={1:X4} hkl={2:X8}): {3}",
                        profile.dwProfileType, profile.langid,
                        profile.hkl.ToInt64(), Describe(ex));
                return false;
            }
        }

        // —— 旧接口 ITfInputProcessorProfiles（Vista 前的 API）——
        // 用于交叉验证：新接口的 GetActiveProfile 在部分系统上恒返回
        // E_INVALIDARG(0x80070057)，而本接口的 GetActiveLanguageProfile 可用
        // （im-select 等成熟工具走的就是这条）。vtable 顺序取自 mingw-w64 msctf.h。
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
         Guid("1F02B6C5-7842-4EE6-8A0B-9A24183A95CA")]
        private interface ITfInputProcessorProfiles
        {
            void Register(ref Guid rclsid);
            void Unregister(ref Guid rclsid);
            void AddLanguageProfile(ref Guid rclsid, ushort langid, ref Guid guidProfile,
                IntPtr pchDesc, int cchDesc, IntPtr pchIconFile, int cchFile, uint uIconIndex);
            void RemoveLanguageProfile(ref Guid rclsid, ushort langid, ref Guid guidProfile);
            void EnumInputProcessorInfo(out IntPtr ppEnum);
            void GetDefaultLanguageProfile(ushort langid, ref Guid catid,
                out Guid clsid, out Guid guidProfile);
            void SetDefaultLanguageProfile(ushort langid, ref Guid clsid, ref Guid guidProfile);
            [PreserveSig] int ActivateLanguageProfile(ref Guid clsid, ushort langid, ref Guid guidProfile);
            [PreserveSig] int GetActiveLanguageProfile(ref Guid clsid, out ushort langid, out Guid guidProfile);
        }

        private static ITfInputProcessorProfiles _legacy;

        private static ITfInputProcessorProfiles GetLegacy()
        {
            if (_legacy != null) return _legacy;
            try
            {
                _legacy = (ITfInputProcessorProfiles)(object)new TFInputProcessorProfilesClass();
                return _legacy;
            }
            catch (Exception ex)
            {
                if (Logger.Throttle("tsf-legacy-create", 10000))
                    Logger.Log("TSF legacy CoCreate failed: {0}", Describe(ex));
                return null;
            }
        }

        // 是否有输入法（TIP）处于激活状态。
        // 返回值即 HRESULT：0=S_OK（langid/guidProfile 有效，输入法激活）；
        // 1=S_FALSE（当前是纯键盘布局，打字恒为英文）；负数为失败。
        public static int GetActiveLanguageProfile(out ushort langid, out Guid guidProfile)
        {
            langid = 0;
            guidProfile = Guid.Empty;
            ITfInputProcessorProfiles p = GetLegacy();
            if (p == null) return -1;
            try
            {
                Guid clsid = Guid.Empty;
                return p.GetActiveLanguageProfile(ref clsid, out langid, out guidProfile);
            }
            catch (Exception ex)
            {
                _legacy = null;
                if (Logger.Throttle("tsf-legacy-get", 10000))
                    Logger.Log("TSF GetActiveLanguageProfile failed: {0}", Describe(ex));
                return -1;
            }
        }

        // 用指定 CLSID 查询该输入法是否处于激活（GUID_NULL 查询在某些实现下恒返回 S_FALSE）
        public static int GetActiveLanguageProfileOf(Guid clsid, out ushort langid, out Guid guidProfile)
        {
            langid = 0;
            guidProfile = Guid.Empty;
            ITfInputProcessorProfiles p = GetLegacy();
            if (p == null) return -1;
            try
            {
                return p.GetActiveLanguageProfile(ref clsid, out langid, out guidProfile);
            }
            catch
            {
                _legacy = null;
                return -1;
            }
        }

        // 激活指定输入法（TIP）profile：中文锁定与诊断用。返回值即 HRESULT。
        public static int ActivateTip(ushort langid, Guid clsid, Guid guidProfile)
        {
            ITfInputProcessorProfiles p = GetLegacy();
            if (p == null) return -1;
            try
            {
                return p.ActivateLanguageProfile(ref clsid, langid, ref guidProfile);
            }
            catch (Exception ex)
            {
                _legacy = null;
                if (Logger.Throttle("tsf-legacy-activate", 10000))
                    Logger.Log("TSF ActivateLanguageProfile failed: {0}", Describe(ex));
                return -1;
            }
        }

        // 逐个尝试激活用户已启用的输入法（第一个成功的即用户的首选中文输入法）
        public static bool ActivateChineseIme(ImeApi.TipInfo[] tips)
        {
            if (tips == null) return false;
            foreach (ImeApi.TipInfo t in tips)
            {
                if (ActivateTip(t.LangId, t.Clsid, t.GuidProfile) == 0) return true;
            }
            return false;
        }

        // 激活指定 HKL 的纯键盘布局（langid 由 HKL 低字推导）
        public static bool ActivateKeyboardLayout(IntPtr hkl)
        {
            ushort langid = (ushort)(hkl.ToInt64() & 0xFFFF);
            return ActivateKeyboardLayoutProfile(langid, hkl);
        }

        // 激活某语言的纯键盘布局（hkl 如 0x08040804=中文美式键盘、0x04090409=美式英文）
        public static bool ActivateKeyboardLayoutProfile(ushort langid, IntPtr hkl)
        {
            TsfProfile p = new TsfProfile();
            p.dwProfileType = ProfileTypeKeyboardLayout;
            p.langid = langid;
            p.clsid = Guid.Empty;
            p.guidProfile = Guid.Empty;
            p.catid = Guid.Empty;
            p.hklSubstitute = IntPtr.Zero;
            p.dwCaps = 0;
            p.hkl = hkl;
            p.dwFlags = 0;
            return Activate(p);
        }
    }
}
