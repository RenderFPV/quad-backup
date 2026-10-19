// Quad Backup UI: the mascot, the new-drone window, pop-up notifications and the tray menu.
// Everything is hand-painted with GDI+ so it doesn't look like a 2003 settings dialog.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QuadBackup
{
    static class Theme
    {
        public static readonly Color Bg = Hex(0x15172B);
        public static readonly Color Panel = Hex(0x1F2244);
        public static readonly Color PanelHi = Hex(0x2A2E5A);
        public static readonly Color Line = Hex(0x3A3F78);
        public static readonly Color Ink = Hex(0x0E1024);
        public static readonly Color Text = Hex(0xF5F2FF);
        public static readonly Color Muted = Hex(0xA0A5D0);
        public static readonly Color Amber = Hex(0xFFB627);
        public static readonly Color Pink = Hex(0xFF5C8A);
        public static readonly Color Coral = Hex(0xFF8A5C);
        public static readonly Color Mint = Hex(0x3DDC97);
        public static readonly Color Sky = Hex(0x5AC8FA);
        public static readonly Color Violet = Hex(0x8B7CFF);

        public static Color Hex(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }
        public static Color Alpha(Color c, int a) { return Color.FromArgb(Math.Max(0, Math.Min(255, a)), c); }

        public static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static Font Heavy(float pt)
        {
            var f = new Font("Segoe UI Black", pt);
            if (f.Name == "Segoe UI Black") return f;
            f.Dispose();
            return new Font("Segoe UI", pt, FontStyle.Bold);
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void DrawText(Graphics g, string text, Font font, RectangleF r, Color color, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, text, font, Rectangle.Round(r), color, flags | TextFormatFlags.NoPadding);
        }

        public static float Measure(Graphics g, string text, Font font)
        {
            return TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding).Width;
        }
    }

    static class Native
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        // Windows 11 rounded corners + a thin coloured border on a borderless window. Harmless no-op on Windows 10.
        public static void RoundCorners(IntPtr hwnd, Color border)
        {
            try
            {
                int round = 2;
                DwmSetWindowAttribute(hwnd, 33, ref round, 4);
                int colorRef = border.R | (border.G << 8) | (border.B << 16);
                DwmSetWindowAttribute(hwnd, 34, ref colorRef, 4);
            }
            catch { }
        }

        public static void DragWindow(Form form)
        {
            ReleaseCapture();
            SendMessage(form.Handle, 0xA1, (IntPtr)2, IntPtr.Zero); // WM_NCLBUTTONDOWN on the caption
        }
    }

    // ------------------------------------------------------------------------------------------------ the mascot

    public static class QuadArt
    {
        static readonly PointF[] Motors = { new PointF(-32, -32), new PointF(32, -32), new PointF(32, 32), new PointF(-32, 32) };

        // A top-down quad with a face. Drawn in a 100x100 box centred on (cx, cy) and scaled to `size` pixels.
        // blink: 0 = eyes open, 1 = shut. dizzy swaps the eyes for little spirals of shame.
        // Three twinkling four-point stars around a circle of radius r centred on c.
        public static void Sparkles(Graphics g, PointF c, float r, float t, float s)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float[] angles = { -50, 35, 150 };
            for (int i = 0; i < angles.Length; i++)
            {
                double a = angles[i] * Math.PI / 180;
                float twinkle = (float)(0.55 + 0.45 * Math.Sin(t * 4 + i * 2.1));
                float x = c.X + (float)Math.Cos(a) * (r + 2 * s), y = c.Y + (float)Math.Sin(a) * (r + 2 * s);
                float z = (3.2f + i * 0.8f) * s * twinkle;
                using (var b = new SolidBrush(Theme.Alpha(Theme.Hex(0xFFE58A), (int)(255 * twinkle))))
                {
                    g.FillEllipse(b, x - z * 2, y - z * 0.4f, z * 4, z * 0.8f);
                    g.FillEllipse(b, x - z * 0.4f, y - z * 2, z * 0.8f, z * 4);
                }
            }
        }

        public static void Draw(Graphics g, float cx, float cy, float size, float propAngle, float tilt, float blink,
                                bool face, Color body, bool dizzy = false)
        {
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(cx, cy);
            g.RotateTransform(tilt);
            g.ScaleTransform(size / 100f, size / 100f);

            using (var edge = new Pen(Theme.Ink, 13) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var arm = new Pen(Theme.Hex(0x565CA8), 7.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(edge, -32, -32, 32, 32);
                g.DrawLine(edge, 32, -32, -32, 32);
                g.DrawLine(arm, -32, -32, 32, 32);
                g.DrawLine(arm, 32, -32, -32, 32);
            }

            using (var disc = new SolidBrush(Theme.Alpha(Theme.Amber, 60)))
            using (var blade = new SolidBrush(Theme.Amber))
            using (var hub = new SolidBrush(Theme.Ink))
            using (var cap = new SolidBrush(Theme.Hex(0xFFE08A)))
            {
                for (int i = 0; i < 4; i++)
                {
                    var m = Motors[i];
                    g.FillEllipse(disc, m.X - 20, m.Y - 20, 40, 40);
                    var s = g.Save();
                    g.TranslateTransform(m.X, m.Y);
                    g.RotateTransform((i % 2 == 0 ? 1 : -1) * propAngle + i * 45);
                    g.FillEllipse(blade, -19, -3.6f, 38, 7.2f);
                    g.Restore(s);
                    g.FillEllipse(hub, m.X - 5.5f, m.Y - 5.5f, 11, 11);
                    g.FillEllipse(cap, m.X - 2, m.Y - 2, 4, 4);
                }
            }

            // antenna with a bobble on top
            using (var wire = new Pen(Theme.Muted, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var bobble = new SolidBrush(Theme.Pink))
            {
                g.DrawBezier(wire, 7, -15, 9, -24, 15, -27, 17, -35);
                g.FillEllipse(bobble, 13.5f, -39, 7, 7);
            }

            var bodyRect = new RectangleF(-19, -16, 38, 32);
            using (var path = Theme.Round(bodyRect, 11))
            using (var fill = new LinearGradientBrush(bodyRect, Lighten(body, 0.25f), body, 90f))
            using (var outline = new Pen(Theme.Ink, 2.6f))
            {
                g.FillPath(fill, path);
                g.DrawPath(outline, path);
            }

            if (face)
            {
                using (var ink = new SolidBrush(Theme.Ink))
                using (var white = new SolidBrush(Color.White))
                using (var line = new Pen(Theme.Ink, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                using (var blush = new SolidBrush(Theme.Alpha(Theme.Pink, 150)))
                {
                    if (dizzy)
                    {
                        foreach (var x in new[] { -8f, 8f })
                        {
                            g.DrawArc(line, x - 4.5f, -7.5f, 9, 9, 0, 300);
                            g.DrawArc(line, x - 2, -5, 4, 4, 90, 270);
                        }
                        g.DrawBezier(line, -6, 7, -3, 4, 3, 10, 6, 7); // wobbly mouth
                    }
                    else
                    {
                        float eyeH = 11 * (1 - 0.88f * blink);
                        foreach (var x in new[] { -8f, 8f })
                        {
                            g.FillEllipse(white, x - 4.5f, -3 - eyeH / 2, 9, eyeH);
                            if (blink < 0.5f)
                            {
                                g.FillEllipse(ink, x - 2.2f, -3.4f, 5, 5.4f);
                                g.FillEllipse(white, x - 0.6f, -2.8f, 1.8f, 1.8f);
                            }
                        }
                        g.DrawArc(line, -5.5f, 1.5f, 11, 7, 20, 140);
                    }
                    g.FillEllipse(blush, -17, 4, 6, 3.6f);
                    g.FillEllipse(blush, 11, 4, 6, 3.6f);
                }
            }
            g.Restore(state);
        }

        static Color Lighten(Color c, float amount)
        {
            return Color.FromArgb(c.A, c.R + (int)((255 - c.R) * amount), c.G + (int)((255 - c.G) * amount), c.B + (int)((255 - c.B) * amount));
        }
    }

    public static class Art
    {
        public static Icon MakeIcon(int size)
        {
            using (var bmp = Draw(size)) return Icon.FromHandle(bmp.GetHicon());
        }

        // The original logo: four amber props on a grey X with a blue body. Used for the tray, taskbar and exe icon.
        public static Icon MakeClassicIcon(int size)
        {
            using (var bmp = DrawClassic(size)) return Icon.FromHandle(bmp.GetHicon());
        }

        public static Bitmap DrawClassic(int size)
        {
            var bmp = new Bitmap(size, size);
            float s = size / 32f;
            using (var g = Graphics.FromImage(bmp))
            using (var arm = new Pen(Color.FromArgb(225, 225, 225), 3.2f * s))
            using (var armEdge = new Pen(Color.FromArgb(40, 40, 40), 5.4f * s))
            using (var prop = new SolidBrush(Color.FromArgb(255, 176, 0)))
            using (var propEdge = new Pen(Color.FromArgb(40, 40, 40), 1.4f * s))
            using (var body = new SolidBrush(Color.FromArgb(30, 136, 229)))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float a = 7.5f * s, z = 24.5f * s, r = 5.4f * s;
                g.DrawLine(armEdge, a, a, z, z);
                g.DrawLine(armEdge, z, a, a, z);
                g.DrawLine(arm, a, a, z, z);
                g.DrawLine(arm, z, a, a, z);
                foreach (var c in new[] { new PointF(a, a), new PointF(z, a), new PointF(a, z), new PointF(z, z) })
                {
                    g.FillEllipse(prop, c.X - r, c.Y - r, 2 * r, 2 * r);
                    g.DrawEllipse(propEdge, c.X - r, c.Y - r, 2 * r, 2 * r);
                }
                var bodyRect = new RectangleF(11.5f * s, 11.5f * s, 9 * s, 9 * s);
                g.FillRectangle(body, bodyRect);
                g.DrawRectangle(propEdge, bodyRect.X, bodyRect.Y, bodyRect.Width, bodyRect.Height);
            }
            return bmp;
        }

        public static Bitmap Draw(int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                QuadArt.Draw(g, size / 2f, size / 2f + size * 0.02f, size * 1.12f, 20, 0, 0, size >= 40, Theme.Sky);
            }
            return bmp;
        }
    }


    // ------------------------------------------------------------------------------------------------ scenery

    class SkyScene
    {
        struct Star { public float X, Y, Size, Speed, Phase; }
        readonly Star[] stars;

        public SkyScene(int seed, int count)
        {
            var r = new Random(seed);
            stars = new Star[count];
            for (int i = 0; i < count; i++)
                stars[i] = new Star
                {
                    X = (float)r.NextDouble(),
                    Y = (float)r.NextDouble() * 0.85f,
                    Size = 1f + (float)r.NextDouble() * 2.4f,
                    Speed = 0.8f + (float)r.NextDouble() * 2.2f,
                    Phase = (float)(r.NextDouble() * Math.PI * 2)
                };
        }

        public void Paint(Graphics g, RectangleF r, float t, float s)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), Theme.Hex(0x40308A), Theme.Bg, 90f))
                g.FillRectangle(bg, r);

            // soft pink glow behind the mascot
            using (var glow = new GraphicsPath())
            {
                glow.AddEllipse(r.X + r.Width / 2 - 150 * s, r.Y - 10 * s, 300 * s, 200 * s);
                using (var brush = new PathGradientBrush(glow) { CenterColor = Theme.Alpha(Theme.Pink, 75), SurroundColors = new[] { Color.FromArgb(0, Theme.Pink) } })
                    g.FillPath(brush, glow);
            }

            for (int i = 0; i < 3; i++)
            {
                float travel = (i * 0.41f + t * 0.014f * (1 + i * 0.35f)) % 1.4f - 0.2f;
                Cloud(g, r.X + travel * r.Width, r.Y + r.Height * (0.5f + i * 0.14f), (60 + i * 18) * s, 16 + i * 7);
            }

            foreach (var st in stars)
            {
                int a = (int)(130 + 120 * Math.Sin(t * st.Speed + st.Phase));
                float x = r.X + st.X * r.Width, y = r.Y + st.Y * r.Height, z = st.Size * s;
                using (var b = new SolidBrush(Theme.Alpha(Color.White, a)))
                {
                    if (st.Size > 2.8f)
                    {
                        // a twinkly four-point sparkle
                        g.FillEllipse(b, x - z * 2.2f, y - z * 0.35f, z * 4.4f, z * 0.7f);
                        g.FillEllipse(b, x - z * 0.35f, y - z * 2.2f, z * 0.7f, z * 4.4f);
                    }
                    else g.FillEllipse(b, x - z / 2, y - z / 2, z, z);
                }
            }
        }

        static void Cloud(Graphics g, float cx, float cy, float w, int alpha)
        {
            using (var p = new GraphicsPath { FillMode = FillMode.Winding })
            using (var b = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)))
            {
                p.AddEllipse(cx - w / 2, cy - w * 0.16f, w, w * 0.32f);
                p.AddEllipse(cx - w * 0.3f, cy - w * 0.3f, w * 0.4f, w * 0.4f);
                p.AddEllipse(cx - w * 0.02f, cy - w * 0.24f, w * 0.32f, w * 0.32f);
                g.FillPath(b, p);
            }
        }
    }

    class Confetti
    {
        class Bit { public float X, Y, Vx, Vy, Rot, Spin, W, H; public Color C; }
        static readonly Color[] Colors = { Theme.Pink, Theme.Amber, Theme.Mint, Theme.Sky, Theme.Violet, Color.White };
        readonly List<Bit> bits = new List<Bit>();
        readonly Random rnd = new Random();

        public bool Active { get { return bits.Count > 0; } }

        public void Burst(float x, float y, int count, float s)
        {
            for (int i = 0; i < count; i++)
            {
                double angle = rnd.NextDouble() * Math.PI * 2;
                float speed = (3 + (float)rnd.NextDouble() * 8) * s;
                bits.Add(new Bit
                {
                    X = x, Y = y,
                    Vx = (float)Math.Cos(angle) * speed,
                    Vy = (float)Math.Sin(angle) * speed - 7 * s,
                    Rot = (float)rnd.NextDouble() * 360,
                    Spin = ((float)rnd.NextDouble() - 0.5f) * 36,
                    W = (5 + (float)rnd.NextDouble() * 6) * s,
                    H = (3 + (float)rnd.NextDouble() * 3) * s,
                    C = Colors[rnd.Next(Colors.Length)]
                });
            }
        }

        public void Step(float s, float floor)
        {
            foreach (var b in bits)
            {
                b.Vy += 0.38f * s;
                b.Vx *= 0.985f;
                b.X += b.Vx;
                b.Y += b.Vy;
                b.Rot += b.Spin;
            }
            bits.RemoveAll(b => b.Y > floor + 30);
        }

        public void Paint(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var b in bits)
            {
                var st = g.Save();
                g.TranslateTransform(b.X, b.Y);
                g.RotateTransform(b.Rot);
                using (var brush = new SolidBrush(b.C)) g.FillRectangle(brush, -b.W / 2, -b.H / 2, b.W, b.H);
                g.Restore(st);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------ controls

    class PillButton : Control, IButtonControl
    {
        public bool Primary;
        bool hover, down, isDefault, special;
        Timer shine;

        public DialogResult DialogResult { get; set; }

        // Gold with a shine sweeping across it, for the special backup.
        public bool Special
        {
            get { return special; }
            set
            {
                special = value;
                if (value && shine == null)
                {
                    shine = new Timer { Interval = 40 };
                    shine.Tick += (s, e) => Invalidate();
                    shine.Start();
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && shine != null) shine.Dispose();
            base.Dispose(disposing);
        }

        public PillButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
            BackColor = Theme.Bg;
        }

        public void NotifyDefault(bool value) { isDefault = value; Invalidate(); }
        public void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) PerformClick();
            base.OnKeyUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float lift = down ? 1 : (hover ? -1 : 0);
            var r = new RectangleF(1, 2 + lift, Width - 2, Height - 4);
            using (var path = Theme.Round(r, r.Height / 2))
            {
                if (special)
                {
                    using (var under = Theme.Round(new RectangleF(1, 4, Width - 2, Height - 5), r.Height / 2))
                    using (var shade = new SolidBrush(Theme.Hex(0xB06A00)))
                        g.FillPath(shade, under);
                    var a = hover ? Theme.Hex(0xFFE58A) : Theme.Hex(0xFFD45C);
                    var b = hover ? Theme.Hex(0xFFBE55) : Theme.Hex(0xFFA51F);
                    using (var fill = new LinearGradientBrush(r, a, b, 0f)) g.FillPath(fill, path);
                    // a diagonal glint that sweeps across every couple of seconds
                    float sweep = (Environment.TickCount % 2400) / 2400f;
                    float gx = r.X - 40 + sweep * (r.Width + 120);
                    var state = g.Save();
                    g.SetClip(path);
                    using (var glint = new SolidBrush(Theme.Alpha(Color.White, 110)))
                        g.FillPolygon(glint, new[] { new PointF(gx, r.Bottom), new PointF(gx + 14, r.Bottom), new PointF(gx + 34, r.Y), new PointF(gx + 20, r.Y) });
                    g.Restore(state);
                }
                else if (Primary)
                {
                    // chunky drop "shadow" for a toy-like feel
                    using (var under = Theme.Round(new RectangleF(1, 4, Width - 2, Height - 5), r.Height / 2))
                    using (var shade = new SolidBrush(Theme.Hex(0xA8325A)))
                        g.FillPath(shade, under);
                    var a = hover ? Theme.Hex(0xFF7AA0) : Theme.Pink;
                    var b = hover ? Theme.Hex(0xFFA478) : Theme.Coral;
                    using (var fill = new LinearGradientBrush(r, a, b, 0f)) g.FillPath(fill, path);
                }
                else
                {
                    using (var fill = new SolidBrush(hover ? Theme.PanelHi : Theme.Panel)) g.FillPath(fill, path);
                    using (var pen = new Pen(hover ? Theme.Violet : Theme.Line, 1.5f)) g.DrawPath(pen, path);
                }
                if (Focused && ShowFocusCues)
                    using (var ring = new Pen(Theme.Alpha(Color.White, 140), 1.5f) { DashStyle = DashStyle.Dot })
                    using (var inner = Theme.Round(RectangleF.Inflate(r, -3, -3), (r.Height - 6) / 2))
                        g.DrawPath(ring, inner);
            }
            var color = !Enabled ? Theme.Muted : (special ? Theme.Ink : (Primary ? Color.White : Theme.Text));
            Theme.DrawText(g, Text, Font, r, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    // ------------------------------------------------------------------------------------------------ new drone window

    public class NewDroneForm : Form
    {
        static readonly string[] Hellos = { "A wild quad appears!", "New quad on the bench!", "Ooh, a new one!", "Well hello there, flyer!" };
        const float W = 520;

        readonly float S;
        readonly FcBackup info;
        readonly string port, note;
        readonly TextBox nameBox;
        readonly PillButton homeButton, existingButton;
        readonly Timer timer;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly SkyScene sky = new SkyScene(7, 30);
        readonly Confetti confetti = new Confetti();
        readonly Font headFont, subFont, chipFont, labelFont, inputFont, smallFont, monoFont, buttonFont;
        readonly RectangleF headerRect, closeRect, inputRect, noteRect;
        readonly float headY, subY, chipsY, labelY, locationY, previewY, linksY;

        string headline, subline, location, hover;
        NewDroneChoice result, pending;
        float propAngle;
        long celebrateAt = -1, shakeAt = -1;
        RectangleF changeRect, notNowRect, neverRect;

        public static NewDroneChoice Ask(FcBackup b, string port, string suggestedName, string location, string note, DroneMood mood)
        {
            using (var form = new NewDroneForm(b, port, suggestedName, location, note, mood))
            {
                form.ShowDialog();
                return form.result;
            }
        }

        public NewDroneForm(FcBackup b, string port, string suggestedName, string location, string note, DroneMood mood)
        {
            info = b;
            this.port = port;
            this.note = note;
            this.location = location;
            using (var g = CreateGraphics()) S = g.DpiX / 96f;

            Text = "Quad Backup - new drone";
            Icon = Art.MakeClassicIcon(32);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            TopMost = true;
            KeyPreview = true;
            BackColor = Theme.Bg;
            DoubleBuffered = true;

            headFont = Theme.Heavy(19);
            subFont = new Font("Segoe UI", 10.5f);
            chipFont = new Font("Segoe UI Semibold", 8.75f);
            labelFont = new Font("Segoe UI", 8.25f, FontStyle.Bold);
            inputFont = new Font("Segoe UI Semibold", 12.5f);
            smallFont = new Font("Segoe UI", 9f);
            monoFont = new Font("Consolas", 9f);
            buttonFont = new Font("Segoe UI", 10f, FontStyle.Bold);

            var craft = string.IsNullOrEmpty(b.CraftName) ? null : b.CraftName;
            switch (mood)
            {
                case DroneMood.NewFlightController:
                    headline = "Is that you, " + suggestedName + "?";
                    subline = "Same name, new flight controller. Keep the name to add to its history.";
                    break;
                case DroneMood.Returning:
                    headline = "Welcome back, " + suggestedName + "!";
                    subline = "You forgot me, but I kept my folder. Keep the name to pick up where we left off.";
                    break;
                case DroneMood.MissingFolder:
                    headline = "Hey, where'd my folder go?";
                    subline = suggestedName + "'s backup folder vanished. Let's give it a new one.";
                    break;
                default:
                    headline = Hellos[new Random().Next(Hellos.Length)];
                    subline = craft != null ? "Meet " + craft + ". Let's give it a home for its backups."
                                            : "No craft name set, so let's call it something nice.";
                    break;
            }

            // layout, in 96-dpi units scaled by S
            headerRect = R(0, 0, W, 178);
            closeRect = R(W - 42, 12, 28, 28);
            headY = 188;
            subY = 228;
            chipsY = 262;
            float y = 306;
            if (!string.IsNullOrEmpty(note))
            {
                var h = TextRenderer.MeasureText(note, smallFont, new Size((int)((W - 56 - 30) * S), 0), TextFormatFlags.WordBreak).Height / S;
                noteRect = R(28, y, W - 56, h + 20);
                y += h + 20 + 16;
            }
            labelY = y;
            inputRect = R(28, y + 20, W - 56, 44);
            locationY = y + 74;
            previewY = y + 98;
            float buttonsY = y + 130;
            linksY = buttonsY + 44 + 18;
            ClientSize = new Size((int)(W * S), (int)((linksY + 22 + 18) * S));

            nameBox = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Panel,
                ForeColor = Theme.Text,
                Font = inputFont,
                Text = suggestedName,
                Width = (int)(inputRect.Width - 32 * S)
            };
            PlaceNameBox(0);
            nameBox.TextChanged += (s, e) => Invalidate();
            nameBox.Enter += (s, e) => Invalidate();
            nameBox.Leave += (s, e) => Invalidate();

            homeButton = new PillButton { Primary = true, Text = "Give it a home!", Font = buttonFont };
            homeButton.Bounds = Rectangle.Round(R(W - 28 - 176, buttonsY, 176, 44));
            existingButton = new PillButton { Text = "Use existing folder…", Font = buttonFont };
            existingButton.Bounds = Rectangle.Round(R(W - 28 - 176 - 12 - 196, buttonsY, 196, 44));
            homeButton.Click += (s, e) => GiveItAHome();
            existingButton.Click += (s, e) => PickExisting();
            Controls.Add(nameBox);
            Controls.Add(existingButton);
            Controls.Add(homeButton);
            AcceptButton = homeButton;

            timer = new Timer { Interval = 16 };
            timer.Tick += (s, e) => Tick();
            timer.Start();
            Shown += (s, e) =>
            {
                Activate();
                nameBox.Focus();
                nameBox.SelectAll();
                SystemSounds.Asterisk.Play();
            };
            FormClosed += (s, e) => timer.Stop();
        }

        RectangleF R(float x, float y, float w, float h) { return new RectangleF(x * S, y * S, w * S, h * S); }

        void PlaceNameBox(float offset)
        {
            nameBox.Location = new Point((int)(inputRect.X + 16 * S + offset), (int)(inputRect.Y + (inputRect.Height - nameBox.Height) / 2));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle, Theme.Line);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Finish(null);
            base.OnKeyDown(e);
        }

        float Seconds { get { return clock.ElapsedMilliseconds / 1000f; } }

        void Tick()
        {
            propAngle += celebrateAt >= 0 ? 41 : 23;
            confetti.Step(S, ClientSize.Height);
            if (shakeAt >= 0)
            {
                float p = (clock.ElapsedMilliseconds - shakeAt) / 420f;
                if (p >= 1) { shakeAt = -1; PlaceNameBox(0); }
                else PlaceNameBox((float)Math.Sin(p * Math.PI * 7) * 9 * S * (1 - p));
            }
            if (celebrateAt >= 0 && clock.ElapsedMilliseconds - celebrateAt > 1250)
            {
                Finish(pending);
                return;
            }
            if (celebrateAt >= 0 || confetti.Active || shakeAt >= 0) Invalidate();
            else Invalidate(Rectangle.Ceiling(headerRect));
        }

        void Finish(NewDroneChoice choice)
        {
            result = choice;
            timer.Stop();
            Close();
        }

        void GiveItAHome()
        {
            var name = nameBox.Text.Trim();
            if (name.Length == 0)
            {
                shakeAt = clock.ElapsedMilliseconds;
                SystemSounds.Beep.Play();
                nameBox.Focus();
                return;
            }
            Celebrate(new NewDroneChoice { Folder = Path.Combine(location, BackupFiles.SafeName(name)) });
        }

        void PickExisting()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Pick the folder to save this drone's backups in", SelectedPath = location })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Celebrate(new NewDroneChoice { Folder = dlg.SelectedPath });
            }
        }

        void PickLocation()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Where should this drone's backup folder be created?", SelectedPath = location })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                location = dlg.SelectedPath;
                Invalidate();
            }
        }

        // The quad does a happy flip, confetti flies, then the window closes.
        void Celebrate(NewDroneChoice choice)
        {
            if (celebrateAt >= 0) return;
            pending = choice;
            celebrateAt = clock.ElapsedMilliseconds;
            headline = "Welcome home, " + Path.GetFileName(choice.Folder.TrimEnd('\\', '/')) + "!";
            subline = "First backup going in now. Every plug-in adds another.";
            homeButton.Enabled = existingButton.Enabled = false;
            nameBox.ReadOnly = true;
            confetti.Burst(W / 2 * S, 92 * S, 110, S);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            float t = Seconds;
            g.Clear(Theme.Bg);
            sky.Paint(g, headerRect, t, S);

            // mascot: hovers, sways and blinks; flips when celebrating
            float bob = (float)Math.Sin(t * 2.2) * 4;
            float tilt = (float)Math.Sin(t * 1.4) * 5;
            if (celebrateAt >= 0)
            {
                float p = Math.Min(1, (clock.ElapsedMilliseconds - celebrateAt) / 750f);
                tilt = (1 - (float)Math.Pow(1 - p, 3)) * 360;
                bob = -(float)Math.Sin(p * Math.PI) * 30;
            }
            float phase = (t + 1.8f) % 3.7f;
            float blink = phase < 0.14f ? (float)Math.Sin(phase / 0.14f * Math.PI) : 0;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float shadowW = (74 + bob * 1.2f) * S;
            using (var shadow = new SolidBrush(Theme.Alpha(Theme.Ink, 110)))
                g.FillEllipse(shadow, W / 2 * S - shadowW / 2, 158 * S, shadowW, 9 * S);
            QuadArt.Draw(g, W / 2 * S, (90 + bob) * S, 122 * S, propAngle, tilt, celebrateAt >= 0 ? 0 : blink, true, Theme.Sky);

            // close button
            using (var bubble = new SolidBrush(Theme.Alpha(Color.White, hover == "close" ? 50 : 20)))
                g.FillEllipse(bubble, closeRect);
            using (var x = new Pen(Theme.Alpha(Color.White, hover == "close" ? 255 : 190), 1.8f * S) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                var c = new PointF(closeRect.X + closeRect.Width / 2, closeRect.Y + closeRect.Height / 2);
                float k = 5 * S;
                g.DrawLine(x, c.X - k, c.Y - k, c.X + k, c.Y + k);
                g.DrawLine(x, c.X + k, c.Y - k, c.X - k, c.Y + k);
            }

            var center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            Theme.DrawText(g, headline, headFont, R(20, headY, W - 40, 38), Theme.Text, center);
            Theme.DrawText(g, subline, subFont, R(20, subY, W - 40, 24), Theme.Muted, center);
            PaintChips(g);

            if (!noteRect.IsEmpty)
            {
                using (var path = Theme.Round(noteRect, 10 * S))
                using (var fill = new SolidBrush(Theme.Alpha(Theme.Amber, 28)))
                using (var pen = new Pen(Theme.Alpha(Theme.Amber, 110), 1.2f * S))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(pen, path);
                }
                Theme.DrawText(g, note, smallFont, new RectangleF(noteRect.X + 15 * S, noteRect.Y + 10 * S, noteRect.Width - 30 * S, noteRect.Height - 20 * S),
                           Theme.Hex(0xFFD27A), TextFormatFlags.WordBreak);
            }

            Theme.DrawText(g, "FOLDER NAME", labelFont, R(30, labelY, 200, 16), Theme.Muted, TextFormatFlags.Left);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var box = inputRect;
            if (shakeAt >= 0) box.Offset(nameBox.Left - (inputRect.X + 16 * S), 0);
            using (var path = Theme.Round(box, 12 * S))
            using (var fill = new SolidBrush(Theme.Panel))
            using (var pen = new Pen(shakeAt >= 0 ? Theme.Pink : (nameBox.Focused ? Theme.Violet : Theme.Line), (nameBox.Focused ? 2f : 1.4f) * S))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }

            PaintLocation(g);

            var file = BackupFiles.SafeName(nameBox.Text.Trim()) + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt";
            var prefix = "saves as ";
            float pw = Theme.Measure(g, prefix, smallFont);
            Theme.DrawText(g, prefix, smallFont, R(30, previewY, 100, 18), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            Theme.DrawText(g, file, monoFont, new RectangleF(30 * S + pw, previewY * S, (W - 60) * S - pw, 18 * S), Theme.Mint,
                       TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            PaintLinks(g);
            confetti.Paint(g);
        }

        void PaintChips(Graphics g)
        {
            var chips = new List<KeyValuePair<Color, string>>
            {
                new KeyValuePair<Color, string>(Theme.Amber, info.Board ?? "unknown board"),
                new KeyValuePair<Color, string>(Theme.Mint, info.Firmware ?? info.Variant ?? "firmware?"),
                new KeyValuePair<Color, string>(Theme.Sky, port)
            };
            float gap = 8 * S, h = 26 * S;
            var widths = chips.Select(c => Theme.Measure(g, c.Value, chipFont) + 32 * S).ToList();
            float x = (W * S - widths.Sum() - gap * (chips.Count - 1)) / 2;
            for (int i = 0; i < chips.Count; i++)
            {
                var r = new RectangleF(x, chipsY * S, widths[i], h);
                using (var path = Theme.Round(r, h / 2))
                using (var fill = new SolidBrush(Theme.Panel))
                using (var pen = new Pen(Theme.Line, 1.2f * S))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(pen, path);
                }
                using (var dot = new SolidBrush(chips[i].Key))
                    g.FillEllipse(dot, r.X + 11 * S, r.Y + h / 2 - 3.5f * S, 7 * S, 7 * S);
                Theme.DrawText(g, chips[i].Value, chipFont, new RectangleF(r.X + 22 * S, r.Y, r.Width - 30 * S, h), Theme.Text,
                           TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                x += widths[i] + gap;
            }
        }

        void PaintLocation(Graphics g)
        {
            float y = locationY * S, h = 18 * S;
            // tiny folder icon
            using (var folder = new SolidBrush(Theme.Amber))
            {
                g.FillRectangle(folder, 30 * S, y + 4 * S, 6 * S, 3 * S);
                using (var body = Theme.Round(new RectangleF(30 * S, y + 6 * S, 15 * S, 10 * S), 2 * S)) g.FillPath(folder, body);
            }
            var link = "change";
            float lw = Theme.Measure(g, link, smallFont);
            changeRect = new RectangleF((W - 30) * S - lw, y, lw, h);
            var textRect = new RectangleF(52 * S, y, changeRect.X - 52 * S - 12 * S, h);
            Theme.DrawText(g, location, smallFont, textRect, Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis | TextFormatFlags.SingleLine);
            using (var f = new Font(smallFont, hover == "change" ? FontStyle.Underline : FontStyle.Regular))
                Theme.DrawText(g, link, f, changeRect, hover == "change" ? Theme.Text : Theme.Sky, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        void PaintLinks(Graphics g)
        {
            string a = "not now", sep = "   ·   ", b = "never back up this quad";
            float wa = Theme.Measure(g, a, smallFont), ws = Theme.Measure(g, sep, smallFont), wb = Theme.Measure(g, b, smallFont);
            float x = (W * S - wa - ws - wb) / 2, y = linksY * S, h = 20 * S;
            notNowRect = new RectangleF(x, y, wa, h);
            neverRect = new RectangleF(x + wa + ws, y, wb, h);
            DrawLink(g, a, notNowRect, hover == "notnow");
            Theme.DrawText(g, sep, smallFont, new RectangleF(x + wa, y, ws, h), Theme.Alpha(Theme.Muted, 160), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            DrawLink(g, b, neverRect, hover == "never");
        }

        void DrawLink(Graphics g, string text, RectangleF r, bool hot)
        {
            using (var f = new Font(smallFont, hot ? FontStyle.Underline : FontStyle.Regular))
                Theme.DrawText(g, text, f, r, hot ? Theme.Text : Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        string HitTest(Point p)
        {
            if (celebrateAt >= 0) return null;
            if (closeRect.Contains(p)) return "close";
            if (changeRect.Contains(p)) return "change";
            if (notNowRect.Contains(p)) return "notnow";
            if (neverRect.Contains(p)) return "never";
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var h = HitTest(e.Location);
            if (h != hover)
            {
                hover = h;
                Cursor = h != null ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hover != null) { hover = null; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && HitTest(e.Location) == null) Native.DragWindow(this);
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            switch (HitTest(e.Location))
            {
                case "close":
                case "notnow": Finish(null); break;
                case "never": Finish(new NewDroneChoice { Never = true }); break;
                case "change": PickLocation(); break;
            }
            base.OnMouseUp(e);
        }

        // used by the self-test to grab frames mid-animation
        internal void Advance(int frames) { for (int i = 0; i < frames; i++) Tick(); }
        internal void StartCelebrationForPreview() { Celebrate(new NewDroneChoice { Folder = Path.Combine(location, BackupFiles.SafeName(nameBox.Text)) }); }
    }

    // ------------------------------------------------------------------------------------------------ pop-up notifications

    public class Toast : Form
    {
        const int LifeMs = 7000;
        static readonly List<Toast> Open = new List<Toast>();

        readonly float S;
        readonly ToastKind kind;
        readonly string title, body;
        readonly Action onClick;
        readonly Timer timer;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Font titleFont, bodyFont;
        readonly RectangleF closeRect;
        long lastTick;
        float lived, closingFor = -1, propAngle, targetY, currentY;
        bool hovering, hoverClose;

        public static void Pop(ToastKind kind, string title, string body, Action onClick)
        {
            var toast = new Toast(kind, title, body, onClick);
            Open.Add(toast);
            while (Open.Count(t => t.closingFor < 0) > 3) Open.First(t => t.closingFor < 0).BeginClose();
            Reflow();
            toast.currentY = toast.targetY + 26 * toast.S;
            toast.Location = new Point(toast.Left, (int)toast.currentY);
            toast.Opacity = 0;
            toast.Show();
        }

        static void Reflow()
        {
            var area = Screen.PrimaryScreen.WorkingArea;
            float y = area.Bottom;
            for (int i = Open.Count - 1; i >= 0; i--)
            {
                var t = Open[i];
                y -= 14 * t.S + t.Height;
                t.targetY = y;
            }
        }

        Toast(ToastKind kind, string title, string body, Action onClick)
        {
            this.kind = kind;
            this.title = title;
            this.body = body ?? "";
            this.onClick = onClick;
            using (var g = CreateGraphics()) S = g.DpiX / 96f;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Panel;
            DoubleBuffered = true;
            Cursor = onClick != null ? Cursors.Hand : Cursors.Default;

            titleFont = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            bodyFont = new Font("Segoe UI", 9.25f);
            float width = 380 * S, textW = width - 92 * S - 34 * S;
            var bodyH = TextRenderer.MeasureText(this.body, bodyFont, new Size((int)textW, 0), TextFormatFlags.WordBreak).Height;
            float height = Math.Max(92 * S, 18 * S + 24 * S + bodyH + 20 * S);
            var area = Screen.PrimaryScreen.WorkingArea;
            Bounds = new Rectangle((int)(area.Right - width - 14 * S), area.Bottom, (int)width, (int)height);
            closeRect = new RectangleF(width - 30 * S, 10 * S, 20 * S, 20 * S);

            timer = new Timer { Interval = 16 };
            timer.Tick += (s, e) => Tick();
            timer.Start();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x80 | 0x8; // no-activate, tool window (no taskbar/alt-tab), topmost
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle, Theme.Line);
        }

        Color Accent
        {
            get
            {
                switch (kind)
                {
                    case ToastKind.Success: return Theme.Mint;
                    case ToastKind.Warning: return Theme.Amber;
                    case ToastKind.Error: return Theme.Pink;
                    case ToastKind.Special: return Theme.Hex(0xFFC93C);
                    default: return Theme.Sky;
                }
            }
        }

        void BeginClose() { if (closingFor < 0) closingFor = 0; }

        void Tick()
        {
            long now = clock.ElapsedMilliseconds;
            float dt = now - lastTick;
            lastTick = now;
            if (closingFor >= 0)
            {
                closingFor += dt;
                Opacity = Math.Max(0, 1 - closingFor / 220f);
                if (closingFor >= 220) { Close(); return; }
            }
            else
            {
                if (!hovering) lived += dt;
                if (lived >= LifeMs) BeginClose();
                Opacity = Math.Min(1, now / 200.0);
            }
            currentY += (targetY - currentY) * 0.2f;
            Top = (int)Math.Round(currentY);
            propAngle += 26;
            Invalidate();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            Open.Remove(this);
            Reflow();
            base.OnFormClosed(e);
        }

        protected override void OnMouseEnter(EventArgs e) { hovering = true; base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovering = hoverClose = false; base.OnMouseLeave(e); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            hoverClose = closeRect.Contains(e.Location);
            Cursor = hoverClose || onClick != null ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!closeRect.Contains(e.Location) && onClick != null)
            {
                try { onClick(); } catch { }
            }
            BeginClose();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            float t = clock.ElapsedMilliseconds / 1000f;
            g.Clear(Theme.Panel);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // mascot badge; its mood matches the message
            var c = new PointF(46 * S, Math.Min(Height / 2f, 46 * S));
            float r = 30 * S;
            using (var badge = new SolidBrush(Theme.Alpha(Accent, 40))) g.FillEllipse(badge, c.X - r, c.Y - r, 2 * r, 2 * r);
            using (var ring = new Pen(Theme.Alpha(Accent, 140), 1.5f * S)) g.DrawEllipse(ring, c.X - r, c.Y - r, 2 * r, 2 * r);
            float tilt, bob = 0;
            switch (kind)
            {
                case ToastKind.Success: tilt = (float)Math.Sin(t * 3) * 8; bob = (float)Math.Abs(Math.Sin(t * 4)) * -3 * S; break;
                case ToastKind.Error: tilt = 22 + (float)Math.Sin(t * 11) * 5; break;
                case ToastKind.Warning: tilt = (float)Math.Sin(t * 5) * 12; break;
                case ToastKind.Special: tilt = (t * 140) % 360; bob = (float)Math.Sin(t * 3) * -3 * S; break; // victory spin
                default: tilt = (float)Math.Sin(t * 1.6) * 5; break;
            }
            float phase = (t + 1.6f) % 3.3f;
            float blink = phase < 0.14f ? (float)Math.Sin(phase / 0.14f * Math.PI) : 0;
            QuadArt.Draw(g, c.X, c.Y + bob, 50 * S, propAngle, tilt, blink, true, Accent, kind == ToastKind.Error);
            if (kind == ToastKind.Special) QuadArt.Sparkles(g, c, r, t, S);

            float x = 92 * S, w = Width - x - 34 * S;
            Theme.DrawText(g, title, titleFont, new RectangleF(x, 16 * S, w, 24 * S), Theme.Text, TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            Theme.DrawText(g, body, bodyFont, new RectangleF(x, 42 * S, w, Height - 50 * S), Theme.Muted, TextFormatFlags.WordBreak);

            using (var pen = new Pen(hoverClose ? Theme.Text : Theme.Muted, 1.6f * S) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                var m = new PointF(closeRect.X + closeRect.Width / 2, closeRect.Y + closeRect.Height / 2);
                float k = 4.5f * S;
                g.DrawLine(pen, m.X - k, m.Y - k, m.X + k, m.Y + k);
                g.DrawLine(pen, m.X + k, m.Y - k, m.X - k, m.Y + k);
            }

            // time-left fuse along the bottom
            float left = closingFor >= 0 ? 0 : 1 - Math.Min(1, lived / LifeMs);
            using (var fuse = new SolidBrush(Accent)) g.FillRectangle(fuse, 0, Height - 3 * S, Width * left, 3 * S);
        }

        internal static Toast CreateForPreview(ToastKind kind, string title, string body)
        {
            var t = new Toast(kind, title, body, () => { });
            t.lived = LifeMs * 0.35f;
            return t;
        }
    }

    // ------------------------------------------------------------------------------------------------ tray menu

    class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Panel; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Panel; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Panel; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Panel; } }
        public override Color MenuBorder { get { return Theme.Line; } }
        public override Color MenuItemBorder { get { return Theme.PanelHi; } }
        public override Color MenuItemSelected { get { return Theme.PanelHi; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.PanelHi; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.PanelHi; } }
        public override Color SeparatorDark { get { return Theme.Line; } }
        public override Color SeparatorLight { get { return Theme.Panel; } }
        public override Color CheckBackground { get { return Theme.Panel; } }
        public override Color CheckSelectedBackground { get { return Theme.PanelHi; } }
        public override Color CheckPressedBackground { get { return Theme.PanelHi; } }
    }

    class MenuRenderer : ToolStripProfessionalRenderer
    {
        public MenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if ("header".Equals(e.Item.Tag)) e.TextColor = Theme.Mint;
            else if ("special".Equals(e.Item.Tag)) e.TextColor = Theme.Hex(0xFFC93C);
            else e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            float d = Math.Min(r.Width, r.Height) - 2;
            var box = new RectangleF(r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
            using (var fill = new SolidBrush(Theme.Mint)) g.FillEllipse(fill, box);
            using (var tick = new Pen(Theme.Ink, Math.Max(1.6f, d / 8)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(tick, new[]
                {
                    new PointF(box.X + d * 0.27f, box.Y + d * 0.52f),
                    new PointF(box.X + d * 0.44f, box.Y + d * 0.68f),
                    new PointF(box.X + d * 0.74f, box.Y + d * 0.36f)
                });
        }
    }
}
