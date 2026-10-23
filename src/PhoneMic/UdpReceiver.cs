using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace PhoneMic
{
    /// <summary>
    /// UDP 接收器：唯一的网络入口，电脑侧监听 47810 端口。
    /// 音频、说话开始/结束、心跳、设备发现都走这一个 socket。
    /// </summary>
    internal sealed class UdpReceiver : IDisposable
    {
        private UdpClient _udp;
        private Thread _thread;
        private volatile bool _running;

        /// <summary>收到一个音频包（整包，含 3 字节头）</summary>
        public event Action<byte[], int> AudioReceived;   // (buf, length)
        /// <summary>手机开始说话 —— 应按下配置的键</summary>
        public event Action PttDown;
        /// <summary>手机停止说话 —— 应松开配置的键</summary>
        public event Action PttUp;
        /// <summary>心跳，参数为手机当前模式：0=长按 1=常开</summary>
        public event Action<int> Heartbeat;
        /// <summary>有设备来发现本机</summary>
        public event Action<string> Discovered;           // 参数为对方 IP

        /// <summary>最后一次收到任何包的时间（Stopwatch 时间戳），用于判断掉线</summary>
        public long LastPacketAt { get; private set; }
        /// <summary>对端 IP（最近一次发包的手机）</summary>
        public string PeerIp { get; private set; }

        /// <summary>对端完整地址（IP + 端口），下行音频要往这里发</summary>
        private IPEndPoint _peerEp;

        private int _downSeq;

        /// <summary>把一帧音频发给手机（模式③：手机当耳机）</summary>
        public void SendAudioToPeer(byte[] pcm, int len)
        {
            var ep = _peerEp;
            var s = _udp;
            if (ep == null || s == null || len <= 0) return;
            try
            {
                byte[] pkt = new byte[3 + len];
                pkt[0] = Proto.TAudioDown;
                pkt[1] = (byte)((_downSeq >> 8) & 0xFF);
                pkt[2] = (byte)(_downSeq & 0xFF);
                _downSeq = (_downSeq + 1) & 0xFFFF;
                Buffer.BlockCopy(pcm, 0, pkt, 3, len);
                s.Send(pkt, pkt.Length, ep);
            }
            catch { }
        }

        public string MachineName = Environment.MachineName;

        public void Start()
        {
            if (_running) return;
            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, Proto.Port));
            // 收包缓冲放大一点，避免瞬间堆积被内核丢掉
            try { _udp.Client.ReceiveBufferSize = 1 << 20; } catch { }

            _running = true;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Priority = ThreadPriority.Highest;  // 音频实时性优先
            _thread.Name = "udp-recv";
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { if (_udp != null) _udp.Close(); } catch { }
            _udp = null;
            _thread = null;
        }

        public void Dispose() { Stop(); }

        private void Loop()
        {
            var ep = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                byte[] data;
                try
                {
                    data = _udp.Receive(ref ep);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { continue; }
                catch { continue; }

                if (data == null || data.Length == 0) continue;

                LastPacketAt = Stopwatch.GetTimestamp();
                PeerIp = ep.Address.ToString();
                _peerEp = new IPEndPoint(ep.Address, ep.Port);

                switch (data[0])
                {
                    case Proto.TAudio:
                        if (data.Length > Proto.AudioHeader)
                        {
                            var h = AudioReceived;
                            if (h != null) h(data, data.Length);
                        }
                        break;

                    case Proto.TPttDown:
                        { var h = PttDown; if (h != null) h(); }
                        break;

                    case Proto.TPttUp:
                        { var h = PttUp; if (h != null) h(); }
                        break;

                    case Proto.THeartbeat:
                        {
                            // 心跳要回一个应答：手机靠它判断电脑还活着。
                            // 不回的话电脑关了，手机上仍显示「已连接」，容易误判。
                            try { _udp.Send(new byte[] { Proto.TAck }, 1, ep); } catch { }
                            var h = Heartbeat; if (h != null) h(data.Length > 1 ? data[1] : 0);
                        }
                        break;

                    case Proto.TDiscover:
                        ReplyDiscovery(ep);
                        break;
                }
            }
        }

        /// <summary>回复发现请求，把电脑名字发回去，手机据此知道连上了谁</summary>
        private void ReplyDiscovery(IPEndPoint to)
        {
            try
            {
                byte[] pkt = Proto.BuildDiscoverAck(MachineName);
                _udp.Send(pkt, pkt.Length, to);
                var h = Discovered;
                if (h != null) h(to.Address.ToString());
            }
            catch { }
        }

        /// <summary>毫秒数：距离最后一次收到包过了多久</summary>
        public int MsSinceLastPacket
        {
            get
            {
                long last = LastPacketAt;
                if (last == 0) return int.MaxValue;
                double ms = (Stopwatch.GetTimestamp() - last) * 1000.0 / Stopwatch.Frequency;
                return ms > int.MaxValue ? int.MaxValue : (int)ms;
            }
        }
    }
}
