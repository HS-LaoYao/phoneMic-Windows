using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace PhoneMic
{
    internal static class Program
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [STAThread]
        private static void Main()
        {
            // 单实例：重复双击时不再弹「已经在运行」，而是把已有窗口叫回前台，
            // 这样即使托盘图标被系统折叠起来找不着，也能靠再次双击找回来。
            bool created;
            using (var mutex = new Mutex(true, "phoneMic_single_instance", out created))
            {
                if (!created)
                {
                    BringExistingToFront();
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // 任何未捕获异常都不要让程序直接崩掉，至少弹个提示
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    ShowError(e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    ShowError(e.ExceptionObject as Exception);
                };

                Application.Run(new MainForm());
            }
        }

        /// <summary>把已经在运行的那个实例的窗口显示出来并置前</summary>
        private static void BringExistingToFront()
        {
            IntPtr h = FindWindow(null, "phoneMic");
            if (h != IntPtr.Zero)
            {
                ShowWindow(h, 9);          // SW_RESTORE：从最小化 / 隐藏状态恢复
                SetForegroundWindow(h);
                return;
            }
            MessageBox.Show(
                "phoneMic 已经在运行了。\n\n" +
                "如果没看到窗口，看任务栏右下角的托盘区域（Win11 可能收在 ^ 小箭头里）。",
                "phoneMic", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void ShowError(Exception ex)
        {
            try
            {
                MessageBox.Show("程序出错了：\n\n" + (ex != null ? ex.Message : "未知错误"),
                    "phoneMic", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
    }
}
