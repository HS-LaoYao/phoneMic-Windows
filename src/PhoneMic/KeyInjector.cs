using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PhoneMic
{
    /// <summary>
    /// 往系统里注入按键（SendInput）。
    ///
    /// 用扫描码而不是虚拟键码，兼容性更好 —— 有些程序只认扫描码。
    /// </summary>
    internal static class KeyInjector
    {
        // ===== Win32 =====
        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_SCANCODE = 0x0008;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        /// <summary>按键注入方式</summary>
        public enum InjectMode
        {
            /// <summary>虚拟键码（默认，绝大多数聊天/语音软件认这个）</summary>
            VirtualKey = 0,
            /// <summary>扫描码（部分游戏只认扫描码）</summary>
            ScanCode = 1,
            /// <summary>直接把按键消息投递到前台窗口（给屏蔽了模拟按键的程序用）</summary>
            Message = 2,
            /// <summary>两路一起发：SendInput + 消息投递（最不容易漏）</summary>
            Dual = 3
        }

        public static InjectMode Mode = InjectMode.VirtualKey;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const uint WM_SYSKEYDOWN = 0x0104;
        private const uint WM_SYSKEYUP = 0x0105;

        /// <summary>读某个键现在是不是按下的（用来验证注入有没有生效）</summary>
        public static bool IsKeyDown(int vk)
        {
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        /// <summary>当前前台窗口属于哪个进程（判断焦点是不是在目标软件上）</summary>
        public static string ForegroundProcessName()
        {
            try
            {
                IntPtr h = GetForegroundWindow();
                if (h == IntPtr.Zero) return "";
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid == 0) return "";
                return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
            }
            catch { return ""; }
        }

        /// <summary>当前是否处于「按住」状态，用于保证幂等（不会重复按下）</summary>
        private static bool _held;
        private static int[] _heldKeys = new int[0];

        /// <summary>
        /// 按下整组键（按配置顺序依次按下，例如先 Ctrl 再 Alt 再 Space）。
        /// 返回 false 表示系统拒绝了这次注入（比如目标窗口以管理员运行）。
        /// </summary>
        public static bool Press(int[] keys)
        {
            if (keys == null || keys.Length == 0) return true;
            if (_held && SameKeys(keys)) return true;     // 已经按着了，不重复

            ReleaseAll();                                 // 万一之前按的是别的组合，先松开
            bool ok = true;
            foreach (int vk in keys) ok &= Send(vk, true);
            _heldKeys = keys;
            _held = true;
            return ok;
        }

        /// <summary>松开整组键（逆序松开）</summary>
        public static bool ReleaseAll()
        {
            if (!_held) return true;
            bool ok = true;
            for (int i = _heldKeys.Length - 1; i >= 0; i--) ok &= Send(_heldKeys[i], false);
            _heldKeys = new int[0];
            _held = false;
            return ok;
        }

        public static bool IsHeld { get { return _held; } }

        /// <summary>
        /// 按住期间重发一次「按下」。
        /// 真键盘按住时会不断产生重复的 KEYDOWN，有些程序就是靠这个判断「还按着」；
        /// 而 SendInput 只发一次，所以它们会认为已经松开了。
        /// </summary>
        public static bool Repeat()
        {
            if (!_held) return true;
            bool ok = true;
            foreach (int vk in _heldKeys) ok &= Send(vk, true);
            return ok;
        }

        /// <summary>测试注入：按下 → 等一会 → 松开，返回是否每次都成功</summary>
        public static bool TestPress(int vk, int holdMs)
        {
            bool ok = Send(vk, true);
            System.Threading.Thread.Sleep(holdMs);
            ok &= IsKeyDown(vk);          // 读回来确认系统认了这个按下
            ok &= Send(vk, false);
            return ok;
        }

        private static bool SameKeys(int[] a)
        {
            if (a.Length != _heldKeys.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != _heldKeys[i]) return false;
            return true;
        }

        /// <summary>
        /// 把「左右修饰键」换成通用键。
        ///
        /// 关键点：VK_LMENU(0xA4) / VK_RMENU(0xA5) 这类左右专用键，直接丢给
        /// MapVirtualKey 有可能返回 0。这样一来发出去的就是「扫描码 = 0」的
        /// 无效事件 —— 表现就是按了完全没反应。所以先换成 VK_MENU(0x12) 再取扫描码，
        /// 右手的键额外加扩展标志。
        /// </summary>
        private static void ToBaseKey(int vk, out int baseVk, out bool extended)
        {
            extended = false;
            baseVk = vk;
            switch (vk)
            {
                case 0xA0: baseVk = 0x10; break;                    // 左 Shift
                case 0xA1: baseVk = 0x10; extended = true; break;   // 右 Shift
                case 0xA2: baseVk = 0x11; break;                    // 左 Ctrl
                case 0xA3: baseVk = 0x11; extended = true; break;   // 右 Ctrl
                case 0xA4: baseVk = 0x12; break;                    // 左 Alt
                case 0xA5: baseVk = 0x12; extended = true; break;   // 右 Alt
                default: extended = IsExtended(vk); break;
            }
        }

        private static bool Send(int vk, bool down)
        {
            int baseVk;
            bool ext;
            ToBaseKey(vk, out baseVk, out ext);

            if (Mode == InjectMode.Message)
                return SendByMessage(baseVk, ext, down);

            uint flags = 0;
            ushort wVk = (ushort)baseVk;
            ushort wScan = 0;

            if (Mode == InjectMode.ScanCode)
            {
                uint sc = MapVirtualKey((uint)baseVk, 0);
                if (sc != 0)
                {
                    wVk = 0;                  // 用扫描码时 wVk 必须为 0
                    wScan = (ushort)sc;
                    flags = KEYEVENTF_SCANCODE;
                }
            }
            if (ext) flags |= KEYEVENTF_EXTENDEDKEY;
            if (!down) flags |= KEYEVENTF_KEYUP;

            var input = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = wVk,
                        wScan = wScan,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
            var arr = new INPUT[1];
            arr[0] = input;
            bool ok = SendInput(1, arr, Marshal.SizeOf(typeof(INPUT))) == 1;

            // 双路模式：再补一份窗口消息，防止目标程序只认消息不认注入
            if (Mode == InjectMode.Dual) SendByMessage(baseVk, ext, down);
            return ok;
        }

        /// <summary>
        /// 把按键直接投递到当前前台窗口。
        /// 有些程序会过滤掉 SendInput 产生的「注入」事件，但对正常的窗口消息照收不误，
        /// 这条路就是给它们准备的。缺点是只影响那一个窗口，而且不改变全局键状态。
        /// </summary>
        private static bool SendByMessage(int baseVk, bool ext, bool down)
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return false;

            bool isAlt = baseVk == 0x12;
            uint msg = down
                ? (isAlt ? WM_SYSKEYDOWN : WM_KEYDOWN)
                : (isAlt ? WM_SYSKEYUP : WM_KEYUP);

            uint sc = MapVirtualKey((uint)baseVk, 0);
            int lp = 1 | ((int)sc << 16);
            if (ext) lp |= 1 << 24;
            if (isAlt) lp |= 1 << 29;                 // context code
            if (!down) lp |= unchecked((1 << 30) | (int)0x80000000);   // 上一状态 / 抬起

            return PostMessage(h, msg, (IntPtr)baseVk, (IntPtr)lp);
        }

        /// <summary>这些键要用「扩展键」标志，否则会被当成小键盘上对应的键</summary>
        private static bool IsExtended(int vk)
        {
            switch (vk)
            {
                case 0xA3: // 右 Ctrl
                case 0xA5: // 右 Alt
                case 0x21: // PageUp
                case 0x22: // PageDown
                case 0x23: // End
                case 0x24: // Home
                case 0x25: // ←
                case 0x26: // ↑
                case 0x27: // →
                case 0x28: // ↓
                case 0x2D: // Insert
                case 0x2E: // Delete
                case 0x6F: // 小键盘 /
                case 0x90: // NumLock
                    return true;
            }
            return false;
        }

        // ===== 给界面显示用的键名 =====

        private static readonly Dictionary<int, string> Names = new Dictionary<int, string>
        {
            {0xA0,"左 Shift"},{0xA1,"右 Shift"},{0xA2,"左 Ctrl"},{0xA3,"右 Ctrl"},
            {0xA4,"左 Alt"},{0xA5,"右 Alt"},{0x5B,"左 Win"},{0x5C,"右 Win"},
            {0x0D,"Enter"},{0x20,"空格"},{0x09,"Tab"},{0x1B,"Esc"},{0x08,"退格"},
            {0x14,"CapsLock"},{0x2C,"PrintScreen"},{0x91,"ScrollLock"},{0x13,"Pause"},
            {0x2D,"Insert"},{0x2E,"Delete"},{0x24,"Home"},{0x23,"End"},
            {0x21,"PageUp"},{0x22,"PageDown"},{0x25,"←"},{0x26,"↑"},{0x27,"→"},{0x28,"↓"},
            {0xC0,"`"},{0xBD,"-"},{0xBB,"="},{0xDB,"["},{0xDD,"]"},{0xDC,"\\"},
            {0xBA,";"},{0xDE,"'"},{0xBC,","},{0xBE,"."},{0xBF,"/"},{0x6F,"小键盘 /"},
            {0x6A,"小键盘 *"},{0x6D,"小键盘 -"},{0x6B,"小键盘 +"},{0x6E,"小键盘 ."},
        };

        public static string Name(int vk)
        {
            string s;
            if (Names.TryGetValue(vk, out s)) return s;
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);      // F1..F24
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();  // 0..9
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();  // A..Z
            if (vk >= 0x60 && vk <= 0x69) return "小键盘 " + (vk - 0x60);
            return "VK_" + vk.ToString("X2");
        }

        public static string Describe(int[] keys)
        {
            if (keys == null || keys.Length == 0) return "（不映射）";
            var parts = new string[keys.Length];
            for (int i = 0; i < keys.Length; i++) parts[i] = Name(keys[i]);
            return string.Join(" + ", parts);
        }
    }
}
