using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PhoneMic
{
    /// <summary>
    /// 实时电平波形。
    ///
    /// 每 50ms 推一个「这一小段的峰值音量」进来，控件把最近 90 段画成一排柱状图，
    /// 右边是最新的。这样不用真的听，也能一眼看出「声音到底有没有传到电脑」。
    /// </summary>
    internal sealed class Waveform : Control
    {
        private const int BarCount = 90;

        private static readonly Color CBg = Color.FromArgb(0x15, 0x1C, 0x23);
        private static readonly Color CGrid = Color.FromArgb(0x22, 0x2A, 0x33);
        private static readonly Color CGreen = Color.FromArgb(0x3F, 0xB9, 0x50);
        private static readonly Color CAmber = Color.FromArgb(0xD2, 0x99, 0x22);
        private static readonly Color CRed = Color.FromArgb(0xF8, 0x51, 0x49);

        private readonly float[] _bars = new float[BarCount];
        private int _pos;

        public Waveform()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw, true);
            BackColor = CBg;
        }

        /// <summary>推入一个 0~1 的音量值</summary>
        public void Push(float level)
        {
            if (level < 0f) level = 0f;
            if (level > 1f) level = 1f;
            _bars[_pos] = level;
            _pos = (_pos + 1) % BarCount;
            Invalidate();
        }

        public void Reset()
        {
            Array.Clear(_bars, 0, BarCount);
            _pos = 0;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            g.Clear(CBg);

            int w = Width, h = Height;
            if (w <= 4 || h <= 4) return;

            // 淡网格线（25% / 50% / 75%），让人能估出音量大小
            using (var pen = new Pen(CGrid))
            {
                for (int i = 1; i <= 3; i++)
                {
                    int y = h * i / 4;
                    g.DrawLine(pen, 0, y, w, y);
                }
            }

            float slot = (float)w / BarCount;
            float barW = Math.Max(1f, slot - 2f);
            int mid = h / 2;

            using (var green = new SolidBrush(CGreen))
            using (var amber = new SolidBrush(CAmber))
            using (var red = new SolidBrush(CRed))
            {
                for (int i = 0; i < BarCount; i++)
                {
                    float v = _bars[(_pos + i) % BarCount];   // 左侧最旧、右侧最新
                    if (v <= 0.004f) continue;

                    float half = Math.Max(1f, v * (mid - 2));
                    float x = i * slot + (slot - barW) / 2f;

                    var brush = v > 0.80f ? red : (v > 0.45f ? amber : green);
                    // 对称画在中线上下，像示波器
                    g.FillRectangle(brush, x, mid - half, barW, half * 2f);
                }
            }

            // 中线
            using (var pen = new Pen(Color.FromArgb(0x39, 0x43, 0x4F)))
                g.DrawLine(pen, 0, mid, w, mid);
        }
    }
}
