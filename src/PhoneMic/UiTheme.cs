using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PhoneMic
{
    /// <summary>统一的外观：颜色、字体、圆角卡片、扁平按钮。两个窗口共用。</summary>
    internal static class UiTheme
    {
        public static readonly Color Bg = Color.FromArgb(0x12, 0x16, 0x1A);
        public static readonly Color Card = Color.FromArgb(0x1B, 0x22, 0x2A);
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
        public static Button Button(string text, int x, int y, int w, int h)
        {
            var b = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
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
        public static Panel Card(Control parent, int x, int y, int w, int h, string title)
        {
            var p = new Panel
            {
                Location = new Point(x, y),
                Size = new Size(w, h),
                BackColor = Card
            };
            ApplyRound(p, 16);

            p.Controls.Add(new Label
            {
                Text = title,
                ForeColor = Sub,
                Font = Font(10f),
                AutoSize = true,
                Location = new Point(20, 16)
            });
            parent.Controls.Add(p);
            return p;
        }

        public static Label Label(string text, int x, int y, int w, int h, float size, Color color)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                Font = Font(size),
                ForeColor = color,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft
            };
        }
    }
}
