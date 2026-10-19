# Quad Backup

Backs up a Betaflight flight controller every time you plug it in over USB, then opens Betaflight.

## What happens when you plug in a drone

1. Quad Backup checks that the device is a Betaflight FC and reads its chip ID, which identifies the drone.
2. It runs `diff all` in the CLI and sends `exit`. The FC reboots, the same as when you leave the CLI tab.
3. **First time you plug in that drone:** a window asks you to name a folder for it. It suggests the craft name.
   **After that:** the backup is saved to that folder without asking, as `Name_YYYY-MM-DD_HH-MM-SS.txt`.
4. Betaflight opens, unless it's already open.

Other things it does:
- **Backup when you disconnect in Betaflight:** hit **Disconnect** with the quad still plugged in, and that session's tune is backed up. This copy is only kept if something changed. Betaflight also lets go of the quad on its own for a moment (reboot after Save, CLI, Blackbox, flashing), so the quad has to stay disconnected for **Disconnect wait** (8 seconds by default) before it counts. Closing Betaflight counts as a disconnect too, and there's a separate close-only option if you turn disconnect backups off.
- **No extra reboot:** the app leaves the CLI with `exit noreboot`, so the FC isn't rebooted after a backup. Firmware that doesn't support `noreboot` reboots instead, which is harmless.
- **Reboots aren't plug-ins:** if the FC reboots after you hit Save, it isn't backed up again.
- **New FC, same quad:** if you replace an FC and keep the craft name, the window offers that drone's existing folder.

## Restoring a backup

Flash the **same Betaflight version** as the backup. The version is shown in the second line of the file. Then go to the CLI tab, paste the whole file, and type `save`.

## Opening the app

**Click the little orange quad in the system tray** to open the main window. Launching `QuadBackup.exe` again also opens it. Closing the window just tucks it back into the tray, and backups keep running. To actually quit, right-click the tray icon and choose **Exit**.

The window has three pages:
- **Hangar:** your drones. Each card shows its board, firmware, last backup and how many backups it has. From a card you can:
  - **Open folder**
  - **Rename** (this renames its backup folder too)
  - **Change folder**
  - **Pause backups** / **Resume backups** (also how you undo "Never for this drone")
  - **Forget** (its backups are kept)

  **Back up now** backs up whatever is plugged in.
- **Settings:** changes save as soon as you make them.
  - **Backups folder:** where new drone folders are created. Point it at a OneDrive or Google Drive folder to keep backups off this PC.
  - **Backup command:** `diff all` (default) or `dump all`.
  - **Firmware to back up:** Betaflight, INAV and/or Emuflight.
  - **Back up again when Betaflight closes**
  - **Open Betaflight after a backup**, and which Betaflight app to open.
  - **Pop-up notifications** (with a test button)
  - **Start with Windows:** at login it starts quietly in the tray.
  - **Advanced:** USB chip vendors and the reboot grace time.
- **Flight log:** what the app has been doing, color-coded, with filters for **Backups** and **Problems**.

Right-clicking the tray icon gives shortcuts to the same things, plus **Launch Betaflight**.

## Rebuilding

The source is in `src/`:
- `QuadBackup.cs`: detection and backups
- `Ui.cs`: the mascot, the new-drone window and the pop-ups
- `MainWindow.cs`: the main window

Exit Quad Backup from the tray first, then run `build.cmd`. It uses the C# compiler that comes with Windows, so there's nothing to install.
