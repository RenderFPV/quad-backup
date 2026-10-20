//! Operating-system pieces. Each platform module provides the same set of items:
//! `NativePlatform` (the engine's `Platform`), `find_betaflight_shortcut`, `shell_open`, `open_folder`,
//! `open_text_file`, `startup_enabled` / `set_startup`, `claim_instance` / `Claim` / `Instance`,
//! `other_version_running`, `message_box`, `work_area`, `TOASTS_FROM_TOP` and `TRAY_WORD`.

use crate::core::fc::Link;
use std::io::{self, Read, Write};
use std::time::Duration;

#[cfg(windows)]
mod windows;
#[cfg(windows)]
pub use windows::*;

#[cfg(unix)]
mod posix;
#[cfg(unix)]
pub use posix::*;

/// A serial port through serialport-rs: reads return as soon as any bytes arrive, or Ok(0) on timeout.
pub(crate) struct SerialLink(pub Box<dyn serialport::SerialPort>);

impl Link for SerialLink {
    fn write(&mut self, data: &[u8]) -> io::Result<()> {
        self.0.write_all(data)
    }

    fn read(&mut self, buf: &mut [u8], timeout: Duration) -> io::Result<usize> {
        self.0.set_timeout(timeout.max(Duration::from_millis(1)))?;
        match self.0.read(buf) {
            Ok(n) => Ok(n),
            Err(e) if e.kind() == io::ErrorKind::TimedOut || e.kind() == io::ErrorKind::WouldBlock => Ok(0),
            Err(e) => Err(e),
        }
    }
}
