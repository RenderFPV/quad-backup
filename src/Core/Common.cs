// Shared by the Windows app and the Mac/Linux app: where files live, the log, settings, the drone list and backup files.
// Keep this file C# 5 compatible - the Windows build uses the compiler that ships with Windows.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
#if NET
using System.Text.Json;
#else
using System.Web.Script.Serialization;
#endif

namespace QuadBackup
{
    public static class AppPaths
    {
        public static readonly bool IsWindows = Path.DirectorySeparatorChar == '\\';
        public static readonly bool IsMac = !IsWindows && File.Exists("/System/Library/CoreServices/SystemVersion.plist");
        public static readonly string Dir = ResolveDir();

        public static string Settings { get { return Path.Combine(Dir, "settings.json"); } }
        public static string Drones { get { return Path.Combine(Dir, "drones.json"); } }
        public static string Log { get { return Path.Combine(Dir, "QuadBackup.log"); } }

        // Windows keeps everything next to the exe (portable). Mac/Linux use the usual per-user config folder.
        static string ResolveDir()
        {
            if (IsWindows) return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string dir;
            if (IsMac) dir = Path.Combine(home, "Library", "Application Support", "Quad Backup");
            else
            {
                var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                dir = Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg, "quad-backup");
            }
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string DefaultBackupRoot
        {
            get
            {
                if (IsWindows) return Path.Combine(Dir, "Backups");
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var docs = Path.Combine(home, "Documents");
                return Path.Combine(Directory.Exists(docs) ? docs : home, "Quad Backups");
            }
        }
    }

    public static class Log
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
                    File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
                }
                catch { }
            }
        }
    }

    public static class Json
    {
#if NET
        static readonly JsonSerializerOptions Options = new JsonSerializerOptions { WriteIndented = true };

        public static T Load<T>(string path) where T : class
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        }

        public static void Save(string path, object value)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, value.GetType(), Options), new UTF8Encoding(false));
            File.Copy(tmp, path, true);
            File.Delete(tmp);
        }
#else
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
#endif

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
        public bool BackupWhenDisconnected { get; set; }
        public int DisconnectWaitSeconds { get; set; }
        public bool ShowNotifications { get; set; }
        public string CliCommand { get; set; }
        public List<string> UsbVendorIds { get; set; }
        public List<string> FirmwareVariants { get; set; }
        public int RebootGraceSeconds { get; set; }

        public Settings()
        {
            BackupRoot = AppPaths.DefaultBackupRoot;
            LaunchBetaflight = true;
            BetaflightLaunchPath = "";
            BackupWhenBetaflightCloses = true;
            BackupWhenDisconnected = true;
            // How long the port must stay free, without the quad rebooting, to count as you clicking Disconnect.
            // After a reboot (Save, CLI, Blackbox, flashing) the wait is at least RebootGraceSeconds instead.
            DisconnectWaitSeconds = 2;
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
            if (s == null) s = new Settings();
            if (s.UsbVendorIds == null) s.UsbVendorIds = new Settings().UsbVendorIds;
            if (s.FirmwareVariants == null) s.FirmwareVariants = new Settings().FirmwareVariants;
            return s;
        }

        public static Settings LoadOrCreate(out bool firstRun, Func<string> findLaunchTarget)
        {
            firstRun = !File.Exists(AppPaths.Settings);
            var s = Load();
            if (string.IsNullOrEmpty(s.BetaflightLaunchPath)) s.BetaflightLaunchPath = findLaunchTarget();
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

        // The backup worker and the Hangar page both edit drones.json, so every write re-reads the file under a lock
        // and changes only the drone it means to.
        static readonly object Gate = new object();

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

    public static class BackupFiles
    {
        public static string SafeName(string name)
        {
            // also strip characters Windows rejects, so a backup folder made on a Mac still syncs to a Windows PC
            var bad = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").ToArray();
            var clean = new string((name ?? "").Select(c => bad.Contains(c) || c < 32 ? '_' : c).ToArray()).Trim().Trim('.').Trim();
            return clean.Length > 0 ? clean : "Drone";
        }

        public const string SpecialTag = "_SPECIAL_";

        // specialName != null makes it a named special backup: Name_YYYY-MM-DD_HH-MM-SS_SPECIAL_<name>.txt
        public static string Write(string folder, string droneName, FcBackup b, DateTime when, string specialName = null)
        {
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, SafeName(droneName) + "_" + when.ToString("yyyy-MM-dd_HH-mm-ss") +
                                            (specialName != null ? SpecialTag + SafeName(specialName) : "") +
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

    // ------------------------------------------------------------------------------------------------ shared UI vocabulary

    public enum ToastKind { Success, Info, Warning, Error, Special }

    public enum DroneMood { New, NewFlightController, MissingFolder }

    public class NewDroneChoice
    {
        public bool Never;
        public string Folder;
    }

    public static class Quips
    {
        static readonly Random Rnd = new Random();
        static readonly string[] Saved =
        {
            "Tune secured. Go send it.",
            "Your PIDs are safe with me.",
            "Saved! Now crash responsibly.",
            "Filed away in the hangar.",
            "Snapshot taken. Rip that gap.",
            "Backed up. Props on, fear off.",
            "Safe and sound. Unlike your props.",
        };

        static readonly string[] SpecialSaved =
        {
            "Framed and hung on the hangar wall.",
            "Hall of fame material.",
            "This one's a keeper.",
            "Saved with extra sparkle.",
            "Certified banger tune.",
        };

        public static string ForSave() { lock (Rnd) return Saved[Rnd.Next(Saved.Length)]; }
        public static string ForSpecial() { lock (Rnd) return SpecialSaved[Rnd.Next(SpecialSaved.Length)]; }
    }
}
