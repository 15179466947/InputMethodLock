using System;
using System.IO;
using System.Text;

namespace InputMethodLock
{
    // 最小诊断日志：%AppData%\InputMethodLock\debug.log，超过 256KB 截断
    internal static class Logger
    {
        private static readonly object _gate = new object();

        public static void Log(string message)
        {
            try
            {
                lock (_gate)
                {
                    string path = Path.Combine(Config.GetConfigDir(), "debug.log");
                    FileInfo info = new FileInfo(path);
                    if (info.Exists && info.Length > 256 * 1024)
                        File.WriteAllText(path, "", Encoding.UTF8);
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff",
                        System.Globalization.CultureInfo.InvariantCulture) + " " + message + Environment.NewLine;
                    File.AppendAllText(path, line, Encoding.UTF8);
                }
            }
            catch { }
        }

        public static void Log(string format, params object[] args)
        {
            try
            {
                Log(string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args));
            }
            catch { }
        }
    }
}
