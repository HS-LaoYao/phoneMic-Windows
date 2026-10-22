using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PhoneMic
{
    /// <summary>
    /// 在用户点「录制」时临时挂一个全局键盘钩子，抓取「按了哪些键」。
    ///
    /// 为什么不用 WinForms 的 KeyDown：
    ///   它拿不到左右区分（左 Alt / 右 Alt 都报成 Menu），而 PTT 软件常常是认右 Alt 的。
    ///   底层钩子能直接拿到 0xA4 / 0xA5。
    /// </summary>
    internal sealed class KeyCapture : IDisposable
    {
        /// <summary>抓到组合键时触发（虚拟键码，按按下顺序）</summary>
        public event Action<int[]> Captured;

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private IntPtr _hook = IntPtr.Zero;
        private HookProc _proc;                 // 必须保存，否则会被 GC 回收导致崩溃
        private readonly List<int> _mods = new List<int>();
        private bool _done;

        public bool IsActive { get { return _hook != IntPtr.Zero; } }

        public void Start()
        {
            if (_hook != IntPtr.Zero) return;
            _mods.Clear();
            _done = false;
            _proc = Callback;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        }

        public void Stop()
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
            _proc = null;
        }

        public void Dispose() { Stop(); }

        private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && !_done)
            {
                var info = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                int vk = (int)info.vkCode;
                int msg = wParam.ToInt32();

                bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                // 修正左右修饰键：钩子给出的 vkCode 本身就是 0xA0..0xA5，无需换算
                if (down)
                {
                    if (IsModifier(vk))
                    {
                        if (!_mods.Contains(vk)) _mods.Add(vk);
                        return (IntPtr)1;    // 吞掉，避免被别的程序收到
                    }
                    // 普通键：组合键 = 已按下的修饰键 + 这个键
                    var keys = new List<int>(_mods);
                    keys.Add(vk);
                    Commit(keys.ToArray());
                    return (IntPtr)1;
                }

                if (up && IsModifier(vk))
                {
                    // 只按了一个修饰键就松手 → 单键（比如「左 Alt」）
                    _mods.Remove(vk);
                    if (_mods.Count == 0) Commit(new int[] { vk });
                    return (IntPtr)1;
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private void Commit(int[] keys)
        {
            if (_done) return;
            _done = true;
            var h = Captured;
            // 先解钩再回调，避免回调里动界面时还占着钩子
            IntPtr hook = _hook;
            _hook = IntPtr.Zero;
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            if (h != null) h(keys);
        }

        private static bool IsModifier(int vk)
        {
            return vk == 0xA0 || vk == 0xA1 ||   // Shift
                   vk == 0xA2 || vk == 0xA3 ||   // Ctrl
                   vk == 0xA4 || vk == 0xA5 ||   // Alt
                   vk == 0x5B || vk == 0x5C ||   // Win
                   vk == 0x10 || vk == 0x11 || vk == 0x12; // 通用修饰键（部分键盘会报这个）
        }
    }
}
