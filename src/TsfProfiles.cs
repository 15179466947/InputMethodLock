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

        // GUID 均已对照 Windows SDK 头文件 msctf.h / 微软文档核实
        private static readonly Guid ClsidTfInputProcessorProfiles =
            new Guid("33C53FB8-4DE9-4B2C-B2B9-3CF6ED2E7030");
        private static readonly Guid CatidTipKeyboard =
            new Guid("34745C63-C2AA-4E08-B8F4-DFC60BF4ACB1");

        [ComImport, Guid("33C53FB8-4DE9-4B2C-B2B9-3CF6ED2E7030")]
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

        private static ITfInputProcessorProfileMgr CreateMgr()
        {
            return (ITfInputProcessorProfileMgr)(object)new TFInputProcessorProfilesClass();
        }

        // 读取当前系统激活的输入法 profile（全局状态，含 TSF 输入法）
        public static bool GetActiveProfile(out TsfProfile profile)
        {
            try
            {
                ITfInputProcessorProfileMgr mgr = CreateMgr();
                Guid cat = CatidTipKeyboard;
                mgr.GetActiveProfile(ref cat, out profile);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log("TSF GetActiveProfile failed: {0}", ex.Message);
                profile = new TsfProfile();
                return false;
            }
        }

        // 激活指定 profile（TSF 输入法或键盘布局）
        public static bool Activate(TsfProfile profile)
        {
            try
            {
                ITfInputProcessorProfileMgr mgr = CreateMgr();
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
                Logger.Log("TSF Activate failed: {0}", ex.Message);
                return false;
            }
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
