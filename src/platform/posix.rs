//! Mac and Linux pieces. EXPERIMENTAL: compile-checked and built, not yet run with a real quad.
//!
//! Ports are /dev/cu.usbmodem* (Mac) and /dev/ttyACM* (Linux), with USB vendor IDs from ioreg / sysfs.
//! Unlike Windows, two programs can open the same serial port here, so "does Betaflight have it?" is answered by
//! asking which processes have the device open (lsof on a Mac, /proc on Linux).

use super::SerialLink;
use crate::core::engine::{OpenError, Platform};
use crate::core::fc::Link;
use crate::core::store;
use std::collections::HashMap;
use std::fs;
use std::io;
use std::os::unix::io::AsRawFd;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::Mutex;
use std::time::{Duration, Instant};

const MAC: bool = cfg!(target_os = "macos");
const BETAFLIGHT_URL: &str = "https://app.betaflight.com";
/// Chrome's id for the installed Betaflight web app
const BETAFLIGHT_PWA_ID: &str = "kecmkdilaabfklljnjoaeognfdagnmog";

/// Mac notifications live top-right; Linux pop-ups go bottom-right.
pub const TOASTS_FROM_TOP: bool = MAC;
pub const TRAY_WORD: &str = if MAC { "menu bar" } else { "tray" };

fn run(cmd: &str, args: &[&str]) -> Option<String> {
    let out = Command::new(cmd).args(args).output().ok()?;
    Some(String::from_utf8_lossy(&out.stdout).into_owned())
}

fn home() -> PathBuf {
    std::env::var_os("HOME").map(PathBuf::from).unwrap_or_else(|| PathBuf::from("/"))
}

pub struct PosixPlatform;
pub use self::PosixPlatform as NativePlatform;

impl Platform for PosixPlatform {
    /// USB CDC serial devices only: flight controllers show up as these. USB-to-serial adapters (FTDI, CP210x,
    /// CH340 - usbserial / ttyUSB) are left alone.
    fn list_ports(&self) -> Vec<String> {
        let Ok(entries) = fs::read_dir("/dev") else { return Vec::new() };
        let prefix = if MAC { "cu.usbmodem" } else { "ttyACM" };
        let mut ports: Vec<String> = entries
            .flatten()
            .map(|e| e.file_name().to_string_lossy().into_owned())
            .filter(|n| n.starts_with(prefix))
            .map(|n| format!("/dev/{n}"))
            .collect();
        ports.sort();
        ports
    }

    fn vendor_id(&self, port: &str) -> Option<String> {
        if MAC { mac_vendor_id(port) } else { linux_vendor_id(port) }
    }

    fn open(&self, port: &str) -> Result<Box<dyn Link>, OpenError> {
        if self.is_in_use(port) == Some(true) {
            return Err(OpenError::Busy);
        }
        // serialport takes the port exclusively (TIOCEXCL), so Betaflight can't open it mid-backup either
        match serialport::new(port, 115_200).timeout(Duration::from_millis(50)).open() {
            Ok(mut p) => {
                let _ = p.write_data_terminal_ready(true);
                let _ = p.clear(serialport::ClearBuffer::All);
                Ok(Box::new(SerialLink(p)))
            }
            Err(e) => {
                let text = e.description.to_lowercase();
                if text.contains("busy") {
                    Err(OpenError::Busy)
                } else if text.contains("permission") {
                    let hint = if MAC { String::new() } else { " - add yourself to the 'dialout' group (Arch: 'uucp') and log in again".into() };
                    Err(OpenError::Other(io::Error::new(io::ErrorKind::PermissionDenied, format!("not allowed to open {port}{hint}"))))
                } else {
                    Err(OpenError::Other(io::Error::new(io::ErrorKind::Other, e.description)))
                }
            }
        }
    }

    fn is_in_use(&self, port: &str) -> Option<bool> {
        if !Path::new(port).exists() {
            return None;
        }
        if MAC { mac_in_use(port) } else { linux_in_use(port) }
    }

    fn is_betaflight_running(&self) -> bool {
        let args: &[&str] = if MAC { &["-axo", "command="] } else { &["-eo", "args="] };
        let Some(out) = run("ps", args) else { return false };
        let me = std::env::current_exe().map(|p| p.to_string_lossy().into_owned()).unwrap_or_default();
        out.lines().any(|l| {
            let lower = l.to_lowercase();
            !l.is_empty() && (me.is_empty() || !l.contains(&me))
                && (lower.contains("betaflight") || l.contains(BETAFLIGHT_PWA_ID))
                && !lower.contains("blackbox")
        })
    }

    fn find_launch_target(&self) -> String {
        find_betaflight_shortcut().unwrap_or_else(|| BETAFLIGHT_URL.into())
    }

    fn launch(&self, target: &str) -> io::Result<()> {
        if MAC {
            return spawn("/usr/bin/open", &[target]);
        }
        if target.ends_with(".desktop") {
            let name = Path::new(target).file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default();
            spawn("gtk-launch", &[&name]).or_else(|_| spawn("xdg-open", &[target]))
        } else if target.starts_with("http") {
            spawn("xdg-open", &[target])
        } else {
            spawn(target, &[])
        }
    }

    /// spotting Betaflight by process name can miss a browser tab, so always watch flight controller ports
    fn probe_always(&self) -> bool { true }
    /// lsof / scanning /proc costs more than Windows' quick open/close
    fn probe_interval(&self) -> Duration { Duration::from_millis(if MAC { 1000 } else { 500 }) }
}

fn spawn(cmd: &str, args: &[&str]) -> io::Result<()> {
    Command::new(cmd).args(args).spawn().map(|_| ())
}

// ------------------------------------------------------------------------------------------------ USB vendor IDs

/// /sys/class/tty/ttyACM0/device is the USB interface; its parent is the USB device with idVendor.
fn linux_vendor_id(port: &str) -> Option<String> {
    let name = Path::new(port).file_name()?.to_string_lossy().into_owned();
    let iface = fs::canonicalize(format!("/sys/class/tty/{name}/device")).ok()?;
    let id = fs::read_to_string(iface.parent()?.join("idVendor")).ok()?;
    Some(id.trim().to_uppercase())
}

static IOREG: Mutex<Option<(Instant, HashMap<String, String>)>> = Mutex::new(None);

/// ioreg lists USB devices with their properties, children indented below them. The nearest idVendor above a
/// serial port's IOCalloutDevice belongs to the device that port lives on.
fn mac_vendor_id(port: &str) -> Option<String> {
    let mut cache = IOREG.lock().unwrap_or_else(|e| e.into_inner());
    let stale = cache.as_ref().map_or(true, |(at, map)| at.elapsed() > Duration::from_secs(3) || !map.contains_key(port));
    if stale {
        let mut map = HashMap::new();
        let mut vendor: Option<String> = None;
        for line in run("/usr/sbin/ioreg", &["-r", "-c", "IOUSBHostDevice", "-l", "-w0"]).unwrap_or_default().lines() {
            if let Some(v) = line.split("\"idVendor\" = ").nth(1) {
                vendor = v.trim().parse::<u32>().ok().map(|n| format!("{n:04X}"));
            }
            if let Some(dev) = line.split("\"IOCalloutDevice\" = \"").nth(1) {
                if let (Some(path), Some(v)) = (dev.split('"').next(), &vendor) {
                    map.insert(path.to_string(), v.clone());
                }
            }
        }
        *cache = Some((Instant::now(), map));
    }
    let map = &cache.as_ref().unwrap().1;
    if map.is_empty() {
        // ioreg didn't work at all: let the MSP check decide (only usbmodem devices are ever listed)
        return Some("0483".into());
    }
    map.get(port).cloned()
}

// ------------------------------------------------------------------------------------------------ in use?

fn mac_in_use(port: &str) -> Option<bool> {
    // the same port has a "cu." and a "tty." node; Betaflight may have opened either
    let tty = port.replace("/dev/cu.", "/dev/tty.");
    let out = run("/usr/sbin/lsof", &["-t", port, &tty])?;
    let me = std::process::id();
    Some(out.lines().filter_map(|l| l.trim().parse::<u32>().ok()).any(|pid| pid != me))
}

fn linux_in_use(port: &str) -> Option<bool> {
    let me = std::process::id().to_string();
    let target = Path::new(port);
    for proc in fs::read_dir("/proc").ok()?.flatten() {
        let pid = proc.file_name().to_string_lossy().into_owned();
        if pid == me || !pid.chars().all(|c| c.is_ascii_digit()) {
            continue;
        }
        let Ok(fds) = fs::read_dir(proc.path().join("fd")) else { continue }; // other users' processes
        for fd in fds.flatten() {
            if fs::read_link(fd.path()).map_or(false, |t| t == target) {
                return Some(true);
            }
        }
    }
    Some(false)
}

// ------------------------------------------------------------------------------------------------ Betaflight app

pub fn find_betaflight_shortcut() -> Option<String> {
    let mut candidates: Vec<PathBuf> = Vec::new();
    let dirs: Vec<PathBuf> = if MAC {
        vec!["/Applications".into(), home().join("Applications"), home().join("Applications/Chrome Apps.localized")]
    } else {
        vec![home().join(".local/share/applications"), "/usr/share/applications".into(), "/usr/local/share/applications".into()]
    };
    for dir in dirs {
        if let Ok(entries) = fs::read_dir(dir) {
            candidates.extend(entries.flatten().map(|e| e.path()));
        }
    }
    let mut found: Vec<PathBuf> = candidates.into_iter().filter(|p| is_betaflight_app(p)).collect();
    found.sort_by_key(|p| !p.to_string_lossy().to_lowercase().contains("configur"));
    found.first().map(|p| p.to_string_lossy().into_owned())
}

fn is_betaflight_app(path: &Path) -> bool {
    let name = path.file_name().map(|n| n.to_string_lossy().to_lowercase()).unwrap_or_default();
    let wanted_ext = if MAC { ".app" } else { ".desktop" };
    if !name.ends_with(wanted_ext) || name.contains("blackbox") {
        return false;
    }
    if name.contains("betaflight") || name.contains(BETAFLIGHT_PWA_ID) {
        return true;
    }
    // e.g. chrome-<id>-Default.desktop with "Name=Betaflight - Configuration and management application"
    !MAC && fs::read_to_string(path).map_or(false, |t| t.lines().any(|l| l.starts_with("Name=") && l.contains("Betaflight")) && !t.contains("Blackbox"))
}

// ------------------------------------------------------------------------------------------------ opening things

pub fn shell_open(target: &str) -> io::Result<()> {
    spawn(if MAC { "/usr/bin/open" } else { "xdg-open" }, &[target])
}

pub fn open_folder(folder: &str) {
    let _ = fs::create_dir_all(folder);
    let _ = shell_open(folder);
}

pub fn open_text_file(file: &Path) {
    if !file.exists() {
        let _ = fs::write(file, "");
    }
    let path = file.to_string_lossy().into_owned();
    let _ = if MAC { spawn("/usr/bin/open", &["-t", &path]) } else { spawn("xdg-open", &[&path]) };
}

// ------------------------------------------------------------------------------------------------ start at login

fn startup_file() -> PathBuf {
    if MAC {
        home().join("Library/LaunchAgents/com.quadbackup.rust.plist")
    } else {
        let config = std::env::var_os("XDG_CONFIG_HOME").map(PathBuf::from).unwrap_or_else(|| home().join(".config"));
        config.join("autostart/quad-backup-rust.desktop")
    }
}

pub fn startup_enabled() -> bool { startup_file().exists() }

pub fn set_startup(enabled: bool) -> io::Result<()> {
    let file = startup_file();
    if !enabled {
        return match fs::remove_file(&file) {
            Err(e) if e.kind() != io::ErrorKind::NotFound => Err(e),
            _ => Ok(()),
        };
    }
    let exe = std::env::current_exe()?.to_string_lossy().into_owned();
    fs::create_dir_all(file.parent().unwrap())?;
    let text = if MAC {
        let esc = exe.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;");
        format!("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n<plist version=\"1.0\"><dict>\n  <key>Label</key><string>com.quadbackup.rust</string>\n  <key>ProgramArguments</key><array><string>{esc}</string><string>--tray</string></array>\n  <key>RunAtLoad</key><true/>\n  <key>ProcessType</key><string>Interactive</string>\n</dict></plist>\n")
    } else {
        format!("[Desktop Entry]\nType=Application\nName=Quad Backup\nComment=Backs up Betaflight drones when they're plugged in\nExec=\"{exe}\" --tray\nX-GNOME-Autostart-enabled=true\n")
    };
    fs::write(file, text)
}

// ------------------------------------------------------------------------------------------------ single instance

pub struct Instance {
    _lock: fs::File,
}

pub enum Claim {
    First(Instance),
    AlreadyRunning,
}

fn show_signal() -> PathBuf { store::data_dir().join("show.signal") }

/// An exclusive lock on a file in the data folder marks "the" running copy. A second copy drops a signal file
/// that the first one notices and opens its window.
pub fn claim_instance() -> Claim {
    let path = store::data_dir().join("instance.lock");
    let Ok(file) = fs::OpenOptions::new().create(true).truncate(false).write(true).open(&path) else {
        return Claim::First(Instance { _lock: fs::File::open("/dev/null").unwrap() });
    };
    let locked = unsafe { libc::flock(file.as_raw_fd(), libc::LOCK_EX | libc::LOCK_NB) } == 0;
    if locked {
        let _ = fs::remove_file(show_signal());
        Claim::First(Instance { _lock: file })
    } else {
        let _ = fs::write(show_signal(), "");
        Claim::AlreadyRunning
    }
}

impl Instance {
    /// Waits until another copy asks this one to show its window (or the timeout passes).
    pub fn wait_for_show(&self, timeout: Duration) -> bool {
        let until = Instant::now() + timeout;
        while Instant::now() < until {
            if show_signal().exists() {
                let _ = fs::remove_file(show_signal());
                return true;
            }
            std::thread::sleep(Duration::from_millis(700));
        }
        false
    }
}

/// The C# version only runs on Windows here.
pub fn other_version_running() -> bool { false }

pub fn message_box(title: &str, text: &str) {
    store::log(format!("{title}: {text}"));
    eprintln!("{title}: {text}");
}

/// Not known here; the UI falls back to the monitor size.
pub fn work_area() -> Option<(i32, i32, i32, i32)> { None }
