using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PhoneMic
{
    /// <summary>
    /// 检查更新：从码云的「发行版」读最新版本号，比当前新就下载安装包。
    ///
    /// 版本信息只从 Gitee 读 —— Gitee 是主仓库，GitHub 只是跑 CI 的镜像。
    /// 不引第三方 JSON 库，用正则抓两个字段就够了，包体也省事。
    /// </summary>
    internal static class Updater
    {
        public const string Api =
            "https://gitee.com/api/v5/repos/heishanyao_admin/phoneMic-Windows/releases/latest";

        internal sealed class UpdateInfo
        {
            public string Version;   // 远端版本号，如 1.0.3
            public string Url;       // 安装包下载地址
            public string Error;     // null 表示查询成功
        }

        public static UpdateInfo Check()
        {
            var info = new UpdateInfo();
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                var req = (HttpWebRequest)WebRequest.Create(Api);
                req.Method = "GET";
                req.Timeout = 10000;
                req.ReadWriteTimeout = 10000;
                req.UserAgent = "phoneMic";
                req.Accept = "application/json";

                string s;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    s = rd.ReadToEnd();

                info.Version = First(s, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");

                // 资产里可能有多个文件，只认 .exe 安装包
                foreach (string u in All(s, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\""))
                {
                    if (u.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { info.Url = u; break; }
                }
                if (string.IsNullOrEmpty(info.Url))
                {
                    foreach (string u in All(s, "\"download_url\"\\s*:\\s*\"([^\"]+)\""))
                    {
                        if (u.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { info.Url = u; break; }
                    }
                }

                if (string.IsNullOrEmpty(info.Version)) info.Error = "没读到发行版信息";
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
            }
            return info;
        }

        /// <summary>版本号比较：1.0.10 比 1.0.2 新（按段拆开比数字，不能用字符串比）</summary>
        public static bool IsNewer(string remote, string current)
        {
            string[] a = Split(remote);
            string[] b = Split(current);
            int n = Math.Max(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                int x = i < a.Length ? Num(a[i]) : 0;
                int y = i < b.Length ? Num(b[i]) : 0;
                if (x != y) return x > y;
            }
            return false;
        }

        /// <summary>下载安装包并直接运行（安装程序会自己接管升级）</summary>
        public static string DownloadAndRun(string url, string version)
        {
            string dir = Path.Combine(Path.GetTempPath(), "phoneMic-update");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "phoneMic-setup-" + version + ".exe");

            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = 20000;
            req.UserAgent = "phoneMic";
            req.AllowAutoRedirect = true;

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var src = resp.GetResponseStream())
            using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write))
            {
                byte[] buf = new byte[65536];
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0) fs.Write(buf, 0, n);
            }

            System.Diagnostics.Process.Start(file);
            return file;
        }

        private static string[] Split(string v)
        {
            string s = string.IsNullOrEmpty(v) ? "" : v.Trim();
            if (s.StartsWith("v") || s.StartsWith("V")) s = s.Substring(1);
            return s.Split('.');
        }

        private static int Num(string s)
        {
            int n;
            return int.TryParse(s.Trim(), out n) ? n : 0;
        }

        private static string First(string s, string pattern)
        {
            var m = Regex.Match(s, pattern);
            return m.Success ? Unescape(m.Groups[1].Value) : null;
        }

        private static IEnumerable<string> All(string s, string pattern)
        {
            foreach (Match m in Regex.Matches(s, pattern))
                yield return Unescape(m.Groups[1].Value);
        }

        /// <summary>有些接口会把 / 转义成 \/，还原掉</summary>
        private static string Unescape(string s)
        {
            return s.Replace("\\/", "/").Replace("\\\"", "\"");
        }
    }
}
