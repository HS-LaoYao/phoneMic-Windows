using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.CoreAudioApi;

namespace PhoneMic
{
    /// <summary>
    /// 设置窗口：地址、输出设备、系统麦克风、声卡自检、选项、日志。
    /// 这些都是一次性配置，平时不用看，所以从主界面挪出来，主界面只留状态和波形。
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private const int W = 660;
        private const int H = 994;

        private readonly MainForm _owner;
        private readonly Config _cfg;

        private Label _lblRender, _lblVol;
        private TrackBar _trackVol;
        private CheckBox _chkMutePc;
        private TextBox _txtAddr;
        private ComboBox _cboDevice, _cboKeyMode;
        private Label _lblVb, _lblMicConsole, _lblMicComm, _lblTest;
        private Button _btnInstall, _btnSetMic, _btnRestoreMic, _btnSelfTest;
        private CheckBox _chkMin, _chkAuto;
        private TextBox _txtLog;

        private float _lastPeak = -1f;

        public SettingsForm(MainForm owner, Config cfg)
        {
            _owner = owner;
            _cfg = cfg;

            Text = "phoneMic · 设置";
            BackColor = UiTheme.Bg;
            ForeColor = UiTheme.Text;
            Font = UiTheme.Font(10.5f);
            AutoScaleMode = AutoScaleMode.None;   // 缩放由 UiTheme.S() 统一负责

            UiTheme.Init(this);
            ClientSize = new Size(UiTheme.S(W), UiTheme.S(H));
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            BuildUi();
            LoadDevices();
            RefreshMicInfo();
            RefreshLog();
        }

        // ==================== 界面 ====================

        private void BuildUi()
        {
            int w = W - 48;

            // ---------- 本机地址 ----------
            var c1 = UiTheme.MakeCard(this, 24, 20, w, 100, "本机地址（手机自动发现，一般不用管）");
            _txtAddr = new TextBox
            {
                Location = new Point(UiTheme.S(20), UiTheme.S(48)),
                Size = new Size(UiTheme.S(w - 20 - 132), UiTheme.S(34)),
                ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = UiTheme.Field,
                ForeColor = UiTheme.Blue,
                Font = new System.Drawing.Font("Consolas", 11.5f, FontStyle.Bold),
                Text = NetUtil.LocalIPv4() + " : " + Proto.Port
            };
            c1.Controls.Add(_txtAddr);

            var btnCopy = UiTheme.MakeButton("复制", w - 20 - 120, 48, 120, 34);
            btnCopy.Click += delegate
            {
                try { Clipboard.SetText(_txtAddr.Text); } catch { }
            };
            c1.Controls.Add(btnCopy);

            // ---------- 输出设备 ----------
            var c2 = UiTheme.MakeCard(this, 24, 136, w, 252, "音频路由（麦克风输出 / 扬声器 / 监听）");
            _cboDevice = new ComboBox
            {
                Location = new Point(UiTheme.S(20), UiTheme.S(48)),
                Size = new Size(UiTheme.S(w - 20 - 124), UiTheme.S(30)),
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = UiTheme.Field,
                ForeColor = UiTheme.Text
            };
            _cboDevice.SelectedIndexChanged += OnDeviceChanged;
            c2.Controls.Add(_cboDevice);

            var btnRefresh = UiTheme.MakeButton("刷新", w - 20 - 112, 47, 112, 32);
            btnRefresh.Click += delegate { LoadDevices(); };
            c2.Controls.Add(btnRefresh);

            _lblVb = UiTheme.MakeLabel("", 20, 92, w - 20 - 200, 26, 9.5f, UiTheme.Amber);
            c2.Controls.Add(_lblVb);

            _btnInstall = UiTheme.MakeButton("安装虚拟声卡", w - 20 - 190, 90, 190, 32);
            _btnInstall.ForeColor = UiTheme.Amber;
            _btnInstall.Visible = false;
            _btnInstall.Click += delegate { InstallVb(); };
            c2.Controls.Add(_btnInstall);

            c2.Controls.Add(UiTheme.MakeLabel("位置：控制面板 → 声音 → 录制，能看到 CABLE Output 就说明装好了",
                20, 122, w - 40, 24, 9f, UiTheme.Sub));

            // 这一行是给「装了之后别的软件没声音」准备的：
            // 如果默认播放变成了 CABLE Input，网易云这类软件就"放歌没声"。
            _lblRender = UiTheme.MakeLabel("", 20, 152, w - 20 - 160, 26, 9.5f, UiTheme.Sub);
            c2.Controls.Add(_lblRender);

            var btnSound = UiTheme.MakeButton("打开声音设置", w - 20 - 150, 150, 150, 32);
            btnSound.Font = UiTheme.Font(9.5f);
            btnSound.Click += delegate
            {
                try { System.Diagnostics.Process.Start("ms-settings:sound"); } catch { }
            };
            c2.Controls.Add(btnSound);

            // 监听音量（模式②：手机的声音同时在扬声器放出来）
            c2.Controls.Add(UiTheme.MakeLabel("监听音量", 20, 190, 90, 28, 9.5f, UiTheme.Sub));
            _trackVol = new TrackBar
            {
                Location = new Point(UiTheme.S(112), UiTheme.S(184)),
                Size = new Size(UiTheme.S(360), UiTheme.S(40)),
                Minimum = 0,
                Maximum = 100,
                TickStyle = TickStyle.None,
                Value = Math.Max(0, Math.Min(100, _cfg.MonitorVolume)),
                BackColor = UiTheme.CardBg
            };
            _lblVol = UiTheme.MakeLabel(_cfg.MonitorVolume + "%", 486, 190, 70, 28, 9.5f, UiTheme.Text);
            _trackVol.Scroll += delegate
            {
                _cfg.MonitorVolume = _trackVol.Value;
                _cfg.Save();
                _lblVol.Text = _trackVol.Value + "%";
                _owner.ApplyMonitorVolume(_trackVol.Value / 100f);
            };
            c2.Controls.Add(_trackVol);
            c2.Controls.Add(_lblVol);

            // 模式③时是否把电脑扬声器静音
            _chkMutePc = new CheckBox
            {
                Text = "模式③时静音电脑扬声器（声音只在手机里出）",
                Location = new Point(UiTheme.S(20), UiTheme.S(224)),
                AutoSize = true,
                ForeColor = UiTheme.Sub,
                Font = UiTheme.Font(10f),
                Checked = _cfg.MutePcInHeadset
            };
            _chkMutePc.CheckedChanged += delegate
            {
                _cfg.MutePcInHeadset = _chkMutePc.Checked;
                _cfg.Save();
                _owner.ReloadAfterSettings();
            };
            c2.Controls.Add(_chkMutePc);

            // ---------- 系统麦克风 ----------
            var c3 = UiTheme.MakeCard(this, 24, 404, w, 210, "别的软件用哪个麦克风（必须是 CABLE Output）");

            _lblMicConsole = UiTheme.MakeLabel("默认设备：读取中…", 20, 48, w - 20 - 190, 26, 10f, UiTheme.Sub);
            c3.Controls.Add(_lblMicConsole);

            _lblMicComm = UiTheme.MakeLabel("默认通信设备：读取中…", 20, 80, w - 20 - 190, 26, 10f, UiTheme.Sub);
            c3.Controls.Add(_lblMicComm);

            _btnSetMic = UiTheme.MakeButton("设为默认麦克风", w - 20 - 176, 46, 176, 34);
            _btnSetMic.ForeColor = UiTheme.Green;
            _btnSetMic.Click += delegate { SetAsDefaultMic(); };
            c3.Controls.Add(_btnSetMic);

            _btnRestoreMic = UiTheme.MakeButton("恢复原来的", w - 20 - 176, 84, 176, 30);
            _btnRestoreMic.Font = UiTheme.Font(9.5f);
            _btnRestoreMic.Visible = false;
            _btnRestoreMic.Click += delegate { RestorePrevMic(); };
            c3.Controls.Add(_btnRestoreMic);

            _btnSelfTest = UiTheme.MakeButton("声卡自检（录 3 秒）", 20, 124, 210, 38);
            _btnSelfTest.ForeColor = UiTheme.Blue;
            _btnSelfTest.Click += delegate { RunSelfTest(); };
            c3.Controls.Add(_btnSelfTest);

            _lblTest = UiTheme.MakeLabel("点它，然后对着手机说话 —— 看虚拟声卡里到底有没有声音", 244, 130, w - 20 - 244, 28, 9f, UiTheme.Sub);
            c3.Controls.Add(_lblTest);

            // ---------- 选项 ----------
            var c4 = UiTheme.MakeCard(this, 24, 630, w, 128, "选项");
            _chkMin = new CheckBox
            {
                Text = "关闭窗口时最小化到托盘（不退出）",
                Location = new Point(UiTheme.S(20), UiTheme.S(48)),
                AutoSize = true,
                ForeColor = UiTheme.Sub,
                Font = UiTheme.Font(10f),
                Checked = _cfg.MinimizeToTray
            };
            _chkMin.CheckedChanged += delegate { _cfg.MinimizeToTray = _chkMin.Checked; _cfg.Save(); };
            c4.Controls.Add(_chkMin);

            _chkAuto = new CheckBox
            {
                Text = "打开程序时自动开始监听",
                Location = new Point(UiTheme.S(20), UiTheme.S(72)),
                AutoSize = true,
                ForeColor = UiTheme.Sub,
                Font = UiTheme.Font(10f),
                Checked = _cfg.AutoStartOnLaunch
            };
            _chkAuto.CheckedChanged += delegate { _cfg.AutoStartOnLaunch = _chkAuto.Checked; _cfg.Save(); };
            c4.Controls.Add(_chkAuto);

            // 注入方式：三种都试一遍，哪个能让目标软件有反应就用哪个
            c4.Controls.Add(UiTheme.MakeLabel("按键注入方式", 20, 94, 130, 26, 9.5f, UiTheme.Sub));
            _cboKeyMode = new ComboBox
            {
                Location = new Point(UiTheme.S(150), UiTheme.S(92)),
                Size = new Size(UiTheme.S(320), UiTheme.S(28)),
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = UiTheme.Field,
                ForeColor = UiTheme.Text
            };
            _cboKeyMode.Items.Add("虚拟键码（默认，先试这个）");
            _cboKeyMode.Items.Add("扫描码（游戏或者特殊程序用）");
            _cboKeyMode.Items.Add("消息投递（屏蔽了模拟按键的程序用）");
            _cboKeyMode.Items.Add("双路（两种一起发，最不容易漏，推荐先试这个）");
            _cboKeyMode.SelectedIndex = Math.Max(0, Math.Min(3, _cfg.KeyMode));
            _cboKeyMode.SelectedIndexChanged += delegate
            {
                _cfg.KeyMode = _cboKeyMode.SelectedIndex;
                _cfg.Save();
                KeyInjector.Mode = (KeyInjector.InjectMode)_cfg.KeyMode;
                _owner.Log("按键注入方式改为：" + _cboKeyMode.Text);
            };
            c4.Controls.Add(_cboKeyMode);

            // ---------- 日志 ----------
            var c5 = UiTheme.MakeCard(this, 24, 774, w, 200, "日志（出问题时把这里的内容发我）");
            _txtLog = new TextBox
            {
                Location = new Point(UiTheme.S(20), UiTheme.S(46)),
                Size = new Size(UiTheme.S(w - 40), UiTheme.S(108)),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.None,
                BackColor = UiTheme.Field,
                ForeColor = UiTheme.Sub,
                Font = new System.Drawing.Font("Consolas", 9f),
                WordWrap = false
            };
            c5.Controls.Add(_txtLog);

            var btnCopyLog = UiTheme.MakeButton("复制日志", 20, 160, 120, 32);
            btnCopyLog.Font = UiTheme.Font(9.5f);
            btnCopyLog.Click += delegate { try { Clipboard.SetText(_txtLog.Text); } catch { } };
            c5.Controls.Add(btnCopyLog);

            var btnOpenDir = UiTheme.MakeButton("打开日志文件夹", 148, 160, 150, 32);
            btnOpenDir.Font = UiTheme.Font(9.5f);
            btnOpenDir.Click += delegate
            {
                try { System.Diagnostics.Process.Start("explorer.exe", LogDir); } catch { }
            };
            c5.Controls.Add(btnOpenDir);

            var btnRefreshLog = UiTheme.MakeButton("刷新", w - 20 - 100, 160, 100, 32);
            btnRefreshLog.Font = UiTheme.Font(9.5f);
            btnRefreshLog.Click += delegate { RefreshLog(); };
            c5.Controls.Add(btnRefreshLog);
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

                bool ok = VbCable.IsInstalled();
                _btnInstall.Visible = !ok;
                _lblVb.Text = ok
                    ? "已检测到虚拟声卡"
                    : (VbCable.HasBundled ? "未检测到虚拟声卡（驱动已内置，可离线安装）"
                                          : "未检测到虚拟声卡（需要联网下载）");
                _lblVb.ForeColor = ok ? UiTheme.Green : UiTheme.Amber;
            }
            catch (Exception ex)
            {
                _owner.Log("枚举设备失败：" + ex.Message);
            }
        }

        private void OnDeviceChanged(object sender, EventArgs e)
        {
            var d = _cboDevice.SelectedItem as AudioOutput.DevInfo;
            if (d == null) return;
            _cfg.DeviceId = d.Id;
            _cfg.Save();
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

            _owner.Log("开始安装虚拟声卡…");
            VbCable.InstallAsync((ok, msg) =>
            {
                if (!IsHandleCreated || IsDisposed) return;
                BeginInvoke(new Action(() =>
                {
                    _owner.Log(msg.Replace("\n", " "));
                    LoadDevices();
                    RefreshMicInfo();
                    MessageBox.Show(this, msg, ok ? "完成" : "提示",
                        MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                }));
            });
        }

        // ==================== 系统麦克风 ====================

        private void RefreshMicInfo()
        {
            string console = AudioOutput.GetDefaultCaptureName(Role.Console);
            string comm = AudioOutput.GetDefaultCaptureName(Role.Communications);

            SetMicLine(_lblMicConsole, "默认设备", console);
            SetMicLine(_lblMicComm, "默认通信设备", comm);

            bool isCable = IsCable(console) || IsCable(comm);
            _btnRestoreMic.Visible = isCable && !(string.IsNullOrEmpty(_cfg.PrevConsoleId) && string.IsNullOrEmpty(_cfg.PrevCommId));

            _owner.Log("系统麦克风 → 默认：" + (console ?? "无") + "；通信：" + (comm ?? "无"));
            RefreshRenderInfo();
            _owner.NotifyMicState(isCable, console);
        }

        /// <summary>
        /// 检查「默认播放设备」有没有被指到虚拟声卡。
        /// 这种情况的典型症状：别的软件（网易云、QQ音乐）能播但没声音 —— 声音进了 CABLE Output 没人接。
        /// </summary>
        private void RefreshRenderInfo()
        {
            if (_lblRender == null) return;

            string name = AudioOutput.GetDefaultRenderName(Role.Multimedia);
            if (string.IsNullOrEmpty(name))
            {
                _lblRender.Text = "系统默认播放设备：读不到";
                _lblRender.ForeColor = UiTheme.Amber;
                return;
            }

            bool wrong = name.IndexOf("CABLE", StringComparison.OrdinalIgnoreCase) >= 0;

            // 模式③ 本来就要把默认播放改到虚拟声卡，这时候不能报错
            if (wrong && _cfg.RouteMode == 3)
            {
                _lblRender.Text = "默认播放设备：" + name + "（模式③正常现象，停止后自动恢复）";
                _lblRender.ForeColor = UiTheme.Sub;
                return;
            }

            if (wrong)
            {
                _lblRender.Text = "⚠ 系统默认播放设备是「" + name + "」→ 放歌会没声音，请改回扬声器";
                _lblRender.ForeColor = UiTheme.Red;
                _owner.Log("警告：默认播放设备指向了虚拟声卡（" + name + "），其他软件会没声音");
            }
            else
            {
                _lblRender.Text = "系统默认播放设备：" + name + "   ✓ 正常";
                _lblRender.ForeColor = UiTheme.Green;
            }
        }

        private static bool IsCable(string name)
        {
            return name != null && name.IndexOf("CABLE", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void SetMicLine(Label lbl, string caption, string name)
        {
            if (lbl == null) return;
            if (string.IsNullOrEmpty(name))
            {
                lbl.Text = caption + "：读不到";
                lbl.ForeColor = UiTheme.Amber;
                return;
            }
            bool ok = IsCable(name);
            lbl.Text = caption + "：" + name + (ok ? "   ✓ 正确" : "   ✗ 不是虚拟声卡");
            lbl.ForeColor = ok ? UiTheme.Green : UiTheme.Amber;
        }

        /// <summary>找系统里的 CABLE Output 设备 ID，找不到返回 null</summary>
        private static string FindCableCaptureId()
        {
            try
            {
                foreach (var d in AudioOutput.ListCaptureDevices())
                    if (d.Name != null && d.Name.IndexOf("CABLE", StringComparison.OrdinalIgnoreCase) >= 0)
                        return d.Id;
            }
            catch { }
            return null;
        }

        private void SetAsDefaultMic()
        {
            string cableId = FindCableCaptureId();
            if (cableId == null)
            {
                MessageBox.Show(this, "系统里没有找到 CABLE Output。\n\n虚拟声卡可能没装好，先点上面的「安装虚拟声卡」。",
                    "设为默认麦克风", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (MessageBox.Show(this,
                    "会把系统的「默认设备」和「默认通信设备」都改成 CABLE Output。\n\n" +
                    "改完后 QQ、微信语音、语音输入这些软件就都能收到手机的声音。\n\n" +
                    "注意：不开 phoneMic 时，它们会录到静音（点「恢复原来的」可随时改回）。\n\n继续吗？",
                    "设为默认麦克风", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                return;

            // 改麦克风前，先把「默认播放设备」拍个快照；改完如果被动了就还原
            string[] renderSnap = _owner.SnapshotRenderIds();

            _cfg.PrevConsoleId = AudioDefault.GetDefaultCaptureId(Role.Console);
            _cfg.PrevCommId = AudioOutput.GetDefaultCaptureName(Role.Communications) == null
                ? "" : AudioDefault.GetDefaultCaptureId(Role.Communications);
            _cfg.Save();

            try
            {
                AudioDefault.SetDefaultAll(cableId);
                _owner.RestoreRenderIdsIfChanged(renderSnap);   // 兜底：保证扬声器不受影响
                RefreshMicInfo();
                _owner.Log("已把 CABLE Output 设为系统默认麦克风（含默认通信设备）");
                MessageBox.Show(this,
                    "设置完成。\n\n现在去 QQ / 语音输入软件里试一下，应该能收到声音了。\n\n" +
                    "如果某个软件还不行，说明它自己单独存过麦克风选择，\n只能进那个软件的语音设置里手动改成 CABLE Output。",
                    "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                _owner.Log("设置默认麦克风失败：" + ex.Message);
                MessageBox.Show(this,
                    "自动设置失败：" + ex.Message + "\n\n可以手动改：" +
                    "Win+R → mmsys.cpl →「录制」→ 右键 CABLE Output → 同时选「设为默认设备」和「设为默认通信设备」。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void RestorePrevMic()
        {
            try
            {
                bool any = false;
                if (!string.IsNullOrEmpty(_cfg.PrevConsoleId))
                {
                    AudioDefault.SetDefault(_cfg.PrevConsoleId, Role.Console);
                    any = true;
                }
                if (!string.IsNullOrEmpty(_cfg.PrevCommId))
                {
                    AudioDefault.SetDefault(_cfg.PrevCommId, Role.Communications);
                    any = true;
                }
                RefreshMicInfo();
                _owner.Log(any ? "已恢复原来的默认麦克风" : "没有记录原来的设备");
                MessageBox.Show(this,
                    any ? "已恢复原来的默认麦克风。" : "没有记录到原来用的是哪个麦克风，请到声音设置里手动改。",
                    "恢复", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "恢复失败：" + ex.Message, "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ==================== 声卡自检 ====================

        private void RunSelfTest()
        {
            string devId = FindCableCaptureId();
            if (devId == null)
            {
                MessageBox.Show(this, "系统里没有找到 CABLE Output。\n\n虚拟声卡可能没装好。",
                    "声卡自检", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _btnSelfTest.Enabled = false;
            _btnSelfTest.Text = "录音中…请说话";
            _lblTest.Text = "正在录 3 秒，请现在对着手机说话";
            _lblTest.ForeColor = UiTheme.Text;
            _owner.Log("声卡自检：开始录 3 秒 CABLE Output");

            Task.Run(() =>
            {
                float peak = 0f;
                string err = null;
                try { peak = AudioOutput.CapturePeak(devId, 3000); }
                catch (Exception ex) { err = ex.Message; }

                if (!IsHandleCreated || IsDisposed) return;
                BeginInvoke(new Action(() =>
                {
                    _btnSelfTest.Enabled = true;
                    _btnSelfTest.Text = "声卡自检（录 3 秒）";
                    _lastPeak = peak;

                    if (err != null)
                    {
                        _lblTest.Text = "自检失败：" + err;
                        _lblTest.ForeColor = UiTheme.Red;
                        _owner.Log("声卡自检失败：" + err);
                        return;
                    }

                    int pct = (int)(peak * 100);
                    _owner.Log("声卡自检结果：虚拟声卡峰值 " + pct + "%");

                    if (peak > 0.02f)
                    {
                        _lblTest.Text = "✓ 虚拟声卡里有声音（峰值 " + pct + "%）→ 手机的声音已经到电脑了";
                        _lblTest.ForeColor = UiTheme.Green;
                        MessageBox.Show(this,
                            "✓ 虚拟声卡里检测到声音，峰值 " + pct + "%。\n\n" +
                            "说明手机的声音已经完整进到 CABLE 里了。\n" +
                            "如果别的软件还是收不到，就是它的麦克风没选成 CABLE Output。",
                            "声卡自检", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        _lblTest.Text = "✗ 虚拟声卡里几乎没有声音（峰值 " + pct + "%）→ 检查手机是否在录音";
                        _lblTest.ForeColor = UiTheme.Red;
                        MessageBox.Show(this,
                            "✗ 虚拟声卡里没有检测到声音（峰值 " + pct + "%）。\n\n" +
                            "请检查：\n1. 自检时手机上是不是正在录音（横幅要是红的）\n" +
                            "2. 上面的「输出设备」是不是 CABLE Input\n3. 手机麦克风有没有被静音",
                            "声卡自检", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }));
            });
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

        private void RefreshLog()
        {
            _txtLog.Text = string.Join(Environment.NewLine, _owner.GetLogLines());
            _txtLog.SelectionStart = _txtLog.TextLength;
            _txtLog.ScrollToCaret();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _owner.ReloadAfterSettings();
            base.OnFormClosed(e);
        }
    }
}
