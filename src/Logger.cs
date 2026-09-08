using System;
using System.IO;
using System.Text;

namespace InputMethodLock
{
    // 最小诊断日志：%AppData%\InputMethodLock\debug.log，超过 256KB 截断
    internal static class Logger
    {
        private static readonly object _gate = new object();
        private static readonly System.Collections.Generic.Dictionary<string, int> _throttle =
            new System.Collections.Generic.Dictionary<string, int>();

        // 限流开关：同一个 key 在 intervalMs 内只允许通过一次。
        // 轮询 100ms 一次，同样的失败每秒会记 10 条，256KB 日志几分钟就写满，
        // 真正有用的首条反而被冲掉。
        public static bool Throttle(string key, int intervalMs)
        {
            lock (_gate)
            {
                int last;
                if (_throttle.TryGetValue(key, out last)
                    && Environment.TickCount - last < intervalMs)
                    return false;
                _throttle[key] = Environment.TickCount;
                return true;
            }
        }

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
