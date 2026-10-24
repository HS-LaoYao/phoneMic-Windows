using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PhoneMic
{
    /// <summary>
    /// 把手机发来的裸 PCM 播放到指定声卡（虚拟声卡）。
    ///
    /// 为什么要转成立体声：
    ///   WASAPI 共享模式对声道数/采样格式比较挑，48kHz/16bit/双声道几乎被所有设备接受，
    ///   单声道反而不一定。所以内部统一「单声道进来 → 复制成左右声道再播」。
    /// </summary>
    internal sealed class AudioOutput : IDisposable
    {
        private WasapiOut _out;
        private BufferedWaveProvider _buffer;
        private byte[] _stereo = new byte[Proto.FrameBytes * 2];

        public event Action<string> Error;

        /// <summary>播放音量 0~1（监听模式用来调大小，不动系统音量）</summary>
        public float Volume = 1f;

        /// <summary>列出所有正在工作的播放设备</summary>
        public static List<DevInfo> ListDevices()
        {
            var list = new List<DevInfo>();
            try
            {
                using (var en = new MMDeviceEnumerator())
                {
                    foreach (MMDevice d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    {
                        list.Add(new DevInfo { Id = d.ID, Name = d.FriendlyName });
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception("枚举音频设备失败：" + ex.Message, ex);
            }
            return list;
        }

        /// <summary>挑一个最像虚拟声卡的设备（名字里带 CABLE / Virtual / 虚拟）</summary>
        public static DevInfo GuessVirtualCable(List<DevInfo> devices)
        {
            if (devices == null || devices.Count == 0) return null;
            string[] needles = { "cable input", "cable", "virtual", "vb-audio", "voicemeeter", "虚拟" };
            foreach (string n in needles)
            {
                foreach (var d in devices)
                {
                    if (d.Name != null && d.Name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                        return d;
                }
            }
            return devices[0];
        }

        /// <summary>
        /// 开始播放。deviceId 为空则自动挑虚拟声卡。
        /// </summary>
        public void Start(string deviceId)
        {
            Stop();

            var devices = ListDevices();
            if (devices.Count == 0) throw new Exception("系统里没有任何播放设备");

            DevInfo target = null;
            if (!string.IsNullOrEmpty(deviceId))
            {
                foreach (var d in devices) if (d.Id == deviceId) { target = d; break; }
            }
            if (target == null) target = GuessVirtualCable(devices);

            var fmt = new WaveFormat(Proto.SampleRate, 16, 2);
            _buffer = new BufferedWaveProvider(fmt);
            _buffer.ReadFully = true;                    // 没数据时输出静音，不报错、不卡顿
            _buffer.DiscardOnBufferOverflow = true;      // 手机发太快就丢最旧的，绝不越积越多
            _buffer.BufferDuration = TimeSpan.FromMilliseconds(300);

            MMDevice dev;
            using (var en = new MMDeviceEnumerator())
            {
                dev = en.GetDevice(target.Id);
            }

            // 60ms 播放缓冲：太小容易爆音，太大延迟高
            _out = new WasapiOut(dev, AudioClientShareMode.Shared, false, 60);
            _out.Init(_buffer);
            _out.Play();
        }

        /// <summary>把一段单声道 PCM 塞进播放缓冲</summary>
        public void AddMonoPcm(byte[] data, int offset, int count)
        {
            var buf = _buffer;
            if (buf == null || count <= 0) return;

            int samples = count / 2;
            if (samples * 4 > _stereo.Length) {         // 包比预期大时按缓冲上限截断
                samples = _stereo.Length / 4;
            }
            float vol = Volume;
            if (vol < 0f) vol = 0f;
            if (vol > 1f) vol = 1f;

            for (int i = 0; i < samples; i++)
            {
                short lo = (short)(data[offset + i * 2] & 0xFF);
                short hi = data[offset + i * 2 + 1];
                short s = (short)((hi << 8) | lo);

                if (vol < 1f) s = (short)(s * vol);

                int o = i * 4;
                _stereo[o] = (byte)(s & 0xFF);
                _stereo[o + 1] = (byte)((s >> 8) & 0xFF);
                _stereo[o + 2] = _stereo[o];        // 右声道复制左声道
                _stereo[o + 3] = _stereo[o + 1];
            }
            try { buf.AddSamples(_stereo, 0, samples * 4); }
            catch (Exception ex)
            {
                var h = Error; if (h != null) h("写入播放缓冲失败：" + ex.Message);
            }
        }

        /// <summary>清空缓冲 —— 开始说话时调用，丢掉残留的旧音频</summary>
        public void Clear()
        {
            try { if (_buffer != null) _buffer.ClearBuffer(); } catch { }
        }

        public void Stop()
        {
            try { if (_out != null) { _out.Stop(); _out.Dispose(); } } catch { }
            _out = null;
            _buffer = null;
        }

        public void Dispose() { Stop(); }

        internal sealed class DevInfo
        {
            public string Id;
            public string Name;
            public override string ToString() { return Name; }
        }

        // ==================== 录音侧：用于「系统麦克风」显示与声卡自检 ====================

        /// <summary>列出所有可用的录音设备</summary>
        public static List<DevInfo> ListCaptureDevices()
        {
            var list = new List<DevInfo>();
            using (var en = new MMDeviceEnumerator())
            {
                foreach (MMDevice d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                    list.Add(new DevInfo { Id = d.ID, Name = d.FriendlyName });
            }
            return list;
        }

        /// <summary>
        /// 系统默认录音设备的名字。
        /// Console = 普通程序默认用的；Communications = 通话/语音类程序（QQ、微信语音、会议软件）默认用的。
        /// 这两个是**分开设置**的，很多人只改了前者，结果语音软件还是走的旧麦克风。
        /// </summary>
        public static string GetDefaultCaptureName(Role role)
        {
            try
            {
                using (var en = new MMDeviceEnumerator())
                {
                    var d = en.GetDefaultAudioEndpoint(DataFlow.Capture, role);
                    return d != null ? d.FriendlyName : null;
                }
            }
            catch { return null; }
        }

        /// <summary>按名字片段找播放设备 ID（例如 "CABLE" / "CABLE-B"）</summary>
        public static string FindRenderId(string keyword)
        {
            try
            {
                foreach (var d in ListDevices())
                    if (d.Name != null && d.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                        return d.Id;
            }
            catch { }
            return null;
        }

        /// <summary>按名字片段找录音设备 ID</summary>
        public static string FindCaptureId(string keyword)
        {
            try
            {
                foreach (var d in ListCaptureDevices())
                    if (d.Name != null && d.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                        return d.Id;
            }
            catch { }
            return null;
        }

        /// <summary>系统默认播放（扬声器）设备的名字 —— 用来检查是不是被改成了虚拟声卡</summary>
        public static string GetDefaultRenderName(Role role)
        {
            try
            {
                using (var en = new MMDeviceEnumerator())
                {
                    var d = en.GetDefaultAudioEndpoint(DataFlow.Render, role);
                    return d != null ? d.FriendlyName : null;
                }
            }
            catch { return null; }
        }

        /// <summary>在指定录音设备上录一小段，返回 0~1 的峰值</summary>
        public static float CapturePeak(string deviceId, int milliseconds)
        {
            MMDevice dev;
            using (var en = new MMDeviceEnumerator())
            {
                if (string.IsNullOrEmpty(deviceId))
                    dev = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                else
                    dev = en.GetDevice(deviceId);
            }

            float peak = 0f;
            var sync = new object();

            using (var cap = new WasapiCapture(dev))
            {
                WaveFormat fmt = cap.WaveFormat;
                cap.DataAvailable += delegate(object s, WaveInEventArgs e)
                {
                    float local = ScanPeak(e.Buffer, e.BytesRecorded, fmt);
                    lock (sync) { if (local > peak) peak = local; }
                };
                cap.StartRecording();
                System.Threading.Thread.Sleep(milliseconds);
                cap.StopRecording();
            }
            return peak;
        }

        /// <summary>扫描一段缓冲的绝对值峰值；WASAPI 常见是 32 位浮点或 16 位整数</summary>
        private static float ScanPeak(byte[] buf, int count, WaveFormat fmt)
        {
            float max = 0f;

            if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
            {
                int n = count / 4;
                for (int i = 0; i < n; i++)
                {
                    float v = Math.Abs(BitConverter.ToSingle(buf, i * 4));
                    if (v > max) max = v;
                }
            }
            else if (fmt.BitsPerSample == 32)
            {
                int n = count / 4;
                for (int i = 0; i < n; i++)
                {
                    int raw = BitConverter.ToInt32(buf, i * 4);
                    float v = Math.Abs((float)raw) / 2147483648f;
                    if (v > max) max = v;
                }
            }
            else if (fmt.BitsPerSample == 16)
            {
                int n = count / 2;
                for (int i = 0; i < n; i++)
                {
                    short raw = BitConverter.ToInt16(buf, i * 2);
                    float v = Math.Abs((float)raw) / 32768f;
                    if (v > max) max = v;
                }
            }

            return max > 1f ? 1f : max;
        }
    }
}
