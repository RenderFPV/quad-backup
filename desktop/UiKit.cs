// Look-and-feel for the Mac/Linux app, matching the Windows one: colours, the quad mascot, the classic tray icon,
// starry sky, confetti, and the hand-drawn buttons/toggles.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace QuadBackup
{
    static class Palette
    {
        public static readonly Color Bg = Rgb(0x15172B);
        public static readonly Color Panel = Rgb(0x1F2244);
        public static readonly Color PanelHi = Rgb(0x2A2E5A);
        public static readonly Color Line = Rgb(0x3A3F78);
        public static readonly Color Ink = Rgb(0x0E1024);
        public static readonly Color Text = Rgb(0xF5F2FF);
        public static readonly Color Muted = Rgb(0xA0A5D0);
        public static readonly Color Amber = Rgb(0xFFB627);
        public static readonly Color Pink = Rgb(0xFF5C8A);
        public static readonly Color Coral = Rgb(0xFF8A5C);
        public static readonly Color Mint = Rgb(0x3DDC97);
        public static readonly Color Sky = Rgb(0x5AC8FA);
        public static readonly Color Violet = Rgb(0x8B7CFF);
        public static readonly Color Gold = Rgb(0xFFC93C);

        public static Color Rgb(int rgb) { return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb); }
        public static Color Alpha(Color c, int a) { return Color.FromArgb((byte)Math.Max(0, Math.Min(255, a)), c.R, c.G, c.B); }
        public static Color Mix(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        }
        public static Color Lighten(Color c, double t) { return Mix(c, Colors.White, t); }
        public static IBrush Brush(Color c) { return new SolidColorBrush(c); }
    }

    static class Txt
    {
        public static FormattedText Text(string s, double size, Color c, FontWeight weight = FontWeight.Normal, double maxWidth = 0)
        {
            var ft = new FormattedText(s ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, weight), size, Palette.Brush(c));
            if (maxWidth > 0)
            {
                ft.MaxTextWidth = maxWidth;
                ft.Trimming = TextTrimming.CharacterEllipsis;
                ft.MaxLineCount = 1;
            }
            return ft;
        }

        public static void At(DrawingContext g, string s, double size, Color c, double x, double y, FontWeight w = FontWeight.Normal, double maxWidth = 0)
        {
            g.DrawText(Text(s, size, c, w, maxWidth), new Point(x, y));
        }

        public static void Centered(DrawingContext g, string s, double size, Color c, Rect r, FontWeight w = FontWeight.Normal)
        {
            var ft = Text(s, size, c, w, r.Width);
            g.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, r.Y + (r.Height - ft.Height) / 2));
        }
    }

    // ------------------------------------------------------------------------------------------------ the mascot

    static class QuadArt
    {
        static readonly Point[] Motors = { new Point(-32, -32), new Point(32, -32), new Point(32, 32), new Point(-32, 32) };

        // A top-down quad with a face, drawn in a 100x100 box centred on c and scaled to `size`.
        public static void Draw(DrawingContext g, Point c, double size, double propAngle, double tilt, double blink, bool face, Color body, bool dizzy = false)
        {
            var m = Matrix.CreateScale(size / 100, size / 100) * Matrix.CreateRotation(tilt * Math.PI / 180) * Matrix.CreateTranslation(c.X, c.Y);
            using (g.PushTransform(m))
            {
                var edge = new Pen(Palette.Brush(Palette.Ink), 13, lineCap: PenLineCap.Round);
                var arm = new Pen(Palette.Brush(Palette.Rgb(0x565CA8)), 7.5, lineCap: PenLineCap.Round);
                g.DrawLine(edge, new Point(-32, -32), new Point(32, 32));
                g.DrawLine(edge, new Point(32, -32), new Point(-32, 32));
                g.DrawLine(arm, new Point(-32, -32), new Point(32, 32));
                g.DrawLine(arm, new Point(32, -32), new Point(-32, 32));

                var disc = Palette.Brush(Palette.Alpha(Palette.Amber, 60));
                var blade = Palette.Brush(Palette.Amber);
                for (int i = 0; i < 4; i++)
                {
                    var p = Motors[i];
                    g.DrawEllipse(disc, null, p, 20, 20);
                    double a = ((i % 2 == 0 ? 1 : -1) * propAngle + i * 45) * Math.PI / 180;
                    using (g.PushTransform(Matrix.CreateRotation(a) * Matrix.CreateTranslation(p.X, p.Y)))
                        g.DrawEllipse(blade, null, new Point(0, 0), 19, 3.6);
                    g.DrawEllipse(Palette.Brush(Palette.Ink), null, p, 5.5, 5.5);
                    g.DrawEllipse(Palette.Brush(Palette.Rgb(0xFFE08A)), null, p, 2, 2);
                }

                // antenna with a bobble on top
                var wire = new StreamGeometry();
                using (var w = wire.Open())
                {
                    w.BeginFigure(new Point(7, -15), false);
                    w.CubicBezierTo(new Point(9, -24), new Point(15, -27), new Point(17, -35));
                    w.EndFigure(false);
                }
                g.DrawGeometry(null, new Pen(Palette.Brush(Palette.Muted), 2.4, lineCap: PenLineCap.Round), wire);
                g.DrawEllipse(Palette.Brush(Palette.Pink), null, new Point(17, -35.5), 3.5, 3.5);

                var bodyRect = new Rect(-19, -16, 38, 32);
                var fill = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Palette.Lighten(body, 0.25), 0), new GradientStop(body, 1) }
                };
                g.DrawRectangle(fill, new Pen(Palette.Brush(Palette.Ink), 2.6), bodyRect, 11, 11);

                if (face)
                {
                    var inkPen = new Pen(Palette.Brush(Palette.Ink), 2.2, lineCap: PenLineCap.Round);
                    if (dizzy)
                    {
                        foreach (var x in new[] { -8.0, 8.0 })
                        {
                            g.DrawLine(inkPen, new Point(x - 3, -6), new Point(x + 3, 0));
                            g.DrawLine(inkPen, new Point(x + 3, -6), new Point(x - 3, 0));
                        }
                        var mouth = new StreamGeometry();
                        using (var mc = mouth.Open())
                        {
                            mc.BeginFigure(new Point(-6, 7), false);
                            mc.CubicBezierTo(new Point(-3, 4), new Point(3, 10), new Point(6, 7));
                            mc.EndFigure(false);
                        }
                        g.DrawGeometry(null, inkPen, mouth);
                    }
                    else
                    {
                        double eyeH = 11 * (1 - 0.88 * blink);
                        foreach (var x in new[] { -8.0, 8.0 })
                        {
                            g.DrawEllipse(Brushes.White, null, new Point(x, -3), 4.5, eyeH / 2);
                            if (blink < 0.5)
                            {
                                g.DrawEllipse(Palette.Brush(Palette.Ink), null, new Point(x + 0.3, -0.7), 2.5, 2.7);
                                g.DrawEllipse(Brushes.White, null, new Point(x + 0.3, -1.9), 0.9, 0.9);
                            }
                        }
                        var smile = new StreamGeometry();
                        using (var sc = smile.Open())
                        {
                            sc.BeginFigure(new Point(-4.8, 3.4), false);
                            sc.QuadraticBezierTo(new Point(0, 9.5), new Point(4.8, 3.4));
                            sc.EndFigure(false);
                        }
                        g.DrawGeometry(null, inkPen, smile);
                    }
                    var blush = Palette.Brush(Palette.Alpha(Palette.Pink, 150));
                    g.DrawEllipse(blush, null, new Point(-14, 5.8), 3, 1.8);
                    g.DrawEllipse(blush, null, new Point(14, 5.8), 3, 1.8);
                }
            }
        }

        // Three twinkling four-point stars around a circle.
        public static void Sparkles(DrawingContext g, Point c, double r, double t)
        {
            double[] angles = { -50, 35, 150 };
            for (int i = 0; i < angles.Length; i++)
            {
                double a = angles[i] * Math.PI / 180;
                double twinkle = 0.55 + 0.45 * Math.Sin(t * 4 + i * 2.1);
                var p = new Point(c.X + Math.Cos(a) * (r + 2), c.Y + Math.Sin(a) * (r + 2));
                double z = (3.2 + i * 0.8) * twinkle;
                var b = Palette.Brush(Palette.Alpha(Palette.Rgb(0xFFE58A), (int)(255 * twinkle)));
                g.DrawEllipse(b, null, p, z * 2, z * 0.4);
                g.DrawEllipse(b, null, p, z * 0.4, z * 2);
            }
        }

        // The original logo: four amber props on a grey X with a blue body (tray / dock icon).
        public static void Classic(DrawingContext g, double size)
        {
            double s = size / 32;
            double a = 7.5 * s, z = 24.5 * s, r = 5.4 * s;
            var armEdge = new Pen(Palette.Brush(Color.FromRgb(40, 40, 40)), 5.4 * s);
            var arm = new Pen(Palette.Brush(Color.FromRgb(225, 225, 225)), 3.2 * s);
            var edge = new Pen(Palette.Brush(Color.FromRgb(40, 40, 40)), 1.4 * s);
            g.DrawLine(armEdge, new Point(a, a), new Point(z, z));
            g.DrawLine(armEdge, new Point(z, a), new Point(a, z));
            g.DrawLine(arm, new Point(a, a), new Point(z, z));
            g.DrawLine(arm, new Point(z, a), new Point(a, z));
            foreach (var p in new[] { new Point(a, a), new Point(z, a), new Point(a, z), new Point(z, z) })
                g.DrawEllipse(Palette.Brush(Color.FromRgb(255, 176, 0)), edge, p, r, r);
            g.DrawRectangle(Palette.Brush(Color.FromRgb(30, 136, 229)), edge, new Rect(11.5 * s, 11.5 * s, 9 * s, 9 * s));
        }

        public static WindowIcon ClassicIcon(int px)
        {
            using (var bmp = new RenderTargetBitmap(new PixelSize(px, px), new Vector(96, 96)))
            {
                using (var g = bmp.CreateDrawingContext()) Classic(g, px);
                var ms = new MemoryStream();
                bmp.Save(ms);
                ms.Position = 0;
                return new WindowIcon(ms);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------ scenery

    class SkyScene
    {
        struct Star { public double X, Y, Size, Speed, Phase; }
        readonly Star[] stars;

        public SkyScene(int seed, int count)
        {
            var r = new Random(seed);
            stars = new Star[count];
            for (int i = 0; i < count; i++)
                stars[i] = new Star { X = r.NextDouble(), Y = r.NextDouble() * 0.85, Size = 1 + r.NextDouble() * 2.4, Speed = 0.8 + r.NextDouble() * 2.2, Phase = r.NextDouble() * Math.PI * 2 };
        }

        public void Paint(DrawingContext g, Rect r, double t)
        {
            var bg = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Palette.Rgb(0x40308A), 0), new GradientStop(Palette.Bg, 1) }
            };
            g.DrawRectangle(bg, null, r);
            var glow = new RadialGradientBrush
            {
                GradientStops = { new GradientStop(Palette.Alpha(Palette.Pink, 75), 0), new GradientStop(Palette.Alpha(Palette.Pink, 0), 1) }
            };
            g.DrawEllipse(glow, null, new Point(r.Center.X, r.Y + 90), 150, 100);
            for (int i = 0; i < 3; i++)
            {
                double travel = (i * 0.41 + t * 0.014 * (1 + i * 0.35)) % 1.4 - 0.2;
                Cloud(g, r.X + travel * r.Width, r.Y + r.Height * (0.5 + i * 0.14), 60 + i * 18, 16 + i * 7);
            }
            foreach (var st in stars)
            {
                int a = (int)(130 + 120 * Math.Sin(t * st.Speed + st.Phase));
                var p = new Point(r.X + st.X * r.Width, r.Y + st.Y * r.Height);
                var b = Palette.Brush(Palette.Alpha(Colors.White, a));
                double z = st.Size;
                if (st.Size > 2.8)
                {
                    g.DrawEllipse(b, null, p, z * 2.2, z * 0.35);
                    g.DrawEllipse(b, null, p, z * 0.35, z * 2.2);
                }
                else g.DrawEllipse(b, null, p, z / 2, z / 2);
            }
        }

        static void Cloud(DrawingContext g, double cx, double cy, double w, int alpha)
        {
            var b = Palette.Brush(Color.FromArgb((byte)alpha, 255, 255, 255));
            var geo = new GeometryGroup { FillRule = FillRule.NonZero };
            geo.Children.Add(new EllipseGeometry(new Rect(cx - w / 2, cy - w * 0.16, w, w * 0.32)));
            geo.Children.Add(new EllipseGeometry(new Rect(cx - w * 0.3, cy - w * 0.3, w * 0.4, w * 0.4)));
            geo.Children.Add(new EllipseGeometry(new Rect(cx - w * 0.02, cy - w * 0.24, w * 0.32, w * 0.32)));
            g.DrawGeometry(b, null, geo);
        }
    }

    class Confetti
    {
        class Bit { public double X, Y, Vx, Vy, Rot, Spin, W, H; public Color C; }
        static readonly Color[] Colours = { Palette.Pink, Palette.Amber, Palette.Mint, Palette.Sky, Palette.Violet, Colors.White };
        readonly List<Bit> bits = new List<Bit>();
        readonly Random rnd = new Random();

        public bool Active { get { return bits.Count > 0; } }

        public void Burst(double x, double y, int count)
        {
            for (int i = 0; i < count; i++)
            {
                double angle = rnd.NextDouble() * Math.PI * 2, speed = 3 + rnd.NextDouble() * 8;
                bits.Add(new Bit
                {
                    X = x, Y = y, Vx = Math.Cos(angle) * speed, Vy = Math.Sin(angle) * speed - 7,
                    Rot = rnd.NextDouble() * 360, Spin = (rnd.NextDouble() - 0.5) * 36,
                    W = 5 + rnd.NextDouble() * 6, H = 3 + rnd.NextDouble() * 3, C = Colours[rnd.Next(Colours.Length)]
                });
            }
        }

        public void Step(double floor)
        {
            foreach (var b in bits) { b.Vy += 0.38; b.Vx *= 0.985; b.X += b.Vx; b.Y += b.Vy; b.Rot += b.Spin; }
            bits.RemoveAll(b => b.Y > floor + 30);
        }

        public void Paint(DrawingContext g)
        {
            foreach (var b in bits)
                using (g.PushTransform(Matrix.CreateRotation(b.Rot * Math.PI / 180) * Matrix.CreateTranslation(b.X, b.Y)))
                    g.DrawRectangle(Palette.Brush(b.C), null, new Rect(-b.W / 2, -b.H / 2, b.W, b.H));
        }
    }

    // ------------------------------------------------------------------------------------------------ controls

    class PillButton : Control
    {
        public enum Kind { Primary, Secondary, Special }
        readonly Kind kind;
        string text;
        bool hover, down;
        DispatcherTimer shine;
        public event EventHandler Click;

        public PillButton(string text, Kind kind, double width, double height = 40)
        {
            this.text = text;
            this.kind = kind;
            Width = width;
            Height = height;
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        public string Text { get { return text; } set { text = value; InvalidateVisual(); } }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (kind == Kind.Special)
            {
                shine = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Render, (s, a) => InvalidateVisual());
                shine.Start();
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (shine != null) { shine.Stop(); shine = null; }
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnPointerEntered(PointerEventArgs e) { hover = true; InvalidateVisual(); base.OnPointerEntered(e); }
        protected override void OnPointerExited(PointerEventArgs e) { hover = down = false; InvalidateVisual(); base.OnPointerExited(e); }
        protected override void OnPointerPressed(PointerPressedEventArgs e) { down = true; InvalidateVisual(); e.Handled = true; base.OnPointerPressed(e); }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            bool fire = down && hover && IsEnabled;
            down = false;
            InvalidateVisual();
            base.OnPointerReleased(e);
            if (fire) PerformClick();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Space) { PerformClick(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsEnabledProperty || change.Property == IsFocusedProperty) InvalidateVisual();
        }

        public void PerformClick() { if (IsEnabled && Click != null) Click(this, EventArgs.Empty); }

        public override void Render(DrawingContext g)
        {
            double w = Bounds.Width, h = Bounds.Height;
            double lift = down ? 1 : (hover ? -1 : 0);
            var r = new Rect(1, 2 + lift, w - 2, h - 4);
            double rad = r.Height / 2;
            if (kind == Kind.Secondary)
            {
                g.DrawRectangle(Palette.Brush(hover ? Palette.PanelHi : Palette.Panel), new Pen(Palette.Brush(hover ? Palette.Violet : Palette.Line), 1.5), r, rad, rad);
            }
            else
            {
                bool special = kind == Kind.Special;
                var shade = special ? Palette.Rgb(0xB06A00) : Palette.Rgb(0xA8325A);
                g.DrawRectangle(Palette.Brush(shade), null, new Rect(1, 4, w - 2, h - 5), rad, rad);
                var a = special ? (hover ? Palette.Rgb(0xFFE58A) : Palette.Rgb(0xFFD45C)) : (hover ? Palette.Rgb(0xFF7AA0) : Palette.Pink);
                var b = special ? (hover ? Palette.Rgb(0xFFBE55) : Palette.Rgb(0xFFA51F)) : (hover ? Palette.Rgb(0xFFA478) : Palette.Coral);
                var fill = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(a, 0), new GradientStop(b, 1) }
                };
                g.DrawRectangle(fill, null, r, rad, rad);
                if (special)
                {
                    // a diagonal glint sweeping across every couple of seconds
                    double sweep = (Environment.TickCount64 % 2400) / 2400.0;
                    double gx = r.X - 40 + sweep * (r.Width + 120);
                    var glint = new StreamGeometry();
                    using (var c = glint.Open())
                    {
                        c.BeginFigure(new Point(gx, r.Bottom), true);
                        c.LineTo(new Point(gx + 14, r.Bottom));
                        c.LineTo(new Point(gx + 34, r.Y));
                        c.LineTo(new Point(gx + 20, r.Y));
                        c.EndFigure(true);
                    }
                    using (g.PushClip(new RoundedRect(r, rad)))
                        g.DrawGeometry(Palette.Brush(Palette.Alpha(Colors.White, 110)), null, glint);
                }
            }
            if (IsFocused)
                g.DrawRectangle(null, new Pen(Palette.Brush(Palette.Alpha(Colors.White, 140)), 1.5, new DashStyle(new double[] { 1, 2 }, 0)), r.Deflate(3), rad - 3, rad - 3);
            var color = !IsEnabled ? Palette.Muted : kind == Kind.Special ? Palette.Ink : kind == Kind.Primary ? Colors.White : Palette.Text;
            Txt.Centered(g, text, 13.5, color, r, FontWeight.Bold);
        }
    }

    // On/off switch whose knob is a tiny face: awake when on, asleep when off.
    class FaceToggle : Control
    {
        bool on;
        double knob;
        readonly DispatcherTimer anim;
        public event EventHandler Changed;

        public FaceToggle(bool initial)
        {
            on = initial;
            knob = initial ? 1 : 0;
            Width = 50;
            Height = 28;
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Hand);
            anim = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (s, e) =>
            {
                knob += (on ? 1 : -1) * 0.18;
                if (knob <= 0 || knob >= 1) { knob = Math.Max(0, Math.Min(1, knob)); ((DispatcherTimer)s).Stop(); }
                InvalidateVisual();
            });
        }

        public bool On { get { return on; } }

        void Flip()
        {
            on = !on;
            anim.Start();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e) { Flip(); base.OnPointerReleased(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.Key == Key.Space) { Flip(); e.Handled = true; } base.OnKeyDown(e); }

        public override void Render(DrawingContext g)
        {
            var r = new Rect(1, 1, Bounds.Width - 2, Bounds.Height - 2);
            g.DrawRectangle(Palette.Brush(Palette.Mix(Palette.Line, Palette.Mint, knob)), null, r, r.Height / 2, r.Height / 2);
            double d = r.Height - 6;
            var c = new Point(r.X + 3 + knob * (r.Width - d - 6) + d / 2, r.Y + 3 + d / 2);
            g.DrawEllipse(Brushes.White, null, c, d / 2, d / 2);
            var ink = Palette.Brush(Palette.Ink);
            var pen = new Pen(ink, 1.3, lineCap: PenLineCap.Round);
            if (knob > 0.5)
            {
                g.DrawEllipse(ink, null, new Point(c.X - 2.7, c.Y - 1.7), 1.3, 1.3);
                g.DrawEllipse(ink, null, new Point(c.X + 2.7, c.Y - 1.7), 1.3, 1.3);
                var smile = new StreamGeometry();
                using (var s = smile.Open())
                {
                    s.BeginFigure(new Point(c.X - 2.8, c.Y + 1.2), false);
                    s.QuadraticBezierTo(new Point(c.X, c.Y + 4.2), new Point(c.X + 2.8, c.Y + 1.2));
                    s.EndFigure(false);
                }
                g.DrawGeometry(null, pen, smile);
            }
            else
            {
                g.DrawLine(pen, new Point(c.X - 4.2, c.Y - 1.2), new Point(c.X - 1.4, c.Y - 1.2));
                g.DrawLine(pen, new Point(c.X + 1.4, c.Y - 1.2), new Point(c.X + 4.2, c.Y - 1.2));
            }
        }
    }

    class Segmented : Control
    {
        readonly string[] options;
        int selected, hoverIndex = -1;
        public event EventHandler Changed;

        public Segmented(string[] options, int selected, double width, double height = 36)
        {
            this.options = options;
            this.selected = selected;
            Width = width;
            Height = height;
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        public int Selected { get { return selected; } }

        int IndexAt(double x) { return Math.Max(0, Math.Min(options.Length - 1, (int)(x / (Bounds.Width / options.Length)))); }

        protected override void OnPointerMoved(PointerEventArgs e) { var i = IndexAt(e.GetPosition(this).X); if (i != hoverIndex) { hoverIndex = i; InvalidateVisual(); } base.OnPointerMoved(e); }
        protected override void OnPointerExited(PointerEventArgs e) { hoverIndex = -1; InvalidateVisual(); base.OnPointerExited(e); }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            int i = IndexAt(e.GetPosition(this).X);
            if (i != selected)
            {
                selected = i;
                InvalidateVisual();
                if (Changed != null) Changed(this, EventArgs.Empty);
            }
            base.OnPointerReleased(e);
        }

        public override void Render(DrawingContext g)
        {
            var r = new Rect(0.5, 0.5, Bounds.Width - 1, Bounds.Height - 1);
            g.DrawRectangle(Palette.Brush(Palette.Panel), new Pen(Palette.Brush(Palette.Line), 1.2), r, r.Height / 2, r.Height / 2);
            double w = r.Width / options.Length;
            for (int i = 0; i < options.Length; i++)
            {
                var seg = new Rect(r.X + i * w, r.Y, w, r.Height);
                if (i == selected)
                {
                    var pill = seg.Deflate(3);
                    g.DrawRectangle(Palette.Brush(Palette.Violet), null, pill, pill.Height / 2, pill.Height / 2);
                }
                var c = i == selected ? Colors.White : (i == hoverIndex ? Palette.Text : Palette.Muted);
                Txt.Centered(g, options[i], 12.5, c, seg, FontWeight.Bold);
            }
        }
    }

    class ChipToggle : Control
    {
        readonly string text;
        bool selected, hover;
        public readonly string Value;
        public event EventHandler Changed;

        public ChipToggle(string text, string value, bool selected)
        {
            this.text = text;
            Value = value;
            this.selected = selected;
            Width = Txt.Text(text, 12.5, Palette.Text, FontWeight.Bold).Width + 32;
            Height = 32;
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        public bool Selected { get { return selected; } set { selected = value; InvalidateVisual(); } }

        protected override void OnPointerEntered(PointerEventArgs e) { hover = true; InvalidateVisual(); base.OnPointerEntered(e); }
        protected override void OnPointerExited(PointerEventArgs e) { hover = false; InvalidateVisual(); base.OnPointerExited(e); }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            selected = !selected;
            InvalidateVisual();
            if (Changed != null) Changed(this, EventArgs.Empty);
            base.OnPointerReleased(e);
        }

        public override void Render(DrawingContext g)
        {
            var r = new Rect(0.5, 0.5, Bounds.Width - 1, Bounds.Height - 1);
            var fill = selected ? Palette.Alpha(Palette.Violet, 70) : (hover ? Palette.PanelHi : Palette.Panel);
            g.DrawRectangle(Palette.Brush(fill), new Pen(Palette.Brush(selected ? Palette.Violet : Palette.Line), 1.3), r, r.Height / 2, r.Height / 2);
            Txt.Centered(g, text, 12.5, selected ? Palette.Text : Palette.Muted, r, FontWeight.Bold);
        }
    }

    // A clickable bit of text (used for small actions like "not now").
    class LinkText : TextBlock
    {
        public event EventHandler Click;

        public LinkText(string text, Color color, double size = 12.5, FontWeight weight = FontWeight.SemiBold)
        {
            Text = text;
            Foreground = Palette.Brush(color);
            FontSize = size;
            FontWeight = weight;
            Cursor = new Cursor(StandardCursorType.Hand);
            var normal = color;
            PointerEntered += (s, e) => { Foreground = Palette.Brush(Palette.Text); TextDecorations = Avalonia.Media.TextDecorations.Underline; };
            PointerExited += (s, e) => { Foreground = Palette.Brush(normal); TextDecorations = null; };
            PointerReleased += (s, e) => { if (Click != null) Click(this, EventArgs.Empty); };
        }
    }
}
