using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PhoneMic
{
    /// <summary>
    /// 配置读写。为了不引入 JSON 库，用最朴素的 key=value 文本，
    /// 存在 %APPDATA%\phoneMic\config.ini。
    /// </summary>
    internal sealed class Config
    {
        public string DeviceId = "";              // 输出设备 ID，空 = 自动挑虚拟声卡
        public int[] Keys = new int[0];           // 说话时要按住的键（虚拟键码，按顺序按下）
        public bool MinimizeToTray = true;        // 关窗口是否最小化到托盘
        public bool AutoStartOnLaunch = true;     // 启动即开始监听
        public int KeyMode = 0;                   // 按键注入方式：0=虚拟键码 1=扫描码 2=消息投递 3=双路

        // ===== 作用窗口：选定程序后，说话前自动把它切到前台 =====
        public string TargetExe = "";             // 目标进程名（不含 .exe），空 = 跟随当前前台窗口
        public string TargetTitle = "";           // 窗口标题片段，同名进程多开时用来定位
        public int DeliveryMode = 0;                 // 投递方式：0后台 1后台+子窗口 2切前台 3两条路

        // ===== 音频路由 =====
        public int RouteMode = 1;                 // 1=只当麦克风 2=麦克风+监听 3=手机当耳机
        public int MonitorVolume = 60;            // 监听音量 0~100
        public bool MutePcInHeadset = false;      // 模式③时是否静音电脑扬声器

        // 「一键设为默认麦克风」之前系统用的设备，用来还原
        public string PrevConsoleId = "";
        public string PrevCommId = "";

        private static string Dir
        {
            get
            {
                string d = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phoneMic");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        private static string FilePath { get { return Path.Combine(Dir, "config.ini"); } }

        public static Config Load()
        {
            var c = new Config();
            try
            {
                if (!File.Exists(FilePath)) return c;
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "deviceId": c.DeviceId = v; break;
                        case "keys": c.Keys = ParseKeys(v); break;
                        case "minimizeToTray": c.MinimizeToTray = v == "1"; break;
                        case "autoStart": c.AutoStartOnLaunch = v == "1"; break;
                        case "keyMode": c.KeyMode = int.TryParse(v, out int km) ? km : 0; break;
                        case "targetExe": c.TargetExe = v; break;
                        case "targetTitle": c.TargetTitle = v; break;
                        case "deliveryMode": c.DeliveryMode = int.TryParse(v, out int dm) ? dm : 0; break;
                        case "routeMode": c.RouteMode = int.TryParse(v, out int rm) ? rm : 1; break;
                        case "monitorVolume": c.MonitorVolume = int.TryParse(v, out int mv) ? mv : 60; break;
                        case "mutePcInHeadset": c.MutePcInHeadset = v == "1"; break;
                        case "useScanCode": if (v == "1") c.KeyMode = 1; break;
                        case "prevConsole": c.PrevConsoleId = v; break;
                        case "prevComm": c.PrevCommId = v; break;
                    }
                }
            }
            catch { /* 配置坏了就用默认值，不该影响启动 */ }
            return c;
        }

        public void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# phoneMic 配置（删掉这个文件会恢复默认）");
                sb.AppendLine("deviceId=" + DeviceId);
                sb.AppendLine("keys=" + FormatKeys(Keys));
                sb.AppendLine("minimizeToTray=" + (MinimizeToTray ? "1" : "0"));
                sb.AppendLine("autoStart=" + (AutoStartOnLaunch ? "1" : "0"));
                sb.AppendLine("keyMode=" + KeyMode);
                sb.AppendLine("targetExe=" + TargetExe);
                sb.AppendLine("targetTitle=" + TargetTitle);
                sb.AppendLine("deliveryMode=" + DeliveryMode);
                sb.AppendLine("routeMode=" + RouteMode);
                sb.AppendLine("monitorVolume=" + MonitorVolume);
                sb.AppendLine("mutePcInHeadset=" + (MutePcInHeadset ? "1" : "0"));
                sb.AppendLine("prevConsole=" + PrevConsoleId);
                sb.AppendLine("prevComm=" + PrevCommId);
                File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        // ===== 按键的字符串表示：虚拟键码的十六进制，用逗号分隔，如 "A4" 或 "11,20" =====

        public static int[] ParseKeys(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new int[0];
            var list = new List<int>();
            foreach (string part in s.Split(','))
            {
                int v;
                if (int.TryParse(part.Trim(), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out v) && v > 0)
                    list.Add(v);
            }
            return list.ToArray();
        }

        public static string FormatKeys(int[] keys)
        {
            if (keys == null || keys.Length == 0) return "";
            var parts = new string[keys.Length];
            for (int i = 0; i < keys.Length; i++) parts[i] = keys[i].ToString("X2");
            return string.Join(",", parts);
        }
    }
}
