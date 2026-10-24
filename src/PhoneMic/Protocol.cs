using System;
using System.Text;

namespace PhoneMic
{
    /// <summary>
    /// phoneMic 自研协议（V1）。
    ///
    /// 设计原则：极简，单端口，一个字节的类型 + 负载。
    /// 不做 Opus 编码 —— 48kHz/16bit/单声道 裸 PCM 在局域网只有 96KB/s，
    /// 省掉编解码器能同时减小两端体积和出错概率。
    ///
    /// 所有包第一个字节是类型：
    ///   0x01 音频     [01][seq:u16][PCM 960B]   10ms 一帧，共 963 字节（不会 IP 分片）
    ///   0x10 说话开始 [10]
    ///   0x11 说话结束 [11]
    ///   0x12 心跳     [12][mode]               每秒一次，mode: 0=长按 1=常开
    ///   0x20 发现请求 [20]                     手机广播
    ///   0x21 发现应答 [21][nameLen][name utf8] 电脑回复，手机据此得到电脑 IP
    /// </summary>
    internal static class Proto
    {
        /// <summary>电脑监听的 UDP 端口，手机主动发过来</summary>
        public const int Port = 47810;

        /// <summary>程序版本。检查更新时和码云发行版上的 tag 比（改版本记得同步改 installer/phoneMic.iss）</summary>
        public const string Version = "1.2.0";

        public const byte TAudio = 0x01;
        public const byte TPttDown = 0x10;
        public const byte TPttUp = 0x11;
        public const byte THeartbeat = 0x12;
        /// <summary>心跳应答 [22][当前路由模式] —— 手机据此同步电脑端的模式显示</summary>
        public const byte TAck = 0x22;
        public const byte TAudioDown = 0x02;   // 电脑→手机 的音频（手机当耳机时用）
        public const byte TDiscover = 0x20;
        public const byte TDiscoverAck = 0x21;

        /// <summary>手机远程切换电脑端模式 [14][mode:u8]（1~3，同 RouteMode）</summary>
        public const byte TModeSet = 0x14;

        /// <summary>
        /// 「发送键」[13][seq:u16][key:u8] —— 手机上点「发送」，电脑按一次指定键。
        /// 手机会连发 3 个同样 seq 的包防丢；这里按 seq 去重，保证只按一次。
        /// </summary>
        public const byte TSendKey = 0x13;
        public const int SendEnter = 0;         // 回车
        public const int SendShiftEnter = 1;    // Shift + 回车

        public const int SampleRate = 48000;
        public const int FrameSamples = 480;       // 10ms
        public const int FrameBytes = FrameSamples * 2;
        /// <summary>音频包头长度：类型1 + 序号2</summary>
        public const int AudioHeader = 3;

        /// <summary>
        /// 构造发现应答包。带上电脑名字，手机界面上能看到连的是哪台。
        /// </summary>
        public static byte[] BuildDiscoverAck(string name)
        {
            if (string.IsNullOrEmpty(name)) name = "PC";
            // 名字截断到 40 字节，避免包过大
            byte[] nb = Encoding.UTF8.GetBytes(name);
            if (nb.Length > 40) Array.Resize(ref nb, 40);
            byte[] pkt = new byte[2 + nb.Length];
            pkt[0] = TDiscoverAck;
            pkt[1] = (byte)nb.Length;
            Buffer.BlockCopy(nb, 0, pkt, 2, nb.Length);
            return pkt;
        }
    }
}
