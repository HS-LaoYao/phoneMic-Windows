using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PhoneMic
{
    /// <summary>一个「可以选的作用目标」—— 任务栏上看得见的那种窗口。</summary>
    internal sealed class WinTarget
    {
        /// <summary>进程名，不含 .exe，如 WorkBuddy。真正用来匹配的键（HWND 会变，进程名不会）</summary>
        public string Exe;
        public string Title;
        public IntPtr Handle;

        public string Display
        {
            get
            {
                string t = string.IsNullOrEmpty(Title) ? "" : Title;
                if (t.Length > 26) t = t.Substring(0, 26) + "…";
                return Exe + (t.Length > 0 ? "　" + t : "");
            }
        }

        public override string ToString() { return Display; }
    }

    /// <summary>
    /// 枚举「任务栏上的窗口」并把某个窗口切到前台。
    ///
    /// 为什么不列所有进程：系统后台进程有上百个，绝大多数根本不接收键盘。
    /// 任务栏里那几个就是用户心里「正在用的程序」，用的是系统 Alt-Tab 的同一套判定规则。
    ///
    /// 为什么不直接存 HWND：窗口一旦重建（程序重启、切换会话）句柄就失效了。
    /// 所以记住**进程名**，每次真的要按键时再实时查一遍 HWND。
    /// </summary>
    internal static class WinTargetFinder
    {
        private delegate bool EnumWindowsProc(IntPtr h, IntPtr l);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("user32.dll")]
        private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

        private const uint GW_OWNER = 4;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_APPWINDOW = 0x00040000;
        private const int SW_RESTORE = 9;
        private const int SW_SHOW = 5;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const byte VK_MENU = 0x12;

        public static IntPtr Current() { return GetForegroundWindow(); }

        /// <summary>句柄还有效吗（窗口可能已经被关掉了）</summary>
        public static bool IsAlive(IntPtr h) { return h != IntPtr.Zero && IsWindow(h); }

        /// <summary>任务栏上会出现的那些窗口（Alt-Tab 列表的判定规则）</summary>
        public static List<WinTarget> Enumerate()
        {
            var list = new List<WinTarget>();
            var seen = new HashSet<string>();
            uint selfPid = (uint)Process.GetCurrentProcess().Id;

            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                try
                {
                    if (!IsWindowVisible(h)) return true;
                    if (GetWindowTextLength(h) == 0) return true;

                    int ex = GetWindowLong(h, GWL_EXSTYLE);
                    if ((ex & WS_EX_TOOLWINDOW) != 0) return true;          // 工具窗口不在任务栏

                    bool owned = GetWindow(h, GW_OWNER) != IntPtr.Zero;
                    if (owned && (ex & WS_EX_APPWINDOW) == 0) return true;  // 有宿主且没声明是应用窗口

                    uint pid;
                    GetWindowThreadProcessId(h, out pid);
                    if (pid == 0 || pid == selfPid) return true;            // 跳过自己

                    string title = GetTitle(h);
                    if (string.IsNullOrWhiteSpace(title)) return true;

                    string exe = ProcessName(pid);
                    if (string.IsNullOrEmpty(exe)) return true;

                    var t = new WinTarget { Exe = exe, Title = title, Handle = h };
                    string key = exe + "|" + title;
                    if (seen.Add(key)) list.Add(t);
                }
                catch { }
                return true;
            }, IntPtr.Zero);

            list.Sort((a, b) => string.Compare(a.Exe, b.Exe, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        /// <summary>按进程名（+ 可选标题片段）找到它现在的窗口句柄</summary>
        public static IntPtr Find(string exe, string titlePart)
        {
            if (string.IsNullOrEmpty(exe)) return IntPtr.Zero;
            var all = Enumerate();

            foreach (var t in all)
            {
                if (!Same(t.Exe, exe)) continue;
                if (!string.IsNullOrEmpty(titlePart) && t.Title.IndexOf(titlePart) < 0) continue;
                return t.Handle;
            }
            // 标题对不上（比如程序换了窗口标题）就只按进程名退而求其次
            foreach (var t in all)
                if (Same(t.Exe, exe)) return t.Handle;

            return IntPtr.Zero;
        }

        /// <summary>
        /// 把窗口切到前台。Windows 有「前台锁」，后台进程直接 SetForegroundWindow 会被静默拒绝，
        /// 所以要先把输入线程挂上（AttachThreadInput），再模拟按一下 Alt 解锁，最后才切。
        /// </summary>
        public static bool Activate(IntPtr h)
        {
            if (h == IntPtr.Zero || !IsWindow(h)) return false;
            if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
            else ShowWindow(h, SW_SHOW);

            IntPtr fg = GetForegroundWindow();
            if (fg == h) return true;

            uint pid;
            uint fgTid = GetWindowThreadProcessId(fg, out pid);
            uint myTid = GetCurrentThreadId();
            bool attached = false;
            if (fgTid != 0 && fgTid != myTid) attached = AttachThreadInput(myTid, fgTid, true);

            // 解前台锁（AutoHotkey 的经典做法）
            keybd_event(VK_MENU, 0, 0, IntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, IntPtr.Zero);

            bool ok = SetForegroundWindow(h);
            if (attached) AttachThreadInput(myTid, fgTid, false);

            return ok && GetForegroundWindow() == h;
        }

        private static bool Same(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetTitle(IntPtr h)
        {
            int n = GetWindowTextLength(h);
            if (n <= 0) return "";
            var sb = new StringBuilder(n + 1);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString().Trim();
        }

        private static string ProcessName(uint pid)
        {
            try
            {
                using (var p = Process.GetProcessById((int)pid))
                    return p.ProcessName;
            }
            catch { return ""; }
        }
    }
}
