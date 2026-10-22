using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PhoneMic
{
    /// <summary>取本机在当前局域网里的 IPv4 地址，显示给用户看。</summary>
    internal static class NetUtil
    {
        /// <summary>
        /// 用「假装要连外网」的办法问系统：走默认路由时你的源地址是谁。
        /// 这比遍历网卡更准 —— 虚拟机网卡、VPN 网卡不会误报。
        /// 不会真的发包。
        /// </summary>
        public static string LocalIPv4()
        {
            try
            {
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect("8.8.8.8", 65530);
                    var ep = s.LocalEndPoint as IPEndPoint;
                    if (ep != null) return ep.Address.ToString();
                }
            }
            catch { }

            // 兜底：遍历网卡
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string ip = ua.Address.ToString();
                        if (ip.StartsWith("169.254.")) continue;   // 自动私有地址，等于没连上
                        return ip;
                    }
                }
            }
            catch { }
            return "未连接网络";
        }
    }
}
