//! Where files live, the log, settings, the drone list and backup files.
//! The JSON matches the C# version's settings.json / drones.json, so those files can be copied over as-is.

use serde::{Deserialize, Serialize};
use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, OnceLock, RwLock};

// ------------------------------------------------------------------------------------------------ paths & log

static DATA_DIR: OnceLock<RwLock<PathBuf>> = OnceLock::new();

fn data_dir_lock() -> &'static RwLock<PathBuf> {
    DATA_DIR.get_or_init(|| {
        // QB_DATA_DIR overrides where settings, the drone list and the log live
        let dir = std::env::var_os("QB_DATA_DIR").map(PathBuf::from).unwrap_or_else(default_data_dir);
        let _ = fs::create_dir_all(&dir);
        RwLock::new(dir)
    })
}

fn home() -> PathBuf {
    std::env::var_os("HOME").or_else(|| std::env::var_os("USERPROFILE")).map(PathBuf::from).unwrap_or_else(|| PathBuf::from("."))
}

/// Windows keeps everything next to the exe (portable). Mac and Linux use the usual per-user folders, since an app
/// bundle or a system install isn't somewhere to write.
fn default_data_dir() -> PathBuf {
    if cfg!(windows) {
        std::env::current_exe().ok().and_then(|p| p.parent().map(Path::to_path_buf)).unwrap_or_else(|| PathBuf::from("."))
    } else if cfg!(target_os = "macos") {
        home().join("Library/Application Support/Quad Backup Rust")
    } else {
        std::env::var_os("XDG_CONFIG_HOME").map(PathBuf::from).unwrap_or_else(|| home().join(".config")).join("quad-backup-rust")
    }
}

fn default_backup_root() -> PathBuf {
    if cfg!(windows) {
        return data_dir().join("Backups");
    }
    let docs = home().join("Documents");
    if docs.is_dir() { docs.join("Quad Backups") } else { home().join("Quad Backups") }
}

pub fn data_dir() -> PathBuf {
    data_dir_lock().read().unwrap().clone()
}

/// Points the app at another folder (tests use this).
#[cfg(test)]
pub fn set_data_dir(dir: &Path) {
    let _ = fs::create_dir_all(dir);
    *data_dir_lock().write().unwrap() = dir.to_path_buf();
}

pub fn settings_path() -> PathBuf { data_dir().join("settings.json") }
pub fn drones_path() -> PathBuf { data_dir().join("drones.json") }
pub fn log_path() -> PathBuf { data_dir().join("QuadBackup.log") }

static LOG_GATE: Mutex<()> = Mutex::new(());

pub fn log(message: impl AsRef<str>) {
    let _g = LOG_GATE.lock().unwrap_or_else(|e| e.into_inner());
    let file = log_path();
    if let Ok(meta) = fs::metadata(&file) {
        if meta.len() > 1_000_000 {
            let _ = fs::copy(&file, file.with_extension("log.old"));
            let _ = fs::remove_file(&file);
        }
    }
    if let Ok(mut f) = fs::OpenOptions::new().create(true).append(true).open(&file) {
        let _ = write!(f, "{}  {}\r\n", now_stamp(), message.as_ref());
    }
}

pub fn now_stamp() -> String {
    chrono::Local::now().format("%Y-%m-%d %H:%M:%S").to_string()
}

fn save_json<T: Serialize>(path: &Path, value: &T) -> std::io::Result<()> {
    let tmp = path.with_extension("json.tmp");
    fs::write(&tmp, serde_json::to_string_pretty(value)?)?;
    fs::rename(&tmp, path)
}

fn load_json<T: for<'de> Deserialize<'de>>(path: &Path) -> Option<Result<T, serde_json::Error>> {
    let text = fs::read_to_string(path).ok()?;
    // the C# version writes a UTF-8 byte order mark sometimes
    Some(serde_json::from_str(text.trim_start_matches('\u{feff}')))
}

// ------------------------------------------------------------------------------------------------ settings

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "PascalCase", default)]
pub struct Settings {
    pub backup_root: String,
    pub launch_betaflight: bool,
    pub betaflight_launch_path: String,
    pub backup_when_betaflight_closes: bool,
    pub backup_when_disconnected: bool,
    /// a backup identical to the last one for that quad isn't saved again (special backups always are)
    pub skip_unchanged_backups: bool,
    /// how long the port must stay free, without the quad rebooting, to count as you clicking Disconnect
    pub disconnect_wait_seconds: i32,
    pub show_notifications: bool,
    pub cli_command: String,
    /// STMicro (F4/F7/H7), Artery (AT32), Geehy (APM32). Other USB-serial gadgets are never opened.
    pub usb_vendor_ids: Vec<String>,
    pub firmware_variants: Vec<String>,
    pub reboot_grace_seconds: i32,
}

impl Default for Settings {
    fn default() -> Self {
        Settings {
            backup_root: default_backup_root().to_string_lossy().into_owned(),
            launch_betaflight: true,
            betaflight_launch_path: String::new(),
            backup_when_betaflight_closes: true,
            backup_when_disconnected: true,
            skip_unchanged_backups: true,
            disconnect_wait_seconds: 2,
            show_notifications: true,
            cli_command: "diff all".into(),
            usb_vendor_ids: vec!["0483".into(), "2E3C".into(), "314B".into()],
            firmware_variants: vec!["BTFL".into()],
            reboot_grace_seconds: 8,
        }
    }
}

impl Settings {
    pub fn load() -> Settings {
        match load_json::<Settings>(&settings_path()) {
            Some(Ok(s)) => s,
            Some(Err(e)) => {
                log(format!("settings.json is invalid, using defaults: {e}"));
                Settings::default()
            }
            None => Settings::default(),
        }
    }

    /// Loads (or creates) settings; `first_run` is true when there was no settings.json yet.
    pub fn load_or_create(find_launch_target: impl Fn() -> String) -> (Settings, bool) {
        let first_run = !settings_path().exists();
        let mut s = Settings::load();
        if s.betaflight_launch_path.is_empty() {
            s.betaflight_launch_path = find_launch_target();
        }
        if let Err(e) = s.save() {
            log(format!("Couldn't write settings.json: {e}"));
        }
        (s, first_run)
    }

    pub fn save(&self) -> std::io::Result<()> {
        save_json(&settings_path(), self)
    }

    pub fn is_fc_vendor(&self, vid: &str) -> bool {
        self.usb_vendor_ids.iter().any(|v| v.eq_ignore_ascii_case(vid))
    }
}

// ------------------------------------------------------------------------------------------------ drones

#[derive(Serialize, Deserialize, Clone, Debug, Default, PartialEq)]
#[serde(rename_all = "PascalCase", default)]
pub struct Drone {
    pub name: Option<String>,
    pub folder: Option<String>,
    pub ignored: bool,
    pub fc_id: Option<String>,
    pub craft_name: Option<String>,
    pub board: Option<String>,
    pub firmware: Option<String>,
    pub first_seen: Option<String>,
    pub last_backup: Option<String>,
}

impl Drone {
    pub fn name_or_default(&self) -> String {
        [&self.name, &self.craft_name, &self.board]
            .iter()
            .find_map(|v| v.as_ref().filter(|s| !s.is_empty()).cloned())
            .unwrap_or_else(|| "Unnamed quad".into())
    }

    pub fn folder_exists(&self) -> bool {
        self.folder.as_deref().map(|f| !f.is_empty() && Path::new(f).is_dir()).unwrap_or(false)
    }

    fn is(&self, fc_id: &str) -> bool {
        self.fc_id.as_deref().map(|id| id.eq_ignore_ascii_case(fc_id)).unwrap_or(false)
    }
}

#[derive(Serialize, Deserialize, Clone, Debug, Default)]
#[serde(rename_all = "PascalCase", default)]
pub struct DroneDb {
    pub drones: Vec<Drone>,
    /// Drones you clicked Forget on, kept (with their folder) so they can be restored.
    pub forgotten: Vec<Drone>,
}

// The backup worker and the UI both edit drones.json, so every write re-reads it under this lock.
static DB_GATE: Mutex<()> = Mutex::new(());

impl DroneDb {
    pub fn load() -> DroneDb {
        let _g = DB_GATE.lock().unwrap_or_else(|e| e.into_inner());
        Self::load_unlocked()
    }

    fn load_unlocked() -> DroneDb {
        match load_json::<DroneDb>(&drones_path()) {
            Some(Ok(db)) => db,
            Some(Err(e)) => {
                log(format!("drones.json is invalid: {e}"));
                DroneDb::default()
            }
            None => DroneDb::default(),
        }
    }

    pub fn find(&self, fc_id: &str) -> Option<&Drone> {
        self.drones.iter().find(|d| d.is(fc_id))
    }

    pub fn find_forgotten(&self, fc_id: &str) -> Option<&Drone> {
        self.forgotten.iter().find(|d| d.is(fc_id))
    }

    pub fn find_by_craft_name(&self, craft: &str) -> Option<&Drone> {
        if craft.is_empty() {
            return None;
        }
        self.drones.iter().rev().find(|d| {
            !d.ignored
                && d.folder.is_some()
                && (d.craft_name.as_deref().map_or(false, |c| c.eq_ignore_ascii_case(craft))
                    || d.name.as_deref().map_or(false, |n| n.eq_ignore_ascii_case(craft)))
        })
    }

    pub fn mutate(change: impl FnOnce(&mut DroneDb)) {
        let _g = DB_GATE.lock().unwrap_or_else(|e| e.into_inner());
        let mut db = Self::load_unlocked();
        change(&mut db);
        if let Err(e) = save_json(&drones_path(), &db) {
            log(format!("Couldn't write drones.json: {e}"));
        }
    }

    pub fn upsert(drone: Drone) {
        let id = drone.fc_id.clone().unwrap_or_default();
        Self::mutate(|db| {
            match db.drones.iter_mut().find(|d| d.is(&id)) {
                Some(existing) => *existing = drone,
                None => db.drones.push(drone),
            }
            db.forgotten.retain(|d| !d.is(&id));
        });
    }

    /// "Forget": out of the Hangar, but remembered so it can be restored.
    pub fn forget(fc_id: &str) {
        Self::mutate(|db| {
            let gone: Vec<Drone> = db.drones.iter().filter(|d| d.is(fc_id)).cloned().collect();
            db.drones.retain(|d| !d.is(fc_id));
            for d in gone {
                db.forgotten.retain(|f| !f.is(fc_id));
                db.forgotten.push(d);
            }
        });
    }

    /// Brings a forgotten drone back into the Hangar with its old folder.
    pub fn restore(fc_id: &str) {
        Self::mutate(|db| {
            if let Some(i) = db.forgotten.iter().position(|d| d.is(fc_id)) {
                let mut d = db.forgotten.remove(i);
                d.ignored = false;
                if db.find(fc_id).is_none() {
                    db.drones.push(d);
                }
            }
        });
    }

    /// Turns backups back on for a drone set to "never" / paused.
    pub fn set_paused(fc_id: &str, paused: bool) {
        Self::mutate(|db| {
            if let Some(d) = db.drones.iter_mut().find(|d| d.is(fc_id)) {
                d.ignored = paused;
            }
        });
    }
}

// ------------------------------------------------------------------------------------------------ backup files

pub const SPECIAL_TAG: &str = "_SPECIAL_";

/// Strips characters Windows won't allow in a file name.
pub fn safe_name(name: &str) -> String {
    let clean: String = name
        .chars()
        .map(|c| if "<>:\"/\\|?*".contains(c) || (c as u32) < 32 { '_' } else { c })
        .collect();
    let clean = clean.trim().trim_matches('.').trim().to_string();
    if clean.is_empty() { "Drone".into() } else { clean }
}

/// Writes Name_YYYY-MM-DD_HH-MM-SS[_SPECIAL_<name>][_INCOMPLETE].txt and returns its path.
pub fn write_backup(folder: &Path, drone_name: &str, cli: &str, complete: bool, when: chrono::NaiveDateTime, special: Option<&str>) -> std::io::Result<PathBuf> {
    fs::create_dir_all(folder)?;
    let mut name = format!("{}_{}", safe_name(drone_name), when.format("%Y-%m-%d_%H-%M-%S"));
    if let Some(s) = special {
        name.push_str(SPECIAL_TAG);
        name.push_str(&safe_name(s));
    }
    if !complete {
        name.push_str("_INCOMPLETE");
    }
    let file = folder.join(name + ".txt");
    fs::write(&file, cli)?;
    Ok(file)
}

/// True when the newest complete backup in the folder has exactly this content.
pub fn same_as_latest(folder: &Path, cli: &str) -> bool {
    let Ok(entries) = fs::read_dir(folder) else { return false };
    let latest = entries
        .flatten()
        .filter(|e| {
            let n = e.file_name().to_string_lossy().to_string();
            n.ends_with(".txt") && !n.contains("_INCOMPLETE")
        })
        .filter_map(|e| Some((e.metadata().ok()?.modified().ok()?, e.path())))
        .max_by_key(|(t, _)| *t);
    match latest {
        Some((_, path)) => fs::read_to_string(path).map(|t| t == cli).unwrap_or(false),
        None => false,
    }
}

pub fn count_backups(folder: Option<&str>) -> (usize, usize) {
    let Some(folder) = folder else { return (0, 0) };
    let Ok(entries) = fs::read_dir(folder) else { return (0, 0) };
    let names: Vec<String> = entries
        .flatten()
        .map(|e| e.file_name().to_string_lossy().to_string())
        .filter(|n| n.ends_with(".txt"))
        .collect();
    let specials = names.iter().filter(|n| n.contains(SPECIAL_TAG)).count();
    (names.len(), specials)
}

// ------------------------------------------------------------------------------------------------ shared UI vocabulary

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ToastKind { Success, Info, Warning, Error, Special }

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DroneMood { New, NewFlightController, MissingFolder, Returning }

#[derive(Clone, Debug, PartialEq)]
pub enum NewDroneChoice {
    Folder(String),
    Never,
}

pub mod quips {
    use std::sync::atomic::{AtomicUsize, Ordering};
    static N: AtomicUsize = AtomicUsize::new(0);

    fn pick(list: &[&'static str]) -> &'static str {
        let seed = chrono::Local::now().timestamp_subsec_nanos() as usize;
        list[(seed + N.fetch_add(1, Ordering::Relaxed)) % list.len()]
    }

    pub fn saved() -> &'static str {
        pick(&[
            "Tune secured. Go send it.",
            "Your PIDs are safe with me.",
            "Saved! Now crash responsibly.",
            "Filed away in the hangar.",
            "Snapshot taken. Rip that gap.",
            "Backed up. Props on, fear off.",
            "Safe and sound. Unlike your props.",
        ])
    }

    pub fn special() -> &'static str {
        pick(&[
            "Framed and hung on the hangar wall.",
            "Hall of fame material.",
            "This one's a keeper.",
            "Saved with extra sparkle.",
            "Certified banger tune.",
        ])
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn safe_names() {
        assert_eq!(safe_name("My:Quad?/7"), "My_Quad__7");
        assert_eq!(safe_name("  ..  "), "Drone");
    }

    #[test]
    fn reads_csharp_json() {
        let json = "\u{feff}{\"Drones\":[{\"Name\":\"ERA\",\"Folder\":\"C:\\\\x\\\\ERA\",\"Ignored\":false,\"FcId\":\"ABC\",\"CraftName\":\"ERA\",\"Board\":null,\"Firmware\":null,\"FirstSeen\":null,\"LastBackup\":null}]}";
        let db: DroneDb = serde_json::from_str(json.trim_start_matches('\u{feff}')).unwrap();
        assert_eq!(db.drones[0].folder.as_deref(), Some("C:\\x\\ERA"));
        assert!(db.forgotten.is_empty());
        let s: Settings = serde_json::from_str("{\"CliCommand\":\"dump all\",\"UsbVendorIds\":[\"0483\"]}").unwrap();
        assert_eq!(s.cli_command, "dump all");
        assert!(s.skip_unchanged_backups && s.disconnect_wait_seconds == 2);
    }
}
