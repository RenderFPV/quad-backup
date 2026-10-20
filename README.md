# Quad Backup

Backs up a Betaflight flight controller every time you plug it in over USB, then opens Betaflight.

> **Built with AI.** I came up with the idea and tested it on my own quads, but the code was written by Claude, Anthropic's AI, using Claude Code. I'm a pilot, not a programmer, so if something's broken, an issue describing what happened will get further than asking me how the code works.

This is the **Rust version**: one small exe with nothing to install. Releases v1.0.0 and v1.1.0 were the original C# version, and their code is still in this repo's history under those tags.

| | Status |
|---|---|
| **Windows** | Tested on my own quads |
| **Mac** (Apple Silicon and Intel) | ⚠️ **Experimental, untested.** Build it with one script (below) |
| **Linux** (x64) | ⚠️ **Experimental, untested** |

The Mac and Linux versions share all the backup logic with the Windows one and pass the same simulated plug-in, disconnect and special-backup tests. But nobody has plugged a real quad into them yet. If you try one, an issue saying whether it worked is gold.

## Getting it

### Windows

Grab `QuadBackup.exe` from the **[Releases page](../../releases)** (or build it, below), put it in a folder of its own and run it. It starts with Windows from then on, and its settings, drone list and backups live next to the exe.

### Mac (experimental)

A Mac app has to be built on a Mac. Download this repo (**Code → Download ZIP**), unzip it, open **Terminal** in that folder and run:

```bash
bash mac/build-mac.sh
```

It installs Apple's command line tools and Rust if they're missing, then makes `dist/Quad Backup Rust.app`, which runs on both Apple Silicon and Intel. Drag it into **Applications** and open it. It lives in the **menu bar** (top right), not the Dock.

Settings and the drone list are in `~/Library/Application Support/Quad Backup Rust/`. Backups go to `~/Documents/Quad Backups/` by default.

### Linux (experimental)

Build it with `bash linux/build-linux.sh` (it installs Rust if needed and packs `dist/QuadBackup-rust-linux-x64.tar.gz`), then:

```bash
tar xzf QuadBackup-rust-linux-x64.tar.gz
```
```bash
./QuadBackup-rust-linux-x64/QuadBackup
```

Your user needs permission to use USB serial devices: `sudo usermod -aG dialout $USER` (Arch: `uucp`), then log out and back in. The tray icon needs a desktop with tray support; on GNOME, install the AppIndicator extension. Without a tray, run the app again to open its window. Settings live in `~/.config/quad-backup-rust/`. See `linux/README.txt` for more.

## What happens when you plug in a drone

1. Quad Backup checks that the device is a Betaflight FC and reads its chip ID, which identifies the drone.
2. It runs `diff all` in the CLI, then leaves the CLI with `exit noreboot`, so the FC doesn't reboot.
3. **First time you plug in that drone:** a window asks you to name a folder for it. It suggests the craft name.
   **After that:** the backup is saved to that folder without asking, as `Name_YYYY-MM-DD_HH-MM-SS.txt`.
4. Betaflight opens, unless it's already open.

Other things it does:
- **Backup when you disconnect in Betaflight:** hit **Disconnect** with the quad still plugged in, and that session's tune is backed up a couple of seconds later. This copy is only kept if something changed. When Betaflight reboots the quad itself (Save, leaving the CLI tab, Blackbox, flashing), the app waits for Betaflight to reconnect instead of grabbing the quad. Closing Betaflight counts as a disconnect too.
- **No pile of duplicates:** if a quad's settings match its last backup, no new copy is saved. You get a "nothing new" pop-up instead. You can turn this off in Settings, and special backups are always kept.
- **Reboots aren't plug-ins:** if the FC reboots after you hit Save, it isn't backed up again.
- **New FC, same quad:** if you replace an FC and keep the craft name, the window offers that drone's existing folder.

## ★ Special backups

Nailed a tune you never want to lose? Hit **★ Special backup**. It's in the Hangar, on a plugged-in drone's card, and in the tray / menu bar menu. Give it a name like `locked in freestyle`.

- **If Betaflight isn't connected,** it's saved right away.
- **If you're mid-tune and connected,** it's ready and waiting. Hit **Save** in Betaflight, then **Disconnect**, and it's saved under your name. The drone's card shows that it's waiting. If you unplug instead, your next plug-in uses the name.
- **Special backups are always kept,** even if nothing changed. They're easy to spot in the folder, for example `ERA_2026-10-19_14-32-05_SPECIAL_locked in freestyle.txt`, and each drone's card shows how many it has.

## Restoring a backup

Flash the **same Betaflight version** as the backup. The version is shown in the second line of the file. Then go to the CLI tab, paste the whole file, and type `save`.

## The app window

Click the quad icon in the system tray (Windows, Linux) or open it from the menu bar (Mac). Launching the app again also opens the window. Closing the window just tucks it away, and backups keep running. To actually quit, use **Exit** / **Quit** in the icon's menu.

The window has three pages:
- **Hangar:** your drones. Each card shows its board, firmware, last backup and how many backups it has. From a card you can:
  - **★ Special backup** (when it's plugged in)
  - **Open folder**
  - **Rename** (this renames its backup folder too)
  - **Change folder**
  - **Pause backups** / **Resume backups** (also how you undo "Never for this drone")
  - **Forget** (its backups are kept, and you can restore it from Settings)

  **Back up now** backs up whatever is plugged in.
- **Settings:** changes save as soon as you make them.
  - **Backups folder:** where new drone folders are created. Point it at a OneDrive, iCloud or Google Drive folder to keep backups off this computer.
  - **Backup command:** `diff all` (default) or `dump all`.
  - **Firmware to back up:** Betaflight, INAV and/or Emuflight.
  - **Skip backups that haven't changed** (on by default)
  - **Back up when you disconnect in Betaflight** and **Back up when Betaflight closes**
  - **Turned-off drones:** drones you set to "never back up" or forgot. **Back up again** or **Restore** brings each one back. A restored drone keeps its old folder.
  - **Open Betaflight after a backup**, and which Betaflight app to open.
  - **Pop-up notifications** (with a test button)
  - **Start with Windows / at login:** it starts quietly in the tray or menu bar.
  - **Advanced:** USB chip vendors, the reboot grace time and the disconnect wait.
- **Flight log:** what the app has been doing, color-coded, with filters for **Backups** and **Problems**.

## Building from source

You need [Rust](https://rustup.rs). On Windows you also need the Visual Studio C++ build tools.

```bash
cargo build --release
```

The Windows exe is `target/release/QuadBackup.exe`, about 7 MB. Copy it into a folder of its own (for example `dist/`) and run it from there, since it keeps its data next to itself. The Mac and Linux builds have their own scripts, described above.

How the code is laid out:

| | |
|---|---|
| `src/core/store.rs` | Settings, the drone list and backup files. |
| `src/core/fc.rs` | Talking to the flight controller: the MSP check, CLI `diff all` and `exit noreboot`. Also a simulated FC for tests. |
| `src/core/engine.rs` | Decides when to back up: plug-ins, reboots, disconnects, special backups, skipping unchanged copies, forgotten and paused drones. |
| `src/platform/` | The operating-system pieces: serial ports and USB vendor IDs, spotting whether Betaflight has a port, launching Betaflight, start at login, single instance. `windows.rs` for Windows, `posix.rs` for Mac and Linux. |
| `src/ui/` | Everything on screen, drawn with [egui](https://github.com/emilk/egui): the mascot, the Hangar / Settings / Flight log window, the new-drone window, pop-ups, dialogs and the tray / menu bar icon. |
| `mac/`, `linux/` | Build scripts for those platforms. |

### Tests

```bash
cargo test --release
```

One of the tests replays whole sessions against a simulated flight controller: plug-ins, a quick replug, an old-firmware reboot, disconnects with and without changes, a reboot after Save, special backups while connected and while free, Back up now, Forget and restore, and Never then resume.

To look at a screen without a quad (it never starts the backup engine or touches a port), set `QB_DEMO` to one of `hangar`, `settings`, `log`, `drone`, `toasts` or `special`, and optionally `QB_DATA_DIR` to a folder with a copy of your data.
