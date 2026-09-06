using System;
using Microsoft.Win32;

namespace InputMethodLock
{
    // 开机自启（HKCU\Software\Microsoft\Windows\CurrentVersion\Run）
    public static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "InputMethodLock";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    if (key == null) return false;
                    object val = key.GetValue(ValueName);
                    return val != null && val.ToString().Length > 0;
                }
            }
            catch { return false; }
        }

        public static void Set(bool enable, string exePath)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return;
                    if (enable)
                        key.SetValue(ValueName, "\"" + exePath + "\"");
                    else
                        key.DeleteValue(ValueName, false);
                }
            }
            catch { }
        }
    }
}
