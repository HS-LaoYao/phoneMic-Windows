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

        public bool IsPlaying { get { return _out != null && _out.PlaybackState == PlaybackState.Playing; } }

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
            for (int i = 0; i < samples; i++)
            {
                byte lo = data[offset + i * 2];
                byte hi = data[offset + i * 2 + 1];
                int o = i * 4;
                _stereo[o] = lo; _stereo[o + 1] = hi;   // 左
                _stereo[o + 2] = lo; _stereo[o + 3] = hi; // 右
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
    }
}
