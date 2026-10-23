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
        // 设计尺寸（96 DPI 下的逻辑单位）。实际像素由 UiTheme.S() 按 DPI 换算。
        private const int W = 660;
        private const int H = 716;

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
            KeyInjector.Mode = (KeyInjector.InjectMode)_cfg.KeyMode;

            Text = "phoneMic";
            BackColor = UiTheme.Bg;
            ForeColor = UiTheme.Text;
            Font = UiTheme.Font(10.5f);
            // 缩放全部由 UiTheme.S() 负责，这里必须关掉 WinForms 自己的缩放，
            // 否则会和我们的换算叠加，界面就会错位。
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            Icon = MakeIcon();

            UiTheme.Init(this);
            ClientSize = new Size(UiTheme.S(W), UiTheme.S(H));
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

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
            FixRenderDefault();

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
            int w = W;                       // 设计宽度，卡片/按钮内部会按 DPI 换算

            Controls.Add(UiTheme.MakeLabel("phoneMic", 24, 16, 300, 40, 18f, UiTheme.Text));
            Controls.Add(UiTheme.MakeLabel("手机当电脑麦克风", 26, 58, 320, 24, 10f, UiTheme.Sub));

            _btnSettings = UiTheme.MakeButton("设 置", w - 24 - 108, 24, 108, 38);
            _btnSettings.Click += delegate
            {
                using (var f = new SettingsForm(this, _cfg))
                    f.ShowDialog(this);
            };
            Controls.Add(_btnSettings);

            // ---------- 状态 + 波形 ----------
            var c1 = UiTheme.MakeCard(this, 24, 92, w - 48, 252, "连接状态");

            _dot = new Panel
            {
                Location = new Point(UiTheme.S(22), UiTheme.S(36)),
                Size = new Size(UiTheme.S(20), UiTheme.S(20)),
                BackColor = UiTheme.Sub
            };
            UiTheme.ApplyRound(_dot, UiTheme.S(10));
            c1.Controls.Add(_dot);

            _lblStatus = UiTheme.MakeLabel("未开始", 54, 26, w - 48 - 22 - 54 - 20, 44, 21f, UiTheme.Text);
            _lblStatus.Font = UiTheme.Font(21f, FontStyle.Bold);
            c1.Controls.Add(_lblStatus);

            _lblDetail = UiTheme.MakeLabel("", 22, 78, w - 48 - 44, 24, 10.5f, UiTheme.Sub);
            c1.Controls.Add(_lblDetail);

            _wave = new Waveform
            {
                Location = new Point(UiTheme.S(22), UiTheme.S(112)),
                Size = new Size(UiTheme.S(w - 48 - 44), UiTheme.S(92))
            };
            c1.Controls.Add(_wave);

            _lblMeter = UiTheme.MakeLabel("电平 0%", 22, 210, w - 48 - 44, 24, 9.5f, UiTheme.Sub);
            _lblMeter.TextAlign = ContentAlignment.MiddleRight;
            c1.Controls.Add(_lblMeter);

            // ---------- 联动按键 ----------
            var c2 = UiTheme.MakeCard(this, 24, 362, w - 48, 108, "说话时同时按住的键（不映射就留空）");

            _lblKey = UiTheme.MakeLabel("（不映射）", 22, 50, w - 48 - 22 - 240, 38, 15f, UiTheme.Sub);
            _lblKey.Font = UiTheme.Font(15f, FontStyle.Bold);
            c2.Controls.Add(_lblKey);

            var btnTest = UiTheme.MakeButton("测 试", w - 48 - 20 - 216 - 8 - 92, 52, 92, 36);
            btnTest.ForeColor = UiTheme.Blue;
            btnTest.Click += delegate { TestKey(); };
            c2.Controls.Add(btnTest);

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
            _bar = new Panel
            {
                Location = new Point(UiTheme.S(24), UiTheme.S(488)),
                Size = new Size(UiTheme.S(w - 48), UiTheme.S(92)),
                BackColor = UiTheme.CardBg
            };
            UiTheme.ApplyRound(_bar, UiTheme.S(16));
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
                Location = new Point(UiTheme.S(24), UiTheme.S(600)),
                Size = new Size(UiTheme.S(w - 48), UiTheme.S(64)),
                FlatStyle = FlatStyle.Flat,
                BackColor = UiTheme.Blue,
                ForeColor = Color.White,
                Font = UiTheme.Font(13.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnStart.FlatAppearance.BorderSize = 0;
            _btnStart.Click += delegate { if (_running) StopAll(); else StartAll(); };
            UiTheme.ApplyRound(_btnStart, UiTheme.S(14));
            _btnStart.Resize += delegate { UiTheme.ApplyRound(_btnStart, UiTheme.S(14)); };
            Controls.Add(_btnStart);

            // ---------- 底部一行 ----------
            _lblFoot = UiTheme.MakeLabel("点这里查看日志", 26, 676, w - 52, 22, 9f, UiTheme.Sub);
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

        /// <summary>
        /// 兜底修复：安装虚拟声卡驱动后，Windows 有可能把「默认播放设备」也切到 CABLE Input。
        /// 那样别的软件（网易云、QQ音乐…）能播但没声音。这里检测到就自动改回真实扬声器 ——
        /// 只有游戏/直播软件会故意把默认输出设成虚拟线，普通用户不会是故意的。
        /// </summary>
        private void FixRenderDefault()
        {
            try
            {
                string cur = AudioOutput.GetDefaultRenderName(NAudio.CoreAudioApi.Role.Multimedia);
                if (!IsCable(cur)) return;

                AudioOutput.DevInfo target = null;
                foreach (var d in AudioOutput.ListDevices())
                {
                    if (!IsCable(d.Name)) { target = d; break; }
                }
                if (target == null) return;

                AudioDefault.SetDefault(target.Id, NAudio.CoreAudioApi.Role.Console);
                AudioDefault.SetDefault(target.Id, NAudio.CoreAudioApi.Role.Multimedia);
                AudioDefault.SetDefault(target.Id, NAudio.CoreAudioApi.Role.Communications);

                Log("检测到默认播放设备是「" + cur + "」，已自动改回：" + target.Name);
            }
            catch (Exception ex)
            {
                Log("修复默认播放设备失败：" + ex.Message);
            }
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
                _lblBar.Top = UiTheme.S(34);
                _lblBar.Height = UiTheme.S(24);
                _btnFixMic.Visible = false;
            }
            else
            {
                _bar.BackColor = Color.FromArgb(0x2B, 0x22, 0x12);
                _lblBar.Text = "系统麦克风现在是「" + (string.IsNullOrEmpty(currentName) ? "未知" : currentName) +
                               "」\nQQ、微信、语音输入录的是它 —— 所以收不到手机的声音";
                _lblBar.ForeColor = UiTheme.Amber;
                _lblBar.Top = UiTheme.S(18);
                _lblBar.Height = UiTheme.S(56);
                _btnFixMic.Visible = true;
            }
            UiTheme.ApplyRound(_bar, UiTheme.S(16));
        }

        /// <summary>供设置窗口调用：返回当前默认播放设备 ID（改麦克风前先存一份）</summary>
        internal string[] SnapshotRenderIds()
        {
            return new string[]
            {
                AudioDefault.GetDefaultRenderId(NAudio.CoreAudioApi.Role.Console),
                AudioDefault.GetDefaultRenderId(NAudio.CoreAudioApi.Role.Multimedia),
                AudioDefault.GetDefaultRenderId(NAudio.CoreAudioApi.Role.Communications)
            };
        }

        /// <summary>供设置窗口调用：如果播放设备被动过，就按快照还原</summary>
        internal bool RestoreRenderIdsIfChanged(string[] snap)
        {
            if (snap == null || snap.Length < 3) return false;
            try
            {
                var roles = new[]
                {
                    NAudio.CoreAudioApi.Role.Console,
                    NAudio.CoreAudioApi.Role.Multimedia,
                    NAudio.CoreAudioApi.Role.Communications
                };
                bool changed = false;
                for (int i = 0; i < 3; i++)
                {
                    if (string.IsNullOrEmpty(snap[i])) continue;
                    string now = AudioDefault.GetDefaultRenderId(roles[i]);
                    if (!string.Equals(now, snap[i], StringComparison.OrdinalIgnoreCase))
                    {
                        AudioDefault.SetDefault(snap[i], roles[i]);
                        changed = true;
                    }
                }
                if (changed) Log("设置过程中默认播放设备被动过，已自动还原");
                return changed;
            }
            catch { return false; }
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

            // 按键只会送到「当前前台窗口」。如果焦点还在 phoneMic 自己身上，
            // 那 Alt 就打到我们窗口里了，目标软件当然没反应 —— 先把这件事记进日志。
            string fg = KeyInjector.ForegroundProcessName();
            if (string.Equals(fg, "PhoneMic", StringComparison.OrdinalIgnoreCase))
                Ui(() => Log("注意：当前焦点在 phoneMic 窗口上，按键打不到目标软件。请先点一下要说话的那个软件。"));

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

        /// <summary>
        /// 模拟按住配置的键 1.5 秒，并回报结果。
        /// 用来分辨到底是「注入失败」还是「目标软件不认 / 焦点不对」。
        /// </summary>
        private void TestKey()
        {
            int[] keys = _cfg.Keys;
            if (keys == null || keys.Length == 0)
            {
                MessageBox.Show(this, "还没有设置要按住的键。\n\n先点「重新录制」，然后按一下键盘上的键。",
                    "按键测试", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string desc = KeyInjector.Describe(keys);
            string fg = KeyInjector.ForegroundProcessName();
            Log("按键测试：" + desc + "，当前前台进程 = " + (string.IsNullOrEmpty(fg) ? "未知" : fg) +
                "，注入方式 = " + ModeName());

            System.Threading.Tasks.Task.Run(() =>
            {
                bool okPress = false, downAfter = false, okRelease = false;
                try
                {
                    lock (_keyLock)
                    {
                        okPress = KeyInjector.Press(keys);
                        System.Threading.Thread.Sleep(3000);
                        downAfter = KeyInjector.IsKeyDown(keys[keys.Length - 1]);
                        okRelease = KeyInjector.ReleaseAll();
                    }
                }
                catch (Exception ex)
                {
                    Ui(() => Log("按键测试异常：" + ex.Message));
                }

                Ui(() =>
                {
                    Log("按键测试结果：按下=" + okPress + " 读回=" + downAfter + " 松开=" + okRelease);

                    if (okPress && downAfter)
                    {
                        MessageBox.Show(this,
                            "已模拟按住「" + desc + "」3 秒，系统也确认这个键处于按下状态。\n\n" +
                            "说明注入是成功的。如果目标软件仍然没反应，那就是：\n" +
                            "1. 当时焦点不在那个软件上（说话前先点一下它）；\n" +
                            "2. 或者那个软件屏蔽了模拟按键。\n\n" +
                            (string.Equals(fg, "PhoneMic", StringComparison.OrdinalIgnoreCase)
                                ? "注意：刚才前台是 phoneMic 自己，请再切到目标软件后试一次。"
                                : "刚才前台进程是：" + fg),
                            "按键测试", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        MessageBox.Show(this,
                            "注入没成功（按下=" + okPress + "，读回=" + downAfter + "）。\n\n" +
                            "常见原因：目标软件是以管理员身份运行的，而本程序不是。\n" +
                            "解决办法：右键 phoneMic →「以管理员身份运行」，再试。\n\n" +
                            "也可以到设置里把「按键注入方式」换成扫描码再试一次。",
                            "按键测试", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                });
            });
        }

        private static string ModeName()
        {
            switch (KeyInjector.Mode)
            {
                case KeyInjector.InjectMode.ScanCode: return "扫描码";
                case KeyInjector.InjectMode.Message: return "消息投递";
                default: return "虚拟键码";
            }
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
