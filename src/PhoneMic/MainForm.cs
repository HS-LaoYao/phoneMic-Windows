using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PhoneMic
{
    /// <summary>
    /// 主窗口。左列从上到下：状态 / 本机地址 / 输出设备 / 联动按键 / 选项 / 日志。
    /// 界面全部用代码搭，不用 Designer，省掉一堆文件。
    /// </summary>
    internal sealed class MainForm : Form
    {
        // ===== 配色（深色，克制一点，看着干净）=====
        private static readonly Color CBg = Color.FromArgb(0x12, 0x16, 0x1A);
        private static readonly Color CCard = Color.FromArgb(0x1B, 0x22, 0x2A);
        private static readonly Color CField = Color.FromArgb(0x11, 0x16, 0x1B);
        private static readonly Color CText = Color.FromArgb(0xE6, 0xED, 0xF3);
        private static readonly Color CSub = Color.FromArgb(0x8B, 0x98, 0xA5);
        private static readonly Color CBlue = Color.FromArgb(0x3F, 0xA7, 0xFF);
        private static readonly Color CGreen = Color.FromArgb(0x3F, 0xB9, 0x50);
        private static readonly Color CRed = Color.FromArgb(0xF8, 0x51, 0x49);
        private static readonly Color CAmber = Color.FromArgb(0xD2, 0x99, 0x22);

        private Config _cfg;
        private UdpReceiver _rx;
        private AudioOutput _audio;
        private KeyCapture _cap;
        private Timer _tick;
        private NotifyIcon _tray;

        private readonly object _keyLock = new object();
        private bool _running;
        private bool _talking;
        private bool _connected;

        // ===== 控件 =====
        private Panel _dot;
        private Label _lblStatus, _lblDetail, _lblKey, _lblVb;
        private TextBox _txtAddr, _txtLog;
        private ComboBox _cboDevice;
        private Button _btnCopy, _btnRefresh, _btnInstall, _btnRecord, _btnClear, _btnStart;
        private CheckBox _chkMin, _chkAuto;

        public MainForm()
        {
            _cfg = Config.Load();

            Text = "phoneMic";
            ClientSize = new Size(560, 668);
            MinimumSize = new Size(560, 668);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = CBg;
            ForeColor = CText;
            Font = Ui(9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            DoubleBuffered = true;
            Icon = MakeIcon();

            BuildUi();
            BuildTray();

            _tick = new Timer();
            _tick.Interval = 400;
            _tick.Tick += OnTick;

            LoadDevices();
            UpdateKeyLabel();
            SetStatus("未开始", CSub, "点下面的「开始」按钮");

            Log("phoneMic 已启动。手机和电脑连同一个 WiFi 即可。");
            if (_cfg.AutoStartOnLaunch) BeginInvoke(new Action(StartAll));
        }

        // ==================== 界面搭建 ====================

        private static Font Ui(float size)
        {
            return Ui(size, FontStyle.Regular);
        }

        private static Font Ui(float size, FontStyle style)
        {
            try { return new Font("Microsoft YaHei UI", size, style); }
            catch { return new Font(FontFamily.GenericSansSerif, size, style); }
        }

        private Panel Card(int y, int h, string title)
        {
            var p = new Panel
            {
                Location = new Point(20, y),
                Size = new Size(ClientSize.Width - 40, h),
                BackColor = CCard,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            p.Controls.Add(new Label
            {
                Text = title,
                ForeColor = CSub,
                Font = Ui(8.5f),
                AutoSize = true,
                Location = new Point(14, 9)
            });
            Controls.Add(p);
            return p;
        }

        private static void Circle(Control c)
        {
            var gp = new GraphicsPath();
            gp.AddEllipse(0, 0, c.Width, c.Height);
            c.Region = new Region(gp);
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
                Font = Ui(9f),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(0x2C, 0x35, 0x40);
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0x25, 0x2F, 0x3A);
            return b;
        }

        private void BuildUi()
        {
            // ---------- 1. 状态 ----------
            var c1 = Card(16, 96, "连接状态");
            _dot = new Panel { Location = new Point(16, 38), Size = new Size(14, 14), BackColor = CSub };
            Circle(_dot);
            c1.Controls.Add(_dot);

            _lblStatus = new Label
            {
                Location = new Point(42, 32),
                Size = new Size(c1.Width - 60, 30),
                Font = Ui(15f, FontStyle.Bold),
                ForeColor = CText,
                Text = "未开始",
                AutoEllipsis = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            c1.Controls.Add(_lblStatus);

            _lblDetail = new Label
            {
                Location = new Point(42, 64),
                Size = new Size(c1.Width - 60, 22),
                Font = Ui(9f),
                ForeColor = CSub,
                Text = "",
                AutoEllipsis = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            c1.Controls.Add(_lblDetail);

            // ---------- 2. 本机地址 ----------
            var c2 = Card(124, 88, "本机地址（手机自动发现，一般不用管）");
            _txtAddr = new TextBox
            {
                Location = new Point(14, 34),
                Size = new Size(c2.Width - 130, 28),
                ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = CField,
                ForeColor = CBlue,
                Font = new Font("Consolas", 11f, FontStyle.Bold),
                Text = "…",
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            c2.Controls.Add(_txtAddr);
            _btnCopy = FlatButton("复制", c2.Width - 104, 34, 90, 28);
            _btnCopy.Click += delegate { CopyAddr(); };
            c2.Controls.Add(_btnCopy);

            // ---------- 3. 输出设备 ----------
            var c3 = Card(224, 104, "输出设备（选虚拟声卡，声音才能被别的软件当麦克风）");
            _cboDevice = new ComboBox
            {
                Location = new Point(14, 34),
                Size = new Size(c3.Width - 130, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = CField,
                ForeColor = CText,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _cboDevice.SelectedIndexChanged += OnDeviceChanged;
            c3.Controls.Add(_cboDevice);

            _btnRefresh = FlatButton("刷新", c3.Width - 104, 33, 90, 28);
            _btnRefresh.Click += delegate { LoadDevices(); };
            c3.Controls.Add(_btnRefresh);

            _lblVb = new Label
            {
                Location = new Point(14, 70),
                Size = new Size(c3.Width - 160, 22),
                Font = Ui(8.5f),
                ForeColor = CAmber,
                Text = "",
                AutoEllipsis = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            c3.Controls.Add(_lblVb);

            _btnInstall = FlatButton("安装虚拟声卡", c3.Width - 154, 68, 140, 26);
            _btnInstall.ForeColor = CAmber;
            _btnInstall.Click += delegate { InstallVb(); };
            _btnInstall.Visible = false;
            c3.Controls.Add(_btnInstall);

            // ---------- 4. 联动按键 ----------
            var c4 = Card(340, 104, "说话时同时按住的键（不映射就留空）");
            _lblKey = new Label
            {
                Location = new Point(14, 34),
                Size = new Size(c4.Width - 230, 26),
                Font = Ui(12f, FontStyle.Bold),
                ForeColor = CText,
                Text = "（不映射）",
                AutoEllipsis = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            c4.Controls.Add(_lblKey);

            _btnRecord = FlatButton("重新录制", c4.Width - 214, 33, 100, 28);
            _btnRecord.Click += delegate { RecordKey(); };
            c4.Controls.Add(_btnRecord);

            _btnClear = FlatButton("清除", c4.Width - 104, 33, 90, 28);
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
                Location = new Point(14, 70),
                Size = new Size(c4.Width - 30, 22),
                Font = Ui(8.5f),
                ForeColor = CSub,
                Text = "点「重新录制」后直接在键盘上按一下；想录组合键就按住组合再松手。",
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            });

            // ---------- 5. 选项 ----------
            var c5 = new Panel
            {
                Location = new Point(20, 456),
                Size = new Size(ClientSize.Width - 40, 30),
                BackColor = CBg,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _chkMin = new CheckBox
            {
                Text = "关闭窗口时最小化到托盘",
                Location = new Point(0, 4),
                AutoSize = true,
                ForeColor = CSub,
                Checked = _cfg.MinimizeToTray
            };
            _chkMin.CheckedChanged += delegate { _cfg.MinimizeToTray = _chkMin.Checked; SaveCfg(); };
            c5.Controls.Add(_chkMin);

            _chkAuto = new CheckBox
            {
                Text = "启动时自动开始监听",
                Location = new Point(230, 4),
                AutoSize = true,
                ForeColor = CSub,
                Checked = _cfg.AutoStartOnLaunch
            };
            _chkAuto.CheckedChanged += delegate { _cfg.AutoStartOnLaunch = _chkAuto.Checked; SaveCfg(); };
            c5.Controls.Add(_chkAuto);
            Controls.Add(c5);

            // ---------- 6. 开始 / 停止 ----------
            _btnStart = new Button
            {
                Text = "开 始",
                Location = new Point(20, 496),
                Size = new Size(ClientSize.Width - 40, 46),
                FlatStyle = FlatStyle.Flat,
                BackColor = CBlue,
                ForeColor = Color.White,
                Font = Ui(12f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _btnStart.FlatAppearance.BorderSize = 0;
            _btnStart.Click += delegate { if (_running) StopAll(); else StartAll(); };
            Controls.Add(_btnStart);

            // ---------- 7. 日志 ----------
            _txtLog = new TextBox
            {
                Location = new Point(20, 556),
                Size = new Size(ClientSize.Width - 40, 92),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.None,
                BackColor = CCard,
                ForeColor = CSub,
                Font = Ui(8.5f),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };
            Controls.Add(_txtLog);

            Resize += delegate
            {
                // 卡片宽度跟着窗口走，其余靠 Anchor 自动处理
                foreach (Control c in Controls)
                {
                    if (c is Panel && c.BackColor == CCard && c.Height >= 88)
                        c.Width = ClientSize.Width - 40;
                }
                _btnStart.Width = ClientSize.Width - 40;
                _txtLog.Width = ClientSize.Width - 40;
                _btnInstall.Left = _btnInstall.Parent.Width - 154;
                _btnRefresh.Left = _btnRefresh.Parent.Width - 104;
                _btnCopy.Left = _btnCopy.Parent.Width - 104;
                _btnRecord.Left = _btnRecord.Parent.Width - 214;
                _btnClear.Left = _btnClear.Parent.Width - 104;
            };
        }

        private void BuildTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示主窗口", null, delegate { ShowFromTray(); });
            menu.Items.Add("开始 / 停止", null, delegate { if (_running) StopAll(); else StartAll(); });
            menu.Items.Add(new ToolStripSeparator());
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
                        g.FillRectangle(br, 14, 7, 4, 11);       // 话筒柱
                        g.FillRectangle(br, 11, 15, 10, 3);      // 话筒头底座
                        g.FillRectangle(br, 15, 18, 2, 5);       // 支架
                        g.FillRectangle(br, 11, 23, 10, 2);      // 底座
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
                BeginInvoke(new Action(() =>
                {
                    Log(msg.Replace("\n", " "));
                    if (ok) { LoadDevices(); MessageBox.Show(this, msg, "完成"); }
                    else MessageBox.Show(this, msg, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }));
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
                BeginInvoke(new Action(() =>
                {
                    MessageBox.Show(this, msg, ok ? "卸载完成" : "提示",
                        MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                }));
            });
        }

        private void OnDeviceChanged(object sender, EventArgs e)
        {
            var d = _cboDevice.SelectedItem as AudioOutput.DevInfo;
            if (d == null) return;
            _cfg.DeviceId = d.Id;
            SaveCfg();
            if (_running)                     // 换设备要重开播放
            {
                StopAudio();
                StartAudio();
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
            _rx.Discovered += delegate(string ip) { BeginInvoke(new Action(() => Log("手机正在查找本机：" + ip))); };
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
            _btnStart.Text = "停 止";
            _btnStart.BackColor = Color.FromArgb(0x2C, 0x35, 0x40);
            SetStatus("等待手机连接", CAmber, "在手机上打开 phoneMic 会自动找到这台电脑");
            _tick.Start();
            Log("开始监听 UDP " + Proto.Port + "，请在手机上连接");
        }

        private bool StartAudio()
        {
            try
            {
                StopAudio();
                _audio = new AudioOutput();
                _audio.Error += delegate(string m) { BeginInvoke(new Action(() => Log(m))); };
                _audio.Start(_cfg.DeviceId);
                return true;
            }
            catch (Exception ex)
            {
                _audio = null;
                MessageBox.Show(this,
                    "打不开输出设备：\n" + ex.Message +
                    "\n\n请先在上面的下拉框里选一个设备；如果列表里没有虚拟声卡，点「安装虚拟声卡」。",
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
            _tick.Stop();
            lock (_keyLock) KeyInjector.ReleaseAll();
            if (_rx != null) { _rx.Dispose(); _rx = null; }
            StopAudio();
            _running = false;
            _talking = false;
            _connected = false;
            _btnStart.Text = "开 始";
            _btnStart.BackColor = CBlue;
            SetStatus("未开始", CSub, "点下面的「开始」按钮");
            Log("已停止");
        }

        // ==================== 网络事件（在 UDP 线程上触发）====================

        private void OnAudio(byte[] buf, int len)
        {
            var a = _audio;
            if (a != null) a.AddMonoPcm(buf, Proto.AudioHeader, len - Proto.AudioHeader);
            MarkConnected();
        }

        private void OnPttDown()
        {
            var a = _audio;
            if (a != null) a.Clear();             // 丢掉缓冲里的旧声音，避免开口时说半句旧的
            lock (_keyLock) KeyInjector.Press(_cfg.Keys);
            MarkConnected();
            BeginInvoke(new Action(() => SetTalking(true)));
        }

        private void OnPttUp()
        {
            lock (_keyLock) KeyInjector.ReleaseAll();
            BeginInvoke(new Action(() => SetTalking(false)));
        }

        private void OnHeartbeat(int mode)
        {
            MarkConnected();
        }

        private void MarkConnected()
        {
            if (_connected) return;
            _connected = true;
            BeginInvoke(new Action(() =>
            {
                if (!_talking)
                    SetStatus("已连接 · 待命", CGreen,
                        "手机：" + (_rx != null ? _rx.PeerIp : "") + "　在手机上按住按钮说话");
                Log("手机已连接：" + (_rx != null ? _rx.PeerIp : ""));
            }));
        }

        // ==================== 界面状态 ====================

        private void SetStatus(string text, Color color, string detail)
        {
            _lblStatus.Text = text;
            _dot.BackColor = color;
            _lblDetail.Text = detail;
            if (_tray != null) _tray.Text = "phoneMic - " + text;
        }

        private void SetTalking(bool on)
        {
            _talking = on;
            if (on) SetStatus("● 正在说话 · 录音中", CRed, "声音正在送往：" + SelectedDeviceName());
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
                // 2 秒没收到任何包 = 掉线。这里必须松开按键，否则 Alt 会一直按着。
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
            if (_cap != null) return;                 // 已经在录了
            _btnRecord.Text = "请按键…";
            _btnRecord.Enabled = false;
            Log("请按下要联动的键（按 Esc 取消）");

            _cap = new KeyCapture();
            _cap.Captured += delegate(int[] keys)
            {
                BeginInvoke(new Action(() =>
                {
                    // 单独按 Esc 视为取消，避免误录
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
                    _cap.Dispose();
                    _cap = null;
                    _btnRecord.Text = "重新录制";
                    _btnRecord.Enabled = true;
                    UpdateKeyLabel();
                }));
            };
            _cap.Start();
        }

        private void UpdateKeyLabel()
        {
            bool has = _cfg.Keys != null && _cfg.Keys.Length > 0;
            _lblKey.Text = KeyInjector.Describe(_cfg.Keys);
            _lblKey.ForeColor = has ? CText : CSub;
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

        private void Log(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine;
            if (_txtLog.TextLength > 8000) _txtLog.Clear();
            _txtLog.AppendText(line);
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
