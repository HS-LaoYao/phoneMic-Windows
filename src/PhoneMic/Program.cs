using System;
using System.Threading;
using System.Windows.Forms;

namespace PhoneMic
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // 单实例：重复打开就把已开的那个窗口叫出来
            bool created;
            using (var mutex = new Mutex(true, "phoneMic_single_instance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("phoneMic 已经在运行了，看任务栏右下角的托盘图标。",
                        "phoneMic", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
