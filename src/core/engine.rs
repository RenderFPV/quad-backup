//! Watches serial ports and decides when to back up (plug-in, disconnect in Betaflight, Betaflight closing,
//! "Back up now", special backups), then writes the files. Everything OS-specific goes through `Platform`,
//! everything on screen through `EngineUi`.

use super::fc::{self, FcBackup, Link, ReadError};
use super::store::{self, log, DroneDb, DroneMood, Drone, NewDroneChoice, Settings, ToastKind};
use std::collections::{HashMap, HashSet};
use std::io;
use std::path::Path;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{channel, Receiver, Sender};
use std::sync::{Arc, Mutex, RwLock};
use std::thread;
use std::time::{Duration, Instant};

pub enum OpenError {
    /// another program (Betaflight) has the port open
    Busy,
    Other(io::Error),
}

pub trait Platform: Send + Sync + 'static {
    fn list_ports(&self) -> Vec<String>;
    /// USB vendor ID like "0483", or None if unknown
    fn vendor_id(&self, port: &str) -> Option<String>;
    fn open(&self, port: &str) -> Result<Box<dyn Link>, OpenError>;
    /// Some(true) = another program has it open, Some(false) = free, None = unknown/gone
    fn is_in_use(&self, port: &str) -> Option<bool>;
    fn is_betaflight_running(&self) -> bool;
    fn find_launch_target(&self) -> String;
    fn launch(&self, target: &str) -> io::Result<()>;
    /// check ports for a Betaflight connection even when Betaflight isn't seen running
    fn probe_always(&self) -> bool { false }
    fn probe_interval(&self) -> Duration { Duration::from_millis(200) }
}

pub struct AskRequest {
    pub backup: FcBackup,
    pub port: String,
    pub suggested_name: String,
    pub location: String,
    pub note: Option<String>,
    pub mood: DroneMood,
}

pub trait EngineUi: Send + Sync + 'static {
    fn notify(&self, kind: ToastKind, title: &str, text: &str, folder: Option<String>);
    /// Blocks until the person answers. None = "skip this time".
    fn ask_where_to_save(&self, request: AskRequest) -> Option<NewDroneChoice>;
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum JobKind { PluggedIn, BetaflightClosed, Disconnected, Manual, Special }

struct Job {
    kind: JobKind,
    port: Option<String>,
    special: Option<String>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Outcome { NotFlightController, Busy, Skipped, Failed, Saved }

/// What the monitor knows about one flight controller's port while Betaflight might be using it.
#[derive(Default)]
struct PortWatch {
    seen_in_use: bool,
    free_since: Option<Instant>,
    rebooted: bool,
}

struct Inner {
    platform: Arc<dyn Platform>,
    ui: Arc<dyn EngineUi>,
    tx: Mutex<Option<Sender<Job>>>,
    rx: Mutex<Option<Receiver<Job>>>,
    port_fc_ids: Mutex<HashMap<String, String>>,
    /// special backups asked for while Betaflight had the quad: FC id (or "port:COMx") -> name
    pending_special: Mutex<HashMap<String, String>>,
    /// ports we just sent "exit" to: the next time each one reappears is its reboot, not a plug-in
    expected_reboots: Mutex<HashMap<String, Instant>>,
    /// ports known to have a flight controller on them; watched for Betaflight disconnects
    fc_ports: Mutex<HashSet<String>>,
    /// held while the worker has a port open, so the in-use check never collides with a backup
    port_gate: Mutex<()>,
    stopping: AtomicBool,
    settings: RwLock<Settings>,
    plug_in_settle: Duration,
    monitor_sleep: Duration,
    cli_timeout: Duration,
}

#[derive(Clone)]
pub struct Engine {
    inner: Arc<Inner>,
}

pub struct EngineOptions {
    pub plug_in_settle: Duration,
    pub monitor_sleep: Duration,
}

impl Default for EngineOptions {
    fn default() -> Self {
        EngineOptions { plug_in_settle: Duration::from_millis(800), monitor_sleep: Duration::from_millis(200) }
    }
}

impl Engine {
    pub fn new(platform: Arc<dyn Platform>, ui: Arc<dyn EngineUi>, options: EngineOptions) -> Engine {
        let (tx, rx) = channel();
        Engine {
            inner: Arc::new(Inner {
                platform,
                ui,
                tx: Mutex::new(Some(tx)),
                rx: Mutex::new(Some(rx)),
                port_fc_ids: Mutex::new(HashMap::new()),
                pending_special: Mutex::new(HashMap::new()),
                expected_reboots: Mutex::new(HashMap::new()),
                fc_ports: Mutex::new(HashSet::new()),
                port_gate: Mutex::new(()),
                stopping: AtomicBool::new(false),
                settings: RwLock::new(Settings::load()),
                plug_in_settle: options.plug_in_settle,
                monitor_sleep: options.monitor_sleep,
                cli_timeout: Duration::from_secs(30),
            }),
        }
    }

    pub fn start(&self) {
        // what's plugged in right now counts as "already there"; take it before the thread starts so nothing
        // plugged in during start-up is mistaken for it
        let initial: HashSet<String> = self.inner.platform.list_ports().into_iter().collect();
        let m = self.inner.clone();
        thread::Builder::new().name("monitor".into()).spawn(move || m.monitor_loop(initial)).unwrap();
        let w = self.inner.clone();
        let rx = self.inner.rx.lock().unwrap().take().expect("engine started twice");
        thread::Builder::new().name("worker".into()).spawn(move || w.worker_loop(rx)).unwrap();
    }

    pub fn stop(&self) {
        self.inner.stopping.store(true, Ordering::SeqCst);
        self.inner.tx.lock().unwrap().take();
    }

    pub fn settings(&self) -> Settings { self.inner.settings.read().unwrap().clone() }

    pub fn reload_settings(&self) { *self.inner.settings.write().unwrap() = Settings::load(); }

    pub fn backup_now(&self) { self.inner.queue(Job { kind: JobKind::Manual, port: None, special: None }); }

    /// Which plugged-in quad a special backup is for: (port, drone name). None if no FC is plugged in.
    pub fn find_special_target(&self, fc_id: Option<&str>) -> Option<(String, Option<String>)> {
        let fc_ports = self.inner.fc_ports.lock().unwrap().clone();
        let candidates: Vec<String> = self.inner.platform.list_ports().into_iter().filter(|p| fc_ports.contains(p)).collect();
        let ids = self.inner.port_fc_ids.lock().unwrap().clone();
        let port = fc_id
            .and_then(|id| candidates.iter().find(|p| ids.get(*p).map_or(false, |x| x.eq_ignore_ascii_case(id))).cloned())
            .or_else(|| candidates.first().cloned())?;
        let name = ids.get(&port).and_then(|id| DroneDb::load().find(id).and_then(|d| d.name.clone()));
        Some((port, name))
    }

    pub fn queue_special(&self, port: String, name: String) {
        self.inner.queue(Job { kind: JobKind::Special, port: Some(port), special: Some(name) });
    }

    pub fn pending_special(&self, fc_id: &str) -> Option<String> {
        let pending = self.inner.pending_special.lock().unwrap();
        if let Some(n) = pending.iter().find(|(k, _)| k.eq_ignore_ascii_case(fc_id)).map(|(_, v)| v.clone()) {
            return Some(n);
        }
        // waiting under the port before the FC was identified
        let ids = self.inner.port_fc_ids.lock().unwrap();
        ids.iter()
            .filter(|(_, id)| id.eq_ignore_ascii_case(fc_id))
            .find_map(|(port, _)| pending.get(&format!("port:{port}")).cloned())
    }

    pub fn is_plugged_in(&self, fc_id: &str) -> bool {
        let present = self.inner.platform.list_ports();
        self.inner.port_fc_ids.lock().unwrap().iter().any(|(p, id)| id.eq_ignore_ascii_case(fc_id) && present.contains(p))
    }

    pub fn launch_betaflight(&self) { self.inner.launch_betaflight(); }
}

impl Inner {
    fn queue(&self, job: Job) {
        if let Some(tx) = self.tx.lock().unwrap().as_ref() {
            let _ = tx.send(job);
        }
    }

    fn settings(&self) -> Settings { self.settings.read().unwrap().clone() }

    fn notify(&self, kind: ToastKind, title: &str, text: &str, folder: Option<String>) {
        if self.settings().show_notifications {
            self.ui.notify(kind, title, text, folder);
        }
    }

    fn launch_betaflight(&self) {
        let target = Settings::load().betaflight_launch_path;
        let target = if target.is_empty() { self.platform.find_launch_target() } else { target };
        match self.platform.launch(&target) {
            Ok(()) => log("Launched Betaflight"),
            Err(e) => {
                log(format!("Couldn't launch Betaflight: {e}"));
                self.notify(ToastKind::Error, "Couldn't launch Betaflight", "Check which Betaflight app to open in Settings.", None);
            }
        }
    }

    fn has_pending_special(&self, port: &str) -> bool {
        let pending = self.pending_special.lock().unwrap();
        pending.contains_key(&format!("port:{port}"))
            || self.port_fc_ids.lock().unwrap().get(port).map_or(false, |id| pending.contains_key(id))
    }

    fn take_pending_special(&self, port: &str, fc_id: Option<&str>) -> Option<String> {
        let mut pending = self.pending_special.lock().unwrap();
        if let Some(id) = fc_id {
            if let Some(n) = pending.remove(id) {
                return Some(n);
            }
        }
        pending.remove(&format!("port:{port}"))
    }

    fn is_fc_vendor(&self, port: &str) -> bool {
        self.platform.vendor_id(port).map_or(false, |vid| self.settings().is_fc_vendor(&vid))
    }

    fn is_present(&self, port: &str) -> bool { self.platform.list_ports().iter().any(|p| p == port) }

    // ------------------------------------------------------------------ watching ports

    /// Polls the port list. Every new port is a plug-in, except a port coming back from a reboot we know about:
    /// our own "exit" after a backup, or (while Betaflight is open) a quick return after Save.
    fn monitor_loop(self: Arc<Self>, initial: HashSet<String>) {
        let mut present = initial;
        let mut last_seen: HashMap<String, Instant> = HashMap::new();
        let mut watches: HashMap<String, PortWatch> = HashMap::new();
        for p in &present {
            last_seen.insert(p.clone(), Instant::now());
            if self.is_fc_vendor(p) {
                self.fc_ports.lock().unwrap().insert(p.clone()); // FCs already plugged in when the app started
            }
        }
        let mut betaflight_was_running = self.platform.is_betaflight_running();
        let mut next_betaflight_check = Instant::now();
        let mut next_probe = Instant::now();

        while !self.stopping.load(Ordering::SeqCst) {
            let now = Instant::now();
            let s = self.settings();
            let reboot_grace = Duration::from_secs(s.reboot_grace_seconds.max(2) as u64);
            let ports = self.platform.list_ports();
            for port in &ports {
                if present.contains(port) {
                    continue;
                }
                let expected = self.expected_reboots.lock().unwrap().remove(port);
                if expected.map_or(false, |deadline| now < deadline) {
                    log(format!("{port} came back after the backup reboot"));
                } else if betaflight_was_running && last_seen.get(port).map_or(false, |t| now.duration_since(*t) < reboot_grace) {
                    log(format!("{port} came back quickly while Betaflight is open - treating it as a reboot, not a plug-in"));
                } else {
                    log(format!("{port} plugged in"));
                    watches.remove(port); // fresh plug-in: its own backup covers it
                    self.queue(Job { kind: JobKind::PluggedIn, port: Some(port.clone()), special: None });
                }
            }
            present = ports.iter().cloned().collect();
            for p in &ports {
                last_seen.insert(p.clone(), now);
            }

            if now >= next_betaflight_check {
                next_betaflight_check = now + Duration::from_secs(2);
                let running = self.platform.is_betaflight_running();
                // with disconnect backups on, closing Betaflight frees the port and is caught as a disconnect
                if betaflight_was_running && !running && s.backup_when_betaflight_closes && !s.backup_when_disconnected && !present.is_empty() {
                    log("Betaflight closed");
                    self.queue(Job { kind: JobKind::BetaflightClosed, port: None, special: None });
                }
                betaflight_was_running = running;
            }

            // a waiting special backup needs the disconnect watcher even if disconnect backups are off
            let pending_any = !self.pending_special.lock().unwrap().is_empty();
            if now >= next_probe && (s.backup_when_disconnected || pending_any) {
                next_probe = now + self.platform.probe_interval();
                self.watch_for_disconnects(&present, &mut watches, betaflight_was_running, now, &s);
            }
            thread::sleep(self.monitor_sleep);
        }
    }

    /// A flight controller's port going from "in use" to "free" while still plugged in means it was disconnected in
    /// Betaflight. Clicking Disconnect leaves the USB port in place, so a short wait is enough. When Betaflight
    /// reboots the quad itself (Save, CLI, Blackbox, flashing) the port vanishes and comes back and Betaflight
    /// reconnects a few seconds later; then we wait at least the reboot grace so we never grab the port first.
    fn watch_for_disconnects(&self, present: &HashSet<String>, watches: &mut HashMap<String, PortWatch>, betaflight_running: bool, now: Instant, s: &Settings) {
        let fc_ports: Vec<String> = self.fc_ports.lock().unwrap().iter().cloned().collect();
        for port in fc_ports {
            let w = watches.entry(port.clone()).or_default();
            if !present.contains(&port) {
                // rebooting or unplugged
                w.free_since = None;
                if w.seen_in_use {
                    w.rebooted = true;
                }
                continue;
            }
            if !betaflight_running && !w.seen_in_use && !self.platform.probe_always() {
                continue; // nothing could have it open
            }
            let in_use = match self.port_gate.try_lock() {
                Ok(_g) => self.platform.is_in_use(&port),
                Err(_) => continue, // we're backing something up right now
            };
            match in_use {
                Some(true) => {
                    if !w.seen_in_use {
                        log(format!("{port}: connected in Betaflight"));
                    }
                    w.seen_in_use = true;
                    w.rebooted = false;
                    w.free_since = None;
                }
                Some(false) if w.seen_in_use => {
                    let wait = if w.rebooted { s.disconnect_wait_seconds.max(s.reboot_grace_seconds) } else { s.disconnect_wait_seconds }.max(1);
                    match w.free_since {
                        None => w.free_since = Some(now),
                        Some(since) if now.duration_since(since) >= Duration::from_secs(wait as u64) => {
                            log(format!("{port}: disconnected in Betaflight{}", if w.rebooted { " (after a reboot, Betaflight didn't reconnect)" } else { "" }));
                            *w = PortWatch::default();
                            if s.backup_when_disconnected || self.has_pending_special(&port) {
                                self.queue(Job { kind: JobKind::Disconnected, port: Some(port.clone()), special: None });
                            }
                        }
                        Some(_) => {}
                    }
                }
                _ => {}
            }
        }
    }

    // ------------------------------------------------------------------ backing up

    fn worker_loop(self: Arc<Self>, rx: Receiver<Job>) {
        while let Ok(job) = rx.recv() {
            if self.stopping.load(Ordering::SeqCst) {
                break;
            }
            self.run_job(job);
        }
    }

    fn run_job(&self, job: Job) {
        *self.settings.write().unwrap() = Settings::load();
        let s = self.settings();
        match job.kind {
            JobKind::PluggedIn => {
                let port = job.port.unwrap();
                thread::sleep(self.plug_in_settle); // let a freshly plugged-in FC finish booting
                if !self.is_present(&port) {
                    return;
                }
                let outcome = self.backup_port(&port, job.kind, None);
                if outcome != Outcome::NotFlightController && outcome != Outcome::Busy && s.launch_betaflight && !self.platform.is_betaflight_running() {
                    self.launch_betaflight();
                }
            }
            JobKind::Disconnected | JobKind::Special => {
                let port = job.port.unwrap();
                if self.is_present(&port) {
                    self.backup_port(&port, job.kind, job.special);
                } else if job.kind == JobKind::Special {
                    self.notify(ToastKind::Warning, "Where'd it go?",
                        &format!("The quad was unplugged before I could save \"{}\". Plug it back in and try again.", job.special.unwrap_or_default()), None);
                }
            }
            JobKind::Manual | JobKind::BetaflightClosed => {
                if job.kind == JobKind::BetaflightClosed {
                    thread::sleep(Duration::from_millis(1500)); // give the browser a moment to let go of the port
                }
                let found = self.platform.list_ports().iter().filter(|p| self.backup_port(p, job.kind, None) != Outcome::NotFlightController).count();
                if job.kind == JobKind::Manual && found == 0 {
                    self.notify(ToastKind::Info, "No quads here...", "Plug a flight controller in over USB and try again.", None);
                }
            }
        }
    }

    fn backup_port(&self, port: &str, kind: JobKind, mut special: Option<String>) -> Outcome {
        let s = self.settings();
        let timer = Instant::now();
        // a disconnect or special backup comes from a port already confirmed to be a flight controller,
        // so the vendor lookup can be skipped; the MSP check still confirms it's Betaflight
        let known_fc = self.fc_ports.lock().unwrap().contains(port);
        if !((kind == JobKind::Disconnected || kind == JobKind::Special) && known_fc) {
            let vid = self.platform.vendor_id(port);
            if !vid.as_deref().map_or(false, |v| s.is_fc_vendor(v)) {
                log(format!("{port}: USB vendor {} isn't a flight-controller chip - ignoring", vid.as_deref().unwrap_or("unknown")));
                return Outcome::NotFlightController;
            }
        }

        // Older firmware reboots on leaving the CLI; expect that before it can happen and withdraw it if it didn't.
        self.expected_reboots.lock().unwrap().insert(port.to_string(), Instant::now() + Duration::from_secs(60));
        let result = {
            let _gate = self.port_gate.lock().unwrap();
            match self.platform.open(port) {
                Ok(mut link) => fc::read_backup(link.as_mut(), &s.cli_command, &s.firmware_variants, self.cli_timeout).map_err(Some),
                Err(OpenError::Busy) => Err(None),
                Err(OpenError::Other(e)) => Err(Some(ReadError::Io(e))),
            }
        };
        let b = match result {
            Ok(b) => {
                if b.rebooted {
                    self.expected_reboots.lock().unwrap().insert(port.to_string(), Instant::now() + Duration::from_secs(20));
                } else {
                    self.expected_reboots.lock().unwrap().remove(port);
                }
                self.fc_ports.lock().unwrap().insert(port.to_string());
                b
            }
            Err(None) => {
                self.expected_reboots.lock().unwrap().remove(port);
                self.fc_ports.lock().unwrap().insert(port.to_string()); // the disconnect watcher will catch it
                log(format!("{port}: in use by another program (Betaflight connected first)"));
                match kind {
                    JobKind::Special => {
                        // mid-tune: Betaflight has the quad, so save it the moment it lets go
                        let name = special.unwrap_or_default();
                        let key = self.port_fc_ids.lock().unwrap().get(port).cloned().unwrap_or_else(|| format!("port:{port}"));
                        self.pending_special.lock().unwrap().insert(key, name.clone());
                        log(format!("{port}: special backup \"{name}\" waiting for a disconnect"));
                        self.notify(ToastKind::Special, &format!("\u{2605} \"{name}\" is ready!"),
                            "Disconnect in Betaflight and I'll save it. (Hit Save in Betaflight first if you haven't.)", None);
                    }
                    JobKind::PluggedIn => {
                        let later = if s.backup_when_disconnected { " I'll back it up when you disconnect." }
                            else if s.backup_when_betaflight_closes { " I'll back it up when you close Betaflight." } else { "" };
                        self.notify(ToastKind::Warning, "Betaflight beat me to it!", &format!("It grabbed {port} first.{later}"), None);
                    }
                    JobKind::Manual => self.notify(ToastKind::Warning, &format!("Betaflight has dibs on {port}"), "Disconnect it in Betaflight first, then try again.", None),
                    _ => {}
                }
                return Outcome::Busy;
            }
            Err(Some(ReadError::NotFlightController(why))) => {
                self.expected_reboots.lock().unwrap().remove(port);
                log(format!("{port}: not backed up - {why}"));
                return Outcome::NotFlightController;
            }
            Err(Some(ReadError::Io(e))) => {
                log(format!("{port}: backup failed - {e}"));
                self.notify(ToastKind::Error, &format!("Oof. Backup failed on {port}"), &e.to_string(), None);
                return Outcome::Failed;
            }
        };

        let fc_id = match b.fc_id.clone().or_else(|| self.port_fc_ids.lock().unwrap().get(port).cloned()) {
            Some(id) => id,
            None => {
                log(format!("{port}: FC was stuck in CLI mode and couldn't be identified; unplug and replug it"));
                self.notify(ToastKind::Warning, "Who goes there?", &format!("Couldn't tell which drone is on {port}. Unplug it and plug it back in."), None);
                return Outcome::Failed;
            }
        };
        self.port_fc_ids.lock().unwrap().insert(port.to_string(), fc_id.clone());
        log(format!("{port}: read {} {} '{}' id {fc_id} ({} chars{}, {}, {:.1}s)",
            b.firmware.as_deref().or(b.variant.as_deref()).unwrap_or("?"), b.board.as_deref().unwrap_or("?"), b.craft_name.as_deref().unwrap_or(""),
            b.cli.len(), if b.complete { "" } else { ", INCOMPLETE" }, if b.rebooted { "rebooted" } else { "no reboot" }, timer.elapsed().as_secs_f32()));

        // a special backup asked for earlier (while Betaflight had the quad) rides along with this one
        let waiting = self.take_pending_special(port, Some(&fc_id));
        if special.is_none() {
            special = waiting;
        }

        let db = DroneDb::load();
        let mut drone = db.find(&fc_id).cloned();
        if drone.as_ref().map_or(false, |d| d.ignored) && special.is_none() {
            log(format!("{port}: drone is set to never back up"));
            return Outcome::Skipped;
        }
        if !drone.as_ref().map_or(false, |d| d.folder_exists()) {
            let Some(choice) = self.ask_where_to_save(&db, &b, port, drone.as_ref(), &s) else {
                log(format!("{port}: skipped this time"));
                return Outcome::Skipped;
            };
            let d = drone.get_or_insert_with(|| Drone { fc_id: Some(fc_id.clone()), first_seen: Some(store::now_stamp()), ..Default::default() });
            match choice {
                NewDroneChoice::Never => {
                    d.ignored = true;
                    d.name = Some(b.craft_name.clone().filter(|c| !c.is_empty()).or(b.board.clone()).unwrap_or_default());
                    DroneDb::upsert(d.clone());
                    log(format!("{port}: drone {fc_id} set to never back up"));
                    return Outcome::Skipped;
                }
                NewDroneChoice::Folder(folder) => {
                    let _ = std::fs::create_dir_all(&folder);
                    d.name = Path::new(folder.trim_end_matches(['\\', '/'])).file_name().map(|n| n.to_string_lossy().to_string());
                    d.folder = Some(folder);
                }
            }
        }
        let mut drone = drone.unwrap();
        drone.craft_name = b.craft_name.clone();
        drone.board = b.board.clone();
        drone.firmware = b.firmware.clone();
        let folder = drone.folder.clone().unwrap_or_default();
        let name = drone.name_or_default();

        // A copy identical to the last backup isn't saved again (SkipUnchangedBackups, on by default;
        // end-of-session backups always skip). Special backups are always kept.
        let skip_if_same = s.skip_unchanged_backups || kind == JobKind::BetaflightClosed || kind == JobKind::Disconnected;
        if special.is_none() && skip_if_same && store::same_as_latest(Path::new(&folder), &b.cli) {
            log(format!("{port}: {name} unchanged since last backup"));
            DroneDb::upsert(drone);
            self.notify(ToastKind::Info, &format!("{name}: nothing new"), "Same as the last backup, so I didn't save another copy.", Some(folder));
            return Outcome::Saved;
        }

        let file = match store::write_backup(Path::new(&folder), &name, &b.cli, b.complete, chrono::Local::now().naive_local(), special.as_deref()) {
            Ok(f) => f,
            Err(e) => {
                log(format!("{port}: couldn't write the backup - {e}"));
                self.notify(ToastKind::Error, "Couldn't save the backup", &e.to_string(), None);
                return Outcome::Failed;
            }
        };
        drone.last_backup = Some(store::now_stamp());
        DroneDb::upsert(drone);
        log(format!("{port}: saved {}{}", if special.is_some() { "special backup " } else { "" }, file.display()));
        let file_name = file.file_name().map(|n| n.to_string_lossy().to_string()).unwrap_or_default();
        if let (Some(sp), true) = (&special, b.complete) {
            self.notify(ToastKind::Special, &format!("\u{2605} \"{sp}\" saved!"), &format!("{}\n{file_name}", store::quips::special()), Some(folder));
        } else if b.complete {
            self.notify(ToastKind::Success, &format!("{name} is backed up!"), &format!("{}\n{file_name}", store::quips::saved()), Some(folder));
        } else {
            self.notify(ToastKind::Warning, &format!("Partial backup of {name}"), &format!("The FC stopped talking partway through. Check the file:\n{file_name}"), Some(folder));
        }
        Outcome::Saved
    }

    fn ask_where_to_save(&self, db: &DroneDb, b: &FcBackup, port: &str, known: Option<&Drone>, s: &Settings) -> Option<NewDroneChoice> {
        let split = |folder: &str| {
            let p = Path::new(folder.trim_end_matches(['\\', '/']));
            (p.file_name().map(|n| n.to_string_lossy().to_string()).unwrap_or_default(),
             p.parent().map(|n| n.to_string_lossy().to_string()).unwrap_or_default())
        };
        let fc_id = b.fc_id.as_deref().unwrap_or("");
        let forgotten = if known.is_none() { db.find_forgotten(fc_id).and_then(|d| d.folder.clone()) } else { None };
        let same_name = if known.is_none() { db.find_by_craft_name(b.craft_name.as_deref().unwrap_or("")).and_then(|d| d.folder.clone()) } else { None };
        let (name, mut location, note, mood) = if let Some(folder) = forgotten {
            // you clicked Forget on this one earlier; offer its old folder back
            let (n, l) = split(&folder);
            (n, l, None, DroneMood::Returning)
        } else if let Some(folder) = known.and_then(|k| k.folder.clone()).filter(|f| !f.is_empty()) {
            let (_, l) = split(&folder);
            (known.unwrap().name_or_default(), l, Some(format!("It used to live here, but the folder's gone:\n{folder}")), DroneMood::MissingFolder)
        } else if let Some(folder) = same_name {
            let (n, l) = split(&folder);
            (n, l, None, DroneMood::NewFlightController)
        } else {
            let n = b.craft_name.clone().filter(|c| !c.is_empty()).or(b.board.clone()).unwrap_or_else(|| "Drone".into());
            (n, s.backup_root.clone(), None, DroneMood::New)
        };
        if location.is_empty() || !Path::new(&location).is_dir() {
            location = s.backup_root.clone();
        }
        let _ = std::fs::create_dir_all(&location);
        self.ui.ask_where_to_save(AskRequest { backup: b.clone(), port: port.to_string(), suggested_name: store::safe_name(&name), location, note, mood })
    }
}

// ------------------------------------------------------------------------------------------------ simulation

#[cfg(test)]
mod tests {
    use super::*;
    use crate::core::fc::fake::{FakeFc, DIFF, FC_ID};
    use std::fs;

    #[derive(Clone, Default)]
    struct Device {
        diff: String,
        old_firmware: bool,
    }

    /// A pretend computer: ports appear/disappear, "Betaflight" can hold a port, FCs can reboot.
    #[derive(Default)]
    struct FakePlatform {
        present: Mutex<HashMap<String, Device>>,
        in_use: Mutex<HashSet<String>>,
        betaflight_running: AtomicBool,
        launches: Mutex<u32>,
    }

    impl FakePlatform {
        fn plug(&self, port: &str, d: &Device) { self.present.lock().unwrap().insert(port.into(), d.clone()); }
        fn unplug(&self, port: &str) { self.present.lock().unwrap().remove(port); self.in_use.lock().unwrap().remove(port); }
        fn connect(&self, port: &str) { self.in_use.lock().unwrap().insert(port.into()); }
        fn disconnect(&self, port: &str) { self.in_use.lock().unwrap().remove(port); }
    }

    fn reboot(platform: Arc<FakePlatform>, port: String, down: Duration) {
        let d = platform.present.lock().unwrap().remove(&port);
        platform.in_use.lock().unwrap().remove(&port);
        if let Some(d) = d {
            thread::spawn(move || { thread::sleep(down); platform.present.lock().unwrap().insert(port, d); });
        }
    }

    struct FakeLink {
        fc: FakeFc,
        platform: Arc<FakePlatform>,
        port: String,
    }

    impl Link for FakeLink {
        fn write(&mut self, data: &[u8]) -> io::Result<()> { self.fc.write(data) }
        fn read(&mut self, buf: &mut [u8], t: Duration) -> io::Result<usize> { self.fc.read(buf, t) }
    }

    impl Drop for FakeLink {
        fn drop(&mut self) {
            if self.fc.rebooted_on_exit() {
                reboot(self.platform.clone(), self.port.clone(), Duration::from_millis(1200));
            }
        }
    }

    struct SharedPlatform(Arc<FakePlatform>);

    impl Platform for SharedPlatform {
        fn list_ports(&self) -> Vec<String> { self.0.present.lock().unwrap().keys().cloned().collect() }
        fn vendor_id(&self, port: &str) -> Option<String> { self.0.present.lock().unwrap().contains_key(port).then(|| "0483".into()) }
        fn open(&self, port: &str) -> Result<Box<dyn Link>, OpenError> {
            let d = self.0.present.lock().unwrap().get(port).cloned().ok_or_else(|| OpenError::Other(io::Error::new(io::ErrorKind::NotFound, "gone")))?;
            if self.0.in_use.lock().unwrap().contains(port) {
                return Err(OpenError::Busy);
            }
            let mut fc = FakeFc::new(&d.diff);
            fc.old_firmware = d.old_firmware;
            Ok(Box::new(FakeLink { fc, platform: self.0.clone(), port: port.into() }))
        }
        fn is_in_use(&self, port: &str) -> Option<bool> {
            self.0.present.lock().unwrap().contains_key(port).then(|| self.0.in_use.lock().unwrap().contains(port))
        }
        fn is_betaflight_running(&self) -> bool { self.0.betaflight_running.load(Ordering::SeqCst) }
        fn find_launch_target(&self) -> String { "fake-betaflight".into() }
        fn launch(&self, _: &str) -> io::Result<()> { *self.0.launches.lock().unwrap() += 1; Ok(()) }
    }

    #[derive(Default)]
    struct FakeUi {
        toasts: Mutex<Vec<String>>,
        asks: Mutex<u32>,
        folder: String,
        answer_never: AtomicBool,
        last: Mutex<Option<(DroneMood, String)>>,
    }

    impl FakeUi {
        fn count(&self, text: &str) -> usize { self.toasts.lock().unwrap().iter().filter(|t| t.contains(text)).count() }
        fn total(&self) -> usize { self.toasts.lock().unwrap().len() }
        fn asks(&self) -> u32 { *self.asks.lock().unwrap() }
    }

    struct SharedUi(Arc<FakeUi>);

    impl EngineUi for SharedUi {
        fn notify(&self, kind: ToastKind, title: &str, _: &str, _: Option<String>) { self.0.toasts.lock().unwrap().push(format!("{kind:?}: {title}")); }
        fn ask_where_to_save(&self, r: AskRequest) -> Option<NewDroneChoice> {
            *self.0.asks.lock().unwrap() += 1;
            *self.0.last.lock().unwrap() = Some((r.mood, Path::new(&r.location).join(&r.suggested_name).to_string_lossy().to_string()));
            Some(if self.0.answer_never.load(Ordering::SeqCst) { NewDroneChoice::Never } else { NewDroneChoice::Folder(self.0.folder.clone()) })
        }
    }

    fn wait_for(cond: impl Fn() -> bool, ms: u64) -> bool {
        let until = Instant::now() + Duration::from_millis(ms);
        while Instant::now() < until {
            if cond() { return true; }
            thread::sleep(Duration::from_millis(50));
        }
        cond()
    }

    fn files(dir: &str) -> usize { fs::read_dir(dir).map(|r| r.flatten().filter(|e| e.path().extension().map_or(false, |x| x == "txt")).count()).unwrap_or(0) }

    #[test]
    fn simulated_sessions() {
        let dir = std::env::temp_dir().join(format!("quad-backup-engine-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        store::set_data_dir(&dir);
        let root = dir.join("Backups");
        let s = Settings {
            backup_root: root.to_string_lossy().into(), betaflight_launch_path: "fake-betaflight".into(),
            disconnect_wait_seconds: 1, reboot_grace_seconds: 3, ..Settings::default()
        };
        s.save().unwrap();

        let platform = Arc::new(FakePlatform::default());
        let ui = Arc::new(FakeUi { folder: root.join("ERA").to_string_lossy().into(), ..Default::default() });
        let engine = Engine::new(Arc::new(SharedPlatform(platform.clone())), Arc::new(SharedUi(ui.clone())),
            EngineOptions { plug_in_settle: Duration::from_millis(100), ..Default::default() });
        engine.start();
        let folder = ui.folder.clone();
        let n = || files(&folder);
        let mut dev = Device { diff: DIFF.into(), old_firmware: false };
        let replug = |d: &Device| { platform.unplug("COM9"); thread::sleep(Duration::from_millis(500)); platform.plug("COM9", d); };

        // 1. brand-new quad plugged in, Betaflight closed
        platform.plug("COM9", &dev);
        assert!(wait_for(|| n() == 1, 8000), "new quad -> asked for a folder and backed up");
        // the pop-up follows the file write, so give it a moment on a busy machine
        assert!(wait_for(|| ui.asks() == 1 && ui.count("Success") == 1, 2000));
        assert!(wait_for(|| *platform.launches.lock().unwrap() == 1, 2000), "Betaflight launched after the plug-in backup");

        // 2. quick replug, same settings -> checked, no duplicate file; changed settings -> new file
        replug(&dev);
        assert!(wait_for(|| ui.count("nothing new") == 1, 8000), "quick replug, same settings -> 'nothing new'");
        assert!(n() == 1 && ui.asks() == 1);
        dev.diff = DIFF.replace("set d_roll = 40", "set d_roll = 42");
        replug(&dev);
        assert!(wait_for(|| n() == 2, 8000), "replug after a change -> new backup");

        // 3. old firmware reboots on leaving the CLI; that reboot must not count as a plug-in
        dev.old_firmware = true;
        dev.diff = DIFF.replace("set d_roll = 40", "set d_roll = 44");
        replug(&dev);
        assert!(wait_for(|| n() == 3 && ui.count("Success") == 3, 8000), "old-firmware quad backed up");
        let before = ui.total();
        thread::sleep(Duration::from_millis(3500));
        assert!(n() == 3 && ui.total() == before, "its own reboot is ignored");
        dev.old_firmware = false;
        platform.present.lock().unwrap().get_mut("COM9").expect("back after its reboot").old_firmware = false;

        // 4. connect + disconnect in Betaflight with nothing changed -> "nothing new"
        platform.betaflight_running.store(true, Ordering::SeqCst);
        thread::sleep(Duration::from_millis(2500));
        platform.connect("COM9");
        thread::sleep(Duration::from_millis(800));
        let nothing = ui.count("nothing new");
        platform.disconnect("COM9");
        assert!(wait_for(|| ui.count("nothing new") == nothing + 1, 5000), "disconnect without changes");
        assert_eq!(n(), 3);

        // 5. tune changed, then disconnect -> new file
        platform.present.lock().unwrap().get_mut("COM9").unwrap().diff = DIFF.replace("p_pitch = 52", "p_pitch = 55");
        platform.connect("COM9");
        thread::sleep(Duration::from_millis(800));
        platform.disconnect("COM9");
        assert!(wait_for(|| n() == 4, 5000), "disconnect after a tune change -> backed up");

        // 6. Save in Betaflight reboots the quad and Betaflight reconnects ~1.5s later: nothing should happen
        platform.connect("COM9");
        thread::sleep(Duration::from_millis(800));
        let before = ui.total();
        reboot(platform.clone(), "COM9".into(), Duration::from_millis(800));
        thread::sleep(Duration::from_millis(2300));
        platform.connect("COM9");
        thread::sleep(Duration::from_millis(1500));
        assert!(n() == 4 && ui.total() == before, "reboot after Save -> no backup, no early disconnect backup");

        // 7. special backup while connected waits for the disconnect, then is always kept
        let (port, name) = engine.find_special_target(None).expect("special finds the plugged-in quad");
        assert_eq!((port.as_str(), name.as_deref()), ("COM9", Some("ERA")));
        engine.queue_special(port, "keeper".into());
        assert!(wait_for(|| ui.count("is ready") == 1, 3000), "special while connected -> ready pop-up");
        assert_eq!(engine.pending_special(FC_ID).as_deref(), Some("keeper"));
        platform.disconnect("COM9");
        assert!(wait_for(|| n() == 5, 5000), "special saved on disconnect though unchanged");
        assert!(fs::read_dir(&folder).unwrap().flatten().any(|e| e.file_name().to_string_lossy().ends_with("_SPECIAL_keeper.txt")));
        assert!(engine.pending_special(FC_ID).is_none());

        // 8. special when nothing has the port -> saved right away
        engine.queue_special("COM9".into(), "free one".into());
        assert!(wait_for(|| n() == 6, 5000), "special while disconnected -> saved immediately");

        // 9. "Back up now" while Betaflight holds the port
        platform.connect("COM9");
        thread::sleep(Duration::from_millis(600));
        engine.backup_now();
        assert!(wait_for(|| ui.count("dibs") == 1, 4000), "back up now while connected -> dibs");
        platform.disconnect("COM9");
        thread::sleep(Duration::from_millis(2500));
        platform.betaflight_running.store(false, Ordering::SeqCst);
        thread::sleep(Duration::from_millis(2500));

        // 10. Forget, then plug back in -> offered its old folder ("welcome back")
        DroneDb::forget(FC_ID);
        assert!(DroneDb::load().find(FC_ID).is_none() && DroneDb::load().find_forgotten(FC_ID).is_some());
        let asks = ui.asks();
        replug(&dev);
        assert!(wait_for(|| ui.asks() == asks + 1, 8000), "forgotten quad -> asked again");
        let (mood, suggestion) = ui.last.lock().unwrap().clone().unwrap();
        assert_eq!(mood, DroneMood::Returning);
        assert_eq!(suggestion, folder, "old folder suggested");
        assert!(wait_for(|| DroneDb::load().find(FC_ID).is_some() && DroneDb::load().find_forgotten(FC_ID).is_none(), 3000));

        // 11. "never back up" -> left alone until turned back on
        DroneDb::forget(FC_ID);
        DroneDb::mutate(|db| db.forgotten.clear());
        ui.answer_never.store(true, Ordering::SeqCst);
        let asks = ui.asks();
        replug(&dev);
        assert!(wait_for(|| ui.asks() == asks + 1, 8000) && wait_for(|| DroneDb::load().find(FC_ID).map_or(false, |d| d.ignored), 3000), "'never' -> paused");
        let (files_before, toasts_now) = (n(), ui.total());
        replug(&dev);
        thread::sleep(Duration::from_millis(3500));
        assert!(ui.asks() == asks + 1 && n() == files_before && ui.total() == toasts_now, "paused quad -> not asked, not backed up");
        ui.answer_never.store(false, Ordering::SeqCst);
        DroneDb::set_paused(FC_ID, false);
        dev.diff = DIFF.replace("set d_roll = 40", "set d_roll = 46");
        replug(&dev);
        assert!(wait_for(|| ui.asks() == asks + 2, 8000), "turned back on -> asks where to save");
        assert!(wait_for(|| n() == files_before + 1, 5000), "...and backs it up");

        engine.stop();
        println!("pop-ups: {:?}", ui.toasts.lock().unwrap());
    }
}
