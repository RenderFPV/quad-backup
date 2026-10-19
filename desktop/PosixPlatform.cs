// Mac and Linux pieces the backup engine needs: finding flight controller ports, telling whether Betaflight has one open,
// spotting/launching Betaflight, and starting at login.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text.RegularExpressions;

namespace QuadBackup
{
    class PosixPlatform : IPlatform
    {
        static readonly bool Mac = AppPaths.IsMac;
        const string BetaflightUrl = "https://app.betaflight.com";
        const string BetaflightPwaId = "kecmkdilaabfklljnjoaeognfdagnmog"; // Chrome's id for the installed Betaflight web app

        // ------------------------------------------------------------------ ports

        // USB CDC serial devices: /dev/cu.usbmodem* on a Mac, /dev/ttyACM* on Linux. Flight controllers show up as these;
        // USB-to-serial adapters (FTDI, CP210x, CH340 - usbserial / ttyUSB) are left alone.
        public string[] ListPorts()
        {
            try
            {
                return Mac ? Directory.GetFiles("/dev", "cu.usbmodem*") : Directory.GetFiles("/dev", "ttyACM*");
            }
            catch { return new string[0]; }
        }

        public string GetVendorId(string port)
        {
            return Mac ? MacVendorId(port) : LinuxVendorId(port);
        }

        // /sys/class/tty/ttyACM0/device -> the USB interface; its parent is the USB device with idVendor.
        static string LinuxVendorId(string port)
        {
            try
            {
                var device = new DirectoryInfo("/sys/class/tty/" + Path.GetFileName(port) + "/device");
                var target = device.ResolveLinkTarget(true);
                var iface = target != null ? target.FullName : device.FullName;
                var file = Path.Combine(Path.GetDirectoryName(iface), "idVendor");
                return File.Exists(file) ? File.ReadAllText(file).Trim().ToUpperInvariant() : null;
            }
            catch { return null; }
        }

        static readonly object IoregGate = new object();
        static Dictionary<string, string> ioregCache;
        static DateTime ioregAt;

        // ioreg lists USB devices with their properties, children indented below them. The nearest idVendor above a
        // serial port's IOCalloutDevice belongs to the device that port lives on.
        static string MacVendorId(string port)
        {
            lock (IoregGate)
            {
                if (ioregCache == null || (DateTime.UtcNow - ioregAt).TotalSeconds > 3 || !ioregCache.ContainsKey(port))
                {
                    var map = new Dictionary<string, string>(StringComparer.Ordinal);
                    var output = Run("/usr/sbin/ioreg", "-r -c IOUSBHostDevice -l -w0", 4000);
                    string vendor = null;
                    foreach (var line in (output ?? "").Split('\n'))
                    {
                        var v = Regex.Match(line, "\"idVendor\" = (\\d+)");
                        if (v.Success) vendor = int.Parse(v.Groups[1].Value).ToString("X4");
                        var c = Regex.Match(line, "\"IOCalloutDevice\" = \"([^\"]+)\"");
                        if (c.Success && vendor != null) map[c.Groups[1].Value] = vendor;
                    }
                    ioregCache = map;
                    ioregAt = DateTime.UtcNow;
                }
                string vid;
                // if ioreg didn't work at all, let the MSP check decide (only usbmodem devices are ever listed)
                if (ioregCache.Count == 0) return "0483";
                return ioregCache.TryGetValue(port, out vid) ? vid : null;
            }
        }

        public ILink Open(string port)
        {
            if (IsInUse(port) == true) throw new PortBusyException(port + " is in use by another program");
            return new PosixSerialLink(port);
        }

        // Unlike Windows, Mac and Linux let two programs open the same port, so "is Betaflight connected?" is answered by
        // asking which processes have the device open.
        public bool? IsInUse(string port)
        {
            if (!File.Exists(port)) return null;
            return Mac ? MacInUse(port) : LinuxInUse(port);
        }

        static bool? MacInUse(string port)
        {
            // the same port has a "cu." and a "tty." node; Betaflight may have opened either
            var tty = port.Replace("/dev/cu.", "/dev/tty.");
            var output = Run("/usr/sbin/lsof", "-t \"" + port + "\" \"" + tty + "\"", 3000);
            if (output == null) return null;
            int me = Environment.ProcessId;
            return output.Split('\n').Any(l => { int pid; return int.TryParse(l.Trim(), out pid) && pid != me; });
        }

        static bool? LinuxInUse(string port)
        {
            int me = Environment.ProcessId;
            try
            {
                foreach (var proc in Directory.GetDirectories("/proc"))
                {
                    int pid;
                    if (!int.TryParse(Path.GetFileName(proc), out pid) || pid == me) continue;
                    string[] fds;
                    try { fds = Directory.GetFiles(proc + "/fd"); } catch { continue; } // other users' processes
                    foreach (var fd in fds)
                    {
                        try
                        {
                            var target = new FileInfo(fd).LinkTarget;
                            if (target == port) return true;
                        }
                        catch { }
                    }
                }
                return false;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ Betaflight

        public bool IsBetaflightRunning()
        {
            var output = Run("ps", Mac ? "-axo command=" : "-eo args=", 3000) ?? "";
            var self = Environment.ProcessPath ?? "";
            foreach (var line in output.Split('\n'))
            {
                if (line.Length == 0 || (self.Length > 0 && line.Contains(self))) continue;
                bool isBf = line.IndexOf("betaflight", StringComparison.OrdinalIgnoreCase) >= 0 || line.Contains(BetaflightPwaId);
                if (isBf && line.IndexOf("blackbox", StringComparison.OrdinalIgnoreCase) < 0) return true;
            }
            return false;
        }

        public string FindLaunchTarget()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidates = new List<string>();
            if (Mac)
            {
                foreach (var dir in new[] { "/Applications", Path.Combine(home, "Applications"), Path.Combine(home, "Applications", "Chrome Apps.localized") })
                {
                    try { candidates.AddRange(Directory.GetDirectories(dir, "*.app")); } catch { }
                }
            }
            else
            {
                foreach (var dir in new[] { Path.Combine(home, ".local", "share", "applications"), "/usr/share/applications", "/usr/local/share/applications" })
                {
                    try { candidates.AddRange(Directory.GetFiles(dir, "*.desktop")); } catch { }
                }
            }
            var match = candidates.Where(IsBetaflightApp)
                .OrderByDescending(f => Path.GetFileName(f).IndexOf("Configur", StringComparison.OrdinalIgnoreCase) >= 0)
                .FirstOrDefault();
            return match ?? BetaflightUrl;
        }

        static bool IsBetaflightApp(string path)
        {
            var name = Path.GetFileName(path);
            if (name.IndexOf("blackbox", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (name.IndexOf("betaflight", StringComparison.OrdinalIgnoreCase) >= 0 || name.Contains(BetaflightPwaId)) return true;
            if (!path.EndsWith(".desktop")) return false;
            try
            {
                // e.g. chrome-<id>-Default.desktop with "Name=Betaflight - Configuration and management application"
                var text = File.ReadAllText(path);
                return Regex.IsMatch(text, @"^Name=.*Betaflight", RegexOptions.Multiline) && text.IndexOf("Blackbox", StringComparison.OrdinalIgnoreCase) < 0;
            }
            catch { return false; }
        }

        public void Launch(string target)
        {
            if (Mac) { Start("/usr/bin/open", Quote(target)); return; }
            if (target.EndsWith(".desktop"))
            {
                // gtk-launch takes the desktop file's name; fall back to xdg-open if it isn't there
                if (!Start("gtk-launch", Quote(Path.GetFileNameWithoutExtension(target)))) Start("xdg-open", Quote(target));
            }
            else if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase)) Start("xdg-open", Quote(target));
            else Start(target, "");
        }

        public bool ProbeAlways { get { return true; } }       // spotting Betaflight by process name can miss a browser tab
        public int ProbeIntervalMs { get { return Mac ? 1000 : 500; } } // lsof / scanning /proc costs more than Windows' check

        // ------------------------------------------------------------------ helpers shared with the UI

        public static void OpenFolder(string folder)
        {
            try { Directory.CreateDirectory(folder); } catch { }
            Start(Mac ? "/usr/bin/open" : "xdg-open", Quote(folder));
        }

        public static void OpenTextFile(string file)
        {
            try { if (!File.Exists(file)) File.WriteAllText(file, ""); } catch { }
            if (Mac) Start("/usr/bin/open", "-t " + Quote(file));
            else Start("xdg-open", Quote(file));
        }

        public static string Quote(string s) { return "\"" + s.Replace("\"", "\\\"") + "\""; }

        public static bool Start(string file, string args)
        {
            try
            {
                Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false });
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Couldn't run " + file + ": " + ex.Message);
                return false;
            }
        }

        // Runs a command and returns its output, or null if it couldn't run.
        public static string Run(string file, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    var output = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return null; }
                    return output.Result;
                }
            }
            catch { return null; }
        }
    }

    // System.IO.Ports on Mac/Linux (it takes the port exclusively, so Betaflight can't open it mid-backup either).
    sealed class PosixSerialLink : ILink
    {
        readonly SerialPort port;

        public PosixSerialLink(string name)
        {
            port = new SerialPort(name, 115200) { DtrEnable = true, RtsEnable = true, WriteTimeout = 2000 };
            try { port.Open(); }
            catch (UnauthorizedAccessException) { throw new PortBusyException(name + " is in use by another program"); }
            catch (IOException ex)
            {
                if (ex.Message.IndexOf("busy", StringComparison.OrdinalIgnoreCase) >= 0) throw new PortBusyException(name + " is busy");
                throw;
            }
            port.DiscardInBuffer();
        }

        public void Write(byte[] data) { port.Write(data, 0, data.Length); }

        public int Read(byte[] buffer, int timeoutMs)
        {
            port.ReadTimeout = Math.Max(1, timeoutMs);
            try { return port.Read(buffer, 0, buffer.Length); }
            catch (TimeoutException) { return 0; }
        }

        public void Dispose()
        {
            try { port.Close(); } catch { } // the FC may have rebooted out from under us
            try { port.Dispose(); } catch { }
        }
    }

    // Start at login: a LaunchAgent on a Mac, an XDG autostart entry on Linux.
    static class PosixStartup
    {
        static string MacAgent
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "com.quadbackup.app.plist"); }
        }

        static string LinuxEntry
        {
            get
            {
                var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                var config = string.IsNullOrEmpty(xdg) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config") : xdg;
                return Path.Combine(config, "autostart", "quad-backup.desktop");
            }
        }

        static string File { get { return AppPaths.IsMac ? MacAgent : LinuxEntry; } }

        public static bool IsEnabled() { return System.IO.File.Exists(File); }

        public static void Set(bool enabled)
        {
            if (!enabled) { if (System.IO.File.Exists(File)) System.IO.File.Delete(File); return; }
            var exe = Environment.ProcessPath;
            Directory.CreateDirectory(Path.GetDirectoryName(File));
            if (AppPaths.IsMac)
            {
                System.IO.File.WriteAllText(File,
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                    "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                    "<plist version=\"1.0\"><dict>\n" +
                    "  <key>Label</key><string>com.quadbackup.app</string>\n" +
                    "  <key>ProgramArguments</key><array><string>" + System.Security.SecurityElement.Escape(exe) + "</string><string>--tray</string></array>\n" +
                    "  <key>RunAtLoad</key><true/>\n" +
                    "  <key>ProcessType</key><string>Interactive</string>\n" +
                    "</dict></plist>\n");
            }
            else
            {
                System.IO.File.WriteAllText(File,
                    "[Desktop Entry]\nType=Application\nName=Quad Backup\nComment=Backs up Betaflight drones when they're plugged in\n" +
                    "Exec=\"" + exe + "\" --tray\nX-GNOME-Autostart-enabled=true\n");
            }
        }
    }
}
