// The Mac/Linux main window: Hangar (drone list), Settings and Flight log, under a starry header with the mascot.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace QuadBackup
{
    class MainHeader : Control
    {
        readonly SkyScene sky = new SkyScene(11, 34);
        readonly Stopwatch clock = Stopwatch.StartNew();
        double prop;
        public string LastBackup = "";

        public MainHeader() { Height = 128; }

        public void Tick() { prop += 20; InvalidateVisual(); }

        public override void Render(DrawingContext g)
        {
            double t = clock.ElapsedMilliseconds / 1000.0;
            var r = new Rect(Bounds.Size);
            using (g.PushClip(r))
            {
                sky.Paint(g, r, t);
                double bob = Math.Sin(t * 2.2) * 3, phase = (t + 1.8) % 3.9;
                double blink = phase < 0.14 ? Math.Sin(phase / 0.14 * Math.PI) : 0;
                g.DrawEllipse(Palette.Brush(Palette.Alpha(Palette.Ink, 110)), null, new Point(72, 116), 38 + bob, 4);
                QuadArt.Draw(g, new Point(72, 62 + bob), 104, prop, Math.Sin(t * 1.4) * 5, blink, true, Palette.Sky);
                Txt.At(g, "Quad Backup", 28, Palette.Text, 140, 18, FontWeight.Black);
                double pulse = 0.5 + 0.5 * Math.Sin(t * 3);
                g.DrawEllipse(Palette.Brush(Palette.Alpha(Palette.Mint, (int)(70 * pulse))), null, new Point(147, 76), 7, 7);
                g.DrawEllipse(Palette.Brush(Palette.Mint), null, new Point(147, 76), 3.5, 3.5);
                Txt.At(g, "On patrol  ·  watching USB for quads", 13.5, Palette.Text, 162, 66, FontWeight.SemiBold);
                Txt.At(g, LastBackup, 12.5, Palette.Muted, 162, 89, FontWeight.Normal, r.Width - 190);
            }
        }
    }

    class TabPill : Control
    {
        readonly string text;
        bool hover;
        public bool Selected;
        public int Badge;
        public event EventHandler Click;

        public TabPill(string text) { this.text = text; Height = 36; Cursor = new Cursor(StandardCursorType.Hand); Measure(); }

        public void Measure() { Width = Txt.Text(text, 13.5, Palette.Text, FontWeight.SemiBold).Width + (Badge > 0 ? 66 : 40); InvalidateVisual(); }

        protected override void OnPointerEntered(PointerEventArgs e) { hover = true; InvalidateVisual(); base.OnPointerEntered(e); }
        protected override void OnPointerExited(PointerEventArgs e) { hover = false; InvalidateVisual(); base.OnPointerExited(e); }
        protected override void OnPointerReleased(PointerReleasedEventArgs e) { if (Click != null) Click(this, EventArgs.Empty); base.OnPointerReleased(e); }

        public override void Render(DrawingContext g)
        {
            var r = new Rect(Bounds.Size);
            if (Selected) g.DrawRectangle(Palette.Brush(Palette.Violet), null, r, r.Height / 2, r.Height / 2);
            else if (hover) g.DrawRectangle(Palette.Brush(Palette.Panel), null, r, r.Height / 2, r.Height / 2);
            var color = Selected ? Colors.White : (hover ? Palette.Text : Palette.Muted);
            if (Badge > 0)
            {
                var badge = new Rect(r.Right - 40, r.Y + 8, 24, 20);
                g.DrawRectangle(Palette.Brush(Selected ? Palette.Alpha(Colors.White, 60) : Palette.PanelHi), null, badge, 10, 10);
                Txt.Centered(g, Badge.ToString(), 11, Selected ? Colors.White : Palette.Text, badge, FontWeight.Bold);
                var ft = Txt.Text(text, 13.5, color, FontWeight.SemiBold);
                g.DrawText(ft, new Point(r.X + 20, r.Y + (r.Height - ft.Height) / 2));
            }
            else Txt.Centered(g, text, 13.5, color, r, FontWeight.SemiBold);
        }
    }

    class DroneCard : Control
    {
        public readonly Drone Drone;
        public event Action<DroneCard, string> ActionClicked;
        readonly int backups, specials;
        readonly DateTime? last;
        readonly bool folderMissing, pluggedIn;
        readonly string pending;
        readonly Color color;
        readonly List<KeyValuePair<string, Rect>> links = new List<KeyValuePair<string, Rect>>();
        string hover;
        bool hot;

        public DroneCard(Drone d, bool pluggedIn, string pending)
        {
            Drone = d;
            this.pluggedIn = pluggedIn;
            this.pending = pending;
            Height = pending != null ? 140 : 116;
            Margin = new Thickness(0, 0, 0, 12);
            folderMissing = string.IsNullOrEmpty(d.Folder) || !Directory.Exists(d.Folder);
            if (!folderMissing)
            {
                try
                {
                    var files = Directory.GetFiles(d.Folder, "*.txt");
                    backups = files.Length;
                    specials = files.Count(f => Path.GetFileName(f).Contains(BackupFiles.SpecialTag));
                }
                catch { }
            }
            last = HangarWindow.ParseTime(d.LastBackup);
            color = HangarWindow.ColorFor(d.FcId ?? d.Name ?? "");
        }

        protected override void OnPointerEntered(PointerEventArgs e) { hot = true; InvalidateVisual(); base.OnPointerEntered(e); }
        protected override void OnPointerExited(PointerEventArgs e) { hot = false; hover = null; Cursor = Cursor.Default; InvalidateVisual(); base.OnPointerExited(e); }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            var p = e.GetPosition(this);
            string h = null;
            foreach (var l in links) if (l.Value.Contains(p)) h = l.Key;
            if (h != hover)
            {
                hover = h;
                Cursor = h != null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
                InvalidateVisual();
            }
            base.OnPointerMoved(e);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            if (hover != null && ActionClicked != null) ActionClicked(this, hover);
            base.OnPointerReleased(e);
        }

        public override void Render(DrawingContext g)
        {
            var r = new Rect(0.5, 0.5, Bounds.Width - 1, Bounds.Height - 1);
            g.DrawRectangle(Palette.Brush(hot ? Palette.PanelHi : Palette.Panel), new Pen(Palette.Brush(hot ? Palette.Alpha(Palette.Violet, 200) : Palette.Line), 1.2), r, 16, 16);

            g.DrawEllipse(Palette.Brush(Palette.Alpha(Drone.Ignored ? Palette.Muted : color, 34)), null, new Point(52, 54), 36, 36);
            QuadArt.Draw(g, new Point(52, 54), 64, 25 + (Drone.FcId ?? "").Length * 7, Drone.Ignored ? 8 : -6, Drone.Ignored ? 1 : 0, true, Drone.Ignored ? Palette.Muted : color);
            if (Drone.Ignored) Txt.At(g, "z Z", 11, Palette.Muted, 70, 8, FontWeight.Bold);

            double x = 104, rightW = 170;
            var name = string.IsNullOrEmpty(Drone.Name) ? "Unnamed quad" : Drone.Name;
            var nameText = Txt.Text(name, 17, Palette.Text, FontWeight.Bold, Bounds.Width - x - rightW - 130);
            g.DrawText(nameText, new Point(x, 14));
            string chip = Drone.Ignored ? "paused" : (folderMissing ? "folder missing" : "on patrol");
            var chipColor = Drone.Ignored ? Palette.Amber : (folderMissing ? Palette.Pink : Palette.Mint);
            var chipText = Txt.Text(chip, 11, chipColor, FontWeight.Bold);
            var chipRect = new Rect(x + nameText.Width + 12, 18, chipText.Width + 18, 21);
            g.DrawRectangle(Palette.Brush(Palette.Alpha(chipColor, 40)), null, chipRect, 10.5, 10.5);
            g.DrawText(chipText, new Point(chipRect.X + 9, chipRect.Y + (chipRect.Height - chipText.Height) / 2));

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Drone.Board)) parts.Add(Drone.Board);
            if (!string.IsNullOrEmpty(Drone.Firmware)) parts.Add(Drone.Firmware);
            if (!string.IsNullOrEmpty(Drone.CraftName) && Drone.CraftName != Drone.Name) parts.Add("craft name \"" + Drone.CraftName + "\"");
            Txt.At(g, string.Join("  ·  ", parts), 12, Palette.Muted, x, 47, FontWeight.Normal, Bounds.Width - x - rightW - 16);

            double rx = Bounds.Width - 18;
            RightAligned(g, "last backup", 12, Palette.Muted, rx, 14, FontWeight.Normal);
            RightAligned(g, last.HasValue ? HangarWindow.Ago(last.Value) : "never", 14, Palette.Text, rx, 32, FontWeight.Bold);
            var count = backups == 1 ? "1 backup" : backups + " backups";
            if (specials > 0)
            {
                var star = "★ " + specials + " special";
                double sw = RightAligned(g, star, 12, Palette.Gold, rx, 57, FontWeight.Normal);
                RightAligned(g, count + "  ·  ", 12, Palette.Muted, rx - sw, 57, FontWeight.Normal);
            }
            else RightAligned(g, count, 12, Palette.Muted, rx, 57, FontWeight.Normal);

            double ly = 82;
            if (pending != null)
            {
                var note = new Rect(x, 74, Bounds.Width - x - 18, 26);
                g.DrawRectangle(Palette.Brush(Palette.Alpha(Palette.Gold, 34)), null, note, 13, 13);
                Txt.At(g, "★  \"" + pending + "\" is ready: disconnect in Betaflight and I'll save it", 12, Palette.Rgb(0xFFD86B), note.X + 12, note.Y + 5, FontWeight.Bold, note.Width - 24);
                ly = 108;
            }

            links.Clear();
            var items = new List<KeyValuePair<string, string>>();
            if (pluggedIn && !Drone.Ignored) items.Add(new KeyValuePair<string, string>("special", "★ Special backup"));
            items.Add(new KeyValuePair<string, string>("open", "Open folder"));
            items.Add(new KeyValuePair<string, string>("rename", "Rename"));
            items.Add(new KeyValuePair<string, string>("move", "Change folder"));
            items.Add(new KeyValuePair<string, string>("pause", Drone.Ignored ? "Resume backups" : "Pause backups"));
            items.Add(new KeyValuePair<string, string>("forget", "Forget"));
            double lx = x;
            foreach (var item in items)
            {
                bool isHover = hover == item.Key;
                var c = isHover ? Palette.Text : item.Key == "forget" ? Palette.Alpha(Palette.Pink, 220) : item.Key == "special" ? Palette.Gold : Palette.Sky;
                var ft = Txt.Text(item.Value, 12, c, FontWeight.Bold);
                g.DrawText(ft, new Point(lx, ly));
                if (isHover) g.DrawLine(new Pen(Palette.Brush(c), 1), new Point(lx, ly + ft.Height), new Point(lx + ft.Width, ly + ft.Height));
                links.Add(new KeyValuePair<string, Rect>(item.Key, new Rect(lx - 4, ly - 3, ft.Width + 8, ft.Height + 6)));
                lx += ft.Width + 22;
            }
        }

        static double RightAligned(DrawingContext g, string s, double size, Color c, double right, double y, FontWeight w)
        {
            var ft = Txt.Text(s, size, c, w);
            g.DrawText(ft, new Point(right - ft.Width, y));
            return ft.Width;
        }
    }

    class EmptyHangar : Control
    {
        public EmptyHangar() { Height = 260; }

        public override void Render(DrawingContext g)
        {
            double cx = Bounds.Width / 2;
            g.DrawEllipse(Palette.Brush(Palette.Alpha(Palette.Ink, 120)), null, new Point(cx, 156), 50, 6);
            QuadArt.Draw(g, new Point(cx, 92), 130, 15, 0, 1, true, Palette.Sky);
            Txt.At(g, "z", 20, Palette.Muted, cx + 50, 30, FontWeight.Bold);
            Txt.At(g, "Z", 28, Palette.Alpha(Palette.Muted, 170), cx + 70, 4, FontWeight.Bold);
            Txt.Centered(g, "The hangar's empty", 20, Palette.Text, new Rect(0, 182, Bounds.Width, 32), FontWeight.Black);
            Txt.Centered(g, "Plug in a quad and I'll take it from there.", 13.5, Palette.Muted, new Rect(0, 214, Bounds.Width, 24));
        }
    }

    class HangarWindow : Window
    {
        public enum Page { Hangar, Settings, Log }
        static readonly Color[] DroneColors = { Palette.Sky, Palette.Mint, Palette.Pink, Palette.Amber, Palette.Violet, Palette.Coral };

        readonly DesktopApp app;
        readonly MainHeader header = new MainHeader();
        readonly Dictionary<Page, TabPill> tabs = new Dictionary<Page, TabPill>();
        readonly Dictionary<Page, Control> pages = new Dictionary<Page, Control>();
        readonly ContentControl content = new ContentControl();
        readonly TextBlock saved;
        readonly DispatcherTimer anim, refresh;
        Page page = Page.Hangar;
        StackPanel cards;
        TextBlock fleetSummary, backupRootDesc;
        SelectableTextBlock logText;
        ScrollViewer logScroll;
        Segmented logFilter;
        string hangarSignature, logSignature;
        public bool ReallyClose;

        public HangarWindow(DesktopApp app)
        {
            this.app = app;
            Title = "Quad Backup";
            Width = 780;
            Height = 700;
            CanResize = false;
            Background = Palette.Brush(Palette.Bg);
            Icon = QuadArt.ClassicIcon(64);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var tabRow = new DockPanel { Margin = new Thickness(24, 10, 24, 0), Height = 44 };
            saved = WindowKit.Label("✓  Saved", 12, Palette.Mint, FontWeight.Bold);
            saved.Opacity = 0;
            saved.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(saved, Dock.Right);
            tabRow.Children.Add(saved);
            var tabStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (Page p in Enum.GetValues(typeof(Page)))
            {
                var pill = new TabPill(p == Page.Hangar ? "Hangar" : p == Page.Settings ? "Settings" : "Flight log");
                var target = p;
                pill.Click += (s, e) => ShowPage(target);
                tabs[p] = pill;
                tabStack.Children.Add(pill);
            }
            tabRow.Children.Add(tabStack);

            pages[Page.Hangar] = BuildHangarPage();
            pages[Page.Settings] = BuildSettingsPage();
            pages[Page.Log] = BuildLogPage();

            var root = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            DockPanel.SetDock(tabRow, Dock.Top);
            root.Children.Add(header);
            root.Children.Add(tabRow);
            var line = new Border { Height = 1, Background = Palette.Brush(Palette.Alpha(Palette.Line, 150)), Margin = new Thickness(24, 6, 24, 8) };
            DockPanel.SetDock(line, Dock.Top);
            root.Children.Add(line);
            root.Children.Add(content);
            Content = root;

            anim = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (s, e) =>
            {
                header.Tick();
                if (saved.Opacity > 0) saved.Opacity = Math.Max(0, saved.Opacity - 0.02);
            });
            refresh = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (s, e) => RefreshData(false));
            Opened += (s, e) => { anim.Start(); refresh.Start(); RefreshData(true); };
            Closing += (s, e) =>
            {
                // closing just tucks it away; Quit in the menu really exits
                if (!ReallyClose) { e.Cancel = true; Hide(); }
            };
            PropertyChanged += (s, e) =>
            {
                if (e.Property != IsVisibleProperty) return;
                if (IsVisible) { anim.Start(); refresh.Start(); RefreshData(true); } else { anim.Stop(); refresh.Stop(); }
            };
            ShowPage(Page.Hangar);
        }

        public void ShowOn(Page p)
        {
            ShowPage(p);
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        void ShowPage(Page p)
        {
            page = p;
            // Settings is rebuilt each time so the turned-off drones list is current
            if (p == Page.Settings) pages[Page.Settings] = BuildSettingsPage();
            foreach (var kv in tabs) { kv.Value.Selected = kv.Key == p; kv.Value.InvalidateVisual(); }
            content.Content = pages[p];
            RefreshData(true);
        }

        void FlashSaved() { saved.Opacity = 1; }

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

        static int CountFiles(string folder)
        {
            try { return !string.IsNullOrEmpty(folder) && Directory.Exists(folder) ? Directory.GetFiles(folder, "*.txt").Length : 0; }
            catch { return 0; }
        }

        void RefreshData(bool force)
        {
            var db = DroneDb.Load();
            var latest = db.Drones.Select(d => new { d, t = ParseTime(d.LastBackup) }).Where(x => x.t.HasValue).OrderByDescending(x => x.t.Value).FirstOrDefault();
            header.LastBackup = latest == null ? "No backups yet. Plug in a quad!" : "Last backup: " + latest.d.Name + "  ·  " + Ago(latest.t.Value);
            tabs[Page.Hangar].Badge = db.Drones.Count;
            tabs[Page.Hangar].Measure();
            if (page == Page.Hangar) RefreshHangar(db, force);
            if (page == Page.Log) RefreshLog(force);
        }

        // ------------------------------------------------------------------ hangar

        Control BuildHangarPage()
        {
            var top = new DockPanel { Margin = new Thickness(24, 0, 24, 12) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var folder = new PillButton("Open folder", PillButton.Kind.Secondary, 120, 42);
            folder.Click += (s, e) => PosixPlatform.OpenFolder(Settings.Load().BackupRoot);
            var now = new PillButton("Back up now", PillButton.Kind.Primary, 140, 42);
            now.Click += (s, e) => app.Engine.BackupNow();
            var special = new PillButton("★ Special backup", PillButton.Kind.Special, 180, 42);
            special.Click += async (s, e) => { await app.SpecialBackup(this, null); RefreshData(true); };
            buttons.Children.Add(folder);
            buttons.Children.Add(now);
            buttons.Children.Add(special);
            DockPanel.SetDock(buttons, Dock.Right);
            top.Children.Add(buttons);
            var titles = new StackPanel { Spacing = 2 };
            titles.Children.Add(WindowKit.Label("Your fleet", 20, Palette.Text, FontWeight.Black));
            fleetSummary = WindowKit.Label("", 12.5, Palette.Muted);
            titles.Children.Add(fleetSummary);
            top.Children.Add(titles);

            cards = new StackPanel { Margin = new Thickness(24, 0, 24, 16) };
            var page = new DockPanel();
            DockPanel.SetDock(top, Dock.Top);
            page.Children.Add(top);
            page.Children.Add(new ScrollViewer { Content = cards });
            return page;
        }

        void RefreshHangar(DroneDb db, bool force)
        {
            var sig = string.Join("|", db.Drones.Select(d => d.FcId + "/" + d.Name + "/" + d.Folder + "/" + d.Ignored + "/" + d.LastBackup + "/" +
                                                         CountFiles(d.Folder) + "/" + Plugged(d) + "/" + Pending(d)));
            if (!force && sig == hangarSignature)
            {
                foreach (var c in cards.Children) c.InvalidateVisual(); // "5 min ago" ticks over
                return;
            }
            hangarSignature = sig;
            cards.Children.Clear();
            if (db.Drones.Count == 0) cards.Children.Add(new EmptyHangar());
            foreach (var d in db.Drones.OrderBy(d => d.Ignored).ThenByDescending(d => ParseTime(d.LastBackup) ?? DateTime.MinValue))
            {
                var card = new DroneCard(d, Plugged(d), Pending(d));
                card.ActionClicked += OnCardAction;
                cards.Children.Add(card);
            }
            int total = db.Drones.Sum(d => CountFiles(d.Folder)), paused = db.Drones.Count(d => d.Ignored);
            fleetSummary.Text = db.Drones.Count == 0 ? "Nobody's home yet."
                : Plural(db.Drones.Count, "quad") + "  ·  " + Plural(total, "backup") + (paused > 0 ? "  ·  " + paused + " paused" : "");
        }

        bool Plugged(Drone d) { return d.FcId != null && app.Engine.IsPluggedIn(d.FcId); }
        string Pending(Drone d) { return d.FcId != null ? app.Engine.PendingSpecial(d.FcId) : null; }
        static string Plural(int n, string word) { return n + " " + word + (n == 1 ? "" : "s"); }

        async void OnCardAction(DroneCard card, string action)
        {
            var d = card.Drone;
            try
            {
                switch (action)
                {
                    case "special":
                        await app.SpecialBackup(this, d.FcId);
                        break;
                    case "open":
                        if (Directory.Exists(d.Folder)) PosixPlatform.OpenFolder(d.Folder);
                        else await ThemedDialog.Alert(this, "That folder's gone missing", "Use Change folder to point " + d.Name + " somewhere new.");
                        break;
                    case "rename":
                        await Rename(d);
                        break;
                    case "move":
                        var picked = await WindowKit.PickFolder(this, "Pick the folder for " + d.Name + "'s backups", d.Folder ?? Settings.Load().BackupRoot);
                        if (picked == null) return;
                        d.Folder = picked;
                        d.Name = Path.GetFileName(picked.TrimEnd('/', '\\'));
                        DroneDb.Upsert(d);
                        break;
                    case "pause":
                        d.Ignored = !d.Ignored;
                        DroneDb.Upsert(d);
                        break;
                    case "forget":
                        if (!await ThemedDialog.Confirm(this, "Forget " + d.Name + "?",
                            "Its backups stay right where they are. Next time it's plugged in, I'll ask where to save it.", "Forget it", true)) return;
                        DroneDb.Remove(d.FcId);
                        break;
                }
            }
            catch (Exception ex)
            {
                await ThemedDialog.Alert(this, "Hmm, that didn't work", ex.Message);
            }
            RefreshData(true);
        }

        async System.Threading.Tasks.Task Rename(Drone d)
        {
            var name = await ThemedDialog.Prompt(this, "Rename " + d.Name, "This renames its backup folder too. Old backups keep their file names.", d.Name, "Rename");
            if (name == null) return;
            name = BackupFiles.SafeName(name);
            if (name == d.Name) return;
            var oldFolder = d.Folder;
            var parent = string.IsNullOrEmpty(oldFolder) ? Settings.Load().BackupRoot : Path.GetDirectoryName(oldFolder.TrimEnd('/', '\\'));
            var newFolder = Path.Combine(parent, name);
            if (!string.IsNullOrEmpty(oldFolder) && Directory.Exists(oldFolder))
            {
                if (string.Equals(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase))
                {
                    var temp = newFolder + ".renaming";
                    Directory.Move(oldFolder, temp); // case-only rename needs a hop on case-insensitive disks
                    Directory.Move(temp, newFolder);
                }
                else if (Directory.Exists(newFolder))
                {
                    await ThemedDialog.Alert(this, "That name's taken", "There's already a folder called \"" + name + "\" there. Pick another name, or use Change folder to point at it.");
                    return;
                }
                else Directory.Move(oldFolder, newFolder);
            }
            DroneDb.Mutate(db =>
            {
                foreach (var x in db.Drones.Where(x => x.FcId == d.FcId || (oldFolder != null && string.Equals(x.Folder, oldFolder, StringComparison.OrdinalIgnoreCase))))
                {
                    x.Folder = newFolder;
                    x.Name = name;
                }
            });
        }

        // ------------------------------------------------------------------ settings

        Control BuildSettingsPage()
        {
            var s = Settings.Load();
            var list = new StackPanel { Margin = new Thickness(24, 0, 24, 24) };

            Section(list, "BACKUPS");
            var change = new PillButton("Change…", PillButton.Kind.Secondary, 112, 36);
            change.Click += async (o, e) =>
            {
                var picked = await WindowKit.PickFolder(this, "Where should new drone folders be created?", Settings.Load().BackupRoot);
                if (picked == null) return;
                SaveSetting(x => x.BackupRoot = picked);
                backupRootDesc.Text = picked;
            };
            backupRootDesc = Row(list, "Backups folder", s.BackupRoot, true, change);

            var cmd = new Segmented(new[] { "diff all", "dump all" }, s.CliCommand == "dump all" ? 1 : 0, 210);
            cmd.Changed += (o, e) => SaveSetting(x => x.CliCommand = cmd.Selected == 1 ? "dump all" : "diff all");
            Row(list, "Backup command", "diff all saves only what you've changed (best for restoring). dump all saves every single setting.", false, cmd);

            var chips = new List<ChipToggle>
            {
                new ChipToggle("Betaflight", "BTFL", s.FirmwareVariants.Contains("BTFL")),
                new ChipToggle("INAV", "INAV", s.FirmwareVariants.Contains("INAV")),
                new ChipToggle("Emuflight", "EMUF", s.FirmwareVariants.Contains("EMUF"))
            };
            foreach (var chip in chips)
            {
                var c = chip;
                c.Changed += (o, e) =>
                {
                    if (!chips.Any(x => x.Selected)) { c.Selected = true; return; } // need at least one
                    SaveSetting(x => x.FirmwareVariants = chips.Where(k => k.Selected).Select(k => k.Value).ToList());
                };
            }
            Row(list, "Firmware to back up", "Flight controllers running anything else are left alone.", false, chips.ToArray());

            var skipSame = new FaceToggle(s.SkipUnchangedBackups);
            skipSame.Changed += (o, e) => SaveSetting(x => x.SkipUnchangedBackups = skipSame.On);
            Row(list, "Skip backups that haven't changed", "If a quad's settings match its last backup, I won't save another copy. Special backups are always kept.", false, skipSame);

            var whenDisconnected = new FaceToggle(s.BackupWhenDisconnected);
            whenDisconnected.Changed += (o, e) => SaveSetting(x => x.BackupWhenDisconnected = whenDisconnected.On);
            Row(list, "Back up when you disconnect in Betaflight", "Hit Disconnect with the quad still plugged in and I'll save that session's tune. Only kept if something changed. Also covers closing Betaflight.", false, whenDisconnected);

            var whenClosed = new FaceToggle(s.BackupWhenBetaflightCloses);
            whenClosed.Changed += (o, e) => SaveSetting(x => x.BackupWhenBetaflightCloses = whenClosed.On);
            Row(list, "Back up when Betaflight closes", "For when disconnect backups are off: close Betaflight with the quad still plugged in to save that session's tune.", false, whenClosed);

            Section(list, "TURNED-OFF DRONES");
            var db = DroneDb.Load();
            var paused = db.Drones.Where(d => d.Ignored).ToList();
            if (paused.Count == 0 && db.Forgotten.Count == 0)
                Row(list, "None right now", "Every drone you've plugged in is on patrol. Drones you set to \"never back up\" or Forget show up here, so you can bring them back.", false);
            foreach (var d in paused)
            {
                var drone = d;
                var on = new PillButton("Back up again", PillButton.Kind.Secondary, 132, 36);
                on.Click += (o, e) => { DroneDb.Unpause(drone.FcId); ShowPage(Page.Settings); };
                Row(list, DroneTitle(drone), "Set to never back up" + DroneDetails(drone) +
                    (string.IsNullOrEmpty(drone.Folder) ? ". I'll ask where to save it next time it's plugged in." : "."), false, on);
            }
            foreach (var d in db.Forgotten)
            {
                var drone = d;
                var restore = new PillButton("Restore", PillButton.Kind.Secondary, 104, 36);
                restore.Click += (o, e) => { DroneDb.Restore(drone.FcId); ShowPage(Page.Settings); };
                Row(list, DroneTitle(drone), "Forgotten" + DroneDetails(drone) +
                    (!string.IsNullOrEmpty(drone.Folder) ? ". Its backups are still in " + drone.Folder : "."), false, restore);
            }

            Section(list, "BETAFLIGHT");
            var launch = new FaceToggle(s.LaunchBetaflight);
            launch.Changed += (o, e) => SaveSetting(x => x.LaunchBetaflight = launch.On);
            Row(list, "Open Betaflight after a backup", "Skipped if it's already open.", false, launch);

            var target = WindowKit.Input(s.BetaflightLaunchPath);
            target.Width = 300;
            target.FontSize = 12.5;
            target.LostFocus += (o, e) => SaveSetting(x => x.BetaflightLaunchPath = target.Text.Trim());
            var detect = new PillButton("Auto-detect", PillButton.Kind.Secondary, 116, 36);
            detect.Click += (o, e) =>
            {
                var t = app.Platform.FindLaunchTarget();
                target.Text = t;
                SaveSetting(x => x.BetaflightLaunchPath = t);
            };
            Row(list, "Betaflight app", "An app, a .desktop file, or https://app.betaflight.com", false, target, detect);

            Section(list, "POP-UPS & STARTUP");
            var test = new PillButton("Test", PillButton.Kind.Secondary, 76, 36);
            test.Click += (o, e) => Toast.Pop(ToastKind.Success, "Test pop-up!", Quips.ForSave(), null);
            var notify = new FaceToggle(s.ShowNotifications);
            notify.Changed += (o, e) => SaveSetting(x => x.ShowNotifications = notify.On);
            Row(list, "Pop-up notifications", "Little pop-ups when backups happen (or don't).", false, test, notify);

            var startup = new FaceToggle(PosixStartup.IsEnabled());
            startup.Changed += async (o, e) =>
            {
                try { PosixStartup.Set(startup.On); FlashSaved(); }
                catch (Exception ex) { await ThemedDialog.Alert(this, "Couldn't change startup", ex.Message); }
            };
            Row(list, "Start at login", "So I'm already on patrol when you plug in a quad.", false, startup);

            Section(list, "ADVANCED");
            var vendors = WindowKit.Input(string.Join(", ", s.UsbVendorIds));
            vendors.Width = 200;
            vendors.LostFocus += (o, e) =>
            {
                var ids = Regex.Split(vendors.Text.ToUpperInvariant(), @"[\s,;]+").Where(v => v.Length > 0).ToList();
                bool valid = ids.Count > 0 && ids.All(v => Regex.IsMatch(v, "^[0-9A-F]{4}$"));
                vendors.BorderBrush = Palette.Brush(valid ? Palette.Line : Palette.Pink);
                if (!valid) return;
                vendors.Text = string.Join(", ", ids);
                if (!ids.SequenceEqual(Settings.Load().UsbVendorIds)) SaveSetting(x => x.UsbVendorIds = ids);
            };
            Row(list, "USB chip vendors", "Only USB devices from these vendors are ever opened. 0483 = STM32, 2E3C = AT32, 314B = APM32.", false, vendors);

            Row(list, "Reboot grace (seconds)", "While Betaflight is open, a quad that reconnects this fast is treated as a reboot after Save, not a new plug-in. Also how long Betaflight gets to reconnect after a reboot. 2 to 60.",
                false, NumberBox(s.RebootGraceSeconds, 2, 60, n => SaveSetting(x => x.RebootGraceSeconds = n)));
            Row(list, "Disconnect wait (seconds)", "How long after you hit Disconnect before I back up. 1 to 60.",
                false, NumberBox(s.DisconnectWaitSeconds, 1, 60, n => SaveSetting(x => x.DisconnectWaitSeconds = n)));

            var footer = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
            var raw = new PillButton("Open settings.json", PillButton.Kind.Secondary, 168, 36);
            raw.Click += (o, e) => PosixPlatform.OpenTextFile(AppPaths.Settings);
            DockPanel.SetDock(raw, Dock.Right);
            footer.Children.Add(raw);
            footer.Children.Add(WindowKit.Label("Changes save automatically.", 12.5, Palette.Muted));
            list.Children.Add(footer);
            return new ScrollViewer { Content = list };
        }

        static string DroneTitle(Drone d)
        {
            return !string.IsNullOrEmpty(d.Name) ? d.Name : (!string.IsNullOrEmpty(d.CraftName) ? d.CraftName : (d.Board ?? "Unnamed quad"));
        }

        static string DroneDetails(Drone d)
        {
            var parts = new[] { d.Board, d.Firmware }.Where(x => !string.IsNullOrEmpty(x)).ToList();
            return parts.Count > 0 ? " · " + string.Join(" · ", parts) : "";
        }

        TextBox NumberBox(int value, int min, int max, Action<int> save)
        {
            var box = WindowKit.Input(value.ToString());
            box.Width = 80;
            box.LostFocus += (o, e) =>
            {
                int n;
                bool valid = int.TryParse(box.Text.Trim(), out n) && n >= min && n <= max;
                box.BorderBrush = Palette.Brush(valid ? Palette.Line : Palette.Pink);
                if (valid) save(n);
            };
            return box;
        }

        static void Section(StackPanel list, string title)
        {
            list.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Palette.Brush(Palette.Violet), Margin = new Thickness(0, 18, 0, 4) });
        }

        static TextBlock Row(StackPanel list, string title, string desc, bool singleLine, params Control[] right)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 10, 0, 10) };
            var text = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 24, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(WindowKit.Label(title, 14.5, Palette.Text, FontWeight.SemiBold));
            var d = WindowKit.Label(desc, 12.5, Palette.Muted, wrap: !singleLine);
            text.Children.Add(d);
            grid.Children.Add(text);
            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            foreach (var c in right) controls.Children.Add(c);
            Grid.SetColumn(controls, 1);
            grid.Children.Add(controls);
            list.Children.Add(grid);
            list.Children.Add(new Border { Height = 1, Background = Palette.Brush(Palette.Rgb(0x24284E)) });
            return d;
        }

        void SaveSetting(Action<Settings> change)
        {
            try
            {
                var s = Settings.Load();
                change(s);
                s.Save();
                FlashSaved();
                app.Engine.ReloadSettings();
            }
            catch (Exception ex)
            {
                Log.Write("Couldn't save settings: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ log

        Control BuildLogPage()
        {
            var top = new DockPanel { Margin = new Thickness(24, 0, 24, 12) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var open = new PillButton("Open file", PillButton.Kind.Secondary, 120, 42);
            open.Click += (s, e) => PosixPlatform.OpenTextFile(AppPaths.Log);
            var clear = new PillButton("Clear", PillButton.Kind.Secondary, 96, 42);
            clear.Click += async (s, e) =>
            {
                if (!await ThemedDialog.Confirm(this, "Clear the flight log?", "This wipes the log history. Your backups aren't touched.", "Clear it", true)) return;
                try { File.WriteAllText(AppPaths.Log, ""); } catch { }
                RefreshLog(true);
            };
            buttons.Children.Add(open);
            buttons.Children.Add(clear);
            DockPanel.SetDock(buttons, Dock.Right);
            top.Children.Add(buttons);
            var titles = new StackPanel { Spacing = 2 };
            titles.Children.Add(WindowKit.Label("Flight log", 20, Palette.Text, FontWeight.Black));
            titles.Children.Add(WindowKit.Label("Everything I've been up to, newest at the bottom.", 12.5, Palette.Muted));
            top.Children.Add(titles);

            logFilter = new Segmented(new[] { "Everything", "Backups", "Problems" }, 0, 330) { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(24, 0, 24, 12) };
            logFilter.Changed += (s, e) => RefreshLog(true);

            logText = new SelectableTextBlock { FontFamily = new FontFamily("Menlo, DejaVu Sans Mono, monospace"), FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
            logScroll = new ScrollViewer { Content = logText, Padding = new Thickness(14, 10) };
            var frame = new Border
            {
                Background = Palette.Brush(Palette.Panel), BorderBrush = Palette.Brush(Palette.Line), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14), Margin = new Thickness(24, 0, 24, 20), Child = logScroll
            };

            var page = new DockPanel();
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(logFilter, Dock.Top);
            page.Children.Add(top);
            page.Children.Add(logFilter);
            page.Children.Add(frame);
            return page;
        }

        void RefreshLog(bool force)
        {
            string[] lines;
            string sig;
            try
            {
                var fi = new FileInfo(AppPaths.Log);
                sig = (fi.Exists ? fi.Length + "/" + fi.LastWriteTimeUtc.Ticks : "none") + "/" + logFilter.Selected;
                if (!force && sig == logSignature) return;
                lines = fi.Exists ? File.ReadAllLines(AppPaths.Log) : new string[0];
            }
            catch { return; }
            logSignature = sig;

            var colors = new[] { Palette.Muted, Palette.Text, Palette.Mint, Palette.Pink, Palette.Amber, Palette.Sky, Palette.Violet };
            logText.Inlines.Clear();
            int shown = 0;
            foreach (var line in lines.Skip(Math.Max(0, lines.Length - 800)))
            {
                if (line.Length == 0) continue;
                string time = line.Length > 21 ? line.Substring(5, 14) : "";
                string msg = line.Length > 21 ? line.Substring(21) : line;
                int color = LogColor(msg);
                if (logFilter.Selected == 1 && !IsBackupLine(msg)) continue;
                if (logFilter.Selected == 2 && color != 4 && color != 5) continue;
                logText.Inlines.Add(new Run(time + "   ") { Foreground = Palette.Brush(Palette.Muted) });
                logText.Inlines.Add(new Run(msg + "\n") { Foreground = Palette.Brush(colors[color - 1]) });
                shown++;
            }
            if (shown == 0)
                logText.Inlines.Add(new Run(logFilter.Selected == 2 ? "No problems. Clear skies!" : "Nothing here yet. It's been a quiet day on the bench.") { Foreground = Palette.Brush(Palette.Muted) });
            Dispatcher.UIThread.Post(() => logScroll.ScrollToEnd(), DispatcherPriority.Background);
        }

        // 1 muted, 2 text, 3 mint, 4 pink, 5 amber, 6 sky, 7 violet
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
    }
}
