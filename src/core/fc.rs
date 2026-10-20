//! The MSP + CLI conversation that reads a "diff all" from a Betaflight flight controller.

use regex::Regex;
use std::io;
use std::sync::OnceLock;
use std::time::{Duration, Instant};

pub trait Link: Send {
    fn write(&mut self, data: &[u8]) -> io::Result<()>;
    /// Returns as soon as any bytes are available, or Ok(0) after `timeout` with nothing received.
    fn read(&mut self, buf: &mut [u8], timeout: Duration) -> io::Result<usize>;
}

#[derive(Clone, Debug, Default)]
pub struct FcBackup {
    /// MCU unique ID; None if the FC was found already sitting in CLI mode
    pub fc_id: Option<String>,
    pub variant: Option<String>,   // "BTFL"
    pub firmware: Option<String>,  // "Betaflight 4.5.1"
    pub board: Option<String>,     // "MATEKF405"
    pub craft_name: Option<String>,
    pub cli: String,               // the diff, CRLF line endings
    pub complete: bool,
    /// the FC rebooted on leaving the CLI, so its port will drop out and come back
    pub rebooted: bool,
}

#[derive(Debug)]
pub enum ReadError {
    NotFlightController(String),
    Io(io::Error),
}

impl From<io::Error> for ReadError {
    fn from(e: io::Error) -> Self { ReadError::Io(e) }
}

impl std::fmt::Display for ReadError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ReadError::NotFlightController(m) => write!(f, "{m}"),
            ReadError::Io(e) => write!(f, "{e}"),
        }
    }
}

const MSP_FC_VARIANT: u8 = 2;
const MSP_UID: u8 = 160;

pub fn read_backup(link: &mut dyn Link, cli_command: &str, variants: &[String], cli_timeout: Duration) -> Result<FcBackup, ReadError> {
    let mut r = FcBackup::default();

    let mut variant = None;
    for _ in 0..3 {
        variant = msp(link, MSP_FC_VARIANT, Duration::from_millis(700))?;
        if variant.is_some() {
            break;
        }
    }

    let already_in_cli;
    match variant {
        None => {
            // No MSP reply. Either it's not an FC, or something left it in CLI mode.
            link.write(b"\n")?;
            let (_, ok) = read_until_prompt(link, Duration::from_millis(1000), 150)?;
            if !ok {
                return Err(ReadError::NotFlightController("no reply to MSP".into()));
            }
            already_in_cli = true;
        }
        Some(v) => {
            let name = String::from_utf8_lossy(&v[..v.len().min(4)]).to_string();
            if !variants.iter().any(|x| x.eq_ignore_ascii_case(&name)) {
                return Err(ReadError::NotFlightController(format!("firmware is {name}, not one of FirmwareVariants in settings.json")));
            }
            r.variant = Some(name);
            if let Some(uid) = msp(link, MSP_UID, Duration::from_millis(700))? {
                if uid.len() >= 12 {
                    let w = |i: usize| u32::from_le_bytes([uid[i], uid[i + 1], uid[i + 2], uid[i + 3]]);
                    r.fc_id = Some(format!("{:08X}{:08X}{:08X}", w(0), w(4), w(8)));
                }
            }
            already_in_cli = false;
        }
    }

    // From here on, always try to leave the CLI so the FC isn't left stuck in it.
    let result = cli_session(link, cli_command, cli_timeout, already_in_cli, &mut r);
    // "exit noreboot" hands the port straight back to MSP. Firmware that doesn't know "noreboot" just reboots,
    // which is harmless; the caller needs to know which happened.
    r.rebooted = match link.write(b"exit noreboot\n").and_then(|_| read_for(link, Duration::from_millis(250))) {
        Ok(tail) => String::from_utf8_lossy(&tail).to_ascii_lowercase().contains("rebooting"),
        Err(_) => true, // the port vanished mid-read: it rebooted
    };
    result?;
    Ok(r)
}

fn cli_session(link: &mut dyn Link, cli_command: &str, timeout: Duration, already_in_cli: bool, r: &mut FcBackup) -> Result<(), ReadError> {
    if !already_in_cli {
        link.write(b"#")?;
        let (_, ok) = read_until_prompt(link, Duration::from_millis(3000), 150)?;
        if !ok {
            return Err(io::Error::new(io::ErrorKind::Other, "Flight controller didn't enter CLI mode").into());
        }
    }
    link.write(b"\n")?; // flush anything half-typed on the CLI line
    read_until_prompt(link, Duration::from_millis(1000), 100)?;

    link.write(format!("{cli_command}\n").as_bytes())?;
    let (raw, ok) = read_until_prompt(link, timeout, 500)?;
    r.complete = ok;
    r.cli = clean(&String::from_utf8_lossy(&raw), cli_command);
    parse(r);
    if r.cli.trim().is_empty() || !r.cli.contains('\n') {
        return Err(io::Error::new(io::ErrorKind::Other, "Flight controller returned an empty backup").into());
    }
    Ok(())
}

/// MSP v1: $M< size cmd payload checksum  ->  $M> size cmd payload checksum ($M! = error)
fn msp(link: &mut dyn Link, cmd: u8, timeout: Duration) -> io::Result<Option<Vec<u8>>> {
    link.write(&[b'$', b'M', b'<', 0, cmd, cmd])?;
    let mut buf: Vec<u8> = Vec::new();
    let mut chunk = [0u8; 512];
    let start = Instant::now();
    while start.elapsed() < timeout {
        let n = link.read(&mut chunk, Duration::from_millis(50))?;
        buf.extend_from_slice(&chunk[..n]);
        let mut i = 0;
        while i + 6 <= buf.len() {
            if buf[i] == b'$' && buf[i + 1] == b'M' && (buf[i + 2] == b'>' || buf[i + 2] == b'!') {
                let size = buf[i + 3] as usize;
                if i + 6 + size > buf.len() {
                    break; // frame not fully received yet
                }
                let mut check = (size as u8) ^ buf[i + 4];
                for b in &buf[i + 5..i + 5 + size] {
                    check ^= b;
                }
                if check == buf[i + 5 + size] && buf[i + 4] == cmd {
                    return Ok(if buf[i + 2] == b'!' { None } else { Some(buf[i + 5..i + 5 + size].to_vec()) });
                }
            }
            i += 1;
        }
    }
    Ok(None)
}

/// Reads until the output ends with the CLI prompt "\n# " and nothing more arrives for `quiet_ms`.
/// (diff output is full of lines starting with "# ", so the quiet period is what tells the prompt apart.)
/// A diff/dump ends with "batch end" then the prompt; once that's in, there's nothing left to wait for.
fn read_until_prompt(link: &mut dyn Link, timeout: Duration, quiet_ms: u64) -> io::Result<(Vec<u8>, bool)> {
    let mut buf = Vec::new();
    let mut chunk = [0u8; 4096];
    let start = Instant::now();
    let mut last_data = Instant::now();
    while start.elapsed() < timeout {
        let n = link.read(&mut chunk, Duration::from_millis(20))?;
        if n > 0 {
            buf.extend_from_slice(&chunk[..n]);
            last_data = Instant::now();
            continue;
        }
        if ends_with_prompt(&buf) && (last_data.elapsed() >= Duration::from_millis(quiet_ms) || ends_with_batch_end(&buf)) {
            return Ok((buf, true));
        }
    }
    Ok((buf, false))
}

fn ends_with_prompt(b: &[u8]) -> bool {
    let n = b.len();
    n >= 3 && b[n - 1] == b' ' && b[n - 2] == b'#' && (b[n - 3] == b'\n' || b[n - 3] == b'\r')
}

fn ends_with_batch_end(b: &[u8]) -> bool {
    static RE: OnceLock<Regex> = OnceLock::new();
    let tail = String::from_utf8_lossy(&b[b.len().saturating_sub(32)..]).to_string();
    RE.get_or_init(|| Regex::new(r"\nbatch end\s*\n# $").unwrap()).is_match(&tail)
}

fn read_for(link: &mut dyn Link, time: Duration) -> io::Result<Vec<u8>> {
    let mut buf = Vec::new();
    let mut chunk = [0u8; 1024];
    let start = Instant::now();
    while start.elapsed() < time {
        let n = link.read(&mut chunk, Duration::from_millis(50))?;
        buf.extend_from_slice(&chunk[..n]);
    }
    Ok(buf)
}

fn clean(text: &str, cli_command: &str) -> String {
    let mut text = text.replace("\r\n", "\n").replace('\r', "\n");
    if let Some(echo) = text.find(cli_command) {
        text = match text[echo..].find('\n') {
            Some(eol) => text[echo + eol + 1..].to_string(),
            None => String::new(),
        };
    }
    let mut text = text.trim_end().to_string();
    if text.ends_with("\n#") {
        text.pop();
        text = text.trim_end().to_string();
    }
    let text = format!("# {cli_command}\n\n{}\n", text.trim_matches('\n'));
    text.replace('\n', "\r\n")
}

fn parse(r: &mut FcBackup) {
    static VERSION: OnceLock<Regex> = OnceLock::new();
    static BOARD: OnceLock<Regex> = OnceLock::new();
    static CRAFT: OnceLock<Regex> = OnceLock::new();
    static OLD_NAME: OnceLock<Regex> = OnceLock::new();
    let version = VERSION.get_or_init(|| Regex::new(r"(?m)^# (\w+) / (\S+) \(\w+\) (\S+)").unwrap());
    if let Some(m) = version.captures(&r.cli) {
        r.firmware = Some(format!("{} {}", &m[1], &m[3]));
        r.board = Some(m[2].to_string());
    }
    if let Some(m) = BOARD.get_or_init(|| Regex::new(r"(?m)^board_name\s+([^\r\n]+)").unwrap()).captures(&r.cli) {
        r.board = Some(m[1].trim().to_string());
    }
    let craft = CRAFT.get_or_init(|| Regex::new(r"(?m)^set craft_name = ([^\r\n]*)").unwrap()).captures(&r.cli)
        .or_else(|| OLD_NAME.get_or_init(|| Regex::new(r"(?m)^name ([^\r\n]*)").unwrap()).captures(&r.cli));
    if let Some(m) = craft {
        let name = m[1].trim().to_string();
        r.craft_name = Some(if name == "-" { String::new() } else { name });
    }
    if r.variant.is_none() {
        r.variant = r.firmware.as_ref().and_then(|f| f.split(' ').next().map(str::to_string));
    }
}

// ------------------------------------------------------------------------------------------------ simulated FC

/// A pretend Betaflight FC for tests: answers MSP, enters the CLI on '#', echoes input and streams "diff all"
/// in chunks with pauses.
#[cfg(test)]
pub mod fake {
    use super::*;
    use std::collections::VecDeque;

    pub const DIFF: &str = "
# version
# Betaflight / STM32F405 (S405) 4.5.1 Sep 12 2024 / 08:42:16 (77d01ba3b) MSP API: 1.46
# config rev: 3e4b2a1

# start the command batch
batch start

# reset configuration to default settings
defaults nosave

board_name SPEEDYBEEF405V4
manufacturer_id SPBE

# name: Chimera7

# feature
feature -AIRMODE

# master
set gyro_lpf1_static_hz = 0
set craft_name = Chimera7
set pilot_name = Render

profile 0

# profile 0
set p_pitch = 52
set i_pitch = 88
set d_pitch = 46
set f_pitch = 140
set p_roll = 48
set i_roll = 82
set d_roll = 40

rateprofile 0

# rateprofile 0
set roll_rc_rate = 12

# end the command batch
batch end
";

    pub const UID: [u8; 12] = [0x10, 0x00, 0x32, 0x00, 0x0A, 0x51, 0x37, 0x30, 0x33, 0x34, 0x38, 0x38];
    pub const FC_ID: &str = "003200103037510A38383433";

    #[derive(Default)]
    pub struct FakeFc {
        pub diff: String,
        pub variant: String,
        pub silent: bool,
        pub start_in_cli: bool,
        pub never_prompt: bool,
        pub old_firmware: bool,
        pub exited: bool,
        cli: bool,
        msp: Vec<u8>,
        line: String,
        out: VecDeque<(u64, Vec<u8>)>, // (delay before, bytes)
    }

    impl FakeFc {
        pub fn new(diff: &str) -> FakeFc {
            FakeFc { diff: diff.into(), variant: "BTFL".into(), ..Default::default() }
        }

        fn out(&mut self, s: &str, delay: u64) {
            self.out.push_back((delay, s.as_bytes().to_vec()));
        }

        pub fn rebooted_on_exit(&self) -> bool { self.exited && self.old_firmware }
    }

    impl Link for FakeFc {
        fn write(&mut self, data: &[u8]) -> io::Result<()> {
            if self.silent {
                return Ok(());
            }
            if self.start_in_cli {
                self.cli = true;
                self.start_in_cli = false;
            }
            for &b in data {
                if !self.cli {
                    if b == b'#' && self.msp.is_empty() {
                        self.cli = true;
                        self.out("\r\nEntering CLI Mode, type 'exit' to return, or 'help'\r\n\r\n# ", 0);
                        continue;
                    }
                    self.msp.push(b);
                    if self.msp.len() >= 6 && self.msp[0] == b'$' {
                        let cmd = self.msp[4];
                        self.msp.clear();
                        let payload: Vec<u8> = match cmd {
                            2 => self.variant.as_bytes().to_vec(),
                            160 => UID.to_vec(),
                            _ => vec![],
                        };
                        let mut f = vec![b'$', b'M', b'>', payload.len() as u8, cmd];
                        f.extend_from_slice(&payload);
                        let mut ck = payload.len() as u8 ^ cmd;
                        for p in &payload {
                            ck ^= p;
                        }
                        f.push(ck);
                        // split the frame to exercise partial-frame handling
                        self.out.push_back((0, f[..3].to_vec()));
                        self.out.push_back((30, f[3..].to_vec()));
                    }
                    continue;
                }
                if b == b'\n' || b == b'\r' {
                    let cmd = std::mem::take(&mut self.line);
                    if cmd.is_empty() {
                        continue;
                    }
                    self.out("\r\n", 0);
                    if cmd == "diff all" {
                        let text = self.diff.replace('\n', "\r\n");
                        let cut = text.find("# master").unwrap();
                        let text_head = text[..cut + 2].to_string();
                        self.out(&text_head, 0);
                        let pause = text[cut + 2..cut + 202].to_string();
                        self.out(&pause, 250); // pause right after a "# " line start: must not stop early
                        let mut i = cut + 202;
                        while i < text.len() {
                            let end = (i + 64).min(text.len());
                            let piece = text[i..end].to_string();
                            self.out(&piece, 5);
                            i = end;
                        }
                        if !self.never_prompt {
                            self.out("\r\n# ", 0);
                        }
                    } else if cmd.starts_with("exit") {
                        self.exited = true;
                        self.cli = false;
                        // newer firmware honours "noreboot"; older firmware ignores it and reboots
                        if !(cmd == "exit noreboot" && !self.old_firmware) {
                            self.out("\r\nLeaving CLI mode, unsaved changes lost.\r\n# Rebooting", 0);
                        }
                    } else if cmd.starts_with('#') {
                        self.out("# ", 0);
                    } else {
                        self.out("Invalid name\r\n\r\n# ", 0);
                    }
                } else {
                    self.line.push(b as char);
                    let echo = (b as char).to_string();
                    self.out(&echo, 0);
                }
            }
            Ok(())
        }

        fn read(&mut self, buf: &mut [u8], timeout: Duration) -> io::Result<usize> {
            match self.out.front_mut() {
                None => {
                    std::thread::sleep(timeout);
                    Ok(0)
                }
                Some((delay, _)) if *delay > 0 => {
                    let wait = (*delay).min(timeout.as_millis() as u64);
                    std::thread::sleep(Duration::from_millis(wait));
                    *delay -= wait;
                    Ok(0)
                }
                Some(_) => {
                    let (_, bytes) = self.out.pop_front().unwrap();
                    buf[..bytes.len()].copy_from_slice(&bytes);
                    Ok(bytes.len())
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::fake::*;
    use super::*;

    fn variants() -> Vec<String> { vec!["BTFL".into()] }

    #[test]
    fn normal_backup() {
        let mut fc = FakeFc::new(DIFF);
        let b = read_backup(&mut fc, "diff all", &variants(), Duration::from_secs(3)).unwrap();
        assert_eq!(b.fc_id.as_deref(), Some(FC_ID));
        assert_eq!(b.variant.as_deref(), Some("BTFL"));
        assert_eq!(b.firmware.as_deref(), Some("Betaflight 4.5.1"));
        assert_eq!(b.board.as_deref(), Some("SPEEDYBEEF405V4"));
        assert_eq!(b.craft_name.as_deref(), Some("Chimera7"));
        assert!(b.complete);
        assert!(b.cli.starts_with("# diff all\r\n\r\n# version\r\n"));
        assert!(b.cli.trim_end().ends_with("batch end"), "prompt stripped, not cut early at '# master'");
        assert!(b.cli.contains("set d_roll = 40"));
        assert!(!b.cli.replace("\r\n", "").contains('\n'));
        assert!(fc.exited && !b.rebooted, "'exit noreboot' leaves new firmware running");
    }

    #[test]
    fn old_firmware_reboots() {
        let mut fc = FakeFc::new(DIFF);
        fc.old_firmware = true;
        let b = read_backup(&mut fc, "diff all", &variants(), Duration::from_secs(3)).unwrap();
        assert!(b.complete && fc.exited && b.rebooted);
    }

    #[test]
    fn stuck_in_cli() {
        let mut fc = FakeFc::new(DIFF);
        fc.start_in_cli = true;
        let b = read_backup(&mut fc, "diff all", &variants(), Duration::from_secs(3)).unwrap();
        assert!(b.fc_id.is_none() && b.complete && b.variant.as_deref() == Some("Betaflight") && fc.exited);
    }

    #[test]
    fn not_a_flight_controller() {
        let mut fc = FakeFc::new(DIFF);
        fc.silent = true;
        assert!(matches!(read_backup(&mut fc, "diff all", &variants(), Duration::from_secs(3)), Err(ReadError::NotFlightController(_))));
        let mut inav = FakeFc::new(DIFF);
        inav.variant = "INAV".into();
        assert!(matches!(read_backup(&mut inav, "diff all", &variants(), Duration::from_secs(3)), Err(ReadError::NotFlightController(_))));
    }

    #[test]
    fn hung_fc_gives_incomplete_backup() {
        let mut fc = FakeFc::new(DIFF);
        fc.never_prompt = true;
        let b = read_backup(&mut fc, "diff all", &variants(), Duration::from_secs(3)).unwrap();
        assert!(!b.complete && b.cli.contains("set p_pitch") && fc.exited);
    }

    #[test]
    fn batch_end_finishes_early() {
        let start = Instant::now();
        read_backup(&mut FakeFc::new(DIFF), "diff all", &variants(), Duration::from_secs(3)).unwrap();
        let with = start.elapsed();
        let start = Instant::now();
        read_backup(&mut FakeFc::new(&DIFF.replace("batch end", "batch fin")), "diff all", &variants(), Duration::from_secs(3)).unwrap();
        let without = start.elapsed();
        assert!(without > with + Duration::from_millis(300), "{with:?} vs {without:?}");
    }
}
