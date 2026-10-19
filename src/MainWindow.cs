// The main Quad Backup window (opened from the tray icon): Hangar (drone list), Settings and Flight log pages,
// plus the small themed controls they're built from.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace QuadBackup
{
    static class Fonts
    {
        public static readonly Font Title = Theme.Heavy(20);
        public static readonly Font Heading = Theme.Heavy(14);
        public static readonly Font DialogTitle = Theme.Heavy(13);
        public static readonly Font Body = new Font("Segoe UI", 9.75f);
        public static readonly Font BodyBold = new Font("Segoe UI Semibold", 10.5f);
        public static readonly Font Small = new Font("Segoe UI", 9f);
        public static readonly Font SmallBold = new Font("Segoe UI", 8.25f, FontStyle.Bold);
        public static readonly Font Status = new Font("Segoe UI Semibold", 10f);
        public static readonly Font Tab = new Font("Segoe UI Semibold", 10f);
        public static readonly Font Button = new Font("Segoe UI", 10f, FontStyle.Bold);
        public static readonly Font ButtonSmall = new Font("Segoe UI", 9f, FontStyle.Bold);
        public static readonly Font CardName = new Font("Segoe UI", 12.5f, FontStyle.Bold);
        public static readonly Font Input = new Font("Segoe UI Semibold", 10.5f);
    }

    static class DarkScroll
    {
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        // Dark scrollbars (Windows 10 1809+). Silently does nothing elsewhere.
        public static void Apply(Control c)
        {
            EventHandler apply = (s, e) => { try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { } };
            if (c.IsHandleCreated) apply(c, EventArgs.Empty);
            else c.HandleCreated += apply;
        }
    }

    // ------------------------------------------------------------------------------------------------ small controls

    abstract class PaintedControl : Control
    {
        protected readonly float S;

        protected PaintedControl(float scale)
        {
            S = scale;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Theme.Bg;
        }
    }

    // On/off switch whose knob is a tiny face: awake when on, asleep when off.
    class Toggle : PaintedControl
    {
        readonly Timer anim = new Timer { Interval = 15 };
        bool on;
        float knob;
        public event EventHandler Changed;

        public Toggle(float s) : base(s)
        {
            SetStyle(ControlStyles.Selectable, true);
            Size = new Size((int)(50 * s), (int)(28 * s));
            Cursor = Cursors.Hand;
            anim.Tick += (o, e) =>
            {
                knob += (on ? 1 : -1) * 0.18f;
                if (knob <= 0 || knob >= 1) { knob = Math.Max(0, Math.Min(1, knob)); anim.Stop(); }
                Invalidate();
            };
        }

        public bool On { get { return on; } }

        public void SetInitial(bool value) { on = value; knob = value ? 1 : 0; Invalidate(); }

        void Flip()
        {
            on = !on;
            anim.Start();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        protected override void OnClick(EventArgs e) { Focus(); Flip(); base.OnClick(e); }
        protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Space) Flip(); base.OnKeyUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void Dispose(bool disposing) { if (disposing) anim.Dispose(); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(1, 1, Width - 2, Height - 2);
            using (var path = Theme.Round(r, r.Height / 2))
            using (var fill = new SolidBrush(Theme.Mix(Theme.Line, Theme.Mint, knob)))
                g.FillPath(fill, path);
            if (Focused && ShowFocusCues)
                using (var ring = new Pen(Theme.Alpha(Color.White, 120), 1.2f * S) { DashStyle = DashStyle.Dot })
                using (var path = Theme.Round(r, r.Height / 2))
                    g.DrawPath(ring, path);

            float d = r.Height - 6 * S;
            float x = r.X + 3 * S + knob * (r.Width - d - 6 * S), y = r.Y + 3 * S;
            using (var white = new SolidBrush(Color.White)) g.FillEllipse(white, x, y, d, d);
            float cx = x + d / 2, cy = y + d / 2;
            using (var ink = new SolidBrush(Theme.Ink))
            using (var pen = new Pen(Theme.Ink, 1.3f * S) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                if (knob > 0.5f)
                {
                    g.FillEllipse(ink, cx - 4 * S, cy - 3 * S, 2.6f * S, 2.6f * S);
                    g.FillEllipse(ink, cx + 1.4f * S, cy - 3 * S, 2.6f * S, 2.6f * S);
                    g.DrawArc(pen, cx - 3 * S, cy - 1 * S, 6 * S, 4.5f * S, 20, 140);
                }
                else
                {
                    g.DrawLine(pen, cx - 4.2f * S, cy - 1.2f * S, cx - 1.4f * S, cy - 1.2f * S);
                    g.DrawLine(pen, cx + 1.4f * S, cy - 1.2f * S, cx + 4.2f * S, cy - 1.2f * S);
                }
            }
        }
    }

    class Segmented : PaintedControl
    {
        readonly string[] options;
        int selected, hoverIndex = -1;
        public event EventHandler Changed;

        public Segmented(float s, string[] options) : base(s)
        {
            this.options = options;
            Cursor = Cursors.Hand;
            Font = Fonts.ButtonSmall;
        }

        public int Selected { get { return selected; } set { selected = value; Invalidate(); } }

        int IndexAt(int x) { return Math.Max(0, Math.Min(options.Length - 1, (int)(x / (Width / (float)options.Length)))); }

        protected override void OnMouseMove(MouseEventArgs e) { var i = IndexAt(e.X); if (i != hoverIndex) { hoverIndex = i; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { hoverIndex = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            int i = IndexAt(e.X);
            if (i != selected)
            {
                selected = i;
                Invalidate();
                if (Changed != null) Changed(this, EventArgs.Empty);
            }
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using (var path = Theme.Round(r, r.Height / 2))
            using (var fill = new SolidBrush(Theme.Panel))
            using (var pen = new Pen(Theme.Line, 1.2f * S))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
            float w = r.Width / options.Length;
            for (int i = 0; i < options.Length; i++)
            {
                var seg = new RectangleF(r.X + i * w, r.Y, w, r.Height);
                if (i == selected)
                {
                    var pill = RectangleF.Inflate(seg, -3 * S, -3 * S);
                    using (var path = Theme.Round(pill, pill.Height / 2))
                    using (var fill = new LinearGradientBrush(pill, Theme.Violet, Theme.Hex(0x6A5CF0), 90f))
                        g.FillPath(fill, path);
                }
                var color = i == selected ? Color.White : (i == hoverIndex ? Theme.Text : Theme.Muted);
                Theme.DrawText(g, options[i], Font, seg, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }
    }

    class ChipToggle : PaintedControl
    {
        bool selected, hover;
        public readonly string Value;
        public event EventHandler Changed;

        public ChipToggle(float s, string text, string value) : base(s)
        {
            Text = text;
            Value = value;
            Font = Fonts.ButtonSmall;
            Cursor = Cursors.Hand;
            Size = new Size(TextRenderer.MeasureText(text, Font).Width + (int)(30 * s), (int)(32 * s));
        }

        public bool Selected { get { return selected; } set { selected = value; Invalidate(); } }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnClick(EventArgs e)
        {
            selected = !selected;
            Invalidate();
            if (Changed != null) Changed(this, EventArgs.Empty);
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using (var path = Theme.Round(r, r.Height / 2))
            using (var fill = new SolidBrush(selected ? Theme.Alpha(Theme.Violet, 70) : (hover ? Theme.PanelHi : Theme.Panel)))
            using (var pen = new Pen(selected ? Theme.Violet : Theme.Line, 1.3f * S))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
            Theme.DrawText(g, Text, Font, r, selected ? Theme.Text : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    // A rounded text field.
    class InputBox : PaintedControl
    {
        public readonly TextBox Box;
        Color fill = Theme.Panel;
        bool bad;

        public InputBox(float s, Font font) : base(s)
        {
            Box = new TextBox { BorderStyle = BorderStyle.None, BackColor = fill, ForeColor = Theme.Text, Font = font };
            Controls.Add(Box);
            Box.GotFocus += (o, e) => Invalidate();
            Box.LostFocus += (o, e) => Invalidate();
            Cursor = Cursors.IBeam;
        }

        public Color Fill { set { fill = value; Box.BackColor = value; Invalidate(); } }
        public bool Bad { set { bad = value; Invalidate(); } }

        protected override void OnClick(EventArgs e) { Box.Focus(); base.OnClick(e); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            Box.Width = Width - (int)(26 * S);
            Box.Location = new Point((int)(13 * S), (Height - Box.Height) / 2);
            base.OnLayout(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(1, 1, Width - 2, Height - 2);
            using (var path = Theme.Round(r, 10 * S))
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(bad ? Theme.Pink : (Box.Focused ? Theme.Violet : Theme.Line), (Box.Focused || bad ? 1.8f : 1.2f) * S))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }
        }
    }

    // Rounded panel used to frame the log.
    class RoundedFrame : Panel
    {
        readonly float S;

        public RoundedFrame(float s)
        {
            S = s;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            Padding = new Padding((int)(14 * s), (int)(12 * s), (int)(6 * s), (int)(12 * s));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using (var path = Theme.Round(r, 14 * S))
            using (var fill = new SolidBrush(Theme.Panel))
            using (var pen = new Pen(Theme.Line, 1.2f * S))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------ themed pop-up dialogs

    class ThemedDialog : Form
    {
        const float W = 440;
        readonly float S;
        readonly string title, message;
        readonly bool danger;
        readonly InputBox input;
        readonly Timer timer = new Timer { Interval = 33 };
        readonly float messageH;
        float propAngle;
        bool ok;

        public static string Prompt(IWin32Window owner, string title, string message, string initial, string okText)
        {
            using (var d = new ThemedDialog(title, message, initial, okText, "Cancel", false))
            {
                d.ShowDialog(owner);
                return d.ok ? d.input.Box.Text.Trim() : null;
            }
        }

        public static bool Confirm(IWin32Window owner, string title, string message, string okText, bool danger)
        {
            using (var d = new ThemedDialog(title, message, null, okText, "Cancel", danger))
            {
                d.ShowDialog(owner);
                return d.ok;
            }
        }

        public static void Alert(IWin32Window owner, string title, string message)
        {
            using (var d = new ThemedDialog(title, message, null, "Got it", null, true)) d.ShowDialog(owner);
        }

        ThemedDialog(string title, string message, string initial, string okText, string cancelText, bool danger)
        {
            this.title = title;
            this.message = message;
            this.danger = danger;
            using (var g = CreateGraphics()) S = g.DpiX / 96f;
            Text = title;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;
            KeyPreview = true;
            TopMost = true;
            BackColor = Theme.Panel;
            DoubleBuffered = true;

            messageH = TextRenderer.MeasureText(message, Fonts.Body, new Size((int)((W - 112) * S), 0), TextFormatFlags.WordBreak).Height / S;
            float y = Math.Max(92, 58 + messageH + 18);
            if (initial != null)
            {
                input = new InputBox(S, Fonts.Input) { BackColor = Theme.Panel, Bounds = Rectangle.Round(R(24, y, W - 48, 42)) };
                input.Fill = Theme.Bg;
                input.Box.Text = initial;
                Controls.Add(input);
                y += 58;
            }
            var okButton = new PillButton { Primary = true, Text = okText, Font = Fonts.Button, BackColor = Theme.Panel, Bounds = Rectangle.Round(R(W - 24 - 132, y, 132, 42)) };
            okButton.Click += (o, e) => { ok = true; Close(); };
            Controls.Add(okButton);
            AcceptButton = okButton;
            if (cancelText != null)
            {
                var cancel = new PillButton { Text = cancelText, Font = Fonts.Button, BackColor = Theme.Panel, Bounds = Rectangle.Round(R(W - 24 - 132 - 10 - 112, y, 112, 42)) };
                cancel.Click += (o, e) => Close();
                Controls.Add(cancel);
            }
            ClientSize = new Size((int)(W * S), (int)((y + 42 + 22) * S));
            timer.Tick += (o, e) => { propAngle += 22; Invalidate(Rectangle.Round(R(0, 0, 90, 90))); };
            Shown += (o, e) =>
            {
                timer.Start();
                if (input != null) { input.Box.Focus(); input.Box.SelectAll(); }
                else okButton.Focus();
            };
            FormClosed += (o, e) => timer.Stop();
        }

        RectangleF R(float x, float y, float w, float h) { return new RectangleF(x * S, y * S, w * S, h * S); }

        internal static Form CreateForPreview(string title, string message, string initial, bool danger)
        {
            return new ThemedDialog(title, message, initial, initial != null ? "Rename" : "Forget it", "Cancel", danger);
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.RoundCorners(Handle, Theme.Line); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); base.OnKeyDown(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) Native.DragWindow(this); base.OnMouseDown(e); }
        protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Panel);
            QuadArt.Draw(g, 46 * S, 48 * S, 58 * S, propAngle, danger ? 14 : -6, 0, true, danger ? Theme.Pink : Theme.Sky, false);
            Theme.DrawText(g, title, Fonts.DialogTitle, R(88, 22, W - 112, 30), Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            Theme.DrawText(g, message, Fonts.Body, R(88, 56, W - 112, messageH + 4), Theme.Muted, TextFormatFlags.WordBreak);
        }
    }

    // ------------------------------------------------------------------------------------------------ hangar cards

    class DroneCard : PaintedControl
    {
        public readonly Drone Drone;
        public event Action<DroneCard, string> ActionClicked;
        readonly int backups;
        readonly DateTime? last;
        readonly bool folderMissing;
        readonly Color color;
        readonly List<KeyValuePair<string, RectangleF>> links = new List<KeyValuePair<string, RectangleF>>();
        string hover;
        bool hot;

        public DroneCard(Drone d, float s) : base(s)
        {
            Drone = d;
            Height = (int)(116 * s);
            Margin = new Padding(0, 0, 0, (int)(12 * s));
            folderMissing = string.IsNullOrEmpty(d.Folder) || !Directory.Exists(d.Folder);
            if (!folderMissing)
            {
                try { backups = Directory.GetFiles(d.Folder, "*.txt").Length; } catch { }
            }
            last = MainWindow.ParseTime(d.LastBackup);
            color = MainWindow.ColorFor(d.FcId ?? d.Name ?? "");
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; hover = null; Cursor = Cursors.Default; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            string h = null;
            foreach (var l in links) if (l.Value.Contains(e.Location)) h = l.Key;
            if (h != hover)
            {
                hover = h;
                Cursor = h != null ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (hover != null && ActionClicked != null) ActionClicked(this, hover);
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using (var path = Theme.Round(r, 16 * S))
            using (var fill = new SolidBrush(hot ? Theme.PanelHi : Theme.Panel))
            using (var pen = new Pen(hot ? Theme.Alpha(Theme.Violet, 200) : Theme.Line, 1.2f * S))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }

            // mascot badge in the drone's colour; asleep when paused
            var badge = new RectangleF(16 * S, 18 * S, 72 * S, 72 * S);
            using (var b = new SolidBrush(Theme.Alpha(Drone.Ignored ? Theme.Muted : color, 34))) g.FillEllipse(b, badge);
            QuadArt.Draw(g, 52 * S, 54 * S, 64 * S, 25 + (Drone.FcId ?? "").Length * 7, Drone.Ignored ? 8 : -6, Drone.Ignored ? 1 : 0, true,
                         Drone.Ignored ? Theme.Muted : color);
            if (Drone.Ignored)
                Theme.DrawText(g, "z Z", Fonts.SmallBold, new RectangleF(70 * S, 10 * S, 30 * S, 16 * S), Theme.Muted, TextFormatFlags.Left);

            float x = 104 * S, rightW = 170 * S;
            var name = string.IsNullOrEmpty(Drone.Name) ? "Unnamed quad" : Drone.Name;
            float nameW = Math.Min(Theme.Measure(g, name, Fonts.CardName), Width - x - rightW - 120 * S);
            Theme.DrawText(g, name, Fonts.CardName, new RectangleF(x, 14 * S, nameW + 4, 28 * S), Theme.Text,
                           TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

            string chip = folderMissing ? "folder missing" : (Drone.Ignored ? "paused" : "on patrol");
            var chipColor = folderMissing ? Theme.Pink : (Drone.Ignored ? Theme.Amber : Theme.Mint);
            float cw = Theme.Measure(g, chip, Fonts.SmallBold) + 18 * S;
            var chipRect = new RectangleF(x + nameW + 12 * S, 18 * S, cw, 21 * S);
            using (var path = Theme.Round(chipRect, chipRect.Height / 2))
            using (var b = new SolidBrush(Theme.Alpha(chipColor, 40)))
                g.FillPath(b, path);
            Theme.DrawText(g, chip, Fonts.SmallBold, chipRect, chipColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Drone.Board)) parts.Add(Drone.Board);
            if (!string.IsNullOrEmpty(Drone.Firmware)) parts.Add(Drone.Firmware);
            if (!string.IsNullOrEmpty(Drone.CraftName) && Drone.CraftName != Drone.Name) parts.Add("craft name \"" + Drone.CraftName + "\"");
            Theme.DrawText(g, string.Join("  ·  ", parts), Fonts.Small, new RectangleF(x, 46 * S, Width - x - rightW - 16 * S, 20 * S), Theme.Muted,
                           TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

            var right = new RectangleF(Width - rightW - 18 * S, 0, rightW, 0);
            var rf = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            Theme.DrawText(g, "last backup", Fonts.Small, new RectangleF(right.X, 14 * S, rightW, 18 * S), Theme.Muted, rf);
            Theme.DrawText(g, last.HasValue ? MainWindow.Ago(last.Value) : "never", Fonts.BodyBold, new RectangleF(right.X, 32 * S, rightW, 24 * S), Theme.Text, rf);
            Theme.DrawText(g, backups == 1 ? "1 backup" : backups + " backups", Fonts.Small, new RectangleF(right.X, 56 * S, rightW, 18 * S), Theme.Muted, rf);

            links.Clear();
            var items = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("open", "Open folder"),
                new KeyValuePair<string, string>("rename", "Rename"),
                new KeyValuePair<string, string>("move", "Change folder"),
                new KeyValuePair<string, string>("pause", Drone.Ignored ? "Resume backups" : "Pause backups"),
                new KeyValuePair<string, string>("forget", "Forget")
            };
            float lx = x, ly = 80 * S;
            foreach (var item in items)
            {
                bool isHover = hover == item.Key;
                float w = Theme.Measure(g, item.Value, Fonts.SmallBold);
                var lr = new RectangleF(lx, ly, w, 20 * S);
                links.Add(new KeyValuePair<string, RectangleF>(item.Key, RectangleF.Inflate(lr, 4 * S, 3 * S)));
                var c = isHover ? Theme.Text : (item.Key == "forget" ? Theme.Alpha(Theme.Pink, 220) : Theme.Sky);
                using (var f = new Font(Fonts.SmallBold, isHover ? FontStyle.Bold | FontStyle.Underline : FontStyle.Bold))
                    Theme.DrawText(g, item.Value, f, lr, c, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                lx += w + 22 * S;
            }
        }
    }

    class EmptyHangar : PaintedControl
    {
        public EmptyHangar(float s) : base(s) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            float cx = Width / 2f;
            using (var shadow = new SolidBrush(Theme.Alpha(Theme.Ink, 120))) g.FillEllipse(shadow, cx - 50 * S, 150 * S, 100 * S, 12 * S);
            QuadArt.Draw(g, cx, 92 * S, 130 * S, 15, 0, 1, true, Theme.Sky);
            Theme.DrawText(g, "z", Fonts.Heading, new RectangleF(cx + 50 * S, 30 * S, 30 * S, 30 * S), Theme.Muted, TextFormatFlags.Left);
            Theme.DrawText(g, "Z", Fonts.Title, new RectangleF(cx + 70 * S, 6 * S, 40 * S, 40 * S), Theme.Alpha(Theme.Muted, 170), TextFormatFlags.Left);
            var center = TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.SingleLine;
            Theme.DrawText(g, "The hangar's empty", Fonts.Heading, new RectangleF(0, 182 * S, Width, 32 * S), Theme.Text, center);
            Theme.DrawText(g, "Plug in a quad and I'll take it from there.", Fonts.Body, new RectangleF(0, 216 * S, Width, 24 * S), Theme.Muted, center);
        }
    }

    // ------------------------------------------------------------------------------------------------ the main window

    public class MainWindow : Form
    {
        public enum Page { Hangar, Settings, Log }

        const float W = 780, H = 680, PageTop = 186;
        static readonly Color[] DroneColors = { Theme.Sky, Theme.Mint, Theme.Pink, Theme.Amber, Theme.Violet, Theme.Coral };

        readonly float S;
        readonly Action backupNow, settingsChanged;
        readonly Timer anim = new Timer { Interval = 33 };
        readonly Timer refreshTimer = new Timer { Interval = 2000 };
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly SkyScene sky = new SkyScene(11, 34);
        readonly RectangleF headerRect, closeRect, minRect;
        readonly Dictionary<Page, Panel> pages = new Dictionary<Page, Panel>();
        readonly Dictionary<Page, RectangleF> tabRects = new Dictionary<Page, RectangleF>();

        Page page = Page.Hangar;
        string hover, lastBackupText = "", hangarSignature, logSignature;
        int droneCount;
        float propAngle;
        long savedAt = -100000;

        FlowLayoutPanel cards;
        Label fleetSummary, backupRootDesc, launchPathDesc;
        RichTextBox logBox;
        Segmented logFilter;

        public MainWindow(Action backupNow, Action settingsChanged)
        {
            this.backupNow = backupNow;
            this.settingsChanged = settingsChanged;
            using (var g = CreateGraphics()) S = g.DpiX / 96f;

            Text = "Quad Backup";
            Icon = Art.MakeClassicIcon(32);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Bg;
            DoubleBuffered = true;
            KeyPreview = true;
            ClientSize = new Size((int)(W * S), (int)(H * S));

            headerRect = R(0, 0, W, 128);
            minRect = R(W - 82, 14, 30, 30);
            closeRect = R(W - 46, 14, 30, 30);

            pages[Page.Hangar] = BuildHangarPage();
            pages[Page.Settings] = BuildSettingsPage();
            pages[Page.Log] = BuildLogPage();
            foreach (var p in pages.Values)
            {
                p.Bounds = Rectangle.Round(R(0, PageTop, W, H - PageTop));
                Controls.Add(p);
            }

            anim.Tick += (o, e) => { propAngle += 20; Invalidate(Rectangle.Ceiling(R(0, 0, W, PageTop))); };
            refreshTimer.Tick += (o, e) => RefreshData(false);
            VisibleChanged += (o, e) =>
            {
                if (Visible) { anim.Start(); refreshTimer.Start(); RefreshData(true); }
                else { anim.Stop(); refreshTimer.Stop(); }
            };
            ShowPage(Page.Hangar);
        }

        RectangleF R(float x, float y, float w, float h) { return new RectangleF(x * S, y * S, w * S, h * S); }
        Point Pt(float x, float y) { return new Point((int)(x * S), (int)(y * S)); }
        Size Sz(float w, float h) { return new Size((int)(w * S), (int)(h * S)); }

        public void ShowOn(Page p)
        {
            ShowPage(p);
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        void ShowPage(Page p)
        {
            page = p;
            foreach (var kv in pages) kv.Value.Visible = kv.Key == p;
            if (Visible) RefreshData(true);
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.RoundCorners(Handle, Theme.Line); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // closing the window just tucks it back into the tray; Exit in the tray menu really quits
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            base.OnFormClosing(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Hide();
            base.OnKeyDown(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { anim.Dispose(); refreshTimer.Dispose(); }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ data

        public static DateTime? ParseTime(string s)
        {
            DateTime t;
            if (!string.IsNullOrEmpty(s) && DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return t;
            return null;
        }

        public static string Ago(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalSeconds < 60) return "just now";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + " min ago";
            if (d.TotalHours < 24) { int h = (int)d.TotalHours; return h + (h == 1 ? " hour ago" : " hours ago"); }
            if (d.TotalDays < 30) { int days = (int)d.TotalDays; return days == 1 ? "yesterday" : days + " days ago"; }
            return t.ToString("MMM d, yyyy");
        }

        public static Color ColorFor(string key)
        {
            int h = 17;
            foreach (char c in key) h = unchecked(h * 31 + c);
            return DroneColors[Math.Abs(h % DroneColors.Length)];
        }

        void RefreshData(bool force)
        {
            var db = DroneDb.Load();
            droneCount = db.Drones.Count;
            var latest = db.Drones.Select(d => new { d, t = ParseTime(d.LastBackup) }).Where(x => x.t.HasValue)
                .OrderByDescending(x => x.t.Value).FirstOrDefault();
            lastBackupText = latest == null ? "No backups yet. Plug in a quad!" : "Last backup: " + latest.d.Name + "  ·  " + Ago(latest.t.Value);
            if (page == Page.Hangar) RefreshHangar(db, force);
            if (page == Page.Log) RefreshLog(force);
        }

        // ------------------------------------------------------------------ hangar page

        Panel BuildHangarPage()
        {
            var p = new Panel { BackColor = Theme.Bg };
            p.Controls.Add(new Label { Text = "Your fleet", Font = Fonts.Heading, ForeColor = Theme.Text, BackColor = Theme.Bg, AutoSize = true, Location = Pt(22, 0) });
            fleetSummary = new Label { Font = Fonts.Small, ForeColor = Theme.Muted, BackColor = Theme.Bg, AutoSize = false, Bounds = Rectangle.Round(R(24, 34, 380, 20)) };
            p.Controls.Add(fleetSummary);

            var now = new PillButton { Primary = true, Text = "Back up now", Font = Fonts.Button, Bounds = Rectangle.Round(R(W - 24 - 150, 4, 150, 42)) };
            now.Click += (o, e) => { if (backupNow != null) backupNow(); };
            var folder = new PillButton { Text = "Open hangar folder", Font = Fonts.Button, Bounds = Rectangle.Round(R(W - 24 - 150 - 10 - 180, 4, 180, 42)) };
            folder.Click += (o, e) => OpenFolder(Settings.Load().BackupRoot);
            p.Controls.Add(now);
            p.Controls.Add(folder);

            cards = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = Theme.Bg,
                Bounds = Rectangle.Round(R(24, 64, W - 30, H - PageTop - 64 - 12))
            };
            DarkScroll.Apply(cards);
            p.Controls.Add(cards);
            return p;
        }

        void RefreshHangar(DroneDb db, bool force)
        {
            var sig = string.Join("|", db.Drones.Select(d => d.FcId + "/" + d.Name + "/" + d.Folder + "/" + d.Ignored + "/" + d.LastBackup + "/" + CountFiles(d.Folder)));
            if (!force && sig == hangarSignature)
            {
                foreach (Control c in cards.Controls) c.Invalidate(); // "5 min ago" ticks over
                return;
            }
            hangarSignature = sig;
            int scroll = cards.VerticalScroll.Value;
            int cardW = (int)((W - 48) * S);

            cards.SuspendLayout();
            foreach (var c in cards.Controls.Cast<Control>().ToList()) c.Dispose();
            if (db.Drones.Count == 0)
                cards.Controls.Add(new EmptyHangar(S) { Size = new Size(cardW, (int)(260 * S)) });
            foreach (var d in db.Drones.OrderBy(d => d.Ignored).ThenByDescending(d => ParseTime(d.LastBackup) ?? DateTime.MinValue))
            {
                var card = new DroneCard(d, S) { Width = cardW };
                card.ActionClicked += OnCardAction;
                cards.Controls.Add(card);
            }
            cards.ResumeLayout();
            if (scroll > 0) cards.AutoScrollPosition = new Point(0, scroll);

            int total = db.Drones.Sum(d => CountFiles(d.Folder));
            int paused = db.Drones.Count(d => d.Ignored);
            fleetSummary.Text = db.Drones.Count == 0 ? "Nobody's home yet."
                : Plural(db.Drones.Count, "quad") + "  ·  " + Plural(total, "backup") + (paused > 0 ? "  ·  " + paused + " paused" : "");
        }

        static string Plural(int n, string word) { return n + " " + word + (n == 1 ? "" : "s"); }

        static int CountFiles(string folder)
        {
            try { return !string.IsNullOrEmpty(folder) && Directory.Exists(folder) ? Directory.GetFiles(folder, "*.txt").Length : 0; }
            catch { return 0; }
        }

        void OnCardAction(DroneCard card, string action)
        {
            var d = card.Drone;
            try
            {
                switch (action)
                {
                    case "open":
                        if (Directory.Exists(d.Folder)) OpenFolder(d.Folder);
                        else ThemedDialog.Alert(this, "That folder's gone missing", "Use Change folder to point " + d.Name + " somewhere new.");
                        break;
                    case "rename":
                        Rename(d);
                        break;
                    case "move":
                        using (var dlg = new FolderBrowserDialog { Description = "Pick the folder for " + d.Name + "'s backups", SelectedPath = d.Folder ?? Settings.Load().BackupRoot })
                        {
                            if (dlg.ShowDialog(this) != DialogResult.OK) return;
                            d.Folder = dlg.SelectedPath;
                            d.Name = Path.GetFileName(dlg.SelectedPath.TrimEnd('\\', '/'));
                            DroneDb.Upsert(d);
                        }
                        break;
                    case "pause":
                        d.Ignored = !d.Ignored;
                        DroneDb.Upsert(d);
                        break;
                    case "forget":
                        if (!ThemedDialog.Confirm(this, "Forget " + d.Name + "?",
                            "Its backups stay right where they are. Next time it's plugged in, I'll ask where to save it.", "Forget it", true)) return;
                        DroneDb.Remove(d.FcId);
                        break;
                }
            }
            catch (Exception ex)
            {
                ThemedDialog.Alert(this, "Hmm, that didn't work", ex.Message);
            }
            RefreshData(true);
        }

        void Rename(Drone d)
        {
            var name = ThemedDialog.Prompt(this, "Rename " + d.Name, "This renames its backup folder too. Old backups keep their file names.", d.Name, "Rename");
            if (name == null) return;
            name = BackupFiles.SafeName(name);
            if (name == d.Name) return;
            var oldFolder = d.Folder;
            var parent = string.IsNullOrEmpty(oldFolder) ? Settings.Load().BackupRoot : Path.GetDirectoryName(oldFolder.TrimEnd('\\', '/'));
            var newFolder = Path.Combine(parent, name);
            if (!string.IsNullOrEmpty(oldFolder) && Directory.Exists(oldFolder))
            {
                if (string.Equals(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase))
                {
                    var temp = newFolder + ".renaming";
                    Directory.Move(oldFolder, temp); // case-only rename needs a hop on Windows
                    Directory.Move(temp, newFolder);
                }
                else if (Directory.Exists(newFolder))
                {
                    ThemedDialog.Alert(this, "That name's taken", "There's already a folder called \"" + name + "\" there. Pick another name, or use Change folder to point at it.");
                    return;
                }
                else Directory.Move(oldFolder, newFolder);
            }
            // every flight controller that shared the old folder (e.g. after an FC swap) follows it
            DroneDb.Mutate(db =>
            {
                foreach (var x in db.Drones.Where(x => x.FcId == d.FcId || (oldFolder != null && string.Equals(x.Folder, oldFolder, StringComparison.OrdinalIgnoreCase))))
                {
                    x.Folder = newFolder;
                    x.Name = name;
                }
            });
        }

        // ------------------------------------------------------------------ settings page

        Panel BuildSettingsPage()
        {
            var p = new Panel { AutoScroll = true, BackColor = Theme.Bg };
            DarkScroll.Apply(p);
            var s = Settings.Load();
            float y = 0;

            Section(p, "BACKUPS", ref y);
            var change = SmallButton("Change…", 112);
            change.Click += (o, e) => PickBackupRoot();
            backupRootDesc = Row(p, "Backups folder", s.BackupRoot, ref y, true, change);

            var cmd = new Segmented(S, new[] { "diff all", "dump all" }) { Size = Sz(210, 36), Selected = s.CliCommand == "dump all" ? 1 : 0 };
            cmd.Changed += (o, e) => SaveSetting(x => x.CliCommand = cmd.Selected == 1 ? "dump all" : "diff all");
            Row(p, "Backup command", "diff all saves only what you've changed (best for restoring). dump all saves every single setting.", ref y, false, cmd);

            var chips = new List<ChipToggle> { new ChipToggle(S, "Betaflight", "BTFL"), new ChipToggle(S, "INAV", "INAV"), new ChipToggle(S, "Emuflight", "EMUF") };
            foreach (var c in chips)
            {
                var chip = c;
                chip.Selected = s.FirmwareVariants.Any(v => string.Equals(v, chip.Value, StringComparison.OrdinalIgnoreCase));
                chip.Changed += (o, e) =>
                {
                    if (!chips.Any(x => x.Selected)) { chip.Selected = true; return; } // need at least one
                    SaveSetting(x => x.FirmwareVariants = chips.Where(k => k.Selected).Select(k => k.Value).ToList());
                };
            }
            Row(p, "Firmware to back up", "Flight controllers running anything else are left alone.", ref y, false, chips.ToArray());

            var whenClosed = new Toggle(S);
            whenClosed.SetInitial(s.BackupWhenBetaflightCloses);
            whenClosed.Changed += (o, e) => SaveSetting(x => x.BackupWhenBetaflightCloses = whenClosed.On);
            Row(p, "Back up again when Betaflight closes", "Catches that session's tune if the quad's still plugged in. Only kept if something changed.", ref y, false, whenClosed);

            Section(p, "BETAFLIGHT", ref y);
            var launch = new Toggle(S);
            launch.SetInitial(s.LaunchBetaflight);
            launch.Changed += (o, e) => SaveSetting(x => x.LaunchBetaflight = launch.On);
            Row(p, "Open Betaflight after a backup", "Skipped if it's already open.", ref y, false, launch);

            var browse = SmallButton("Browse…", 104);
            browse.Click += (o, e) => PickLaunchTarget();
            var detect = SmallButton("Auto-detect", 116);
            detect.Click += (o, e) =>
            {
                var target = Betaflight.FindLaunchTarget();
                SaveSetting(x => x.BetaflightLaunchPath = target);
                launchPathDesc.Text = FriendlyLaunch(target);
            };
            launchPathDesc = Row(p, "Betaflight app", FriendlyLaunch(s.BetaflightLaunchPath), ref y, true, browse, detect);

            Section(p, "POP-UPS & STARTUP", ref y);
            var test = SmallButton("Test", 76);
            test.Click += (o, e) => Toast.Pop(ToastKind.Success, "Test pop-up!", Quips.ForSave(), null);
            var notify = new Toggle(S);
            notify.SetInitial(s.ShowNotifications);
            notify.Changed += (o, e) => SaveSetting(x => x.ShowNotifications = notify.On);
            Row(p, "Pop-up notifications", "Little pop-ups when backups happen (or don't).", ref y, false, test, notify);

            var startup = new Toggle(S);
            startup.SetInitial(Startup.IsEnabled());
            startup.Changed += (o, e) =>
            {
                try { Startup.Set(startup.On); savedAt = clock.ElapsedMilliseconds; }
                catch (Exception ex) { ThemedDialog.Alert(this, "Couldn't change startup", ex.Message); }
            };
            Row(p, "Start with Windows", "So I'm already on patrol when you plug in a quad.", ref y, false, startup);

            Section(p, "ADVANCED", ref y);
            var vendors = new InputBox(S, Fonts.Input) { Size = Sz(200, 38) };
            vendors.Box.Text = string.Join(", ", s.UsbVendorIds);
            Action commitVendors = () =>
            {
                var ids = Regex.Split(vendors.Box.Text.ToUpperInvariant(), @"[\s,;]+").Where(v => v.Length > 0).ToList();
                bool valid = ids.Count > 0 && ids.All(v => Regex.IsMatch(v, "^[0-9A-F]{4}$"));
                vendors.Bad = !valid;
                if (!valid) return;
                vendors.Box.Text = string.Join(", ", ids);
                if (!ids.SequenceEqual(Settings.Load().UsbVendorIds)) SaveSetting(x => x.UsbVendorIds = ids);
            };
            vendors.Box.Leave += (o, e) => commitVendors();
            vendors.Box.KeyDown += (o, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; commitVendors(); } };
            Row(p, "USB chip vendors", "Only USB devices from these vendors are ever opened. 0483 = STM32, 2E3C = AT32, 314B = APM32.", ref y, false, vendors);

            var grace = new InputBox(S, Fonts.Input) { Size = Sz(80, 38) };
            grace.Box.Text = s.RebootGraceSeconds.ToString();
            Action commitGrace = () =>
            {
                int n;
                bool valid = int.TryParse(grace.Box.Text.Trim(), out n) && n >= 2 && n <= 60;
                grace.Bad = !valid;
                if (valid && n != Settings.Load().RebootGraceSeconds) SaveSetting(x => x.RebootGraceSeconds = n);
            };
            grace.Box.Leave += (o, e) => commitGrace();
            grace.Box.KeyDown += (o, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; commitGrace(); } };
            Row(p, "Reboot grace (seconds)", "While Betaflight is open, a quad that reconnects this fast is treated as a reboot after Save, not a new plug-in. 2 to 60.", ref y, false, grace);

            y += 18;
            p.Controls.Add(new Label { Text = "Changes save automatically.", Font = Fonts.Small, ForeColor = Theme.Muted, BackColor = Theme.Bg, AutoSize = true, Location = Pt(24, y + 8) });
            var raw = SmallButton("Open settings.json", 168);
            raw.Location = Pt(W - 24 - 20 - 168, y);
            raw.Click += (o, e) => { try { Process.Start("notepad.exe", "\"" + AppPaths.Settings + "\""); } catch { } };
            p.Controls.Add(raw);
            p.Controls.Add(new Panel { BackColor = Theme.Bg, Location = Pt(0, y + 34 + 24), Size = Sz(1, 1) }); // bottom padding when scrolled
            return p;
        }

        PillButton SmallButton(string text, float width)
        {
            return new PillButton { Text = text, Font = Fonts.ButtonSmall, Size = Sz(width, 36), BackColor = Theme.Bg };
        }

        void Section(Panel p, string title, ref float y)
        {
            p.Controls.Add(new Label { Text = title, Font = Fonts.SmallBold, ForeColor = Theme.Violet, BackColor = Theme.Bg, AutoSize = true, Location = Pt(24, y + 16) });
            y += 38;
        }

        Label Row(Panel p, string title, string desc, ref float y, bool singleLine, params Control[] right)
        {
            float rightW = right.Sum(c => c.Width / S) + 8 * (right.Length - 1);
            float rowW = W - 48 - 20;
            float textW = rowW - rightW - 28;
            int descH = singleLine ? (int)(20 * S)
                : TextRenderer.MeasureText(desc, Fonts.Small, new Size((int)(textW * S), 0), TextFormatFlags.WordBreak).Height;
            float rowH = Math.Max(40 + descH / S + 14, 70);

            p.Controls.Add(new Label { Text = title, Font = Fonts.BodyBold, ForeColor = Theme.Text, BackColor = Theme.Bg, AutoSize = false, Location = Pt(24, y + 12), Size = Sz(textW, 24) });
            var d = new Label
            {
                Text = desc, Font = Fonts.Small, ForeColor = Theme.Muted, BackColor = Theme.Bg, AutoSize = false, AutoEllipsis = singleLine,
                Location = Pt(24, y + 38), Size = new Size((int)(textW * S), descH + 2)
            };
            p.Controls.Add(d);
            float x = 24 + rowW;
            for (int i = right.Length - 1; i >= 0; i--)
            {
                var c = right[i];
                x -= c.Width / S;
                c.Location = Pt(x, y + (rowH - c.Height / S) / 2);
                c.BackColor = Theme.Bg;
                p.Controls.Add(c);
                x -= 8;
            }
            p.Controls.Add(new Panel { BackColor = Theme.Hex(0x24284E), Location = Pt(24, y + rowH - 1), Size = Sz(rowW, 1) });
            y += rowH;
            return d;
        }

        void SaveSetting(Action<Settings> change)
        {
            try
            {
                var s = Settings.Load();
                change(s);
                s.Save();
                savedAt = clock.ElapsedMilliseconds;
                if (settingsChanged != null) settingsChanged();
            }
            catch (Exception ex)
            {
                ThemedDialog.Alert(this, "Couldn't save settings", ex.Message);
            }
        }

        void PickBackupRoot()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Where should new drone folders be created?", SelectedPath = Settings.Load().BackupRoot })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                SaveSetting(x => x.BackupRoot = dlg.SelectedPath);
                backupRootDesc.Text = dlg.SelectedPath;
            }
        }

        void PickLaunchTarget()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "What should open after a backup?",
                Filter = "Apps and shortcuts (*.exe;*.lnk)|*.exe;*.lnk|All files (*.*)|*.*",
                DereferenceLinks = false, // keep the shortcut itself, so web-app shortcuts keep their arguments
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Programs)
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                SaveSetting(x => x.BetaflightLaunchPath = dlg.FileName);
                launchPathDesc.Text = FriendlyLaunch(dlg.FileName);
            }
        }

        static string FriendlyLaunch(string target)
        {
            if (string.IsNullOrEmpty(target)) return "Auto-detect";
            if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return Path.GetFileNameWithoutExtension(target) + "  (shortcut)";
            return target;
        }

        // ------------------------------------------------------------------ log page

        Panel BuildLogPage()
        {
            var p = new Panel { BackColor = Theme.Bg };
            p.Controls.Add(new Label { Text = "Flight log", Font = Fonts.Heading, ForeColor = Theme.Text, BackColor = Theme.Bg, AutoSize = true, Location = Pt(22, 0) });
            p.Controls.Add(new Label { Text = "Everything I've been up to, newest at the bottom.", Font = Fonts.Small, ForeColor = Theme.Muted, BackColor = Theme.Bg, AutoSize = true, Location = Pt(24, 34) });

            var clear = new PillButton { Text = "Clear", Font = Fonts.Button, Bounds = Rectangle.Round(R(W - 24 - 96, 4, 96, 42)) };
            clear.Click += (o, e) =>
            {
                if (!ThemedDialog.Confirm(this, "Clear the flight log?", "This wipes the log history. Your backups aren't touched.", "Clear it", true)) return;
                try { File.WriteAllText(AppPaths.Log, ""); } catch { }
                RefreshLog(true);
            };
            var open = new PillButton { Text = "Open file", Font = Fonts.Button, Bounds = Rectangle.Round(R(W - 24 - 96 - 10 - 120, 4, 120, 42)) };
            open.Click += (o, e) => { try { Process.Start("notepad.exe", "\"" + AppPaths.Log + "\""); } catch { } };
            p.Controls.Add(clear);
            p.Controls.Add(open);

            logFilter = new Segmented(S, new[] { "Everything", "Backups", "Problems" }) { Bounds = Rectangle.Round(R(24, 64, 330, 36)) };
            logFilter.Changed += (o, e) => RefreshLog(true);
            p.Controls.Add(logFilter);

            var frame = new RoundedFrame(S) { Bounds = Rectangle.Round(R(24, 112, W - 48, H - PageTop - 112 - 20)) };
            logBox = new RichTextBox
            {
                BorderStyle = BorderStyle.None,
                ReadOnly = true,
                BackColor = Theme.Panel,
                ForeColor = Theme.Text,
                Font = new Font("Consolas", 9.5f),
                DetectUrls = false,
                Dock = DockStyle.Fill,
                ScrollBars = RichTextBoxScrollBars.Vertical
            };
            DarkScroll.Apply(logBox);
            frame.Controls.Add(logBox);
            p.Controls.Add(frame);
            return p;
        }

        void RefreshLog(bool force)
        {
            string[] lines;
            string sig;
            try
            {
                var fi = new FileInfo(AppPaths.Log);
                sig = fi.Exists ? fi.Length + "/" + fi.LastWriteTimeUtc.Ticks : "none";
                sig += "/" + logFilter.Selected;
                if (!force && sig == logSignature) return;
                if (fi.Exists)
                    using (var fs = new FileStream(AppPaths.Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(fs))
                        lines = reader.ReadToEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                else lines = new string[0];
            }
            catch { return; }
            logSignature = sig;

            var colors = new[] { Theme.Muted, Theme.Text, Theme.Mint, Theme.Pink, Theme.Amber, Theme.Sky, Theme.Violet };
            var rtf = new StringBuilder(@"{\rtf1\ansi\deff0{\fonttbl{\f0 Consolas;}}{\colortbl ;");
            foreach (var c in colors) rtf.AppendFormat(@"\red{0}\green{1}\blue{2};", c.R, c.G, c.B);
            rtf.Append(@"}\f0\fs19\sl300\slmult1 ");

            int shown = 0;
            foreach (var line in lines.Skip(Math.Max(0, lines.Length - 800)))
            {
                string time = line.Length > 21 ? line.Substring(5, 14) : "";
                string msg = line.Length > 21 ? line.Substring(21) : line;
                int color = LogColor(msg);
                if (logFilter.Selected == 1 && !IsBackupLine(msg)) continue;
                if (logFilter.Selected == 2 && color != 4 && color != 5) continue;
                rtf.Append(@"\cf1 ").Append(Rtf(time)).Append("   ").Append(@"\cf").Append(color).Append(' ').Append(Rtf(msg)).Append(@"\par ");
                shown++;
            }
            if (shown == 0)
                rtf.Append(@"\cf1 ").Append(logFilter.Selected == 2 ? "No problems. Clear skies!" : "Nothing here yet. It's been a quiet day on the bench.").Append(@"\par ");
            rtf.Append('}');
            logBox.Rtf = rtf.ToString();
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
        }

        // 1-based colour table index: 1 muted, 2 text, 3 mint, 4 pink, 5 amber, 6 sky, 7 violet
        static int LogColor(string msg)
        {
            var m = msg.ToLowerInvariant();
            if (m.Contains("saved ")) return 3;
            if (m.Contains("failed") || m.Contains("couldn't") || m.Contains("error") || m.Contains("incomplete") || m.Contains("invalid")) return 4;
            if (m.Contains("in use") || m.Contains("stuck")) return 5;
            if (m.Contains("plugged in")) return 6;
            if (m.StartsWith("started") || m.StartsWith("exited") || m.Contains("launched") || m.Contains("betaflight closed")) return 7;
            if (m.Contains("came back") || m.Contains("ignoring") || m.Contains("not backed up") || m.Contains("skipped") || m.Contains("unchanged") || m.Contains("never back up")) return 1;
            return 2;
        }

        static bool IsBackupLine(string msg)
        {
            var m = msg.ToLowerInvariant();
            return m.Contains("saved ") || m.Contains("plugged in") || m.Contains("unchanged") || m.Contains(": read ");
        }

        static string Rtf(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '\\' || c == '{' || c == '}') sb.Append('\\').Append(c);
                else if (c > 127) sb.Append(@"\u").Append((int)(short)c).Append('?');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ header + tabs

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            float t = clock.ElapsedMilliseconds / 1000f;
            g.Clear(Theme.Bg);
            sky.Paint(g, headerRect, t, S);

            float bob = (float)Math.Sin(t * 2.2) * 3;
            float phase = (t + 1.8f) % 3.9f;
            float blink = phase < 0.14f ? (float)Math.Sin(phase / 0.14f * Math.PI) : 0;
            using (var shadow = new SolidBrush(Theme.Alpha(Theme.Ink, 110)))
                g.FillEllipse(shadow, 34 * S - bob * S, 112 * S, 76 * S + bob * 2 * S, 8 * S);
            QuadArt.Draw(g, 72 * S, (62 + bob) * S, 104 * S, propAngle, (float)Math.Sin(t * 1.4) * 5, blink, true, Theme.Sky);

            Theme.DrawText(g, "Quad Backup", Fonts.Title, R(140, 18, 420, 42), Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            float pulse = (float)(0.5 + 0.5 * Math.Sin(t * 3));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var glow = new SolidBrush(Theme.Alpha(Theme.Mint, (int)(70 * pulse)))) g.FillEllipse(glow, 140 * S, 69 * S, 14 * S, 14 * S);
            using (var dot = new SolidBrush(Theme.Mint)) g.FillEllipse(dot, 143.5f * S, 72.5f * S, 7 * S, 7 * S);
            Theme.DrawText(g, "On patrol  ·  watching USB for quads", Fonts.Status, R(162, 64, 480, 22), Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            Theme.DrawText(g, lastBackupText, Fonts.Small, R(162, 88, 520, 20), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

            WindowButton(g, minRect, hover == "min", true);
            WindowButton(g, closeRect, hover == "close", false);

            // tabs
            float x = 24 * S;
            tabRects.Clear();
            foreach (Page p in Enum.GetValues(typeof(Page)))
            {
                string label = p == Page.Hangar ? "Hangar" : (p == Page.Settings ? "Settings" : "Flight log");
                bool badged = p == Page.Hangar && droneCount > 0;
                float w = Theme.Measure(g, label, Fonts.Tab) + (badged ? 66 : 40) * S;
                var r = new RectangleF(x, 138 * S, w, 36 * S);
                tabRects[p] = r;
                bool sel = p == page, hot = hover == "tab" + p;
                using (var path = Theme.Round(r, r.Height / 2))
                {
                    if (sel)
                    {
                        using (var fill = new LinearGradientBrush(r, Theme.Violet, Theme.Hex(0x6A5CF0), 90f)) g.FillPath(fill, path);
                    }
                    else if (hot)
                    {
                        using (var fill = new SolidBrush(Theme.Panel)) g.FillPath(fill, path);
                    }
                }
                var textRect = r;
                var textFlags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
                if (badged)
                {
                    var badge = new RectangleF(r.Right - 16 * S - 24 * S, r.Y + 8 * S, 24 * S, 20 * S);
                    using (var path = Theme.Round(badge, badge.Height / 2))
                    using (var fill = new SolidBrush(sel ? Theme.Alpha(Color.White, 60) : Theme.PanelHi))
                        g.FillPath(fill, path);
                    Theme.DrawText(g, droneCount.ToString(), Fonts.SmallBold, badge, sel ? Color.White : Theme.Text, textFlags);
                    textRect = new RectangleF(r.X + 20 * S, r.Y, badge.X - r.X - 20 * S, r.Height);
                    textFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
                }
                Theme.DrawText(g, label, Fonts.Tab, textRect, sel ? Color.White : (hot ? Theme.Text : Theme.Muted), textFlags);
                x += w + 8 * S;
            }

            // "Saved" flash after a settings change
            float fade = 1 - (clock.ElapsedMilliseconds - savedAt) / 2200f;
            if (fade > 0)
            {
                int a = (int)(255 * Math.Min(1, fade * 2.5f));
                var r = R(W - 24 - 96, 142, 96, 28);
                using (var path = Theme.Round(r, r.Height / 2))
                using (var fill = new SolidBrush(Theme.Alpha(Theme.Mint, a / 6)))
                    g.FillPath(fill, path);
                Theme.DrawText(g, "✓  Saved", Fonts.SmallBold, r, Theme.Alpha(Theme.Mint, a), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }

            using (var line = new Pen(Theme.Alpha(Theme.Line, 150), 1)) g.DrawLine(line, 24 * S, 182 * S, (W - 24) * S, 182 * S);
        }

        void WindowButton(Graphics g, RectangleF r, bool hot, bool minimize)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bubble = new SolidBrush(Theme.Alpha(Color.White, hot ? 50 : 18))) g.FillEllipse(bubble, r);
            using (var pen = new Pen(Theme.Alpha(Color.White, hot ? 255 : 190), 1.8f * S) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                var c = new PointF(r.X + r.Width / 2, r.Y + r.Height / 2);
                float k = 5 * S;
                if (minimize) g.DrawLine(pen, c.X - k, c.Y, c.X + k, c.Y);
                else
                {
                    g.DrawLine(pen, c.X - k, c.Y - k, c.X + k, c.Y + k);
                    g.DrawLine(pen, c.X + k, c.Y - k, c.X - k, c.Y + k);
                }
            }
        }

        string HitTest(Point p)
        {
            if (closeRect.Contains(p)) return "close";
            if (minRect.Contains(p)) return "min";
            foreach (var kv in tabRects) if (kv.Value.Contains(p)) return "tab" + kv.Key;
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var h = HitTest(e.Location);
            if (h != hover)
            {
                hover = h;
                Cursor = h != null ? Cursors.Hand : Cursors.Default;
                Invalidate(Rectangle.Ceiling(R(0, 0, W, PageTop)));
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = null;
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && HitTest(e.Location) == null) Native.DragWindow(this);
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            var h = HitTest(e.Location);
            if (h == "close") Hide();
            else if (h == "min") WindowState = FormWindowState.Minimized;
            else if (h != null && h.StartsWith("tab")) ShowPage((Page)Enum.Parse(typeof(Page), h.Substring(3)));
            base.OnMouseUp(e);
        }

        static void OpenFolder(string folder)
        {
            try { Directory.CreateDirectory(folder); Process.Start("explorer.exe", "\"" + folder + "\""); } catch { }
        }

        // self-test hooks
        internal void PreviewPage(Page p) { ShowPage(p); RefreshData(true); }
    }
}
