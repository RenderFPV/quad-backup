// Quad Backup for Mac and Linux - entry point, menu bar / tray icon, and the glue between the shared backup engine
// (src/Core) and the Avalonia windows. EXPERIMENTAL: built and simulated on Windows, not yet tested on real hardware.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace QuadBackup
{
    static class Program
    {
        public static bool ShowWindowOnStart;
        static Mutex mutex;

        [STAThread]
        public static void Main(string[] args)
        {
            bool createdNew;
            mutex = new Mutex(true, "QuadBackup.Desktop.SingleInstance", out createdNew);
            if (!createdNew)
            {
                // already running: ask that copy to open its window
                try { File.WriteAllText(DesktopApp.ShowSignalFile, ""); } catch { }
                return;
            }
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("Fatal error: " + e.ExceptionObject);
            ShowWindowOnStart = !args.Any(a => a == "--tray");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }

        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<DesktopApp>()
                .UsePlatformDetect()
                .With(new MacOSPlatformOptions { ShowInDock = false }) // lives in the menu bar
                .LogToTrace();
        }
    }

    class DesktopApp : Application, IEngineUi
    {
        public static string ShowSignalFile { get { return Path.Combine(AppPaths.Dir, "show.signal"); } }

        public BackupEngine Engine;
        public PosixPlatform Platform;
        IClassicDesktopStyleApplicationLifetime lifetime;
        TrayIcon tray;
        HangarWindow main;

        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            RequestedThemeVariant = ThemeVariant.Dark;
        }

        public override void OnFrameworkInitializationCompleted()
        {
            lifetime = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            Platform = new PosixPlatform();

            bool firstRun;
            var settings = Settings.LoadOrCreate(out firstRun, Platform.FindLaunchTarget);
            try { Directory.CreateDirectory(settings.BackupRoot); } catch { }
            try { if (firstRun || PosixStartup.IsEnabled()) PosixStartup.Set(true); }
            catch (Exception ex) { Log.Write("Couldn't update start at login: " + ex.Message); }

            Engine = new BackupEngine(Platform, this);
            BuildTray();
            Engine.Start();

            // a second copy was launched: open the window here
            try { File.Delete(ShowSignalFile); } catch { }
            new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (s, e) =>
            {
                if (!File.Exists(ShowSignalFile)) return;
                try { File.Delete(ShowSignalFile); } catch { }
                ShowMain(HangarWindow.Page.Hangar);
            }).Start();

            Log.Write("Started (" + (AppPaths.IsMac ? "macOS" : "Linux") + "). Backups go to " + settings.BackupRoot);
            var demo = Environment.GetEnvironmentVariable("QB_DEMO");
            if (!string.IsNullOrEmpty(demo)) Demo(demo);
            else if (Program.ShowWindowOnStart) ShowMain(HangarWindow.Page.Hangar);
            else if (firstRun)
                Notify(ToastKind.Info, "Quad Backup is on patrol!", "Plug in a drone and I'll back it up. My icon is up in the " + (AppPaths.IsMac ? "menu bar." : "tray."), settings.BackupRoot);
            base.OnFrameworkInitializationCompleted();
        }

        void BuildTray()
        {
            var menu = new NativeMenu();
            Add(menu, "Open Quad Backup", () => ShowMain(HangarWindow.Page.Hangar));
            menu.Add(new NativeMenuItemSeparator());
            Add(menu, "Back up the plugged-in quad now", () => Engine.BackupNow());
            Add(menu, "★ Special backup…", () => { var _ = SpecialBackup(null, null); });
            Add(menu, "Open backups folder", () => PosixPlatform.OpenFolder(Engine.Settings.BackupRoot));
            Add(menu, "Launch Betaflight", () => Engine.LaunchBetaflight());
            menu.Add(new NativeMenuItemSeparator());
            Add(menu, "Hangar (drone list)", () => ShowMain(HangarWindow.Page.Hangar));
            Add(menu, "Settings", () => ShowMain(HangarWindow.Page.Settings));
            Add(menu, "Flight log", () => ShowMain(HangarWindow.Page.Log));
            menu.Add(new NativeMenuItemSeparator());
            Add(menu, "Quit (your quads will miss me)", Quit);

            tray = new TrayIcon { Icon = QuadArt.ClassicIcon(64), ToolTipText = "Quad Backup", Menu = menu };
            tray.Clicked += (s, e) => ShowMain(HangarWindow.Page.Hangar); // Linux/Windows; on a Mac clicking opens the menu
            TrayIcon.SetIcons(this, new TrayIcons { tray });
        }

        static void Add(NativeMenu menu, string header, Action action)
        {
            var item = new NativeMenuItem(header);
            item.Click += (s, e) => action();
            menu.Add(item);
        }

        void ShowMain(HangarWindow.Page page)
        {
            if (main == null) main = new HangarWindow(this);
            main.ShowOn(page);
        }

        void Quit()
        {
            Engine.Stop();
            if (main != null) { main.ReallyClose = true; main.Close(); }
            tray.IsVisible = false;
            Log.Write("Exited");
            lifetime.Shutdown();
        }

        // Asks for a name and queues the special backup (it waits for a disconnect if Betaflight has the quad).
        public async Task SpecialBackup(Window owner, string fcId)
        {
            string port, droneName;
            if (!Engine.FindSpecialTarget(fcId, out port, out droneName))
            {
                await ThemedDialog.Alert(owner, "No quad plugged in", "Plug in the quad you're tuning, then hit Special backup again.");
                return;
            }
            var name = await ThemedDialog.PromptSpecial(owner, droneName);
            if (name != null) Engine.QueueSpecial(port, name);
        }

        // QB_DEMO=hangar|settings|log|drone|toasts|special opens that screen with sample data, for checking the look
        // without a quad plugged in.
        void Demo(string what)
        {
            switch (what)
            {
                case "settings": ShowMain(HangarWindow.Page.Settings); break;
                case "log": ShowMain(HangarWindow.Page.Log); break;
                case "drone":
                    var sample = new FcBackup { Board = "SPEEDYBEEF405V4", Firmware = "Betaflight 4.5.1", CraftName = "Chimera7", Variant = "BTFL" };
                    new NewDroneWindow(sample, "/dev/cu.usbmodem3101", "Chimera7", Engine.Settings.BackupRoot, null, DroneMood.New).Show();
                    break;
                case "toasts":
                    Toast.Pop(ToastKind.Success, "Chimera7 is backed up!", Quips.ForSave() + "\nChimera7_2026-10-19_14-32-05.txt", null);
                    Toast.Pop(ToastKind.Special, "★ \"locked in freestyle\" saved!", Quips.ForSpecial(), null);
                    Toast.Pop(ToastKind.Error, "Oof. Backup failed on cu.usbmodem3101", "Flight controller didn't enter CLI mode", null);
                    break;
                case "special": var _ = ThemedDialog.PromptSpecial(null, "Chimera7"); break;
                default: ShowMain(HangarWindow.Page.Hangar); break;
            }
        }

        // ------------------------------------------------------------------ IEngineUi (called from the engine's threads)

        public void Notify(ToastKind kind, string title, string text, string folder)
        {
            Action click = folder == null ? null : new Action(() => PosixPlatform.OpenFolder(folder));
            Dispatcher.UIThread.Post(() => Toast.Pop(kind, title, text, click));
        }

        public NewDroneChoice AskWhereToSave(FcBackup b, string port, string suggestedName, string location, string note, DroneMood mood)
        {
            var answer = new TaskCompletionSource<NewDroneChoice>();
            Dispatcher.UIThread.Post(() =>
            {
                var w = new NewDroneWindow(b, port, suggestedName, location, note, mood);
                w.Closed += (s, e) => answer.TrySetResult(w.Result);
                w.Show();
                w.Activate();
            });
            return answer.Task.Result;
        }
    }
}
