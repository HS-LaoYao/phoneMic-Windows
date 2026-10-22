using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PhoneMic
{
    /// <summary>
    /// 虚拟声卡（VB-CABLE）的检测 / 一键安装 / 一键卸载。
    ///
    /// 为什么需要它：Windows 没有内置「可以往里写声音的麦克风」，
    /// 任何软件要扮演麦克风，都必须有一个虚拟声卡驱动做中转。
    /// 我们不在安装包里内置驱动（避免版权问题），而是安装时从官方地址下载 1.1MB 的包再静默安装。
    /// </summary>
    internal static class VbCable
    {
        public const string DownloadUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack43.zip";
        private const string InstallDir = @"C:\Program Files\VB\CABLE";

        /// <summary>系统里是否存在虚拟声卡（播放设备或录音设备里带 CABLE）</summary>
        public static bool IsInstalled()
        {
            try
            {
                foreach (var d in AudioOutput.ListDevices())
                {
                    if (d.Name != null && d.Name.IndexOf("CABLE", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 下载并静默安装。会弹出一次 UAC（装驱动必须有管理员权限）。
        /// </summary>
        public static void InstallAsync(Action<bool, string> done)
        {
            Task.Run(() =>
            {
                try
                {
                    string tmp = Path.Combine(Path.GetTempPath(), "phoneMic_vbcable");
                    if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                    Directory.CreateDirectory(tmp);

                    string zip = Path.Combine(tmp, "vbcable.zip");
                    using (var wc = new WebClient())
                    {
                        wc.Headers.Add("User-Agent", "phoneMic");
                        wc.DownloadFile(DownloadUrl, zip);
                    }
                    ZipFile.ExtractToDirectory(zip, tmp);

                    string setup = Path.Combine(tmp, "VBCABLE_Setup_x64.exe");
                    if (!File.Exists(setup)) setup = Path.Combine(tmp, "VBCABLE_Setup.exe");
                    if (!File.Exists(setup))
                    {
                        done(false, "下载包已解压，但没找到安装程序，请手动运行：" + tmp);
                        return;
                    }

                    var psi = new ProcessStartInfo
                    {
                        FileName = setup,
                        Arguments = "-i -h",           // -i 安装，-h 隐藏界面（静默）
                        UseShellExecute = true,
                        Verb = "runas",                // 以管理员身份运行
                        WorkingDirectory = tmp
                    };
                    var p = Process.Start(psi);
                    if (p != null) p.WaitForExit(180000);

                    done(true, "虚拟声卡已安装。建议重启一次电脑，让音频设备生效。");
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    done(false, "已取消管理员授权，虚拟声卡没有安装。");
                }
                catch (Exception ex)
                {
                    done(false, "安装失败：" + ex.Message);
                }
            });
        }

        /// <summary>彻底卸载：调用官方卸载程序 + 删除我们的配置</summary>
        public static void UninstallAsync(Action<bool, string> done)
        {
            Task.Run(() =>
            {
                try
                {
                    string setup = Path.Combine(InstallDir, "VBCABLE_Setup_x64.exe");
                    if (!File.Exists(setup)) setup = Path.Combine(InstallDir, "VBCABLE_Setup.exe");

                    if (File.Exists(setup))
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = setup,
                            Arguments = "-u -h",       // -u 卸载，-h 隐藏界面
                            UseShellExecute = true,
                            Verb = "runas",
                            WorkingDirectory = InstallDir
                        };
                        var p = Process.Start(psi);
                        if (p != null) p.WaitForExit(180000);
                    }

                    // 删掉本程序的配置文件
                    try
                    {
                        string cfgDir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phoneMic");
                        if (Directory.Exists(cfgDir)) Directory.Delete(cfgDir, true);
                    }
                    catch { }

                    done(true, "已卸载虚拟声卡并删除配置。请重启一次电脑完成清理，之后可以直接删除本程序所在文件夹。");
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    done(false, "已取消管理员授权，卸载未完成。");
                }
                catch (Exception ex)
                {
                    done(false, "卸载失败：" + ex.Message);
                }
            });
        }
    }
}
