using System;
using System.Threading;
using System.Windows.Forms;

namespace InputMethodLock
{
    internal static class Program
    {
        // Local\ = 仅当前用户会话内互斥（托盘工具语义）；Global\ 在受限环境可能抛异常
        private const string MutexName = "Local\\InputMethodLock_SingleInstance";

        [STAThread]
        private static void Main()
        {
            bool createdNew;
            Mutex mutex = null;
            try
            {
                mutex = new Mutex(true, MutexName, out createdNew);
            }
            catch
            {
                createdNew = true; // 拿不到锁按放行处理，不因此阻止启动
            }

            if (!createdNew)
            {
                MessageBox.Show("输入法锁定已在运行中（请查看系统托盘）。",
                    "输入法锁定", MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (mutex != null) mutex.Dispose();
                return;
            }

            // 消息泵内异常兜底：托盘常驻工具忌讳静默退出
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                Logger.Log("UI exception: {0}", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Logger.Log("Fatal: {0}", e.ExceptionObject);
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new AppContext());
            }
            catch (Exception ex)
            {
                Logger.Log("Startup failure: {0}", ex);
                MessageBox.Show("输入法锁定启动失败：" + ex.Message,
                    "输入法锁定", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            if (mutex != null)
            {
                mutex.ReleaseMutex();
                mutex.Dispose();
            }
        }
    }
}
