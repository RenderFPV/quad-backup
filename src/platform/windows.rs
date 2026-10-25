//! Windows pieces the engine needs: serial ports, USB vendor IDs, "does Betaflight have this port?", spotting and
//! launching Betaflight, start with Windows, and single-instance handling.

use crate::core::engine::{OpenError, Platform};
use crate::core::fc::Link;
use super::SerialLink;
use std::ffi::OsStr;
use std::io;
use std::os::windows::ffi::OsStrExt;
use std::path::{Path, PathBuf};
use std::time::Duration;
use windows_sys::Win32::Foundation::{CloseHandle, GetLastError, BOOL, ERROR_ACCESS_DENIED, ERROR_ALREADY_EXISTS, HANDLE, HWND, INVALID_HANDLE_VALUE, LPARAM};
use windows_sys::Win32::Storage::FileSystem::{CreateFileW, OPEN_EXISTING};
use windows_sys::Win32::System::Threading::{
    CreateEventW, CreateMutexW, OpenMutexW, OpenProcess, QueryFullProcessImageNameW, SetEvent, WaitForSingleObject, PROCESS_QUERY_LIMITED_INFORMATION,
};
use windows_sys::Win32::UI::Shell::ShellExecuteW;
use windows_sys::Win32::UI::WindowsAndMessaging::{
    EnumWindows, GetWindowTextLengthW, GetWindowTextW, GetWindowThreadProcessId, IsWindowVisible, MessageBoxW, MB_ICONINFORMATION, MB_OK, SW_SHOWNORMAL,
};

const GENERIC_READ: u32 = 0x8000_0000;
const GENERIC_WRITE: u32 = 0x4000_0000;
const SYNCHRONIZE: u32 = 0x0010_0000;

pub fn wide(s: &str) -> Vec<u16> {
    OsStr::new(s).encode_wide().chain(std::iter::once(0)).collect()
}

// ------------------------------------------------------------------------------------------------ serial

pub struct WinPlatform;
pub use self::WinPlatform as NativePlatform;

/// Pop-ups go bottom-right, above the taskbar.
pub const TOASTS_FROM_TOP: bool = false;
pub const TRAY_WORD: &str = "tray";

impl Platform for WinPlatform {
    /// The same list Device Manager shows under "Ports", straight from the registry (fast enough to poll).
    fn list_ports(&self) -> Vec<String> {
        let key = winreg::RegKey::predef(winreg::enums::HKEY_LOCAL_MACHINE).open_subkey("HARDWARE\\DEVICEMAP\\SERIALCOMM");
        let Ok(key) = key else { return Vec::new() };
        let mut ports: Vec<String> = key
            .enum_values()
            .flatten()
            .filter_map(|(_, v)| String::try_from(v.to_string()).ok())
            .map(|p| p.trim_matches(char::from(0)).trim().to_uppercase())
            .filter(|p| !p.is_empty())
            .collect();
        ports.sort();
        ports.dedup();
        ports
    }

    fn vendor_id(&self, port: &str) -> Option<String> {
        serialport::available_ports().ok()?.into_iter().find(|p| p.port_name.eq_ignore_ascii_case(port)).and_then(|p| match p.port_type {
            serialport::SerialPortType::UsbPort(u) => Some(format!("{:04X}", u.vid)),
            _ => None,
        })
    }

    fn open(&self, port: &str) -> Result<Box<dyn Link>, OpenError> {
        match serialport::new(port, 115_200).timeout(Duration::from_millis(50)).open() {
            Ok(mut p) => {
                let _ = p.write_data_terminal_ready(true);
                let _ = p.write_request_to_send(true);
                let _ = p.clear(serialport::ClearBuffer::All);
                Ok(Box::new(SerialLink(p)))
            }
            Err(e) => {
                let busy = matches!(e.kind(), serialport::ErrorKind::Io(io::ErrorKind::PermissionDenied))
                    || e.description.to_ascii_lowercase().contains("access is denied");
                if busy { Err(OpenError::Busy) } else { Err(OpenError::Other(io::Error::new(io::ErrorKind::Other, e.description))) }
            }
        }
    }

    /// Opens and closes the port without sending anything, so the FC never notices.
    fn is_in_use(&self, port: &str) -> Option<bool> {
        let path = wide(&format!("\\\\.\\{port}"));
        unsafe {
            let h = CreateFileW(path.as_ptr(), GENERIC_READ | GENERIC_WRITE, 0, std::ptr::null(), OPEN_EXISTING, 0, std::ptr::null_mut());
            if h != INVALID_HANDLE_VALUE {
                CloseHandle(h);
                return Some(false);
            }
            if GetLastError() == ERROR_ACCESS_DENIED { Some(true) } else { None }
        }
    }

    fn is_betaflight_running(&self) -> bool {
        betaflight_window_open()
    }

    fn find_launch_target(&self) -> String {
        find_betaflight_shortcut().unwrap_or_else(|| "https://app.betaflight.com".into())
    }

    fn launch(&self, target: &str) -> io::Result<()> {
        shell_open(target)
    }
}

// ------------------------------------------------------------------------------------------------ Betaflight

const BROWSERS: &[&str] = &["chrome", "msedge", "brave", "opera", "vivaldi", "chromium", "arc"];

struct Scan {
    found: bool,
}

unsafe extern "system" fn enum_window(hwnd: HWND, lparam: LPARAM) -> BOOL {
    let scan = &mut *(lparam as *mut Scan);
    if IsWindowVisible(hwnd) == 0 {
        return 1;
    }
    let len = GetWindowTextLengthW(hwnd);
    if len == 0 {
        return 1;
    }
    let mut buf = vec![0u16; len as usize + 1];
    let n = GetWindowTextW(hwnd, buf.as_mut_ptr(), buf.len() as i32);
    let title = String::from_utf16_lossy(&buf[..n.max(0) as usize]);
    let lower = title.to_lowercase();
    if !lower.contains("betaflight") {
        return 1;
    }
    let mut pid = 0u32;
    GetWindowThreadProcessId(hwnd, &mut pid);
    let process = process_name(pid);
    if process.contains("betaflight")
        || (BROWSERS.contains(&process.as_str()) && lower.starts_with("betaflight") && !lower.contains("blackbox"))
    {
        scan.found = true;
        return 0;
    }
    1
}

fn process_name(pid: u32) -> String {
    unsafe {
        let h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if h.is_null() {
            return String::new();
        }
        let mut buf = [0u16; 512];
        let mut size = buf.len() as u32;
        let ok = QueryFullProcessImageNameW(h, 0, buf.as_mut_ptr(), &mut size);
        CloseHandle(h);
        if ok == 0 {
            return String::new();
        }
        let path = String::from_utf16_lossy(&buf[..size as usize]);
        Path::new(&path).file_stem().map(|s| s.to_string_lossy().to_lowercase()).unwrap_or_default()
    }
}

/// True when the desktop Betaflight Configurator or the Betaflight web app (installed or in a browser tab) is open.
pub fn betaflight_window_open() -> bool {
    let mut scan = Scan { found: false };
    unsafe { EnumWindows(Some(enum_window), &mut scan as *mut Scan as LPARAM) };
    scan.found
}

/// A Start Menu / Desktop shortcut for the Betaflight Configurator or installed web app.
pub fn find_betaflight_shortcut() -> Option<String> {
    // Start Menu folders are searched with their subfolders; the Desktop only at the top level (it can hold
    // whole projects with tens of thousands of files)
    let mut roots = Vec::new();
    if let Some(appdata) = std::env::var_os("APPDATA") {
        roots.push((PathBuf::from(appdata).join("Microsoft\\Windows\\Start Menu\\Programs"), 4));
    }
    if let Some(pd) = std::env::var_os("PROGRAMDATA") {
        roots.push((PathBuf::from(pd).join("Microsoft\\Windows\\Start Menu\\Programs"), 4));
    }
    if let Some(home) = std::env::var_os("USERPROFILE") {
        roots.push((PathBuf::from(home).join("Desktop"), 0));
    }
    let mut found = Vec::new();
    for (root, depth) in roots {
        walk(&root, depth, &mut found);
    }
    found.retain(|p: &PathBuf| {
        let n = p.file_stem().map(|s| s.to_string_lossy().to_lowercase()).unwrap_or_default();
        n.starts_with("betaflight") && !n.contains("blackbox") && !n.contains("uninstall")
    });
    found.sort_by_key(|p| !p.to_string_lossy().to_lowercase().contains("configur"));
    found.first().map(|p| p.to_string_lossy().into_owned())
}

/// Collects .lnk files, going `levels` folders deep.
fn walk(dir: &Path, levels: u32, out: &mut Vec<PathBuf>) {
    let Ok(entries) = std::fs::read_dir(dir) else { return };
    for e in entries.flatten() {
        let p = e.path();
        if p.is_dir() {
            if levels > 0 {
                walk(&p, levels - 1, out);
            }
        } else if p.extension().map_or(false, |x| x.eq_ignore_ascii_case("lnk")) {
            out.push(p);
        }
    }
}

/// Opens a file, folder, shortcut or URL the way double-clicking it would.
pub fn shell_open(target: &str) -> io::Result<()> {
    let verb = wide("open");
    let file = wide(target);
    let r = unsafe { ShellExecuteW(std::ptr::null_mut(), verb.as_ptr(), file.as_ptr(), std::ptr::null(), std::ptr::null(), SW_SHOWNORMAL) };
    if r as usize > 32 { Ok(()) } else { Err(io::Error::new(io::ErrorKind::Other, format!("couldn't open {target}"))) }
}

pub fn open_folder(folder: &str) {
    let _ = std::fs::create_dir_all(folder);
    let _ = shell_open(folder);
}

pub fn open_text_file(file: &Path) {
    if !file.exists() {
        let _ = std::fs::write(file, "");
    }
    let _ = std::process::Command::new("notepad.exe").arg(file).spawn();
}

// ------------------------------------------------------------------------------------------------ start with Windows

const RUN_KEY: &str = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
const RUN_VALUE: &str = "QuadBackupRust";

pub fn startup_enabled() -> bool {
    winreg::RegKey::predef(winreg::enums::HKEY_CURRENT_USER)
        .open_subkey(RUN_KEY)
        .and_then(|k| k.get_value::<String, _>(RUN_VALUE))
        .is_ok()
}

pub fn set_startup(enabled: bool) -> io::Result<()> {
    let (key, _) = winreg::RegKey::predef(winreg::enums::HKEY_CURRENT_USER).create_subkey(RUN_KEY)?;
    if enabled {
        let exe = std::env::current_exe()?;
        key.set_value(RUN_VALUE, &format!("\"{}\" --tray", exe.display()))
    } else {
        match key.delete_value(RUN_VALUE) {
            Err(e) if e.kind() != io::ErrorKind::NotFound => Err(e),
            _ => Ok(()),
        }
    }
}

// ------------------------------------------------------------------------------------------------ single instance

pub struct Instance {
    _mutex: HANDLE,
    show: HANDLE,
}

unsafe impl Send for Instance {}
unsafe impl Sync for Instance {}

pub enum Claim {
    First(Instance),
    AlreadyRunning,
}

/// Claims "the" running copy. A second copy just pokes the first to open its window.
/// `wake_running`: if another copy already has it, ask that copy to open its window.
pub fn claim_instance(wake_running: bool) -> Claim {
    unsafe {
        // demo copies get their own names, so they never poke the real app
        let tag = if std::env::var_os("QB_DEMO").is_some() { "QuadBackupRust.Demo" } else { "QuadBackupRust" };
        let mutex = CreateMutexW(std::ptr::null(), 1, wide(&format!("{tag}.SingleInstance")).as_ptr());
        let already = GetLastError() == ERROR_ALREADY_EXISTS;
        let show = CreateEventW(std::ptr::null(), 0, 0, wide(&format!("{tag}.ShowWindow")).as_ptr());
        if already {
            if wake_running {
                SetEvent(show);
            }
            // let go of our handles, or they'd keep the name taken after the other copy exits
            CloseHandle(show);
            CloseHandle(mutex);
            return Claim::AlreadyRunning;
        }
        Claim::First(Instance { _mutex: mutex, show })
    }
}

impl Instance {
    /// Blocks until another copy asks this one to show its window (or the timeout passes).
    pub fn wait_for_show(&self, timeout: Duration) -> bool {
        unsafe { WaitForSingleObject(self.show, timeout.as_millis() as u32) == 0 }
    }
}

/// Is the C# Quad Backup running? Two copies would fight over the same quad.
pub fn other_version_running() -> bool {
    unsafe {
        let h = OpenMutexW(SYNCHRONIZE, 0, wide("QuadBackup.SingleInstance").as_ptr());
        if h.is_null() {
            return false;
        }
        CloseHandle(h);
        true
    }
}

pub fn message_box(title: &str, text: &str) {
    unsafe {
        MessageBoxW(std::ptr::null_mut(), wide(text).as_ptr(), wide(title).as_ptr(), MB_OK | MB_ICONINFORMATION);
    }
}

/// The desktop area not covered by the taskbar, in physical pixels: (left, top, right, bottom).
pub fn work_area() -> Option<(i32, i32, i32, i32)> {
    use windows_sys::Win32::Foundation::RECT;
    use windows_sys::Win32::UI::WindowsAndMessaging::{SystemParametersInfoW, SPI_GETWORKAREA};
    let mut r = RECT { left: 0, top: 0, right: 1920, bottom: 1080 };
    unsafe { SystemParametersInfoW(SPI_GETWORKAREA, 0, &mut r as *mut RECT as *mut _, 0) };
    Some((r.left, r.top, r.right, r.bottom))
}
