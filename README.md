# Quad Backup

Backs up a Betaflight flight controller every time you plug it in over USB, then opens Betaflight.

> **Built with AI.** I came up with the idea and tested it on my own quads, but the code was written by Claude, Anthropic's AI, using Claude Code. I'm a pilot, not a programmer, so if something's broken, an issue describing what happened will get further than asking me how the code works.

## Download

Grab the latest build from the **[Releases page](../../releases)**.

| | File | Status |
|---|---|---|
| **Windows** | `QuadBackup-Windows.zip` | Tested on my own quads |
| **Mac** (M1/M2/M3/M4) | `QuadBackup-macOS-AppleSilicon.zip` | ⚠️ **Experimental, untested** |
| **Mac** (Intel) | `QuadBackup-macOS-Intel.zip` | ⚠️ **Experimental, untested** |
| **Linux** (x64) | `QuadBackup-linux-x64.tar.gz` | ⚠️ **Experimental, untested** |

The Mac and Linux versions share all the backup logic with the Windows one, and they pass the same simulated plug-in, disconnect and special-backup tests. But they haven't been run with a real quad yet. If you try one, an issue saying whether it worked is gold.

### Windows

Unzip it anywhere you like and run `QuadBackup.exe`. It starts with Windows from then on, and its settings and drone list live next to the exe.

### Mac (experimental)

1. Unzip it and drag **Quad Backup.app** into **Applications**.
2. The app isn't signed with an Apple developer account, so macOS won't open it as-is. Open **Terminal** and run these two lines once:
   ```bash
   xattr -cr "/Applications/Quad Backup.app"
   ```
   ```bash
   codesign --force --deep --sign - "/Applications/Quad Backup.app"
   ```
3. Open **Quad Backup**. It lives in the **menu bar** (top right), not the Dock. Click the little quad for the menu.

Settings and the drone list are in `~/Library/Application Support/Quad Backup/`. Backups go to `~/Documents/Quad Backups/` by default. It starts at login, which you can turn off in Settings.

### Linux (experimental)

```bash
tar xzf QuadBackup-linux-x64.tar.gz
```
```bash
./quad-backup/QuadBackup
```

Your user needs permission to use USB serial devices: `sudo usermod -aG dialout $USER` (Arch: `uucp`), then log out and back in. The tray icon needs a desktop with tray support; on GNOME, install the AppIndicator extension. Without a tray, run the app again to open its window. See `README.txt` in the download for more.

## What happens when you plug in a drone

1. Quad Backup checks that the device is a Betaflight FC and reads its chip ID, which identifies the drone.
2. It runs `diff all` in the CLI, then leaves the CLI with `exit noreboot`, so the FC doesn't reboot.
3. **First time you plug in that drone:** a window asks you to name a folder for it. It suggests the craft name.
   **After that:** the backup is saved to that folder without asking, as `Name_YYYY-MM-DD_HH-MM-SS.txt`.
4. Betaflight opens, unless it's already open.

Other things it does:
- **Backup when you disconnect in Betaflight:** hit **Disconnect** with the quad still plugged in, and that session's tune is backed up a couple of seconds later. This copy is only kept if something changed. When Betaflight reboots the quad itself (Save, leaving the CLI tab, Blackbox, flashing), the app waits for Betaflight to reconnect instead of grabbing the quad. Closing Betaflight counts as a disconnect too.
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
  - **Forget** (its backups are kept)

  **Back up now** backs up whatever is plugged in.
- **Settings:** changes save as soon as you make them.
  - **Backups folder:** where new drone folders are created. Point it at a OneDrive, iCloud or Google Drive folder to keep backups off this computer.
  - **Backup command:** `diff all` (default) or `dump all`.
  - **Firmware to back up:** Betaflight, INAV and/or Emuflight.
  - **Back up when you disconnect in Betaflight** and **Back up when Betaflight closes**
  - **Open Betaflight after a backup**, and which Betaflight app to open.
  - **Pop-up notifications** (with a test button)
  - **Start with Windows / at login:** it starts quietly in the tray or menu bar.
  - **Advanced:** USB chip vendors, the reboot grace time and the disconnect wait.
- **Flight log:** what the app has been doing, color-coded, with filters for **Backups** and **Problems**.

## Building from source

The code is in three parts:
- `src/Core/`: shared by every platform. It covers talking to the flight controller (`FcReader.cs`), deciding when to back up (`Engine.cs`), and settings, the drone list and backup files (`Common.cs`).
- `src/Windows/`: the Windows app (tray, windows, Win32 serial). Build it with `build.cmd`, which uses the C# compiler that comes with Windows, so there's nothing to install. Exit Quad Backup from the tray first.
- `desktop/`: the Mac/Linux app, built with [Avalonia](https://avaloniaui.net/). It needs the [.NET 8 SDK](https://dotnet.microsoft.com/download). `python desktop/package.py` builds all the Mac and Linux downloads into `desktop/dist/`.
