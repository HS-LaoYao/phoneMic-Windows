using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PhoneMic
{
    /// <summary>
    /// 抓取电脑正在播放的声音（WASAPI loopback），统一转成
    /// 48kHz / 16bit / 单声道，按 10ms 一帧（960 字节）交给上层发给手机。
    ///
    /// 用途：模式③「手机当耳机」—— Windows 放视频，手机里听得到。
    /// </summary>
    internal sealed class LoopbackSender : IDisposable
    {
        private WasapiLoopbackCapture _cap;
        private ISampleProvider _chain;
        private Thread _thread;
        private volatile bool _running;

        /// <summary>每 10ms 触发一次，参数为 960 字节的 PCM</summary>
        public event Action<byte[], int> FrameReady;

        public void Start()
        {
            if (_running) return;

            _cap = new WasapiLoopbackCapture();          // 跟随系统默认播放设备
            _cap.StartRecording();

            // 转单声道 → 需要时重采样到 48k
            // 注意：WasapiLoopbackCapture 是 IWaveIn，不能直接 ToSampleProvider，
            // 要先套一层 WaveInProvider（把事件式采集转成可读的 IWaveProvider）。
            ISampleProvider sp = new WaveInProvider(_cap).ToSampleProvider();
            if (sp.WaveFormat.Channels == 2)
                sp = new StereoToMonoSampleProvider(sp);
            if (sp.WaveFormat.SampleRate != Proto.SampleRate)
                sp = new WdlResamplingSampleProvider(sp, Proto.SampleRate);
            _chain = sp;

            _running = true;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Priority = ThreadPriority.AboveNormal;
            _thread.Name = "loopback";
            _thread.Start();
        }

        private void Loop()
        {
            var buf = new float[Proto.FrameSamples];
            var pcm = new byte[Proto.FrameBytes];

            while (_running)
            {
                try
                {
                    int n = _chain.Read(buf, 0, buf.Length);
                    if (n <= 0) { Thread.Sleep(5); continue; }

                    for (int i = 0; i < n; i++)
                    {
                        float v = buf[i];
                        if (v > 1f) v = 1f;
                        if (v < -1f) v = -1f;
                        short s = (short)(v * 32767f);
                        pcm[i * 2] = (byte)(s & 0xFF);
                        pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
                    }

                    var h = FrameReady;
                    if (h != null) h(pcm, n * 2);
                }
                catch
                {
                    if (!_running) break;
                    Thread.Sleep(20);
                }
            }
        }

        public void Stop()
        {
            _running = false;
            try { if (_cap != null) _cap.StopRecording(); } catch { }
            try { if (_cap != null) _cap.Dispose(); } catch { }
            _cap = null;
            _chain = null;
            _thread = null;
        }

        public void Dispose() { Stop(); }
    }
}
