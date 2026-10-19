// Shared by the Windows app and the Mac/Linux app: the MSP + CLI conversation that reads a "diff all" from a flight
// controller. Keep this file C# 5 compatible - the Windows build uses the compiler that ships with Windows.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace QuadBackup
{
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
        public bool Rebooted;     // FC rebooted when leaving the CLI, so its COM port will drop out and come back
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
                    // "exit noreboot" hands the port straight back to MSP. Firmware that doesn't know "noreboot" just
                    // reboots, which is harmless; the caller needs to know which happened (see Rebooted).
                    try
                    {
                        link.Write(Ascii("exit noreboot\n"));
                        var tail = Encoding.ASCII.GetString(ReadFor(link, 250));
                        r.Rebooted = tail.IndexOf("rebooting", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    catch { r.Rebooted = true; } // the port vanished mid-read: it rebooted
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
        // A diff/dump ends with "batch end" followed by the prompt; once that's in, there's nothing left to wait for.
        static byte[] ReadUntilPrompt(ILink link, int timeoutMs, int quietMs, out bool ok)
        {
            var buf = new MemoryStream();
            var chunk = new byte[4096];
            var total = Stopwatch.StartNew();
            var quiet = Stopwatch.StartNew();
            while (total.ElapsedMilliseconds < timeoutMs)
            {
                int n = link.Read(chunk, 20);
                if (n > 0) { buf.Write(chunk, 0, n); quiet.Restart(); continue; }
                if (EndsWithPrompt(buf) && (quiet.ElapsedMilliseconds >= quietMs || EndsWithBatchEnd(buf))) { ok = true; return buf.ToArray(); }
            }
            ok = false;
            return buf.ToArray();
        }

        static bool EndsWithBatchEnd(MemoryStream ms)
        {
            int n = (int)Math.Min(ms.Length, 32);
            var tail = Encoding.ASCII.GetString(ms.GetBuffer(), (int)ms.Length - n, n);
            return Regex.IsMatch(tail, @"\nbatch end\s*\n# $");
        }

        static byte[] ReadFor(ILink link, int ms)
        {
            var buf = new MemoryStream();
            var chunk = new byte[1024];
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                int n = link.Read(chunk, 50);
                if (n > 0) buf.Write(chunk, 0, n);
            }
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
}
