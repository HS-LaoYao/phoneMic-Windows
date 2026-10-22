using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace PhoneMic
{
    /// <summary>
    /// 主窗口。
    ///
    /// 布局（从上到下）：状态 + 实时波形 / 本机地址 / 输出设备 / 联动按键 / 选项 / 开始按钮。
    /// 界面全部用代码搭，不用 Designer。
    ///
    /// 关于波形：它画的是**实际收到的音频音量**。柱子随说话起伏 = 手机声音已经到电脑了；
    /// 此时若别的软件还听不到，问题就在对方的麦克风选择上。
    /// </summary>
    internal sealed class MainForm : Form
    {
        // ===== 配色 =====
        private static readonly Color CBg = Color.FromArgb(0x12, 0x16, 0x1A);
        private static readonly Color CCard = Color.FromArgb(0x1B, 0x22, 0x2A);
        private static readonly Color CField = Color.FromArgb(0x11, 0x16, 0x1B);
        private static readonly Color CText = Color.FromArgb(0xE6, 0xED, 0xF3);
        private static readonly Color CSub = Color.FromArgb(0x8B, 0x98, 0xA5);
        private static readonly Color CBlue = Color.FromArgb(0x3F, 0xA7, 0xFF);
        private static readonly Color CGreen = Color.FromArgb(0x3F, 0xB9, 0x50);
        private static readonly Color CRed = Color.FromArgb(0xF8, 0x51, 0x49);
        private static readonly Color CAmber = Color.FromArgb(0xD2, 0x99, 0x22);

        private const int Pad = 20;       // 窗口内边距
        private const int CardPad = 18;   // 卡片内边距

        private Config _cfg;
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

        // 电平采集：UDP 线程写，UI 定时器读
        private volatile float _peak;
        private long _packets;
        private int _silentTicks;

        private readonly List<string> _logBuf = new List<string>();

        // ===== 控件 =====
        private Panel _dot;
        private Label _lblStatus, _lblDetail, _lblMeter, _lblKey, _lblVb, _lblFoot;
        private Waveform _wave;
        private TextBox _txtAddr;
        private ComboBox _cboDevice;
        private Button _btnCopy, _btnRefresh, _btnInstall, _btnRecord, _btnClear, _btnStart;
        private CheckBox _chkMin, _chkAuto;

        public MainForm()
        {
            _cfg = Config.Load();

            Text = "phoneMic";
            ClientSize = new Size(680, 800);
            MinimumSize = Size;      // Size 已包含边框，避免用户把窗口拖到比内容还小
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = CBg;
            ForeColor = CText;
            Font = Ui(9.5f);
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
            _meter.Interval = 50;              // 20 次/秒，波形看着顺
            _meter.Tick += OnMeterTick;

            LoadDevices();
            UpdateKeyLabel();
            SetStatus("未开始", CSub, "点下面的「开始」按钮");

            Log("phoneMic 已启动。手机和电脑连同一个 WiFi 即可。");
        }

        /// <summary>工作线程统一回到 UI 线程的入口（句柄未建 / 已销毁时安全跳过）</summary>
        private void Ui(Action action)
        {
            if (action == null) return;
            if (!IsHandleCreated || IsDisposed) return;
            try { BeginInvoke(action); }
            catch (Exception) { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_cfg.AutoStartOnLaunch) StartAll();
        }

        // ==================== 小工具 ====================

        private static Font Ui(float size) { return Ui(size, FontStyle.Regular); }

        private static Font Ui(float size, FontStyle style)
        {
            try { return new Font("Microsoft YaHei UI", size, style); }
            catch { return new Font(FontFamily.GenericSansSerif, size, style); }
        }

        private static GraphicsPath RoundPath(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            if (d <= 0 || r.Width < d || r.Height < d)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>一张圆角深色卡片，带左上角小标题</summary>
        private Panel Card(int y, int h, string title)
        {
            var p = new Panel
            {
                Location = new Point(Pad, y),
                Size = new Size(ClientSize.Width - Pad * 2, h),
                BackColor = CCard,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            ApplyRound(p, 14);
            p.Resize += delegate { ApplyRound(p, 14); };

            p.Controls.Add(new Label
            {
                Text = title,
                ForeColor = CSub,
                Font = Ui(9f),
                AutoSize = true,
                Location = new Point(CardPad, 14)
            });
            Controls.Add(p);
            return p;
        }

        private static void ApplyRound(Control c, int radius)
        {
            using (var path = RoundPath(new Rectangle(0, 0, c.Width, c.Height), radius))
            {
                var old = c.Region;
                c.Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        private Button FlatButton(string text, int x, int y, int w, int h)
        {
            var b = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                FlatStyle = FlatStyle.Flat,
                BackColor = CField,
                ForeColor = CText,
                Font = Ui(9.5f),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(0x2C, 0x35, 0x40);
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0x25, 0x2F, 0x3A);
            return b;
        }

        // ==================== 界面 ====================

        private void BuildUi()
        {
            // ---------- 标题 ----------
            Controls.Add(new Label
            {
                Text = "phoneMic",
                Location = new Point(Pad, 14),
                AutoSize = true,
                Font = Ui(20f, FontStyle.Bold),
                ForeColor = CText
            });
            Controls.Add(new Label
            {
                Text = "手机当电脑麦克风 · 说话时波形会动",
                Location = new Point(Pad + 3, 52),
                AutoSize = true,
                Font = Ui(9.5f),
                ForeColor = CSub
            });

            // ---------- 1. 状态 + 波形 ----------
            var c1 = Card(84, 190, "连接状态");
            _dot = new Panel { Location = new Point(CardPad, 46), Size = new Size(16, 16), BackColor = CSub };
            ApplyRound(_dot, 8);
            c1.Controls.Add(_dot);

            _lblStatus = new Label
            {
                Location = new Point(CardPad + 26, 40),
                Size = new Size(c1.Width - CardPad * 2 - 26, 28),
                Font = Ui(16f, FontStyle.Bold),
                ForeColor = CText,
                Text = "未开始",
                AutoEllipsis = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            c1.Controls.Add(_lblStatus);

            _lblDetail = new Label
            {
                Location = new Point(CardPad, 74),
                Size = new Size(c1.Width - CardPad * 2, 20),
                Font = Ui(9f),
                ForeColor = CSub,
                Text = "",
                AutoEllipsis = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            c1.Controls.Add(_lblDetail);

            _wave = new Waveform
            {
                Location = new Point(CardPad, 102),
                Size = new Size(c1.Width - CardPad * 2, 56),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            c1.Controls.Add(_wave);

            _lblMeter = new Label
            {
                Location = new Point(CardPad, 162),
                Size = new Size(c1.Width - CardPad * 2, 18),
                Font = Ui(8.5f),
                ForeColor = CSub,
                Text = "电平 0%    已收 0 包",
                TextAlign = ContentAlignment.MiddleRight,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            c1.Controls.Add(_lblMeter);

            // ---------- 2. 本机地址 ----------
            var c2 = Card(288, 104, "本机地址（手机自动发现，一般不用管）");
            _txtAddr = new TextBox
            {
                Location = new Point(CardPad, 44),
                Size = new Size(c2.Width - CardPad * 2 - 112, 30),
                ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = CField,
                ForeColor = CBlue,
                Font = new Font("Consolas", 11.5f, FontStyle.Bold),
                Text = "…",
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            c2.Controls.Add(_txtAddr);
            _btnCopy = FlatButton("复制", c2.Width - CardPad - 100, 44, 100, 30);
            _btnCopy.Click += delegate { CopyAddr(); };
            c2.Controls.Add(_btnCopy);

            // ---------- 3. 输出设备 ----------
            var c3 = Card(406, 124, "输出设备（选虚拟声卡，声音才能被别的软件当麦克风）");
            _cboDevice = new ComboBox
            {
                Location = new Point(CardPad, 44),
                Size = new Size(c3.Width - CardPad * 2 - 112, 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = CField,
                ForeColor = CText,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _cboDevice.SelectedIndexChanged += OnDeviceChanged;
            c3.Controls.Add(_cboDevice);

            _btnRefresh = FlatButton("刷新", c3.Width - CardPad - 100, 43, 100, 30);
            _btnRefresh.Click += delegate { LoadDevices(); };
            c3.Controls.Add(_btnRefresh);

            _lblVb = new Label
            {
                Location = new Point(CardPad, 86),
                Size = new Size(c3.Width - CardPad * 2 - 176, 22),
                Font = Ui(8.5f),
                ForeColor = CAmber,
                Text = "",
                AutoEllipsis = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            c3.Controls.Add(_lblVb);

            _btnInstall = FlatButton("安装虚拟声卡", c3.Width - CardPad - 166, 83, 166, 28);
            _btnInstall.ForeColor = CAmber;
            _btnInstall.Visible = false;
            _btnInstall.Click += delegate { InstallVb(); };
            c3.Controls.Add(_btnInstall);

            // ---------- 4. 联动按键 ----------
            var c4 = Card(544, 124, "说话时同时按住的键（不映射就留空）");
            _lblKey = new Label
            {
                Location = new Point(CardPad, 40),
                Size = new Size(c4.Width - CardPad * 2 - 236, 32),
                Font = Ui(13f, FontStyle.Bold),
                ForeColor = CText,
                Text = "（不映射）",
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            c4.Controls.Add(_lblKey);

            _btnRecord = FlatButton("重新录制", c4.Width - CardPad - 212, 43, 100, 30);
            _btnRecord.Click += delegate { RecordKey(); };
            c4.Controls.Add(_btnRecord);

            _btnClear = FlatButton("清除", c4.Width - CardPad - 102, 43, 90, 30);
            _btnClear.Click += delegate
            {
                _cfg.Keys = new int[0];
                SaveCfg();
                UpdateKeyLabel();
                Log("已取消按键映射");
            };
            c4.Controls.Add(_btnClear);

            c4.Controls.Add(new Label
            {
                Location = new Point(CardPad, 86),
                Size = new Size(c4.Width - CardPad * 2, 22),
                Font = Ui(8.5f),
                ForeColor = CSub,
                Text = "点「重新录制」后直接在键盘上按一下；组合键就按住组合再按主键。",
                AutoEllipsis = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            });

            // ---------- 5. 选项 ----------
            var c5 = new Panel
            {
                Location = new Point(Pad, 684),
                Size = new Size(ClientSize.Width - Pad * 2, 30),
                BackColor = CBg,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _chkMin = new CheckBox
            {
                Text = "关闭窗口时最小化到托盘",
                Location = new Point(2, 6),
                AutoSize = true,
                ForeColor = CSub,
                Font = Ui(9f),
                Checked = _cfg.MinimizeToTray
            };
            _chkMin.CheckedChanged += delegate { _cfg.MinimizeToTray = _chkMin.Checked; SaveCfg(); };
            c5.Controls.Add(_chkMin);

            _chkAuto = new CheckBox
            {
                Text = "启动时自动开始监听",
                Location = new Point(276, 6),
                AutoSize = true,
                ForeColor = CSub,
                Font = Ui(9f),
                Checked = _cfg.AutoStartOnLaunch
            };
            _chkAuto.CheckedChanged += delegate { _cfg.AutoStartOnLaunch = _chkAuto.Checked; SaveCfg(); };
            c5.Controls.Add(_chkAuto);
            Controls.Add(c5);

            // ---------- 6. 开始 / 停止 ----------
            _btnStart = new Button
            {
                Text = "开 始",
                Location = new Point(Pad, 722),
                Size = new Size(ClientSize.Width - Pad * 2, 52),
                FlatStyle = FlatStyle.Flat,
                BackColor = CBlue,
                ForeColor = Color.White,
                Font = Ui(12.5f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _btnStart.FlatAppearance.BorderSize = 0;
            _btnStart.Click += delegate { if (_running) StopAll(); else StartAll(); };
            Controls.Add(_btnStart);

            // ---------- 7. 底部：最新一条日志，点开看全部 ----------
            _lblFoot = new Label
            {
                Location = new Point(Pad + 2, 782),
                Size = new Size(ClientSize.Width - Pad * 2 - 4, 18),
                Font = Ui(8.5f),
                ForeColor = CSub,
                Text = "点这里查看日志",
                AutoEllipsis = true,
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _lblFoot.Click += delegate { ShowLog(); };
            Controls.Add(_lblFoot);
        }

        private void BuildTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示主窗口", null, delegate { ShowFromTray(); });
            menu.Items.Add("开始 / 停止", null, delegate { if (_running) StopAll(); else StartAll(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("查看日志", null, delegate { ShowLog(); });
            menu.Items.Add("彻底卸载（含虚拟声卡）", null, delegate { UninstallAll(); });
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

        private static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var br = new SolidBrush(CBlue)) g.FillEllipse(br, 1, 1, 30, 30);
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

        // ==================== 设备 ====================

        private void LoadDevices()
        {
            try
            {
                var list = AudioOutput.ListDevices();
                _cboDevice.Items.Clear();
                foreach (var d in list) _cboDevice.Items.Add(d);

                int sel = 0;
                for (int i = 0; i < list.Count; i++)
                    if (list[i].Id == _cfg.DeviceId) { sel = i; break; }
                if (string.IsNullOrEmpty(_cfg.DeviceId))
                {
                    var g = AudioOutput.GuessVirtualCable(list);
                    if (g != null) sel = list.IndexOf(g);
                }
                if (_cboDevice.Items.Count > 0) _cboDevice.SelectedIndex = sel;

                UpdateAddr();
                UpdateVbHint();
            }
            catch (Exception ex)
            {
                Log("枚举设备失败：" + ex.Message);
            }
        }

        private void UpdateVbHint()
        {
            bool ok = VbCable.IsInstalled();
            _btnInstall.Visible = !ok;
            _lblVb.Text = ok
                ? "已检测到虚拟声卡"
                : (VbCable.HasBundled ? "未检测到虚拟声卡，点右边安装（驱动已内置）"
                                      : "未检测到虚拟声卡，点右边安装（需联网下载）");
            _lblVb.ForeColor = ok ? CGreen : CAmber;
        }

        private void InstallVb()
        {
            string extra = VbCable.HasBundled
                ? "驱动已随本程序附带，不需要联网。\n\n"
                : "将从官方地址下载驱动（约 1MB）。\n\n";

            if (MessageBox.Show(this,
                    "接下来会打开 VB-CABLE 官方安装程序。\n\n" + extra +
                    "· 会弹一次管理员授权（装驱动必须）\n" +
                    "· 在它的窗口里点一下「Install Driver」即可\n" +
                    "· 装完建议重启一次电脑\n" +
                    "· 不会改变你现有的默认扬声器/麦克风\n\n继续吗？",
                    "安装虚拟声卡", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                return;

            Log(VbCable.HasBundled ? "正在准备内置驱动…" : "正在下载驱动…");
            VbCable.InstallAsync((ok, msg) =>
            {
                Ui(() =>
                {
                    Log(msg.Replace("\n", " "));
                    if (ok) { LoadDevices(); MessageBox.Show(this, msg, "完成"); }
                    else MessageBox.Show(this, msg, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
            });
        }

        private void UninstallAll()
        {
            if (MessageBox.Show(this,
                    "将卸载虚拟声卡并删除配置，之后本程序可以直接删除文件夹。\n\n继续吗？",
                    "彻底卸载", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            StopAll();
            VbCable.UninstallAsync((ok, msg) =>
            {
                Ui(() =>
                {
                    MessageBox.Show(this, msg, ok ? "卸载完成" : "提示",
                        MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                });
            });
        }

        private void OnDeviceChanged(object sender, EventArgs e)
        {
            var d = _cboDevice.SelectedItem as AudioOutput.DevInfo;
            if (d == null) return;
            _cfg.DeviceId = d.Id;
            SaveCfg();
            if (_running)
            {
                StopAudio();
                StartAudio();
                Log("已切换输出设备：" + d.Name);
            }
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
            SetStatus("等待手机连接", CAmber, "在手机上打开 phoneMic 会自动找到这台电脑");
            _tick.Start();
            _meter.Start();
            Log("开始监听 UDP " + Proto.Port + "，输出到：" + SelectedDeviceName());
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
                    "\n\n请在上面的下拉框里选一个设备；如果列表里没有虚拟声卡，点「安装虚拟声卡」。",
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
            _btnStart.Text = "开 始";
            _btnStart.BackColor = CBlue;
            SetStatus("未开始", CSub, "点下面的「开始」按钮");
            Log("已停止");
        }

        // ==================== 网络事件（UDP 线程）====================

        private void OnAudio(byte[] buf, int len)
        {
            int count = len - Proto.AudioHeader;
            var a = _audio;
            if (a != null) a.AddMonoPcm(buf, Proto.AudioHeader, count);

            float lv = Rms(buf, Proto.AudioHeader, count);
            if (lv > _peak) _peak = lv;      // 峰值，UI 定时器取走
            _packets++;

            MarkConnected();
        }

        private void OnPttDown()
        {
            var a = _audio;
            if (a != null) a.Clear();
            lock (_keyLock) KeyInjector.Press(_cfg.Keys);
            MarkConnected();
            Ui(() => { _silentTicks = 0; SetTalking(true); });
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
                if (!_talking)
                    SetStatus("已连接 · 待命", CGreen, "手机：" + (_rx != null ? _rx.PeerIp : ""));
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

            // 在录音、但 3 秒一点声音都没有 → 提醒（多半是手机麦克风被占用或静音）
            if (_talking && _audio != null)
            {
                if (p < 0.005f)
                {
                    _silentTicks++;
                    if (_silentTicks == 60)
                        Log("提示：已在录音，但收到的音量一直是 0 —— 检查手机麦克风是否被别的 App 占用或静音");
                }
                else _silentTicks = 0;
            }
        }

        /// <summary>算一段 PCM 的 RMS 并放大到 0~1（语音幅度本来就小，不放大波形几乎看不见）</summary>
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

        // ==================== 界面状态 ====================

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
            _talking = on;
            if (on)
            {
                SetStatus("● 正在说话 · 录音中", CRed, "声音正在送往：" + SelectedDeviceName());
                if (_wave != null) _wave.Reset();
            }
            else if (_connected) SetStatus("已连接 · 待命", CGreen, "手机：" + (_rx != null ? _rx.PeerIp : ""));
            else SetStatus("等待手机连接", CAmber, "在手机上打开 phoneMic 会自动找到这台电脑");
        }

        private string SelectedDeviceName()
        {
            var d = _cboDevice.SelectedItem as AudioOutput.DevInfo;
            return d != null ? d.Name : "（未选择设备）";
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (!_running || _rx == null) return;
            if (_rx.MsSinceLastPacket > 2000)
            {
                if (_talking)
                {
                    lock (_keyLock) KeyInjector.ReleaseAll();
                    _talking = false;
                    Log("手机断开，已松开所有按键");
                }
                if (_connected)
                {
                    _connected = false;
                    SetStatus("等待手机连接", CAmber, "手机断开了，会在它重新打开时自动连上");
                }
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
                        SaveCfg();
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
            _lblKey.ForeColor = has ? CText : CSub;
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

        private void Log(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg;
            _logBuf.Add(line);
            if (_logBuf.Count > 500) _logBuf.RemoveAt(0);

            // 同时落盘，出问题时可以直接把文件发我
            try { File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8); }
            catch { }

            if (_lblFoot != null) _lblFoot.Text = msg;
        }

        private void ShowLog()
        {
            var f = new Form
            {
                Text = "phoneMic 日志",
                ClientSize = new Size(700, 480),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = CBg,
                ForeColor = CText,
                Font = Ui(9.5f),
                MinimizeBox = false
            };

            var box = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Location = new Point(16, 16),
                Size = new Size(f.ClientSize.Width - 32, f.ClientSize.Height - 76),
                BackColor = CField,
                ForeColor = CText,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9f),
                WordWrap = false,
                Text = string.Join(Environment.NewLine, _logBuf.ToArray()),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };
            f.Controls.Add(box);

            var btnCopy = new Button
            {
                Text = "复制全部",
                Location = new Point(16, f.ClientSize.Height - 48),
                Size = new Size(120, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = CField,
                ForeColor = CText,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };
            btnCopy.FlatAppearance.BorderColor = Color.FromArgb(0x2C, 0x35, 0x40);
            btnCopy.Click += delegate { try { Clipboard.SetText(box.Text); } catch { } };
            f.Controls.Add(btnCopy);

            var btnFile = new Button
            {
                Text = "打开日志文件夹",
                Location = new Point(148, f.ClientSize.Height - 48),
                Size = new Size(150, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = CField,
                ForeColor = CText,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };
            btnFile.FlatAppearance.BorderColor = Color.FromArgb(0x2C, 0x35, 0x40);
            btnFile.Click += delegate { try { System.Diagnostics.Process.Start("explorer.exe", LogDir); } catch { } };
            f.Controls.Add(btnFile);

            f.ShowDialog(this);
        }

        // ==================== 杂项 ====================

        private void UpdateAddr()
        {
            _txtAddr.Text = NetUtil.LocalIPv4() + " : " + Proto.Port;
        }

        private void CopyAddr()
        {
            try
            {
                Clipboard.SetText(_txtAddr.Text);
                Log("地址已复制");
            }
            catch { }
        }

        private void SaveCfg()
        {
            _cfg.Save();
        }

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
