// Shared by the Windows app and the Mac/Linux app: watches serial ports, decides when to back up (plug-in, disconnect
// in Betaflight, Betaflight closing, "Back up now", special backups) and writes the files. Everything OS-specific goes
// through IPlatform, and everything on screen through IEngineUi.
// Keep this file C# 5 compatible - the Windows build uses the compiler that ships with Windows.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace QuadBackup
{
    public interface IPlatform
    {
        string[] ListPorts();
        string GetVendorId(string port);   // USB vendor ID like "0483", or null if unknown
        ILink Open(string port);           // throws PortBusyException if another program has it open
        bool? IsInUse(string port);        // true = another program has it open, false = free, null = unknown/gone
        bool IsBetaflightRunning();
        string FindLaunchTarget();
        void Launch(string target);
        bool ProbeAlways { get; }          // check ports for a Betaflight connection even when Betaflight isn't seen running
        int ProbeIntervalMs { get; }
    }

    public interface IEngineUi
    {
        void Notify(ToastKind kind, string title, string text, string folder);
        // Blocks until the person answers. Returns null for "skip this time".
        NewDroneChoice AskWhereToSave(FcBackup b, string port, string suggestedName, string location, string note, DroneMood mood);
    }

    public enum JobKind { PluggedIn, BetaflightClosed, Disconnected, Manual, Special }

    // What the monitor knows about one flight controller's port while Betaflight might be using it.
    class PortWatch
    {
        public bool SeenInUse;      // Betaflight (or another app) has had it open since the last backup
        public DateTime? FreeSince; // when it was first seen free again
        public bool Rebooted;       // the port vanished and came back since Betaflight last had it (Save, CLI, flashing...)
    }

    class Job
    {
        public JobKind Kind;
        public string Port;
        public string SpecialName; // for JobKind.Special
        public Job(JobKind kind, string port) { Kind = kind; Port = port; }
    }

    public enum Outcome { NotFlightController, Busy, Skipped, Failed, Saved }

    public class BackupEngine
    {
        readonly IPlatform platform;
        readonly IEngineUi ui;
        readonly BlockingCollection<Job> jobs = new BlockingCollection<Job>();
        readonly ConcurrentDictionary<string, string> portFcIds = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Special backups asked for while Betaflight had the quad: FC id (or "port:COMx" if not identified yet) -> name.
        // The next backup of that quad - normally the one when you disconnect - gets the name.
        readonly ConcurrentDictionary<string, string> pendingSpecial = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Ports we just sent "exit" to: the next time each one reappears is its reboot, not a plug-in.
        readonly ConcurrentDictionary<string, DateTime> expectedReboots = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        // Ports known to have a flight controller on them; the monitor watches these for Betaflight disconnects.
        readonly ConcurrentDictionary<string, bool> fcPorts = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        // Held while the worker has a port open, so the monitor's in-use check never collides with a backup.
        readonly object portGate = new object();
        volatile bool stopping;
        volatile int rebootGraceSeconds;
        volatile bool backupWhenBetaflightCloses;
        volatile bool backupWhenDisconnected;
        volatile int disconnectWaitSeconds;
        volatile Settings settings;
        DroneDb db;

        public int PlugInSettleMs = 800; // let a freshly plugged-in FC finish booting before talking to it
        public int MonitorSleepMs = 200;

        public BackupEngine(IPlatform platform, IEngineUi ui)
        {
            this.platform = platform;
            this.ui = ui;
            settings = Settings.Load();
            ApplyLiveSettings();
        }

        public Settings Settings { get { return settings; } }

        public void Start()
        {
            new Thread(MonitorLoop) { IsBackground = true, Name = "monitor" }.Start();
            new Thread(WorkerLoop) { IsBackground = true, Name = "worker" }.Start();
        }

        public void Stop()
        {
            stopping = true;
            jobs.CompleteAdding();
        }

        public void ReloadSettings()
        {
            settings = Settings.Load();
            ApplyLiveSettings();
        }

        void ApplyLiveSettings()
        {
            rebootGraceSeconds = Math.Max(2, settings.RebootGraceSeconds);
            backupWhenBetaflightCloses = settings.BackupWhenBetaflightCloses;
            backupWhenDisconnected = settings.BackupWhenDisconnected;
            disconnectWaitSeconds = Math.Max(1, settings.DisconnectWaitSeconds);
        }

        // ------------------------------------------------------------------ requests from the UI

        public void BackupNow() { if (!jobs.IsAddingCompleted) jobs.Add(new Job(JobKind.Manual, null)); }

        // Which plugged-in quad a special backup is for. False if no flight controller is plugged in.
        public bool FindSpecialTarget(string fcId, out string port, out string droneName)
        {
            var candidates = platform.ListPorts().Where(p => fcPorts.ContainsKey(p)).ToList();
            port = null;
            droneName = null;
            if (fcId != null)
                port = candidates.FirstOrDefault(p => { string id; return portFcIds.TryGetValue(p, out id) && string.Equals(id, fcId, StringComparison.OrdinalIgnoreCase); });
            if (port == null) port = candidates.FirstOrDefault();
            if (port == null) return false;
            string knownId;
            if (portFcIds.TryGetValue(port, out knownId))
            {
                var d = DroneDb.Load().Find(knownId);
                if (d != null) droneName = d.Name;
            }
            return true;
        }

        public void QueueSpecial(string port, string name)
        {
            if (!jobs.IsAddingCompleted) jobs.Add(new Job(JobKind.Special, port) { SpecialName = name });
        }

        public string PendingSpecial(string fcId)
        {
            string name;
            if (pendingSpecial.TryGetValue(fcId, out name)) return name;
            foreach (var kv in portFcIds) // waiting under the port before the FC was identified
                if (string.Equals(kv.Value, fcId, StringComparison.OrdinalIgnoreCase) && pendingSpecial.TryGetValue("port:" + kv.Key, out name))
                    return name;
            return null;
        }

        public bool IsPluggedIn(string fcId)
        {
            var present = platform.ListPorts();
            return portFcIds.Any(kv => string.Equals(kv.Value, fcId, StringComparison.OrdinalIgnoreCase) &&
                                       present.Contains(kv.Key, StringComparer.OrdinalIgnoreCase));
        }

        public void LaunchBetaflight()
        {
            try
            {
                var target = Settings.Load().BetaflightLaunchPath;
                platform.Launch(string.IsNullOrEmpty(target) ? platform.FindLaunchTarget() : target);
                Log.Write("Launched Betaflight");
            }
            catch (Exception ex)
            {
                Log.Write("Couldn't launch Betaflight: " + ex.Message);
                Notify(ToastKind.Error, "Couldn't launch Betaflight", "Check which Betaflight app to open in Settings.", null);
            }
        }

        bool HasPendingSpecial(string port)
        {
            string id;
            return pendingSpecial.ContainsKey("port:" + port) || (portFcIds.TryGetValue(port, out id) && pendingSpecial.ContainsKey(id));
        }

        // Takes (removes) the special name waiting for this quad, if any.
        string TakePendingSpecial(string port, string fcId)
        {
            string name;
            if (fcId != null && pendingSpecial.TryRemove(fcId, out name)) return name;
            if (pendingSpecial.TryRemove("port:" + port, out name)) return name;
            return null;
        }

        // ------------------------------------------------------------------ watching ports

        // Polls the port list. Every new port is a plug-in, except a port coming back from a reboot we know about:
        // our own "exit" after a backup, or (while Betaflight is open) a quick return after Save.
        // A fast unplug/replug with Betaflight closed looks identical to a reboot, so timing alone isn't trusted.
        void MonitorLoop()
        {
            var present = new HashSet<string>(SafeListPorts(), StringComparer.OrdinalIgnoreCase);
            var lastSeen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            var watches = new Dictionary<string, PortWatch>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in present)
            {
                lastSeen[p] = DateTime.UtcNow;
                if (IsFlightControllerVendor(p)) fcPorts[p] = true; // FCs already plugged in when the app started
            }
            bool betaflightWasRunning = SafeIsBetaflightRunning();
            var nextBetaflightCheck = DateTime.UtcNow;
            var nextProbe = DateTime.UtcNow;

            while (!stopping)
            {
                var now = DateTime.UtcNow;
                var ports = SafeListPorts();
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
                        watches.Remove(port); // fresh plug-in: its own backup covers it
                        jobs.Add(new Job(JobKind.PluggedIn, port));
                    }
                }
                present = new HashSet<string>(ports, StringComparer.OrdinalIgnoreCase);
                foreach (var p in ports) lastSeen[p] = now;

                if (now >= nextBetaflightCheck)
                {
                    nextBetaflightCheck = now.AddSeconds(2);
                    bool running = SafeIsBetaflightRunning();
                    // with disconnect backups on, closing Betaflight frees the port and is caught as a disconnect
                    if (betaflightWasRunning && !running && backupWhenBetaflightCloses && !backupWhenDisconnected && present.Count > 0)
                    {
                        Log.Write("Betaflight closed");
                        jobs.Add(new Job(JobKind.BetaflightClosed, null));
                    }
                    betaflightWasRunning = running;
                }

                // a waiting special backup needs the disconnect watcher even if disconnect backups are switched off
                if (now >= nextProbe && (backupWhenDisconnected || !pendingSpecial.IsEmpty))
                {
                    nextProbe = now.AddMilliseconds(platform.ProbeIntervalMs);
                    WatchForDisconnects(present, watches, betaflightWasRunning, now);
                }
                Thread.Sleep(MonitorSleepMs);
            }
        }

        // A flight controller's port going from "in use" to "free" while still plugged in means it was disconnected in
        // Betaflight. Clicking Disconnect leaves the USB port in place, so a short DisconnectWaitSeconds is enough.
        // When Betaflight reboots the quad itself (Save, leaving the CLI tab, Blackbox, flashing) the port vanishes and
        // comes back, and Betaflight reconnects on its own a few seconds later; after that we wait at least
        // RebootGraceSeconds so we never grab the port out from under its reconnect.
        void WatchForDisconnects(HashSet<string> present, Dictionary<string, PortWatch> watches, bool betaflightRunning, DateTime now)
        {
            foreach (var port in fcPorts.Keys)
            {
                PortWatch w;
                if (!watches.TryGetValue(port, out w)) watches[port] = w = new PortWatch();
                if (!present.Contains(port))
                {
                    // rebooting or unplugged
                    w.FreeSince = null;
                    if (w.SeenInUse) w.Rebooted = true;
                    continue;
                }
                if (!betaflightRunning && !w.SeenInUse && !platform.ProbeAlways) continue; // nothing could have it open

                bool? inUse;
                if (!Monitor.TryEnter(portGate)) continue; // we're backing something up right now
                try { inUse = platform.IsInUse(port); }
                catch { inUse = null; }
                finally { Monitor.Exit(portGate); }

                if (inUse == true)
                {
                    if (!w.SeenInUse) Log.Write(port + ": connected in Betaflight");
                    w.SeenInUse = true;
                    w.Rebooted = false;
                    w.FreeSince = null;
                }
                else if (inUse == false && w.SeenInUse)
                {
                    int wait = w.Rebooted ? Math.Max(disconnectWaitSeconds, rebootGraceSeconds) : disconnectWaitSeconds;
                    if (w.FreeSince == null) w.FreeSince = now;
                    else if ((now - w.FreeSince.Value).TotalSeconds >= wait)
                    {
                        Log.Write(port + ": disconnected in Betaflight" + (w.Rebooted ? " (after a reboot, Betaflight didn't reconnect)" : ""));
                        w.SeenInUse = false;
                        w.Rebooted = false;
                        w.FreeSince = null;
                        if (backupWhenDisconnected || HasPendingSpecial(port)) jobs.Add(new Job(JobKind.Disconnected, port));
                    }
                }
            }
        }

        string[] SafeListPorts()
        {
            try { return platform.ListPorts(); } catch { return new string[0]; }
        }

        bool IsFlightControllerVendor(string port)
        {
            string vid = null;
            try { vid = platform.GetVendorId(port); } catch { }
            return vid != null && settings.UsbVendorIds.Any(v => string.Equals(v, vid, StringComparison.OrdinalIgnoreCase));
        }

        bool SafeIsBetaflightRunning()
        {
            try { return platform.IsBetaflightRunning(); } catch { return false; }
        }

        // ------------------------------------------------------------------ backing up

        void WorkerLoop()
        {
            foreach (var job in jobs.GetConsumingEnumerable())
            {
                try { RunJob(job); }
                catch (Exception ex) { Log.Write("Job failed: " + ex); }
            }
        }

        bool IsPresent(string port)
        {
            return SafeListPorts().Contains(port, StringComparer.OrdinalIgnoreCase);
        }

        void RunJob(Job job)
        {
            settings = Settings.Load();
            ApplyLiveSettings();
            db = DroneDb.Load();

            if (job.Kind == JobKind.PluggedIn)
            {
                Thread.Sleep(PlugInSettleMs);
                if (!IsPresent(job.Port)) return;
                var outcome = BackupPort(job.Port, job.Kind);
                if (outcome != Outcome.NotFlightController && outcome != Outcome.Busy && settings.LaunchBetaflight && !SafeIsBetaflightRunning())
                    LaunchBetaflight();
                return;
            }

            if (job.Kind == JobKind.Disconnected || job.Kind == JobKind.Special)
            {
                if (IsPresent(job.Port)) BackupPort(job.Port, job.Kind, job.SpecialName);
                else if (job.Kind == JobKind.Special)
                    Notify(ToastKind.Warning, "Where'd it go?", "The quad was unplugged before I could save \"" + job.SpecialName + "\". Plug it back in and try again.", null);
                return;
            }

            if (job.Kind == JobKind.BetaflightClosed) Thread.Sleep(1500); // give the browser a moment to let go of the port
            int found = 0;
            foreach (var port in SafeListPorts())
            {
                var outcome = BackupPort(port, job.Kind);
                if (outcome != Outcome.NotFlightController) found++;
            }
            if (job.Kind == JobKind.Manual && found == 0)
                Notify(ToastKind.Info, "No quads here...", "Plug a flight controller in over USB and try again.", null);
        }

        Outcome BackupPort(string port, JobKind kind, string specialName = null)
        {
            var timer = Stopwatch.StartNew();
            // A disconnect or special backup comes from a port we've already confirmed is a flight controller, so the
            // (slow) USB vendor lookup can be skipped; the MSP check below still confirms it's Betaflight.
            if (!((kind == JobKind.Disconnected || kind == JobKind.Special) && fcPorts.ContainsKey(port)))
            {
                string vid = null;
                try { vid = platform.GetVendorId(port); } catch (Exception ex) { Log.Write("Device lookup failed for " + port + ": " + ex.Message); }
                if (vid == null || !settings.UsbVendorIds.Any(v => string.Equals(v, vid, StringComparison.OrdinalIgnoreCase)))
                {
                    Log.Write(port + ": USB vendor " + (vid ?? "unknown") + " isn't a flight-controller chip - ignoring");
                    return Outcome.NotFlightController;
                }
            }

            FcBackup b;
            DateTime ignored;
            // Older firmware reboots on leaving the CLI; expect that before it can happen, and withdraw it below if the
            // FC didn't reboot (newer firmware with "exit noreboot", or it never got as far as the CLI).
            expectedReboots[port] = DateTime.UtcNow.AddSeconds(60);
            try
            {
                lock (portGate)
                    using (var link = platform.Open(port))
                        b = FcReader.Read(link, settings.CliCommand, settings.FirmwareVariants);
                if (b.Rebooted) expectedReboots[port] = DateTime.UtcNow.AddSeconds(20);
                else expectedReboots.TryRemove(port, out ignored);
                fcPorts[port] = true;
            }
            catch (PortBusyException)
            {
                expectedReboots.TryRemove(port, out ignored);
                fcPorts[port] = true; // Betaflight has it; the disconnect watcher will catch it when it lets go
                Log.Write(port + ": in use by another program (Betaflight connected first)");
                if (kind == JobKind.Special)
                {
                    // mid-tune: Betaflight has the quad, so save it the moment it lets go
                    string id;
                    pendingSpecial[portFcIds.TryGetValue(port, out id) ? id : "port:" + port] = specialName;
                    Log.Write(port + ": special backup \"" + specialName + "\" waiting for a disconnect");
                    Notify(ToastKind.Special, "★ \"" + specialName + "\" is ready!",
                        "Disconnect in Betaflight and I'll save it. (Hit Save in Betaflight first if you haven't.)", null);
                }
                else if (kind == JobKind.PluggedIn)
                    Notify(ToastKind.Warning, "Betaflight beat me to it!", "It grabbed " + port + " first." +
                        (backupWhenDisconnected ? " I'll back it up when you disconnect."
                         : backupWhenBetaflightCloses ? " I'll back it up when you close Betaflight." : ""), null);
                else if (kind == JobKind.Manual)
                    Notify(ToastKind.Warning, "Betaflight has dibs on " + port, "Disconnect it in Betaflight first, then try again.", null);
                return Outcome.Busy;
            }
            catch (NotAFlightControllerException ex)
            {
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
                      " (" + b.Cli.Length + " chars" + (b.Complete ? "" : ", INCOMPLETE") + ", " +
                      (b.Rebooted ? "rebooted" : "no reboot") + ", " + (timer.ElapsedMilliseconds / 1000.0).ToString("0.0") + "s)");

            // a special backup asked for earlier (while Betaflight had the quad) rides along with this one
            var waiting = TakePendingSpecial(port, b.FcId);
            if (specialName == null) specialName = waiting;

            var drone = db.Find(b.FcId);
            if (drone != null && drone.Ignored && specialName == null) // asking for a special backup overrides "paused"
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

            // end-of-session backups (disconnect / Betaflight closed) are only kept if the tune actually changed;
            // special backups are always kept
            if (specialName == null && (kind == JobKind.BetaflightClosed || kind == JobKind.Disconnected) && BackupFiles.SameAsLatest(drone.Folder, b.Cli))
            {
                Log.Write(port + ": " + drone.Name + " unchanged since last backup");
                DroneDb.Upsert(drone);
                Notify(ToastKind.Info, drone.Name + ": nothing new", "No changes since the last backup, so I didn't save another copy.", drone.Folder);
                return Outcome.Saved;
            }

            var file = BackupFiles.Write(drone.Folder, drone.Name, b, DateTime.Now, specialName);
            drone.LastBackup = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            DroneDb.Upsert(drone);
            Log.Write(port + ": saved " + (specialName != null ? "special backup " : "") + file);
            if (specialName != null && b.Complete)
                Notify(ToastKind.Special, "★ \"" + specialName + "\" saved!", Quips.ForSpecial() + "\n" + Path.GetFileName(file), drone.Folder);
            else if (b.Complete)
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
            try { Directory.CreateDirectory(location); } catch { }
            return ui.AskWhereToSave(b, port, BackupFiles.SafeName(name), location, note, mood);
        }

        void Notify(ToastKind kind, string title, string text, string folder)
        {
            if (!settings.ShowNotifications) return;
            try { ui.Notify(kind, title, text, folder); } catch (Exception ex) { Log.Write("Notification failed: " + ex.Message); }
        }
    }
}
