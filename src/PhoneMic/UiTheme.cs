using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PhoneMic
{
    /// <summary>统一的外观：颜色、字体、圆角卡片、扁平按钮。两个窗口共用。</summary>
    internal static class UiTheme
    {
        // ===== DPI 缩放 =====
        //
        // 界面全部用代码搭，坐标是「设计单位」（按 96 DPI 想的）。
        // 但字体是按磅值渲染的 —— 在 150% 缩放下 10pt 的物理像素是 96 DPI 的 1.5 倍。
        // 如果坐标不跟着放大，文字就会比它的框大 1.5 倍，挤在一起甚至被裁掉。
        // 所以所有坐标都过一遍 S()，按当前 DPI 换算成实际像素。
        private static float _scale = 1f;

        /// <summary>在窗体构造时调用一次，取得当前 DPI 缩放比例</summary>
        public static void Init(Control c)
        {
            float dpi = 96f;
            try { using (var g = c.CreateGraphics()) dpi = g.DpiX; }
            catch { }
            if (dpi < 72f || dpi > 480f) dpi = 96f;
            _scale = dpi / 96f;
        }

        public static int S(int v) { return (int)Math.Round(v * _scale); }

        // ===== 深色标题栏 =====
        //
        // 不做这一步，Win11 会按系统「强调色」给标题栏上色，两个窗口还可能不一样
        // （主窗口深色、设置窗口橙色）。统一刷成和界面同色，看起来才是一套东西。
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        public static void DarkTitle(Form f)
        {
            try
            {
                IntPtr h = f.Handle;                 // 强制建句柄
                int on = 1;
                // 20 是 Win10 1903+ 的 DWMWA_USE_IMMERSIVE_DARK_MODE，更老的是 19
                if (DwmSetWindowAttribute(h, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(h, 19, ref on, sizeof(int));

                // Win11：直接把标题栏刷成界面底色（失败就算了，不影响使用）
                int cap = ToColorRef(Bg);
                DwmSetWindowAttribute(h, 35, ref cap, sizeof(int));   // DWMWA_CAPTION_COLOR
                int txt = ToColorRef(Text);
                DwmSetWindowAttribute(h, 36, ref txt, sizeof(int));   // DWMWA_TEXT_COLOR
            }
            catch { }
        }

        private static int ToColorRef(Color c)
        {
            return c.R | (c.G << 8) | (c.B << 16);     // COLORREF: 0x00BBGGRR
        }

        public static float S(float v) { return v * _scale; }

        public static float Scale { get { return _scale; } }

        public static readonly Color Bg = Color.FromArgb(0x12, 0x16, 0x1A);
        public static readonly Color CardBg = Color.FromArgb(0x1B, 0x22, 0x2A);
        public static readonly Color Field = Color.FromArgb(0x0F, 0x14, 0x19);
        public static readonly Color Text = Color.FromArgb(0xE6, 0xED, 0xF3);
        public static readonly Color Sub = Color.FromArgb(0x8B, 0x98, 0xA5);
        public static readonly Color Line = Color.FromArgb(0x2C, 0x35, 0x40);
        public static readonly Color Blue = Color.FromArgb(0x3F, 0xA7, 0xFF);
        public static readonly Color Green = Color.FromArgb(0x3F, 0xB9, 0x50);
        public static readonly Color Red = Color.FromArgb(0xF8, 0x51, 0x49);
        public static readonly Color Amber = Color.FromArgb(0xD2, 0x99, 0x22);

        public static Font Font(float size) { return Font(size, FontStyle.Regular); }

        public static Font Font(float size, FontStyle style)
        {
            try { return new System.Drawing.Font("Microsoft YaHei UI", size, style); }
            catch { return new System.Drawing.Font(FontFamily.GenericSansSerif, size, style); }
        }

        public static GraphicsPath RoundPath(Rectangle r, int radius)
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

        public static void ApplyRound(Control c, int radius)
        {
            using (var path = RoundPath(new Rectangle(0, 0, c.Width, c.Height), radius))
            {
                var old = c.Region;
                c.Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        /// <summary>扁平样式的按钮（深色、细边）</summary>
        public static Button MakeButton(string text, int x, int y, int w, int h)
        {
            var b = new Button
            {
                Text = text,
                Location = new Point(S(x), S(y)),
                Size = new Size(S(w), S(h)),
                FlatStyle = FlatStyle.Flat,
                BackColor = Field,
                ForeColor = Text,
                Font = Font(10f),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderColor = Line;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0x25, 0x2F, 0x3A);
            return b;
        }

        /// <summary>圆角深色卡片，左上角带一个说明小标题</summary>
        public static Panel MakeCard(Control parent, int x, int y, int w, int h, string title)
        {
            var p = new Panel
            {
                Location = new Point(S(x), S(y)),
                Size = new Size(S(w), S(h)),
                BackColor = CardBg
            };
            ApplyRound(p, S(16));

            p.Controls.Add(new Label
            {
                Text = title,
                ForeColor = Sub,
                Font = Font(10f),
                AutoSize = true,
                Location = new Point(S(20), S(16))
            });
            parent.Controls.Add(p);
            return p;
        }

        public static Label MakeLabel(string text, int x, int y, int w, int h, float size, Color color)
        {
            return new Label
            {
                Text = text,
                Location = new Point(S(x), S(y)),
                Size = new Size(S(w), S(h)),
                Font = Font(size),
                ForeColor = color,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft
            };
        }
    }
}
