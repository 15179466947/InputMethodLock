using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace InputMethodLock
{
    // 简单 key=value 配置文件（%AppData%\InputMethodLock\config.ini）
    public class Config
    {
        public bool LockEnabled = true;
        public string Mode = "english";                 // english | layout
        public string TargetLayout = "00000409";        // 键盘布局 KLID，如 00000409 美式英文
        public string HotkeyModifiers = "Control";      // None|Alt|Control|Shift 组合（+ 号分隔）
        public string HotkeyKey = "Oemtilde";           // Keys 枚举名，None=不启用热键
        public string UnlockLayout = "auto";            // auto=解锁恢复快照 | none=不切换 | hkl:XXXXXXXX=指定
        public List<string> Exceptions = new List<string>(); // 例外进程名（不锁定）
        public bool StartWithWindows = false;

        public static string GetConfigDir()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "InputMethodLock");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        private static string GetConfigPath()
        {
            return Path.Combine(GetConfigDir(), "config.ini");
        }

        public static Config Load()
        {
            var cfg = new Config();
            try
            {
                string path = GetConfigPath();
                if (!File.Exists(path)) return cfg;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string l = line.Trim();
                    if (l.Length == 0 || l.StartsWith("#") || l.StartsWith(";")) continue;
                    int eq = l.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = l.Substring(0, eq).Trim();
                    string val = l.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "LockEnabled": cfg.LockEnabled = val == "true"; break;
                        // 白名单校验，防止新增模式时漏改分支被静默降级（v0.7 修复 chinese 被吞）
                        case "Mode":
                            string m = val.ToLowerInvariant();
                            cfg.Mode = (m == "layout" || m == "chinese") ? m : "english";
                            break;
                        case "TargetLayout": cfg.TargetLayout = val; break;
                        case "HotkeyModifiers": cfg.HotkeyModifiers = val; break;
                        case "HotkeyKey": cfg.HotkeyKey = val; break;
                        case "UnlockLayout":
                            // v0.10 起默认 auto（恢复快照）；旧配置的 none 是默认值而非用户选择，一并升级
                            cfg.UnlockLayout = string.IsNullOrEmpty(val) || val == "none" ? "auto" : val;
                            break;
                        case "Exceptions": cfg.Exceptions = ParseList(val); break;
                        case "StartWithWindows": cfg.StartWithWindows = val == "true"; break;
                    }
                }
            }
            catch { }
            return cfg;
        }

        public void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# InputMethodLock 配置");
                sb.AppendLine("LockEnabled=" + (LockEnabled ? "true" : "false"));
                sb.AppendLine("Mode=" + Mode);
                sb.AppendLine("TargetLayout=" + TargetLayout);
                sb.AppendLine("HotkeyModifiers=" + HotkeyModifiers);
                sb.AppendLine("HotkeyKey=" + HotkeyKey);
                sb.AppendLine("UnlockLayout=" + UnlockLayout);
                sb.AppendLine("Exceptions=" + JoinList(Exceptions));
                sb.AppendLine("StartWithWindows=" + (StartWithWindows ? "true" : "false"));
                File.WriteAllText(GetConfigPath(), sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        private static List<string> ParseList(string val)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(val)) return list;
            foreach (string item in val.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                list.Add(item.Trim());
            return list;
        }

        private static string JoinList(List<string> list)
        {
            return string.Join(",", list != null ? list.ToArray() : new string[0]);
        }
    }
}
