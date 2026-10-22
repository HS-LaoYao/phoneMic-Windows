using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace PhoneMic
{
    /// <summary>
    /// 主窗口 —— 只放「平时要看的东西」：状态、波形、联动按键、开始/停止。
    /// 地址、输出设备、麦克风设置、声卡自检、日志全部收进「设置」窗口，
    /// 这样主界面才能留出足够的空白，不显得拥挤。
    /// </summary>
    internal sealed class MainForm : Form
    {
        private readonly Config _cfg;

        private UdpReceiver _rx;
        private AudioOutput _audio;
        private KeyCapture _cap;
        private Timer _tick;
        private Timer _meter;
        private NotifyIcon _tray;

        private readonly object _keyLock = new object();
        private bool _running;
        private bool _talking;
        private bool _connected;

        private volatile float _peak;
        private long _packets;
        private long _lastAudioTicks;
        private int _silentTicks;

        private readonly List<string> _logBuf = new List<string>();

        // ===== 控件 =====
        private Panel _dot, _bar;
        private Label _lblStatus, _lblDetail, _lblMeter, _lblKey, _lblFoot, _lblBar;
        private Waveform _wave;
        private Button _btnRecord, _btnClear, _btnStart, _btnFixMic, _btnSettings;

        public MainForm()
        {
            _cfg = Config.Load();

            Text = "phoneMic";
            ClientSize = new Size(660, 740);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = UiTheme.Bg;
            ForeColor = UiTheme.Text;
            Font = UiTheme.Font(10.5f);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            DoubleBuffered = true;
            Icon = MakeIcon();

            BuildUi();
            BuildTray();

            _tick = new Timer();
            _tick.Interval = 400;
            _tick.Tick += OnTick;

            _meter = new Timer();
            _meter.Interval = 50;
            _meter.Tick += OnMeterTick;

            UpdateKeyLabel();
            SetStatus("未开始", UiTheme.Sub, "点下面的「开始」按钮");
            CheckMicState();

            Log("phoneMic 已启动。手机和电脑连同一个 WiFi 即可。");
        }

        // ==================== 生命周期 ====================

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_cfg.AutoStartOnLaunch) StartAll();
        }

        /// <summary>工作线程统一回到 UI 线程（句柄未建 / 已销毁时安全跳过）</summary>
        private void Ui(Action action)
        {
            if (action == null) return;
            if (!IsHandleCreated || IsDisposed) return;
            try { BeginInvoke(action); }
            catch (Exception) { }
        }

        // ==================== 界面 ====================

        private void BuildUi()
        {
            int w = ClientSize.Width;

            Controls.Add(UiTheme.MakeLabel("phoneMic", 24, 16, 300, 40, 18f, UiTheme.Text));
            Controls.Add(UiTheme.MakeLabel("手机当电脑麦克风", 26, 54, 300, 24, 10f, UiTheme.Sub));

            _btnSettings = UiTheme.MakeButton("设 置", w - 24 - 108, 24, 108, 38);
            _btnSettings.Click += delegate
            {
                using (var f = new SettingsForm(this, _cfg))
                    f.ShowDialog(this);
            };
            Controls.Add(_btnSettings);

            // ---------- 状态 + 波形 ----------
            var c1 = UiTheme.MakeCard(this, 24, 92, w - 48, 272, "连接状态");

            _dot = new Panel { Location = new Point(22, 36), Size = new Size(20, 20), BackColor = UiTheme.Sub };
            UiTheme.ApplyRound(_dot, 10);
            c1.Controls.Add(_dot);

            _lblStatus = UiTheme.MakeLabel("未开始", 54, 26, w - 48 - 22 - 54 - 20, 44, 21f, UiTheme.Text);
            _lblStatus.Font = UiTheme.Font(21f, FontStyle.Bold);
            c1.Controls.Add(_lblStatus);

            _lblDetail = UiTheme.MakeLabel("", 22, 78, w - 48 - 44, 24, 10.5f, UiTheme.Sub);
            c1.Controls.Add(_lblDetail);

            _wave = new Waveform
            {
                Location = new Point(22, 112),
                Size = new Size(w - 48 - 44, 118)
            };
            c1.Controls.Add(_wave);

            _lblMeter = UiTheme.MakeLabel("电平 0%", 22, 236, w - 48 - 44, 24, 9.5f, UiTheme.Sub);
            _lblMeter.TextAlign = ContentAlignment.MiddleRight;
            c1.Controls.Add(_lblMeter);

            // ---------- 联动按键 ----------
            var c2 = UiTheme.MakeCard(this, 24, 382, w - 48, 108, "说话时同时按住的键（不映射就留空）");

            _lblKey = UiTheme.MakeLabel("（不映射）", 22, 50, w - 48 - 22 - 240, 38, 15f, UiTheme.Sub);
            _lblKey.Font = UiTheme.Font(15f, FontStyle.Bold);
            c2.Controls.Add(_lblKey);

            _btnRecord = UiTheme.MakeButton("重新录制", w - 48 - 20 - 216, 52, 104, 36);
            _btnRecord.Click += delegate { RecordKey(); };
            c2.Controls.Add(_btnRecord);

            _btnClear = UiTheme.MakeButton("清除", w - 48 - 20 - 104, 52, 104, 36);
            _btnClear.Click += delegate
            {
                _cfg.Keys = new int[0];
                _cfg.Save();
                UpdateKeyLabel();
                Log("已取消按键映射");
            };
            c2.Controls.Add(_btnClear);

            // ---------- 麦克风提示条（始终占位，避免布局跳动）----------
            _bar = new Panel { Location = new Point(24, 508), Size = new Size(w - 48, 92), BackColor = UiTheme.CardBg };
            UiTheme.ApplyRound(_bar, 16);
            Controls.Add(_bar);

            _lblBar = UiTheme.MakeLabel("", 22, 20, w - 48 - 22 - 24, 52, 10.5f, UiTheme.Sub);
            _lblBar.TextAlign = ContentAlignment.MiddleLeft;
            _bar.Controls.Add(_lblBar);

            _btnFixMic = UiTheme.MakeButton("一键改成 CABLE Output", w - 48 - 20 - 224, 34, 224, 38);
            _btnFixMic.ForeColor = UiTheme.Green;
            _btnFixMic.Visible = false;
            _btnFixMic.Click += delegate { OpenSettingsForMic(); };
            _bar.Controls.Add(_btnFixMic);

            // ---------- 开始 / 停止 ----------
            _btnStart = new Button
            {
                Text = "开 始",
                Location = new Point(24, 620),
                Size = new Size(w - 48, 64),
                FlatStyle = FlatStyle.Flat,
                BackColor = UiTheme.Blue,
                ForeColor = Color.White,
                Font = UiTheme.Font(13.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnStart.FlatAppearance.BorderSize = 0;
            _btnStart.Click += delegate { if (_running) StopAll(); else StartAll(); };
            UiTheme.ApplyRound(_btnStart, 14);
            _btnStart.Resize += delegate { UiTheme.ApplyRound(_btnStart, 14); };
            Controls.Add(_btnStart);

            // ---------- 底部一行 ----------
            _lblFoot = UiTheme.MakeLabel("点这里查看日志", 26, 700, w - 52, 22, 9f, UiTheme.Sub);
            _lblFoot.Cursor = Cursors.Hand;
            _lblFoot.Click += delegate { OpenSettings(); };
            Controls.Add(_lblFoot);
        }

        private void BuildTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示主窗口", null, delegate { ShowFromTray(); });
            menu.Items.Add("开始 / 停止", null, delegate { if (_running) StopAll(); else StartAll(); });
            menu.Items.Add("设置", null, delegate { OpenSettings(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { RealExit(); });

            _tray = new NotifyIcon
            {
                Icon = Icon,
                Text = "phoneMic",
                Visible = true,
                ContextMenuStrip = menu
            };
            _tray.DoubleClick += delegate { ShowFromTray(); };
            _tray.ShowBalloonTip(3000, "phoneMic 已在后台运行",
                "手机和电脑连同一个 WiFi，在手机上打开 phoneMic 就能连上。", ToolTipIcon.Info);
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void OpenSettings()
        {
            using (var f = new SettingsForm(this, _cfg))
                f.ShowDialog(this);
        }

        /// <summary>点提示条上的按钮 → 直接打开设置页定位到麦克风那块</summary>
        private void OpenSettingsForMic()
        {
            MessageBox.Show(this,
                "打开设置后，点「设为默认麦克风」即可。\n\n" +
                "（会把系统的默认设备和默认通信设备都改成 CABLE Output，\nQQ、微信语音、语音输入就都能收到手机的声音了）",
                "下一步", MessageBoxButtons.OK, MessageBoxIcon.Information);
            OpenSettings();
        }

        private static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var br = new SolidBrush(UiTheme.Blue)) g.FillEllipse(br, 1, 1, 30, 30);
                    using (var br = new SolidBrush(Color.White))
                    {
                        g.FillRectangle(br, 14, 7, 4, 11);
                        g.FillRectangle(br, 11, 15, 10, 3);
                        g.FillRectangle(br, 15, 18, 2, 5);
                        g.FillRectangle(br, 11, 23, 10, 2);
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        // ==================== 麦克风状态提示条 ====================

        /// <summary>打开时检查一次：系统默认麦克风对不对</summary>
        private void CheckMicState()
        {
            string console = AudioOutput.GetDefaultCaptureName(NAudio.CoreAudioApi.Role.Console);
            string comm = AudioOutput.GetDefaultCaptureName(NAudio.CoreAudioApi.Role.Communications);
            bool ok = IsCable(console) && IsCable(comm);
            NotifyMicState(ok, IsCable(console) ? comm : console);
        }

        private static bool IsCable(string name)
        {
            return name != null && name.IndexOf("CABLE", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>由设置窗口回调：把麦克风状态显示成一条提示</summary>
        internal void NotifyMicState(bool isCable, string currentName)
        {
            if (_bar == null || IsDisposed) return;

            if (isCable)
            {
                _bar.BackColor = Color.FromArgb(0x14, 0x2B, 0x1D);
                _lblBar.Text = "✓ 系统麦克风设置正确 —— 其他软件现在能收到手机的声音";
                _lblBar.ForeColor = UiTheme.Green;
                _lblBar.Top = 34;
                _lblBar.Height = 24;
                _btnFixMic.Visible = false;
            }
            else
            {
                _bar.BackColor = Color.FromArgb(0x2B, 0x22, 0x12);
                _lblBar.Text = "系统麦克风现在是「" + (string.IsNullOrEmpty(currentName) ? "未知" : currentName) +
                               "」\nQQ、微信、语音输入录的是它 —— 所以收不到手机的声音";
                _lblBar.ForeColor = UiTheme.Amber;
                _lblBar.Top = 18;
                _lblBar.Height = 56;
                _btnFixMic.Visible = true;
            }
            UiTheme.ApplyRound(_bar, 16);
        }

        internal void ReloadAfterSettings()
        {
            // 设置里可能改了输出设备，正在跑的话重开一次播放
            if (_running)
            {
                StopAudio();
                if (!StartAudio()) return;
            }
            CheckMicState();
        }

        // ==================== 开始 / 停止 ====================

        private void StartAll()
        {
            if (_running) return;
            if (!StartAudio()) return;

            _rx = new UdpReceiver();
            _rx.AudioReceived += OnAudio;
            _rx.PttDown += OnPttDown;
            _rx.PttUp += OnPttUp;
            _rx.Heartbeat += OnHeartbeat;
            _rx.Discovered += delegate(string ip) { Ui(() => Log("手机正在查找本机：" + ip)); };
            try
            {
                _rx.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法监听 UDP " + Proto.Port + "：\n" + ex.Message +
                    "\n\n可能是端口被占用，或防火墙拦截。", "phoneMic");
                StopAudio();
                _rx = null;
                return;
            }

            _running = true;
            _connected = false;
            _packets = 0;
            _btnStart.Text = "停 止";
            _btnStart.BackColor = Color.FromArgb(0x2C, 0x35, 0x40);
            SetStatus("等待手机连接", UiTheme.Amber, "在手机上打开 phoneMic 会自动找到这台电脑");
            _tick.Start();
            _meter.Start();
            Log("开始监听 UDP " + Proto.Port);
        }

        private bool StartAudio()
        {
            try
            {
                StopAudio();
                _audio = new AudioOutput();
                _audio.Error += delegate(string m) { Ui(() => Log(m)); };
                _audio.Start(_cfg.DeviceId);
                return true;
            }
            catch (Exception ex)
            {
                _audio = null;
                MessageBox.Show(this,
                    "打不开输出设备：\n" + ex.Message +
                    "\n\n请到「设置」里选一个设备；如果列表里没有虚拟声卡，先安装它。",
                    "phoneMic");
                return false;
            }
        }

        private void StopAudio()
        {
            if (_audio != null) { _audio.Dispose(); _audio = null; }
        }

        private void StopAll()
        {
            _meter.Stop();
            _tick.Stop();
            lock (_keyLock) KeyInjector.ReleaseAll();
            if (_rx != null) { _rx.Dispose(); _rx = null; }
            StopAudio();
            _running = false;
            _talking = false;
            _connected = false;
            if (_wave != null) _wave.Reset();
            if (_lblMeter != null) _lblMeter.Text = "电平 0%";
            _btnStart.Text = "开 始";
            _btnStart.BackColor = UiTheme.Blue;
            SetStatus("未开始", UiTheme.Sub, "点下面的「开始」按钮");
            Log("已停止");
        }

        // ==================== 网络事件（UDP 线程）====================

        private void OnAudio(byte[] buf, int len)
        {
            int count = len - Proto.AudioHeader;
            var a = _audio;
            if (a != null) a.AddMonoPcm(buf, Proto.AudioHeader, count);

            float lv = Rms(buf, Proto.AudioHeader, count);
            if (lv > _peak) _peak = lv;
            _packets++;
            _lastAudioTicks = Stopwatch.GetTimestamp();

            MarkConnected();

            // 收到音频包就说明手机在录音。不能只依赖 PTT 包 ——
            // 那个包小、又是单向的，一旦丢了状态就永远停在「待命」。
            if (!_talking) Ui(() => SetTalking(true));
        }

        private void OnPttDown()
        {
            var a = _audio;
            if (a != null) a.Clear();

            bool ok = true;
            lock (_keyLock) ok = KeyInjector.Press(_cfg.Keys);

            MarkConnected();
            Ui(() => { _silentTicks = 0; SetTalking(true); });

            bool hasKeys = _cfg.Keys != null && _cfg.Keys.Length > 0;
            if (hasKeys)
            {
                if (ok)
                {
                    string desc = KeyInjector.Describe(_cfg.Keys);
                    Ui(() => Log("按下：" + desc));
                }
                else
                {
                    Ui(() => Log("按键注入被系统拒绝 —— 如果目标程序是以管理员运行的，本程序也要用管理员身份启动"));
                }
            }
        }

        private void OnPttUp()
        {
            lock (_keyLock) KeyInjector.ReleaseAll();
            Ui(() => SetTalking(false));
        }

        private void OnHeartbeat(int mode)
        {
            MarkConnected();
        }

        private void MarkConnected()
        {
            if (_connected) return;
            _connected = true;
            Ui(() =>
            {
                if (!_talking) SetStatus("已连接 · 待命", UiTheme.Green, "手机：" + (_rx != null ? _rx.PeerIp : ""));
                Log("手机已连接：" + (_rx != null ? _rx.PeerIp : ""));
            });
        }

        // ==================== 电平 / 波形 ====================

        private void OnMeterTick(object sender, EventArgs e)
        {
            float p = _peak;
            _peak = 0f;

            if (_wave != null) _wave.Push(p);
            if (_lblMeter != null)
                _lblMeter.Text = "电平 " + (int)(p * 100) + "%    已收 " + _packets + " 包";

            if (_talking && _audio != null)
            {
                if (p < 0.005f)
                {
                    _silentTicks++;
                    if (_silentTicks == 60)
                        Log("提示：已在录音，但收到的音量一直是 0 —— 检查手机麦克风是否被占用或静音");
                }
                else _silentTicks = 0;
            }
        }

        /// <summary>算一段 PCM 的 RMS 并放大到 0~1（语音幅度小，不放大波形几乎看不见）</summary>
        private static float Rms(byte[] d, int off, int count)
        {
            int n = count / 2;
            if (n <= 0) return 0f;
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                int lo = d[off + i * 2] & 0xFF;
                int hi = d[off + i * 2 + 1];
                short s = (short)((hi << 8) | lo);
                sum += (double)s * s;
            }
            float rms = (float)Math.Sqrt(sum / n) / 32768f;
            float lv = rms * 4f;
            return lv > 1f ? 1f : lv;
        }

        // ==================== 状态 ====================

        private void SetStatus(string text, Color color, string detail)
        {
            if (_lblStatus == null) return;
            _lblStatus.Text = text;
            _dot.BackColor = color;
            _lblDetail.Text = detail;
            if (_tray != null) _tray.Text = "phoneMic - " + text;
        }

        private void SetTalking(bool on)
        {
            if (_talking == on) return;
            _talking = on;

            if (on)
            {
                SetStatus("● 正在说话 · 录音中", UiTheme.Red, "声音正在送往虚拟声卡");
                if (_wave != null) _wave.Reset();
            }
            else if (_connected) SetStatus("已连接 · 待命", UiTheme.Green, "手机：" + (_rx != null ? _rx.PeerIp : ""));
            else SetStatus("等待手机连接", UiTheme.Amber, "在手机上打开 phoneMic 会自动找到这台电脑");
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (!_running || _rx == null) return;

            // 1.5 秒没收到音频 → 认为已经停止说话（兼容 PTT 包丢失的情况）
            if (_talking && _lastAudioTicks != 0)
            {
                double ms = (Stopwatch.GetTimestamp() - _lastAudioTicks) * 1000.0 / Stopwatch.Frequency;
                if (ms > 1500)
                {
                    lock (_keyLock) KeyInjector.ReleaseAll();
                    SetTalking(false);
                }
            }

            if (_rx.MsSinceLastPacket > 3000 && _connected)
            {
                lock (_keyLock) KeyInjector.ReleaseAll();
                _connected = false;
                _talking = false;
                SetStatus("等待手机连接", UiTheme.Amber, "手机断开了，会在它重新打开时自动连上");
            }
        }

        // ==================== 按键录制 ====================

        private void RecordKey()
        {
            if (_cap != null) return;
            _btnRecord.Text = "请按键…";
            _btnRecord.Enabled = false;
            Log("请按下要联动的键（按 Esc 取消）");

            _cap = new KeyCapture();
            _cap.Captured += delegate(int[] keys)
            {
                Ui(() =>
                {
                    if (keys.Length == 1 && keys[0] == 0x1B)
                    {
                        Log("已取消按键录制");
                    }
                    else
                    {
                        _cfg.Keys = keys;
                        _cfg.Save();
                        Log("已设置为：" + KeyInjector.Describe(keys));
                    }
                    if (_cap != null) { _cap.Dispose(); _cap = null; }
                    _btnRecord.Text = "重新录制";
                    _btnRecord.Enabled = true;
                    UpdateKeyLabel();
                });
            };
            _cap.Start();
        }

        private void UpdateKeyLabel()
        {
            bool has = _cfg.Keys != null && _cfg.Keys.Length > 0;
            _lblKey.Text = KeyInjector.Describe(_cfg.Keys);
            _lblKey.ForeColor = has ? UiTheme.Text : UiTheme.Sub;
        }

        // ==================== 日志 ====================

        private static string LogDir
        {
            get
            {
                string d = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phoneMic");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        private static string LogPath { get { return Path.Combine(LogDir, "app.log"); } }

        internal void Log(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg;
            _logBuf.Add(line);
            if (_logBuf.Count > 500) _logBuf.RemoveAt(0);

            try { File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8); }
            catch { }

            if (_lblFoot != null) _lblFoot.Text = msg;
        }

        internal string[] GetLogLines()
        {
            return _logBuf.ToArray();
        }

        // ==================== 退出 ====================

        private void RealExit()
        {
            lock (_keyLock) KeyInjector.ReleaseAll();
            if (_rx != null) { _rx.Dispose(); _rx = null; }
            StopAudio();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
            Application.Exit();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && _cfg.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            lock (_keyLock) KeyInjector.ReleaseAll();
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_rx != null) { _rx.Dispose(); _rx = null; }
            StopAudio();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
            base.OnFormClosed(e);
        }
    }
}
