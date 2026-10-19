// Quad Backup - automatically backs up Betaflight flight controllers when they're plugged in over USB,
// then launches Betaflight. Lives in the system tray. Build with build.cmd (uses the C# compiler built into Windows).
//
// How it works:
//   1. Watches for new COM ports. Ignores ports from USB vendors that aren't flight-controller chips.
//   2. Talks MSP to confirm it's Betaflight and read the MCU's unique ID (identifies the drone).
//   3. Enters the CLI, runs "diff all", saves it to that drone's folder as Name_YYYY-MM-DD_HH-MM-SS.txt,
//      then sends "exit" (the FC reboots, same as leaving the CLI tab in Betaflight).
//   4. Launches Betaflight if it isn't already open.
// A port that disappears and comes back within a few seconds is a reboot (e.g. after Save in Betaflight), not a new
// plug-in, so it's left alone.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
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

    // ------------------------------------------------------------------------------------------------ files & settings

    static class AppPaths
    {
        public static readonly string Dir = Path.GetDirectoryName(Application.ExecutablePath);
        public static string Settings { get { return Path.Combine(Dir, "settings.json"); } }
        public static string Drones { get { return Path.Combine(Dir, "drones.json"); } }
        public static string Log { get { return Path.Combine(Dir, "QuadBackup.log"); } }
    }

    static class Log
    {
        static readonly object Gate = new object();

        public static void Write(string message)
        {
            lock (Gate)
            {
                try
                {
                    var file = AppPaths.Log;
                    if (File.Exists(file) && new FileInfo(file).Length > 1000000)
                    {
                        File.Copy(file, file + ".old", true);
                        File.Delete(file);
                    }
                    File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + "\r\n");
                }
                catch { }
            }
        }
    }

    static class Json
    {
        static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        public static T Load<T>(string path) where T : class
        {
            if (!File.Exists(path)) return null;
            return Serializer.Deserialize<T>(File.ReadAllText(path));
        }

        public static void Save(string path, object value)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Pretty(Serializer.Serialize(value)), new UTF8Encoding(false));
            File.Copy(tmp, path, true);
            File.Delete(tmp);
        }

        // JavaScriptSerializer only writes one long line; indent it so people can edit the files by hand.
        public static string Pretty(string json)
        {
            var sb = new StringBuilder();
            int indent = 0;
            bool inString = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < json.Length) sb.Append(json[++i]);
                    else if (c == '"') inString = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inString = true; sb.Append(c); break;
                    case '{':
                    case '[':
                        if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']')) { sb.Append(c).Append(json[++i]); break; }
                        sb.Append(c).Append("\r\n").Append(' ', ++indent * 2);
                        break;
                    case '}':
                    case ']': sb.Append("\r\n").Append(' ', --indent * 2).Append(c); break;
                    case ',': sb.Append(",\r\n").Append(' ', indent * 2); break;
                    case ':': sb.Append(": "); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }

    public class Settings
    {
        public string BackupRoot { get; set; }
        public bool LaunchBetaflight { get; set; }
        public string BetaflightLaunchPath { get; set; }
        public bool BackupWhenBetaflightCloses { get; set; }
        public bool ShowNotifications { get; set; }
        public string CliCommand { get; set; }
        public List<string> UsbVendorIds { get; set; }
        public List<string> FirmwareVariants { get; set; }
        public int RebootGraceSeconds { get; set; }

        public Settings()
        {
            BackupRoot = Path.Combine(AppPaths.Dir, "Backups");
            LaunchBetaflight = true;
            BetaflightLaunchPath = "";
            BackupWhenBetaflightCloses = true;
            ShowNotifications = true;
            CliCommand = "diff all";
            // STMicro (F4/F7/H7), Artery (AT32), Geehy (APM32). Other USB-serial gadgets (Arduinos, printers, radios
            // on CP210x/CH340) are never opened, because opening some of them resets them.
            UsbVendorIds = new List<string> { "0483", "2E3C", "314B" };
            FirmwareVariants = new List<string> { "BTFL" };
            RebootGraceSeconds = 8;
        }

        public static Settings Load()
        {
            Settings s = null;
            try { s = Json.Load<Settings>(AppPaths.Settings); }
            catch (Exception ex) { Log.Write("settings.json is invalid, using defaults: " + ex.Message); }
            return s ?? new Settings();
        }

        public static Settings LoadOrCreate(out bool firstRun)
        {
            firstRun = !File.Exists(AppPaths.Settings);
            var s = Load();
            if (string.IsNullOrEmpty(s.BetaflightLaunchPath)) s.BetaflightLaunchPath = Betaflight.FindLaunchTarget();
            try { s.Save(); } catch (Exception ex) { Log.Write("Couldn't write settings.json: " + ex.Message); }
            return s;
        }

        public void Save() { Json.Save(AppPaths.Settings, this); }
    }

    public class Drone
    {
        public string Name { get; set; }
        public string Folder { get; set; }
        public bool Ignored { get; set; }
        public string FcId { get; set; }
        public string CraftName { get; set; }
        public string Board { get; set; }
        public string Firmware { get; set; }
        public string FirstSeen { get; set; }
        public string LastBackup { get; set; }
    }

    public class DroneDb
    {
        public List<Drone> Drones { get; set; }

        public DroneDb() { Drones = new List<Drone>(); }

        public static DroneDb Load()
        {
            DroneDb db = null;
            try { lock (Gate) db = Json.Load<DroneDb>(AppPaths.Drones); }
            catch (Exception ex) { Log.Write("drones.json is invalid: " + ex.Message); }
            if (db == null) db = new DroneDb();
            if (db.Drones == null) db.Drones = new List<Drone>();
            return db;
        }

        public Drone Find(string fcId)
        {
            return Drones.FirstOrDefault(d => string.Equals(d.FcId, fcId, StringComparison.OrdinalIgnoreCase));
        }

        public Drone FindByCraftName(string craftName)
        {
            if (string.IsNullOrEmpty(craftName)) return null;
            return Drones.LastOrDefault(d => !d.Ignored && d.Folder != null &&
                (string.Equals(d.CraftName, craftName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(d.Name, craftName, StringComparison.OrdinalIgnoreCase)));
        }

        public void Save() { lock (Gate) Json.Save(AppPaths.Drones, this); }

        // The backup worker and the Hangar page both edit drones.json, so every write re-reads the file under a lock
        // and changes only the drone it means to.
        static readonly object Gate = new object();

        public static void Mutate(Action<DroneDb> change)
        {
            lock (Gate)
            {
                var db = Load();
                change(db);
                db.Save();
            }
        }

        public static void Upsert(Drone drone)
        {
            Mutate(db =>
            {
                int i = db.Drones.FindIndex(d => string.Equals(d.FcId, drone.FcId, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) db.Drones[i] = drone;
                else db.Drones.Add(drone);
            });
        }

        public static void Remove(string fcId)
        {
            Mutate(db => db.Drones.RemoveAll(d => string.Equals(d.FcId, fcId, StringComparison.OrdinalIgnoreCase)));
        }
    }

    // ------------------------------------------------------------------------------------------------ serial port

    public interface ILink : IDisposable
    {
        void Write(byte[] data);
        // Returns as soon as any bytes are available, or 0 after timeoutMs with nothing received.
        int Read(byte[] buffer, int timeoutMs);
    }

    public class PortBusyException : IOException
    {
        public PortBusyException(string message) : base(message) { }
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

    // ------------------------------------------------------------------------------------------------ talking to the FC

    public class NotAFlightControllerException : Exception
    {
        public NotAFlightControllerException(string message) : base(message) { }
    }

    public class FcBackup
    {
        public string FcId;       // MCU unique ID; null if the FC was found already sitting in CLI mode
        public string Variant;    // "BTFL"
        public string Firmware;   // "Betaflight 4.5.1"
        public string Board;      // "MATEKF405"
        public string CraftName;
        public string Cli;        // the diff, CRLF line endings
        public bool Complete;
    }

    public static class FcReader
    {
        const byte MSP_FC_VARIANT = 2;
        const byte MSP_UID = 160;
        public static int CliTimeoutMs = 30000;

        public static FcBackup Read(ILink link, string cliCommand, ICollection<string> variants)
        {
            var r = new FcBackup();
            bool ok;

            byte[] variant = null;
            for (int i = 0; i < 3 && variant == null; i++) variant = Msp(link, MSP_FC_VARIANT, 700);

            bool inCli = false;
            if (variant == null)
            {
                // No MSP reply. Either it's not an FC, or something left it in CLI mode (e.g. Betaflight was closed on the CLI tab).
                link.Write(Ascii("\n"));
                ReadUntilPrompt(link, 1000, 150, out ok);
                if (!ok) throw new NotAFlightControllerException("no reply to MSP");
                inCli = true;
            }
            else
            {
                r.Variant = Encoding.ASCII.GetString(variant, 0, Math.Min(4, variant.Length));
                if (!variants.Any(v => string.Equals(v, r.Variant, StringComparison.OrdinalIgnoreCase)))
                    throw new NotAFlightControllerException("firmware is " + r.Variant + ", not one of FirmwareVariants in settings.json");
                var uid = Msp(link, MSP_UID, 700);
                if (uid != null && uid.Length >= 12)
                    r.FcId = string.Format("{0:X8}{1:X8}{2:X8}", BitConverter.ToUInt32(uid, 0), BitConverter.ToUInt32(uid, 4), BitConverter.ToUInt32(uid, 8));
            }

            try
            {
                if (!inCli)
                {
                    inCli = true; // from here on, always try to send "exit" so the FC isn't left stuck in CLI mode
                    link.Write(Ascii("#"));
                    ReadUntilPrompt(link, 3000, 150, out ok);
                    if (!ok) throw new IOException("Flight controller didn't enter CLI mode");
                }
                link.Write(Ascii("\n")); // flush anything half-typed on the CLI line
                ReadUntilPrompt(link, 1000, 100, out ok);

                link.Write(Ascii(cliCommand + "\n"));
                var raw = ReadUntilPrompt(link, CliTimeoutMs, 500, out ok);
                r.Complete = ok;
                r.Cli = Clean(Encoding.UTF8.GetString(raw), cliCommand);
                Parse(r);
                if (string.IsNullOrWhiteSpace(r.Cli) || !r.Cli.Contains("\n"))
                    throw new IOException("Flight controller returned an empty backup");
            }
            finally
            {
                if (inCli)
                {
                    try { link.Write(Ascii("exit\n")); Thread.Sleep(200); } catch { }
                }
            }
            return r;
        }

        static byte[] Ascii(string s) { return Encoding.ASCII.GetBytes(s); }

        // MSP v1: $M< size cmd payload checksum  ->  $M> size cmd payload checksum ($M! = error)
        static byte[] Msp(ILink link, byte cmd, int timeoutMs)
        {
            link.Write(new byte[] { (byte)'$', (byte)'M', (byte)'<', 0, cmd, cmd });
            var buf = new List<byte>();
            var chunk = new byte[512];
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                int n = link.Read(chunk, 50);
                for (int i = 0; i < n; i++) buf.Add(chunk[i]);
                for (int i = 0; i + 6 <= buf.Count; i++)
                {
                    if (buf[i] != '$' || buf[i + 1] != 'M' || (buf[i + 2] != '>' && buf[i + 2] != '!')) continue;
                    int size = buf[i + 3];
                    if (i + 6 + size > buf.Count) break; // frame not fully received yet
                    byte check = (byte)(size ^ buf[i + 4]);
                    for (int j = 0; j < size; j++) check ^= buf[i + 5 + j];
                    if (check != buf[i + 5 + size] || buf[i + 4] != cmd) continue;
                    if (buf[i + 2] == '!') return null;
                    return buf.GetRange(i + 5, size).ToArray();
                }
            }
            return null;
        }

        // Reads until the output ends with the CLI prompt "\n# " and nothing more arrives for quietMs.
        // (diff output is full of lines starting with "# ", so the quiet period is what tells the prompt apart.)
        static byte[] ReadUntilPrompt(ILink link, int timeoutMs, int quietMs, out bool ok)
        {
            var buf = new MemoryStream();
            var chunk = new byte[4096];
            var total = Stopwatch.StartNew();
            var quiet = Stopwatch.StartNew();
            while (total.ElapsedMilliseconds < timeoutMs)
            {
                int n = link.Read(chunk, 50);
                if (n > 0) { buf.Write(chunk, 0, n); quiet.Restart(); continue; }
                if (EndsWithPrompt(buf) && quiet.ElapsedMilliseconds >= quietMs) { ok = true; return buf.ToArray(); }
            }
            ok = false;
            return buf.ToArray();
        }

        static bool EndsWithPrompt(MemoryStream ms)
        {
            var b = ms.GetBuffer();
            long n = ms.Length;
            return n >= 3 && b[n - 1] == ' ' && b[n - 2] == '#' && (b[n - 3] == '\n' || b[n - 3] == '\r');
        }

        static string Clean(string text, string cliCommand)
        {
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            int echo = text.IndexOf(cliCommand, StringComparison.Ordinal);
            if (echo >= 0)
            {
                int eol = text.IndexOf('\n', echo);
                text = eol >= 0 ? text.Substring(eol + 1) : "";
            }
            text = text.TrimEnd();
            if (text.EndsWith("\n#")) text = text.Substring(0, text.Length - 1).TrimEnd();
            text = "# " + cliCommand + "\n\n" + text.Trim('\n') + "\n";
            return text.Replace("\n", "\r\n");
        }

        static void Parse(FcBackup r)
        {
            var m = Regex.Match(r.Cli, @"^# (\w+) / (\S+) \(\w+\) (\S+)", RegexOptions.Multiline);
            if (m.Success)
            {
                r.Firmware = m.Groups[1].Value + " " + m.Groups[3].Value;
                r.Board = m.Groups[2].Value;
            }
            m = Regex.Match(r.Cli, @"^board_name\s+([^\r\n]+)", RegexOptions.Multiline);
            if (m.Success) r.Board = m.Groups[1].Value.Trim();
            m = Regex.Match(r.Cli, @"^set craft_name = ([^\r\n]*)", RegexOptions.Multiline);
            if (!m.Success) m = Regex.Match(r.Cli, @"^name ([^\r\n]*)", RegexOptions.Multiline);
            if (m.Success)
            {
                var name = m.Groups[1].Value.Trim();
                r.CraftName = name == "-" ? "" : name;
            }
            if (r.Variant == null && r.Firmware != null) r.Variant = r.Firmware.Split(' ')[0];
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

    // ------------------------------------------------------------------------------------------------ backups

    public static class BackupFiles
    {
        public static string SafeName(string name)
        {
            var bad = Path.GetInvalidFileNameChars();
            var clean = new string((name ?? "").Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().Trim('.').Trim();
            return clean.Length > 0 ? clean : "Drone";
        }

        public static string Write(string folder, string droneName, FcBackup b, DateTime when)
        {
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, SafeName(droneName) + "_" + when.ToString("yyyy-MM-dd_HH-mm-ss") +
                                            (b.Complete ? "" : "_INCOMPLETE") + ".txt");
            File.WriteAllText(file, b.Cli, new UTF8Encoding(false));
            return file;
        }

        public static bool SameAsLatest(string folder, string cli)
        {
            if (!Directory.Exists(folder)) return false;
            var latest = new DirectoryInfo(folder).GetFiles("*.txt")
                .Where(f => !f.Name.Contains("_INCOMPLETE"))
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            return latest != null && File.ReadAllText(latest.FullName) == cli;
        }
    }

    enum JobKind { PluggedIn, BetaflightClosed, Manual }

    class Job
    {
        public JobKind Kind;
        public string Port;
        public Job(JobKind kind, string port) { Kind = kind; Port = port; }
    }

    enum Outcome { NotFlightController, Busy, Skipped, Failed, Saved }

    class TrayApp : ApplicationContext
    {
        readonly Control ui;
        readonly NotifyIcon tray;
        readonly BlockingCollection<Job> jobs = new BlockingCollection<Job>();
        readonly Dictionary<string, string> portFcIds = new Dictionary<string, string>(); // worker thread only
        // Ports we just sent "exit" to: the next time each one reappears is its reboot, not a plug-in.
        readonly ConcurrentDictionary<string, DateTime> expectedReboots = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        volatile bool stopping;
        volatile int rebootGraceSeconds;
        volatile bool backupWhenBetaflightCloses;
        Settings settings;
        DroneDb db;
        MainWindow main;

        public TrayApp(EventWaitHandle showSignal, bool showWindow)
        {
            ui = new Control();
            ui.CreateControl();
            var forceHandle = ui.Handle;

            bool firstRun;
            settings = Settings.LoadOrCreate(out firstRun);
            ApplyLiveSettings();
            try { Directory.CreateDirectory(settings.BackupRoot); } catch { }
            try
            {
                // first run turns on start-with-Windows; later runs refresh the entry in case the exe moved
                if (firstRun || Startup.IsEnabled()) Startup.Set(true);
            }
            catch (Exception ex) { Log.Write("Couldn't update start with Windows: " + ex.Message); }

            tray = new NotifyIcon { Icon = Art.MakeClassicIcon(32), Text = "Quad Backup - click to open", Visible = true };
            tray.ContextMenuStrip = BuildMenu();
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMain(MainWindow.Page.Hangar); };

            new Thread(MonitorLoop) { IsBackground = true, Name = "monitor" }.Start();
            new Thread(WorkerLoop) { IsBackground = true, Name = "worker" }.Start();
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
                main = new MainWindow(() => jobs.Add(new Job(JobKind.Manual, null)), () => { settings = Settings.Load(); ApplyLiveSettings(); });
            main.ShowOn(page);
        }

        void ApplyLiveSettings()
        {
            rebootGraceSeconds = Math.Max(2, settings.RebootGraceSeconds);
            backupWhenBetaflightCloses = settings.BackupWhenBetaflightCloses;
        }

        ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip { Renderer = new MenuRenderer(), Font = new Font("Segoe UI", 9.5f), ImageScalingSize = new Size(20, 20) };
            menu.Items.Add(new ToolStripMenuItem("Quad Backup · on patrol") { Enabled = false, Tag = "header", Image = Art.DrawClassic(40) });
            menu.Items.Add(new ToolStripSeparator());
            var open = new ToolStripMenuItem("Open Quad Backup", null, (s, e) => ShowMain(MainWindow.Page.Hangar));
            open.Font = new Font(menu.Font, FontStyle.Bold);
            menu.Items.Add(open);
            menu.Items.Add("Back up the plugged-in quad now", null, (s, e) => jobs.Add(new Job(JobKind.Manual, null)));
            menu.Items.Add("Open backups folder", null, (s, e) => OpenFolder(settings.BackupRoot));
            menu.Items.Add("Launch Betaflight", null, (s, e) => LaunchBetaflight());
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
            if (main != null && !main.IsDisposed) main.Dispose();
            jobs.CompleteAdding();
            tray.Visible = false;
            tray.Dispose();
            Log.Write("Exited");
            base.ExitThreadCore();
        }

        // Polls the COM port list. Every new port is a plug-in, except a port coming back from a reboot we know about:
        // our own "exit" after a backup, or (while Betaflight is open) a quick return after Save.
        // A fast unplug/replug with Betaflight closed looks identical to a reboot, so timing alone isn't trusted.
        void MonitorLoop()
        {
            var present = new HashSet<string>(Ports.List(), StringComparer.OrdinalIgnoreCase);
            var lastSeen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in present) lastSeen[p] = DateTime.UtcNow;
            bool betaflightWasRunning = SafeIsBetaflightRunning();
            var nextBetaflightCheck = DateTime.UtcNow;

            while (!stopping)
            {
                var now = DateTime.UtcNow;
                var ports = Ports.List();
                foreach (var port in ports)
                {
                    if (present.Contains(port)) continue;
                    DateTime seen, rebootDeadline;
                    if (expectedReboots.TryRemove(port, out rebootDeadline) && now < rebootDeadline)
                        Log.Write(port + " came back after the backup reboot");
                    else if (betaflightWasRunning && lastSeen.TryGetValue(port, out seen) && (now - seen).TotalSeconds < rebootGraceSeconds)
                        Log.Write(port + " came back quickly while Betaflight is open - treating it as a reboot, not a plug-in");
                    else
                    {
                        Log.Write(port + " plugged in");
                        jobs.Add(new Job(JobKind.PluggedIn, port));
                    }
                }
                present = new HashSet<string>(ports, StringComparer.OrdinalIgnoreCase);
                foreach (var p in ports) lastSeen[p] = now;

                if (now >= nextBetaflightCheck)
                {
                    nextBetaflightCheck = now.AddSeconds(2);
                    bool running = SafeIsBetaflightRunning();
                    if (betaflightWasRunning && !running && backupWhenBetaflightCloses && present.Count > 0)
                    {
                        Log.Write("Betaflight closed");
                        jobs.Add(new Job(JobKind.BetaflightClosed, null));
                    }
                    betaflightWasRunning = running;
                }
                Thread.Sleep(300);
            }
        }

        static bool SafeIsBetaflightRunning()
        {
            try { return Betaflight.IsRunning(); } catch { return false; }
        }

        void WorkerLoop()
        {
            foreach (var job in jobs.GetConsumingEnumerable())
            {
                try { RunJob(job); }
                catch (Exception ex) { Log.Write("Job failed: " + ex); }
            }
        }

        void RunJob(Job job)
        {
            settings = Settings.Load();
            ApplyLiveSettings();
            db = DroneDb.Load();

            if (job.Kind == JobKind.PluggedIn)
            {
                Thread.Sleep(800); // let the FC finish booting
                if (!Ports.List().Contains(job.Port, StringComparer.OrdinalIgnoreCase)) return;
                var outcome = BackupPort(job.Port, job.Kind);
                if (outcome != Outcome.NotFlightController && outcome != Outcome.Busy && settings.LaunchBetaflight && !SafeIsBetaflightRunning())
                    LaunchBetaflight();
                return;
            }

            if (job.Kind == JobKind.BetaflightClosed) Thread.Sleep(1500); // give the browser a moment to let go of the port
            int found = 0;
            foreach (var port in Ports.List())
            {
                var outcome = BackupPort(port, job.Kind);
                if (outcome != Outcome.NotFlightController) found++;
            }
            if (job.Kind == JobKind.Manual && found == 0)
                Notify(ToastKind.Info, "No quads here...", "Plug a flight controller in over USB and try again.", null);
        }

        Outcome BackupPort(string port, JobKind kind)
        {
            var vid = Ports.GetVendorId(port);
            if (vid == null || !settings.UsbVendorIds.Any(v => string.Equals(v, vid, StringComparison.OrdinalIgnoreCase)))
            {
                Log.Write(port + ": USB vendor " + (vid ?? "unknown") + " isn't a flight-controller chip - ignoring");
                return Outcome.NotFlightController;
            }

            FcBackup b;
            // Expect the reboot from "exit" before it can happen; withdrawn below if the FC never got that far.
            expectedReboots[port] = DateTime.UtcNow.AddSeconds(60);
            try
            {
                using (var link = new SerialLink(port))
                    b = FcReader.Read(link, settings.CliCommand, settings.FirmwareVariants);
                expectedReboots[port] = DateTime.UtcNow.AddSeconds(20);
            }
            catch (PortBusyException)
            {
                DateTime ignored;
                expectedReboots.TryRemove(port, out ignored);
                Log.Write(port + ": in use by another program (Betaflight connected first)");
                if (kind == JobKind.PluggedIn)
                    Notify(ToastKind.Warning, "Betaflight beat me to it!", "It grabbed " + port + " first." +
                        (backupWhenBetaflightCloses ? " I'll back it up when you close Betaflight." : ""), null);
                else if (kind == JobKind.Manual)
                    Notify(ToastKind.Warning, "Betaflight has dibs on " + port, "Disconnect it in Betaflight first, then try again.", null);
                return Outcome.Busy;
            }
            catch (NotAFlightControllerException ex)
            {
                DateTime ignored;
                expectedReboots.TryRemove(port, out ignored);
                Log.Write(port + ": not backed up - " + ex.Message);
                return Outcome.NotFlightController;
            }
            catch (Exception ex)
            {
                Log.Write(port + ": backup failed - " + ex);
                Notify(ToastKind.Error, "Oof. Backup failed on " + port, ex.Message, null);
                return Outcome.Failed;
            }

            if (b.FcId == null)
            {
                string cached;
                if (!portFcIds.TryGetValue(port, out cached))
                {
                    Log.Write(port + ": FC was stuck in CLI mode and couldn't be identified; unplug and replug it");
                    Notify(ToastKind.Warning, "Who goes there?", "Couldn't tell which drone is on " + port + ". Unplug it and plug it back in.", null);
                    return Outcome.Failed;
                }
                b.FcId = cached;
            }
            portFcIds[port] = b.FcId;
            Log.Write(port + ": read " + (b.Firmware ?? b.Variant) + " " + b.Board + " '" + b.CraftName + "' id " + b.FcId +
                      " (" + b.Cli.Length + " chars" + (b.Complete ? "" : ", INCOMPLETE") + ")");

            var drone = db.Find(b.FcId);
            if (drone != null && drone.Ignored)
            {
                Log.Write(port + ": drone is set to never back up");
                return Outcome.Skipped;
            }
            if (drone == null || string.IsNullOrEmpty(drone.Folder) || !Directory.Exists(drone.Folder))
            {
                var choice = AskWhereToSave(b, port, drone);
                if (choice == null)
                {
                    Log.Write(port + ": skipped this time");
                    return Outcome.Skipped;
                }
                if (drone == null)
                {
                    drone = new Drone { FcId = b.FcId, FirstSeen = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
                }
                if (choice.Never)
                {
                    drone.Ignored = true;
                    drone.Name = string.IsNullOrEmpty(b.CraftName) ? b.Board : b.CraftName;
                    DroneDb.Upsert(drone);
                    Log.Write(port + ": drone " + b.FcId + " set to never back up");
                    return Outcome.Skipped;
                }
                Directory.CreateDirectory(choice.Folder);
                drone.Folder = choice.Folder;
                drone.Name = Path.GetFileName(choice.Folder.TrimEnd('\\', '/'));
            }
            drone.CraftName = b.CraftName;
            drone.Board = b.Board;
            drone.Firmware = b.Firmware;

            if (kind == JobKind.BetaflightClosed && BackupFiles.SameAsLatest(drone.Folder, b.Cli))
            {
                Log.Write(port + ": " + drone.Name + " unchanged since last backup");
                DroneDb.Upsert(drone);
                return Outcome.Saved;
            }

            var file = BackupFiles.Write(drone.Folder, drone.Name, b, DateTime.Now);
            drone.LastBackup = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            DroneDb.Upsert(drone);
            Log.Write(port + ": saved " + file);
            if (b.Complete)
                Notify(ToastKind.Success, drone.Name + " is backed up!", Quips.ForSave() + "\n" + Path.GetFileName(file), drone.Folder);
            else
                Notify(ToastKind.Warning, "Partial backup of " + drone.Name, "The FC stopped talking partway through. Check the file:\n" + Path.GetFileName(file), drone.Folder);
            return Outcome.Saved;
        }

        NewDroneChoice AskWhereToSave(FcBackup b, string port, Drone known)
        {
            string name, location, note = null;
            var mood = DroneMood.New;
            var sameName = known == null ? db.FindByCraftName(b.CraftName) : null;
            if (known != null && !string.IsNullOrEmpty(known.Folder))
            {
                name = known.Name;
                location = Path.GetDirectoryName(known.Folder);
                note = "It used to live here, but the folder's gone:\n" + known.Folder;
                mood = DroneMood.MissingFolder;
            }
            else if (sameName != null)
            {
                name = Path.GetFileName(sameName.Folder);
                location = Path.GetDirectoryName(sameName.Folder);
                mood = DroneMood.NewFlightController;
            }
            else
            {
                name = !string.IsNullOrEmpty(b.CraftName) ? b.CraftName : (b.Board ?? "Drone");
                location = settings.BackupRoot;
            }
            if (string.IsNullOrEmpty(location) || !Directory.Exists(location)) location = settings.BackupRoot;
            return (NewDroneChoice)ui.Invoke(new Func<NewDroneChoice>(() => NewDroneForm.Ask(b, port, BackupFiles.SafeName(name), location, note, mood)));
        }

        void LaunchBetaflight()
        {
            try
            {
                Betaflight.Launch(Settings.Load().BetaflightLaunchPath);
                Log.Write("Launched Betaflight");
            }
            catch (Exception ex)
            {
                Log.Write("Couldn't launch Betaflight: " + ex.Message);
                Notify(ToastKind.Error, "Couldn't launch Betaflight", "Check BetaflightLaunchPath in settings.json.", null);
            }
        }

        void Notify(ToastKind kind, string title, string text, string folder)
        {
            if (!settings.ShowNotifications) return;
            Action click = folder == null ? null : new Action(() => OpenFolder(folder));
            ui.BeginInvoke(new Action(() => Toast.Pop(kind, title, text, click)));
        }

        static void OpenFolder(string folder)
        {
            try { Directory.CreateDirectory(folder); Process.Start("explorer.exe", "\"" + folder + "\""); } catch { }
        }

        static void OpenInNotepad(string file)
        {
            try
            {
                if (!File.Exists(file)) File.WriteAllText(file, "");
                Process.Start("notepad.exe", "\"" + file + "\"");
            }
            catch { }
        }
    }
}
