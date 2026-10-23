using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PhoneMic
{
    /// <summary>
    /// 抓取电脑正在播放的声音，统一转成 48kHz / 16bit / 单声道，按 10ms 一帧（960 字节）交出去。
    ///
    /// 两种用法：
    ///   Start()                     —— 抓系统默认播放设备（loopback）
    ///   StartFromCapture(deviceId)  —— 直接读某个录音设备（模式③ 读虚拟声卡的 Output 端）
    ///
    /// 转换全部自己算，不依赖 NAudio 的重采样链：
    /// 虚拟声卡的格式可能是 44.1k/48k、16/24/32 位、1~2 声道，
    /// 用通用链路容易出问题（表现就是「只有一点电流声」），手写反而可控。
    /// </summary>
    internal sealed class LoopbackSender : IDisposable
    {
        private IWaveIn _cap;
        private volatile bool _running;

        private readonly byte[] _frame = new byte[Proto.FrameBytes];
        private int _frameFill;
        private long _framesSent;

        // 采样率转换用的状态（线性插值）
        private double _ratio = 1.0;    // 源采样率 / 48000
        private double _acc;
        private float _last;
        private bool _hasLast;

        /// <summary>每 10ms 触发一次，参数为 960 字节的 PCM</summary>
        public event Action<byte[], int> FrameReady;

        /// <summary>源设备格式，写日志用</summary>
        public string SourceFormat { get; private set; }

        /// <summary>已经发出多少帧</summary>
        public long FramesSent { get { return _framesSent; } }

        public void Start()
        {
            StartWith(new WasapiLoopbackCapture());
        }

        public void StartFromCapture(string captureDeviceId)
        {
            if (string.IsNullOrEmpty(captureDeviceId))
                throw new ArgumentNullException("captureDeviceId");

            MMDevice dev;
            using (var en = new MMDeviceEnumerator())
                dev = en.GetDevice(captureDeviceId);

            StartWith(new WasapiCapture(dev));
        }

        private void StartWith(IWaveIn cap)
        {
            if (_running) return;

            _cap = cap;
            var f = cap.WaveFormat;
            SourceFormat = f.SampleRate + "Hz " + f.Channels + "ch " + f.BitsPerSample + "bit " +
                           (f.Encoding == WaveFormatEncoding.IeeeFloat ? "float" : f.Encoding.ToString());

            _ratio = (double)f.SampleRate / Proto.SampleRate;
            _acc = 0;
            _hasLast = false;
            _frameFill = 0;

            _cap.DataAvailable += OnData;
            _cap.StartRecording();
            _running = true;
        }

        private void OnData(object sender, WaveInEventArgs e)
        {
            var fmt = _cap.WaveFormat;
            int ch = fmt.Channels;
            int bps = fmt.BitsPerSample / 8;
            if (ch <= 0 || bps <= 0) return;

            bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat;
            int frames = e.BytesRecorded / (bps * ch);

            for (int i = 0; i < frames; i++)
            {
                int offset = i * bps * ch;
                float v = ReadSample(e.Buffer, offset, bps, isFloat);

                if (ch >= 2)                       // 多声道取平均，变成单声道
                {
                    for (int c = 1; c < ch; c++)
                        v += ReadSample(e.Buffer, offset + c * bps, bps, isFloat);
                    v /= ch;
                }

                Push(v);
            }
        }

        private static float ReadSample(byte[] b, int off, int bps, bool isFloat)
        {
            if (isFloat && bps == 4) return BitConverter.ToSingle(b, off);

            switch (bps)
            {
                case 1: return (b[off] - 128) / 128f;
                case 2: return BitConverter.ToInt16(b, off) / 32768f;
                case 3:
                    {
                        int v = b[off] | (b[off + 1] << 8) | (b[off + 2] << 16);
                        if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                        return v / 8388608f;
                    }
                case 4: return BitConverter.ToInt32(b, off) / 2147483648f;
                default: return 0f;
            }
        }

        /// <summary>输入一个单声道样本，按比例产出输出样本，攒够 960 字节就发一帧</summary>
        private void Push(float v)
        {
            if (!_hasLast)
            {
                _last = v;
                _hasLast = true;
                return;
            }

            _acc += 1.0;
            while (_acc >= _ratio)
            {
                _acc -= _ratio;
                float t = (float)(_ratio > 0 ? _acc / _ratio : 0);
                Emit(_last + (v - _last) * t);
            }
            _last = v;
        }

        private void Emit(float sample)
        {
            if (sample > 1f) sample = 1f;
            if (sample < -1f) sample = -1f;

            short s = (short)(sample * 32767f);
            _frame[_frameFill++] = (byte)(s & 0xFF);
            _frame[_frameFill++] = (byte)((s >> 8) & 0xFF);

            if (_frameFill >= _frame.Length)
            {
                var h = FrameReady;
                if (h != null) h(_frame, _frame.Length);
                _framesSent++;
                _frameFill = 0;
            }
        }

        public void Stop()
        {
            _running = false;
            try { if (_cap != null) { _cap.DataAvailable -= OnData; _cap.StopRecording(); } } catch { }
            try { if (_cap != null) _cap.Dispose(); } catch { }
            _cap = null;
        }

        public void Dispose() { Stop(); }
    }
}
