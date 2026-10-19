// Quad Backup for Windows - automatically backs up Betaflight flight controllers when they're plugged in over USB,
// then launches Betaflight. Lives in the system tray. Build with build.cmd (uses the C# compiler built into Windows).
//
// The backup logic itself (watching ports, plug-ins, disconnects, special backups) is in src/Core and shared with the
// Mac/Linux app. This file is the Windows shell around it: the tray icon, single-instance handling, and the Win32
// pieces the engine needs (serial ports, USB vendor lookup, spotting Betaflight, start with Windows).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace QuadBackup
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool createdNew;
            using (var mutex = new Mutex(true, "QuadBackup.SingleInstance", out createdNew))
            using (var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "QuadBackup.ShowWindow"))
            {
                if (!createdNew)
                {
                    showSignal.Set(); // already running: ask that copy to open its window instead
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Log.Write("UI error: " + e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("Fatal error: " + e.ExceptionObject);
                // Windows startup passes --tray so the window doesn't pop up at every login.
                bool showWindow = !args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
                Application.Run(new TrayApp(showSignal, showWindow));
            }
        }
    }

    class TrayApp : ApplicationContext, IHangarHost, IEngineUi
    {
        readonly Control ui;
        readonly NotifyIcon tray;
        readonly BackupEngine engine;
        volatile bool stopping;
        MainWindow main;

        public TrayApp(EventWaitHandle showSignal, bool showWindow)
        {
            ui = new Control();
            ui.CreateControl();
            var forceHandle = ui.Handle;

            bool firstRun;
            var settings = Settings.LoadOrCreate(out firstRun, Betaflight.FindLaunchTarget);
            try { Directory.CreateDirectory(settings.BackupRoot); } catch { }
            try
            {
                // first run turns on start-with-Windows; later runs refresh the entry in case the exe moved
                if (firstRun || Startup.IsEnabled()) Startup.Set(true);
            }
            catch (Exception ex) { Log.Write("Couldn't update start with Windows: " + ex.Message); }

            engine = new BackupEngine(new WinPlatform(), this);

            tray = new NotifyIcon { Icon = Art.MakeClassicIcon(32), Text = "Quad Backup - click to open", Visible = true };
            tray.ContextMenuStrip = BuildMenu();
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMain(MainWindow.Page.Hangar); };

            engine.Start();
            new Thread(() =>
            {
                // a second copy of the exe was launched: open the window here
                while (!stopping)
                    if (showSignal.WaitOne(1000) && !stopping)
                        ui.BeginInvoke(new Action(() => ShowMain(MainWindow.Page.Hangar)));
            }) { IsBackground = true, Name = "show-signal" }.Start();

            Log.Write("Started. Backups go to " + settings.BackupRoot);
            if (showWindow) ui.BeginInvoke(new Action(() => ShowMain(MainWindow.Page.Hangar)));
            else if (firstRun)
                Notify(ToastKind.Info, "Quad Backup is on patrol!", "Plug in a drone and I'll back it up. Click my little tray icon to open me.", settings.BackupRoot);
        }

        void ShowMain(MainWindow.Page page)
        {
            if (main == null || main.IsDisposed)
                main = new MainWindow(this);
            main.ShowOn(page);
        }

        // ------------------------------------------------------------------ IHangarHost (called on the UI thread)

        public void BackupNow() { engine.BackupNow(); }

        public void SettingsChanged() { engine.ReloadSettings(); }

        public void SpecialBackup(IWin32Window owner, string fcId)
        {
            string port, droneName;
            if (!engine.FindSpecialTarget(fcId, out port, out droneName))
            {
                ThemedDialog.Alert(owner, "No quad plugged in", "Plug in the quad you're tuning, then hit Special backup again.");
                return;
            }
            var name = ThemedDialog.PromptSpecial(owner, droneName);
            if (name != null) engine.QueueSpecial(port, name);
        }

        public string PendingSpecial(string fcId) { return engine.PendingSpecial(fcId); }

        public bool IsPluggedIn(string fcId) { return engine.IsPluggedIn(fcId); }

        // ------------------------------------------------------------------ IEngineUi (called from the engine's threads)

        public void Notify(ToastKind kind, string title, string text, string folder)
        {
            Action click = folder == null ? null : new Action(() => OpenFolder(folder));
            ui.BeginInvoke(new Action(() => Toast.Pop(kind, title, text, click)));
        }

        public NewDroneChoice AskWhereToSave(FcBackup b, string port, string suggestedName, string location, string note, DroneMood mood)
        {
            return (NewDroneChoice)ui.Invoke(new Func<NewDroneChoice>(() => NewDroneForm.Ask(b, port, suggestedName, location, note, mood)));
        }

        // ------------------------------------------------------------------ tray

        ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip { Renderer = new MenuRenderer(), Font = new Font("Segoe UI", 9.5f), ImageScalingSize = new Size(20, 20) };
            menu.Items.Add(new ToolStripMenuItem("Quad Backup · on patrol") { Enabled = false, Tag = "header", Image = Art.DrawClassic(40) });
            menu.Items.Add(new ToolStripSeparator());
            var open = new ToolStripMenuItem("Open Quad Backup", null, (s, e) => ShowMain(MainWindow.Page.Hangar));
            open.Font = new Font(menu.Font, FontStyle.Bold);
            menu.Items.Add(open);
            menu.Items.Add("Back up the plugged-in quad now", null, (s, e) => engine.BackupNow());
            var special = new ToolStripMenuItem("★ Special backup…", null, (s, e) => SpecialBackup(null, null));
            special.Tag = "special"; // drawn in gold by MenuRenderer
            menu.Items.Add(special);
            menu.Items.Add("Open backups folder", null, (s, e) => OpenFolder(engine.Settings.BackupRoot));
            menu.Items.Add("Launch Betaflight", null, (s, e) => engine.LaunchBetaflight());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Hangar (drone list)", null, (s, e) => ShowMain(MainWindow.Page.Hangar));
            menu.Items.Add("Settings", null, (s, e) => ShowMain(MainWindow.Page.Settings));
            menu.Items.Add("Flight log", null, (s, e) => ShowMain(MainWindow.Page.Log));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit (your quads will miss me)", null, (s, e) => ExitThread());
            return menu;
        }

        protected override void ExitThreadCore()
        {
            stopping = true;
            engine.Stop();
            if (main != null && !main.IsDisposed) main.Dispose();
            tray.Visible = false;
            tray.Dispose();
            Log.Write("Exited");
            base.ExitThreadCore();
        }

        static void OpenFolder(string folder)
        {
            try { Directory.CreateDirectory(folder); Process.Start("explorer.exe", "\"" + folder + "\""); } catch { }
        }
    }

    // ------------------------------------------------------------------------------------------------ Win32 pieces

    class WinPlatform : IPlatform
    {
        public string[] ListPorts() { return Ports.List(); }
        public string GetVendorId(string port) { return Ports.GetVendorId(port); }
        public ILink Open(string port) { return new SerialLink(port); }
        public bool? IsInUse(string port) { return SerialLink.IsInUse(port); }
        public bool IsBetaflightRunning() { return Betaflight.IsRunning(); }
        public string FindLaunchTarget() { return Betaflight.FindLaunchTarget(); }
        public void Launch(string target) { Betaflight.Launch(target); }
        public bool ProbeAlways { get { return false; } }  // Betaflight's window is reliably spotted on Windows
        public int ProbeIntervalMs { get { return 200; } } // the in-use check is a cheap open/close
    }

    // Raw Win32 serial access. System.IO.Ports.SerialPort can crash the whole process when a USB device vanishes
    // while the port is open, which is exactly what an FC does when it reboots.
    public sealed class SerialLink : ILink
    {
        readonly SafeFileHandle handle;
        int lastTimeout = -1;

        public SerialLink(string port)
        {
            handle = CreateFile(@"\\.\" + port, 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 5) throw new PortBusyException(port + " is in use by another program");
                throw new IOException("Couldn't open " + port + " (Windows error " + err + ")");
            }
            var dcb = new DCB();
            dcb.DCBlength = (uint)Marshal.SizeOf(typeof(DCB));
            if (GetCommState(handle, ref dcb))
            {
                dcb.BaudRate = 115200;
                dcb.ByteSize = 8;
                dcb.Parity = 0;
                dcb.StopBits = 0;
                dcb.Flags = 0x1011; // binary mode, DTR on, RTS on
                SetCommState(handle, ref dcb);
            }
            PurgeComm(handle, 0x0F);
        }

        public void Write(byte[] data)
        {
            SetTimeout(50);
            uint written;
            if (!WriteFile(handle, data, (uint)data.Length, out written, IntPtr.Zero))
                throw new IOException("Write failed (Windows error " + Marshal.GetLastWin32Error() + ")");
        }

        public int Read(byte[] buffer, int timeoutMs)
        {
            SetTimeout(timeoutMs);
            uint read;
            if (!ReadFile(handle, buffer, (uint)buffer.Length, out read, IntPtr.Zero))
                throw new IOException("Read failed (Windows error " + Marshal.GetLastWin32Error() + ")");
            return (int)read;
        }

        // Is another program (Betaflight) holding the port? true = in use, false = free, null = gone/unknown.
        // Opens and closes the handle without sending anything, so the FC never notices.
        public static bool? IsInUse(string port)
        {
            using (var h = CreateFile(@"\\.\" + port, 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (!h.IsInvalid) return false;
                return Marshal.GetLastWin32Error() == 5 ? (bool?)true : null;
            }
        }

        void SetTimeout(int ms)
        {
            if (ms == lastTimeout) return;
            // MAXDWORD/MAXDWORD/n = return immediately with whatever has arrived, or wait up to n ms for the first byte.
            var t = new COMMTIMEOUTS
            {
                ReadIntervalTimeout = uint.MaxValue,
                ReadTotalTimeoutMultiplier = uint.MaxValue,
                ReadTotalTimeoutConstant = (uint)Math.Max(1, ms),
                WriteTotalTimeoutConstant = 2000
            };
            SetCommTimeouts(handle, ref t);
            lastTimeout = ms;
        }

        public void Dispose() { handle.Dispose(); }

        [StructLayout(LayoutKind.Sequential)]
        struct DCB
        {
            public uint DCBlength, BaudRate, Flags;
            public ushort wReserved, XonLim, XoffLim;
            public byte ByteSize, Parity, StopBits, XonChar, XoffChar, ErrorChar, EofChar, EvtChar;
            public ushort wReserved1;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct COMMTIMEOUTS
        {
            public uint ReadIntervalTimeout, ReadTotalTimeoutMultiplier, ReadTotalTimeoutConstant,
                WriteTotalTimeoutMultiplier, WriteTotalTimeoutConstant;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetCommState(SafeFileHandle h, ref DCB dcb);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetCommState(SafeFileHandle h, ref DCB dcb);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetCommTimeouts(SafeFileHandle h, ref COMMTIMEOUTS t);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool PurgeComm(SafeFileHandle h, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(SafeFileHandle h, byte[] buf, uint count, out uint read, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(SafeFileHandle h, byte[] buf, uint count, out uint written, IntPtr overlapped);
    }

    static class Ports
    {
        public static string[] List()
        {
            try
            {
                return SerialPort.GetPortNames()
                    .Select(p => new string(p.Where(char.IsLetterOrDigit).ToArray()))
                    .Where(p => p.Length > 0).Distinct().ToArray();
            }
            catch { return new string[0]; }
        }

        // USB vendor ID (e.g. "0483") of the device behind a COM port, or null if it isn't a USB device / not found.
        public static string GetVendorId(string port)
        {
            try
            {
                var query = "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE ClassGuid='{4d36e978-e325-11ce-bfc1-08002be10318}'";
                using (var searcher = new ManagementObjectSearcher(query))
                {
                    foreach (ManagementBaseObject o in searcher.Get())
                    {
                        var name = o["Name"] as string;
                        if (name == null || !name.EndsWith("(" + port + ")", StringComparison.OrdinalIgnoreCase)) continue;
                        var m = Regex.Match(o["PNPDeviceID"] as string ?? "", @"VID_([0-9A-F]{4})", RegexOptions.IgnoreCase);
                        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
                    }
                }
            }
            catch (Exception ex) { Log.Write("Device lookup failed for " + port + ": " + ex.Message); }
            return null;
        }
    }

    // ------------------------------------------------------------------------------------------------ Betaflight app

    static class Betaflight
    {
        static readonly string[] Browsers = { "chrome", "msedge", "brave", "opera", "vivaldi", "chromium", "arc" };

        // True when the desktop Betaflight Configurator or the Betaflight web app (installed or in a browser tab) is open.
        public static bool IsRunning()
        {
            bool found = false;
            var processNames = new Dictionary<uint, string>();
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                int len = GetWindowTextLength(hwnd);
                if (len == 0) return true;
                var sb = new StringBuilder(len + 1);
                GetWindowText(hwnd, sb, sb.Capacity);
                var title = sb.ToString();
                if (title.IndexOf("betaflight", StringComparison.OrdinalIgnoreCase) < 0) return true;

                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                string proc;
                if (!processNames.TryGetValue(pid, out proc))
                {
                    try { proc = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); } catch { proc = ""; }
                    processNames[pid] = proc;
                }
                if (proc.Contains("betaflight") ||
                    (Browsers.Contains(proc) && title.StartsWith("Betaflight", StringComparison.OrdinalIgnoreCase) &&
                     title.IndexOf("Blackbox", StringComparison.OrdinalIgnoreCase) < 0))
                {
                    found = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        // Prefer a Start Menu shortcut (desktop Configurator or the installed web app), else the web app URL.
        public static string FindLaunchTarget()
        {
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            };
            var shortcuts = new List<string>();
            foreach (var root in roots)
            {
                try { shortcuts.AddRange(Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories)); } catch { }
            }
            var candidates = shortcuts.Where(f =>
            {
                var n = Path.GetFileNameWithoutExtension(f);
                return n.StartsWith("Betaflight", StringComparison.OrdinalIgnoreCase) &&
                       n.IndexOf("Blackbox", StringComparison.OrdinalIgnoreCase) < 0 &&
                       n.IndexOf("Uninstall", StringComparison.OrdinalIgnoreCase) < 0;
            }).OrderByDescending(f => Path.GetFileName(f).IndexOf("Configur", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            return candidates.Count > 0 ? candidates[0] : "https://app.betaflight.com";
        }

        public static void Launch(string target)
        {
            if (string.IsNullOrEmpty(target)) target = FindLaunchTarget();
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }

        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    }

    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "QuadBackup";

        public static bool IsEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && key.GetValue(ValueName) != null;
        }

        public static void Set(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\" --tray");
                else key.DeleteValue(ValueName, false);
            }
        }
    }
}
