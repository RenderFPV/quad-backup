// Mac/Linux windows: the animated mascot, themed dialogs, pop-up notifications and the new-drone window.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace QuadBackup
{
    // The quad with a face, hovering and blinking. Mood picks how it moves.
    class Mascot : Control
    {
        public enum Mood { Calm, Happy, Nervous, Dizzy, Spin, Asleep }
        readonly double size;
        readonly Stopwatch clock = Stopwatch.StartNew();
        DispatcherTimer timer;
        double prop;
        public Color Body = Palette.Sky;
        public Mood Kind = Mood.Calm;
        public bool Sparkle;
        public bool Badge;  // draw a soft coloured circle behind it

        public Mascot(double size, double box)
        {
            this.size = size;
            Width = box;
            Height = box;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (s, a) => { prop += 22; InvalidateVisual(); });
            timer.Start();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (timer != null) timer.Stop();
            base.OnDetachedFromVisualTree(e);
        }

        public override void Render(DrawingContext g)
        {
            double t = clock.ElapsedMilliseconds / 1000.0;
            var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
            if (Badge)
            {
                double r = Bounds.Width / 2 - 2;
                g.DrawEllipse(Palette.Brush(Palette.Alpha(Body, 40)), new Pen(Palette.Brush(Palette.Alpha(Body, 140)), 1.5), c, r, r);
            }
            double tilt = 0, bob = 0;
            switch (Kind)
            {
                case Mood.Happy: tilt = Math.Sin(t * 3) * 8; bob = -Math.Abs(Math.Sin(t * 4)) * 3; break;
                case Mood.Nervous: tilt = Math.Sin(t * 5) * 12; break;
                case Mood.Dizzy: tilt = 22 + Math.Sin(t * 11) * 5; break;
                case Mood.Spin: tilt = (t * 140) % 360; bob = -Math.Sin(t * 3) * 3; break;
                case Mood.Asleep: tilt = 8; break;
                default: tilt = Math.Sin(t * 1.6) * 5; bob = Math.Sin(t * 2.2) * 2; break;
            }
            double phase = (t + 1.6) % 3.4;
            double blink = Kind == Mood.Asleep ? 1 : (phase < 0.14 ? Math.Sin(phase / 0.14 * Math.PI) : 0);
            QuadArt.Draw(g, new Point(c.X, c.Y + bob), size, Kind == Mood.Asleep ? 15 : prop, tilt, blink, true, Body, Kind == Mood.Dizzy);
            if (Sparkle) QuadArt.Sparkles(g, c, size * 0.6, t);
        }
    }

    static class WindowKit
    {
        public static void Dress(Window w, string title, double width)
        {
            w.Title = title;
            w.Width = width;
            w.SizeToContent = SizeToContent.Height;
            w.CanResize = false;
            w.Background = Palette.Brush(Palette.Panel);
            w.Icon = QuadArt.ClassicIcon(64);
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.Topmost = true;
        }

        public static TextBlock Label(string text, double size, Color color, FontWeight weight = FontWeight.Normal, bool wrap = false)
        {
            return new TextBlock
            {
                Text = text, FontSize = size, Foreground = Palette.Brush(color), FontWeight = weight,
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis
            };
        }

        public static TextBox Input(string text)
        {
            return new TextBox
            {
                Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Palette.Brush(Palette.Text),
                Background = Palette.Brush(Palette.Bg), BorderBrush = Palette.Brush(Palette.Line), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 9)
            };
        }

        // Shows a window and waits for it to close; modal when there's an owner (from the tray there isn't one).
        public static Task ShowAndWait(Window w, Window owner)
        {
            if (owner != null && owner.IsVisible) return w.ShowDialog(owner);
            var done = new TaskCompletionSource<bool>();
            w.Closed += (s, e) => done.TrySetResult(true);
            w.Show();
            w.Activate();
            return done.Task;
        }

        public static async Task<string> PickFolder(Window owner, string title, string start)
        {
            var top = owner ?? (Application.Current.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Windows.FirstOrDefault();
            if (top == null) return null;
            var sp = top.StorageProvider;
            IStorageFolder startFolder = null;
            try { if (!string.IsNullOrEmpty(start) && Directory.Exists(start)) startFolder = await sp.TryGetFolderFromPathAsync(start); } catch { }
            var picked = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false, SuggestedStartLocation = startFolder });
            return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
        }
    }

    // ------------------------------------------------------------------------------------------------ dialogs

    class ThemedDialog : Window
    {
        readonly TextBox input;
        bool ok;

        ThemedDialog(string title, string message, string initial, string okText, string cancelText, bool danger, bool special)
        {
            WindowKit.Dress(this, title, 460);
            var mascot = new Mascot(56, 76)
            {
                Body = special ? Palette.Gold : danger ? Palette.Pink : Palette.Sky,
                Kind = danger ? Mascot.Mood.Nervous : Mascot.Mood.Calm,
                Sparkle = special,
                VerticalAlignment = VerticalAlignment.Top
            };
            var text = new StackPanel { Spacing = 6, Margin = new Thickness(6, 4, 0, 0) };
            text.Children.Add(WindowKit.Label(title, 18, Palette.Text, FontWeight.Black));
            text.Children.Add(WindowKit.Label(message, 13.5, Palette.Muted, wrap: true));
            var top = new DockPanel();
            DockPanel.SetDock(mascot, Dock.Left);
            top.Children.Add(mascot);
            top.Children.Add(text);

            var root = new StackPanel { Margin = new Thickness(22, 18, 22, 20), Spacing = 16 };
            root.Children.Add(top);
            if (initial != null)
            {
                input = WindowKit.Input(initial);
                root.Children.Add(input);
            }
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
            if (cancelText != null)
            {
                var cancel = new PillButton(cancelText, PillButton.Kind.Secondary, 112, 42);
                cancel.Click += (s, e) => Close();
                buttons.Children.Add(cancel);
            }
            var okButton = new PillButton(okText, special ? PillButton.Kind.Special : PillButton.Kind.Primary, 140, 42);
            okButton.Click += (s, e) => { ok = true; Close(); };
            buttons.Children.Add(okButton);
            root.Children.Add(buttons);
            Content = root;

            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { ok = true; Close(); }
                else if (e.Key == Key.Escape) Close();
            };
            Opened += (s, e) => { if (input != null) { input.Focus(); input.SelectAll(); } };
        }

        public static async Task<string> Prompt(Window owner, string title, string message, string initial, string okText)
        {
            var d = new ThemedDialog(title, message, initial, okText, "Cancel", false, false);
            await WindowKit.ShowAndWait(d, owner);
            return d.ok ? d.input.Text.Trim() : null;
        }

        public static async Task<string> PromptSpecial(Window owner, string droneName)
        {
            var d = new ThemedDialog("★ Special backup" + (droneName != null ? " for " + droneName : ""),
                "Name this one so you can find it later, like \"locked in freestyle\" or \"before new props\". It's kept even if nothing changed.",
                "", "★ Save it", "Cancel", false, true);
            await WindowKit.ShowAndWait(d, owner);
            if (!d.ok) return null;
            var name = d.input.Text.Trim();
            return name.Length > 0 ? name : "special";
        }

        public static async Task<bool> Confirm(Window owner, string title, string message, string okText, bool danger)
        {
            var d = new ThemedDialog(title, message, null, okText, "Cancel", danger, false);
            await WindowKit.ShowAndWait(d, owner);
            return d.ok;
        }

        public static Task Alert(Window owner, string title, string message)
        {
            return WindowKit.ShowAndWait(new ThemedDialog(title, message, null, "Got it", null, true, false), owner);
        }
    }

    // ------------------------------------------------------------------------------------------------ pop-ups

    class Toast : Window
    {
        const int LifeMs = 7000;
        static readonly List<Toast> Open = new List<Toast>();
        readonly Action onClick;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly DispatcherTimer timer;
        readonly Fuse fuse;
        double lived, closingFor = -1, targetY = double.NaN, currentY;
        bool hovering;

        class Fuse : Control
        {
            public double Left = 1;
            public Color Colour;
            public override void Render(DrawingContext g) { g.DrawRectangle(Palette.Brush(Colour), null, new Rect(0, 0, Bounds.Width * Left, Bounds.Height)); }
        }

        public static void Pop(ToastKind kind, string title, string body, Action onClick)
        {
            var t = new Toast(kind, title, body, onClick);
            Open.Add(t);
            foreach (var old in Open.Where(x => x.closingFor < 0).Reverse().Skip(3).ToList()) old.closingFor = 0;
            t.Show();
        }

        static Color Accent(ToastKind kind)
        {
            switch (kind)
            {
                case ToastKind.Success: return Palette.Mint;
                case ToastKind.Warning: return Palette.Amber;
                case ToastKind.Error: return Palette.Pink;
                case ToastKind.Special: return Palette.Gold;
                default: return Palette.Sky;
            }
        }

        Toast(ToastKind kind, string title, string body, Action onClick)
        {
            this.onClick = onClick;
            SystemDecorations = SystemDecorations.None;
            Topmost = true;
            ShowActivated = false;
            ShowInTaskbar = false;
            CanResize = false;
            Width = 380;
            SizeToContent = SizeToContent.Height;
            Background = Palette.Brush(Palette.Panel);
            Opacity = 0;
            Cursor = new Cursor(onClick != null ? StandardCursorType.Hand : StandardCursorType.Arrow);

            var accent = Accent(kind);
            var mood = kind == ToastKind.Success ? Mascot.Mood.Happy : kind == ToastKind.Warning ? Mascot.Mood.Nervous
                     : kind == ToastKind.Error ? Mascot.Mood.Dizzy : kind == ToastKind.Special ? Mascot.Mood.Spin : Mascot.Mood.Calm;
            var mascot = new Mascot(48, 64) { Body = accent, Kind = mood, Badge = true, Sparkle = kind == ToastKind.Special, VerticalAlignment = VerticalAlignment.Top };
            var text = new StackPanel { Spacing = 4, Margin = new Thickness(12, 2, 26, 0) };
            text.Children.Add(WindowKit.Label(title, 14, Palette.Text, FontWeight.Bold));
            text.Children.Add(WindowKit.Label(body, 12.5, Palette.Muted, wrap: true));
            var row = new DockPanel { Margin = new Thickness(14, 14, 12, 14) };
            DockPanel.SetDock(mascot, Dock.Left);
            row.Children.Add(mascot);
            row.Children.Add(text);

            var close = new LinkText("✕", Palette.Muted, 13) { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 12, 0) };
            close.Click += (s, e) => closingFor = 0;
            fuse = new Fuse { Height = 3, Colour = accent, VerticalAlignment = VerticalAlignment.Bottom };
            var grid = new Grid();
            grid.Children.Add(row);
            grid.Children.Add(close);
            grid.Children.Add(fuse);
            Content = new Border { BorderBrush = Palette.Brush(Palette.Line), BorderThickness = new Thickness(1), Child = grid };

            PointerEntered += (s, e) => hovering = true;
            PointerExited += (s, e) => hovering = false;
            PointerReleased += (s, e) =>
            {
                if (e.Source == close) return;
                if (this.onClick != null) { try { this.onClick(); } catch { } }
                closingFor = 0;
            };
            timer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Render, (s, e) => Tick());
            Opened += (s, e) => { Reflow(); currentY = targetY + (AppPaths.IsMac ? -20 : 20); timer.Start(); };
            Closed += (s, e) => { timer.Stop(); Open.Remove(this); Reflow(); };
        }

        // Mac notifications live top-right; on Linux bottom-right is the usual spot.
        static void Reflow()
        {
            var first = Open.FirstOrDefault(t => t.Screens != null && t.Screens.Primary != null);
            if (first == null) return;
            var screen = first.Screens.Primary;
            double scale = screen.Scaling;
            var area = screen.WorkingArea;
            if (AppPaths.IsMac)
            {
                double y = area.Y + 14 * scale;
                for (int i = Open.Count - 1; i >= 0; i--) { Open[i].targetY = y; y += Open[i].Bounds.Height * scale + 10 * scale; }
            }
            else
            {
                double y = area.Bottom;
                for (int i = Open.Count - 1; i >= 0; i--) { y -= Open[i].Bounds.Height * scale + 14 * scale; Open[i].targetY = y; }
            }
            foreach (var t in Open) t.MoveTo(area.Right - (t.Bounds.Width + 14) * scale);
        }

        double x;
        void MoveTo(double newX) { x = newX; }

        void Tick()
        {
            double dt = 30;
            if (closingFor >= 0)
            {
                closingFor += dt;
                Opacity = Math.Max(0, 1 - closingFor / 220);
                if (closingFor >= 220) { Close(); return; }
            }
            else
            {
                if (!hovering) lived += dt;
                if (lived >= LifeMs) closingFor = 0;
                Opacity = Math.Min(1, clock.ElapsedMilliseconds / 200.0);
            }
            if (!double.IsNaN(targetY))
            {
                currentY += (targetY - currentY) * 0.25;
                Position = new PixelPoint((int)x, (int)Math.Round(currentY));
            }
            fuse.Left = closingFor >= 0 ? 0 : 1 - Math.Min(1, lived / LifeMs);
            fuse.InvalidateVisual();
        }
    }

    // ------------------------------------------------------------------------------------------------ new drone window

    class SkyHeader : Control
    {
        readonly SkyScene sky = new SkyScene(7, 30);
        readonly Confetti confetti = new Confetti();
        readonly Stopwatch clock = Stopwatch.StartNew();
        DispatcherTimer timer;
        double prop;
        long celebrateAt = -1;

        public SkyHeader() { Height = 178; }

        public void Celebrate()
        {
            celebrateAt = clock.ElapsedMilliseconds;
            confetti.Burst(Bounds.Width / 2, 90, 110);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (s, a) =>
            {
                prop += celebrateAt >= 0 ? 41 : 23;
                confetti.Step(Bounds.Height + 300);
                InvalidateVisual();
            });
            timer.Start();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (timer != null) timer.Stop();
            base.OnDetachedFromVisualTree(e);
        }

        public override void Render(DrawingContext g)
        {
            double t = clock.ElapsedMilliseconds / 1000.0;
            var r = new Rect(Bounds.Size);
            using (g.PushClip(r))
            {
                sky.Paint(g, r, t);
                double bob = Math.Sin(t * 2.2) * 4, tilt = Math.Sin(t * 1.4) * 5;
                if (celebrateAt >= 0)
                {
                    double p = Math.Min(1, (clock.ElapsedMilliseconds - celebrateAt) / 750.0);
                    tilt = (1 - Math.Pow(1 - p, 3)) * 360;
                    bob = -Math.Sin(p * Math.PI) * 30;
                }
                double phase = (t + 1.8) % 3.7;
                double blink = phase < 0.14 ? Math.Sin(phase / 0.14 * Math.PI) : 0;
                double sw = 74 + bob * 1.2;
                g.DrawEllipse(Palette.Brush(Palette.Alpha(Palette.Ink, 110)), null, new Point(r.Width / 2, 162), sw / 2, 4.5);
                QuadArt.Draw(g, new Point(r.Width / 2, 90 + bob), 122, prop, tilt, celebrateAt >= 0 ? 0 : blink, true, Palette.Sky);
                confetti.Paint(g);
            }
        }
    }

    class NewDroneWindow : Window
    {
        static readonly string[] Hellos = { "A wild quad appears!", "New quad on the bench!", "Ooh, a new one!", "Well hello there, flyer!" };
        readonly SkyHeader header = new SkyHeader();
        readonly TextBlock headline, subline, location, preview;
        readonly TextBox nameBox;
        readonly PillButton home, existing;
        string folderParent;
        bool done;
        public NewDroneChoice Result;

        public NewDroneWindow(FcBackup b, string port, string suggestedName, string location, string note, DroneMood mood)
        {
            WindowKit.Dress(this, "Quad Backup - new drone", 540);
            Background = Palette.Brush(Palette.Bg);
            folderParent = location;

            string head, sub;
            var craft = string.IsNullOrEmpty(b.CraftName) ? null : b.CraftName;
            switch (mood)
            {
                case DroneMood.NewFlightController:
                    head = "Is that you, " + suggestedName + "?";
                    sub = "Same name, new flight controller. Keep the name to add to its history.";
                    break;
                case DroneMood.MissingFolder:
                    head = "Hey, where'd my folder go?";
                    sub = suggestedName + "'s backup folder vanished. Let's give it a new one.";
                    break;
                default:
                    head = Hellos[new Random().Next(Hellos.Length)];
                    sub = craft != null ? "Meet " + craft + ". Let's give it a home for its backups." : "No craft name set, so let's call it something nice.";
                    break;
            }

            var body = new StackPanel { Margin = new Thickness(26, 10, 26, 22), Spacing = 10 };
            headline = WindowKit.Label(head, 25, Palette.Text, FontWeight.Black);
            headline.HorizontalAlignment = HorizontalAlignment.Center;
            subline = WindowKit.Label(sub, 14, Palette.Muted);
            subline.HorizontalAlignment = HorizontalAlignment.Center;
            body.Children.Add(headline);
            body.Children.Add(subline);

            var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 6) };
            chips.Children.Add(Chip(Palette.Amber, b.Board ?? "unknown board"));
            chips.Children.Add(Chip(Palette.Mint, b.Firmware ?? b.Variant ?? "firmware?"));
            chips.Children.Add(Chip(Palette.Sky, Path.GetFileName(port)));
            body.Children.Add(chips);

            if (!string.IsNullOrEmpty(note))
            {
                body.Children.Add(new Border
                {
                    Background = Palette.Brush(Palette.Alpha(Palette.Amber, 28)), BorderBrush = Palette.Brush(Palette.Alpha(Palette.Amber, 110)),
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10),
                    Child = WindowKit.Label(note, 12.5, Palette.Rgb(0xFFD27A), wrap: true)
                });
            }

            body.Children.Add(WindowKit.Label("FOLDER NAME", 11, Palette.Muted, FontWeight.Bold));
            nameBox = WindowKit.Input(suggestedName);
            nameBox.TextChanged += (s, e) => UpdatePreview();
            body.Children.Add(nameBox);

            var locRow = new DockPanel();
            var change = new LinkText("change", Palette.Sky);
            change.Click += async (s, e) =>
            {
                var picked = await WindowKit.PickFolder(this, "Where should this drone's backup folder be created?", folderParent);
                if (picked != null) { folderParent = picked; this.location.Text = "in  " + picked; }
            };
            DockPanel.SetDock(change, Dock.Right);
            locRow.Children.Add(change);
            this.location = WindowKit.Label("in  " + location, 12.5, Palette.Muted);
            locRow.Children.Add(this.location);
            body.Children.Add(locRow);

            preview = WindowKit.Label("", 12.5, Palette.Mint);
            preview.FontFamily = new FontFamily("Menlo, DejaVu Sans Mono, monospace");
            body.Children.Add(preview);
            UpdatePreview();

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            existing = new PillButton("Use existing folder…", PillButton.Kind.Secondary, 196, 44);
            existing.Click += async (s, e) =>
            {
                var picked = await WindowKit.PickFolder(this, "Pick the folder to save this drone's backups in", folderParent);
                if (picked != null) Celebrate(new NewDroneChoice { Folder = picked });
            };
            home = new PillButton("Give it a home!", PillButton.Kind.Primary, 176, 44);
            home.Click += (s, e) => GiveItAHome();
            buttons.Children.Add(existing);
            buttons.Children.Add(home);
            body.Children.Add(buttons);

            var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
            var notNow = new LinkText("not now", Palette.Muted, 12.5, FontWeight.Normal);
            notNow.Click += (s, e) => Finish(null);
            var never = new LinkText("never back up this quad", Palette.Muted, 12.5, FontWeight.Normal);
            never.Click += (s, e) => Finish(new NewDroneChoice { Never = true });
            links.Children.Add(notNow);
            links.Children.Add(WindowKit.Label("·", 12.5, Palette.Muted));
            links.Children.Add(never);
            body.Children.Add(links);

            var root = new StackPanel();
            root.Children.Add(header);
            root.Children.Add(body);
            Content = root;

            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) GiveItAHome();
                else if (e.Key == Key.Escape) Finish(null);
            };
            Opened += (s, e) => { Activate(); nameBox.Focus(); nameBox.SelectAll(); };
        }

        static Border Chip(Color dot, string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            row.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = Palette.Brush(dot), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(WindowKit.Label(text, 12, Palette.Text, FontWeight.SemiBold));
            return new Border
            {
                Background = Palette.Brush(Palette.Panel), BorderBrush = Palette.Brush(Palette.Line), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(13), Padding = new Thickness(11, 4, 12, 4), Child = row
            };
        }

        void UpdatePreview()
        {
            preview.Text = "saves as " + BackupFiles.SafeName(nameBox.Text.Trim()) + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt";
        }

        void GiveItAHome()
        {
            if (done) return;
            var name = nameBox.Text.Trim();
            if (name.Length == 0) { nameBox.Focus(); return; }
            Celebrate(new NewDroneChoice { Folder = Path.Combine(folderParent, BackupFiles.SafeName(name)) });
        }

        // The quad does a happy flip, confetti flies, then the window closes.
        void Celebrate(NewDroneChoice choice)
        {
            if (done) return;
            done = true;
            Result = choice;
            headline.Text = "Welcome home, " + Path.GetFileName(choice.Folder.TrimEnd('/', '\\')) + "!";
            subline.Text = "First backup going in now. Every plug-in adds another.";
            home.IsEnabled = existing.IsEnabled = false;
            nameBox.IsReadOnly = true;
            header.Celebrate();
            DispatcherTimer.RunOnce(Close, TimeSpan.FromMilliseconds(1250));
        }

        void Finish(NewDroneChoice choice)
        {
            if (done) return;
            done = true;
            Result = choice;
            Close();
        }
    }
}
