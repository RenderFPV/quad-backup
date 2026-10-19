Quad Backup for Linux - EXPERIMENTAL (built and simulated, not yet tested on a real Linux machine)

Run it:
    ./QuadBackup

First time only - let your user talk to USB serial devices (then log out and back in):
    Debian / Ubuntu / Mint:  sudo usermod -aG dialout $USER
    Arch / Manjaro:          sudo usermod -aG uucp $USER
    Fedora:                  sudo usermod -aG dialout $USER

The tray icon needs a desktop with tray support. On GNOME, install the "AppIndicator and KStatusNotifierItem
Support" extension. Without a tray, run ./QuadBackup again to open the window.

It starts at login automatically (turn that off in Settings). Settings and the drone list live in
~/.config/quad-backup/, backups go to ~/Documents/Quad Backups/ by default.

Found a problem? Open an issue with what happened and the Flight log (Settings > Flight log > Open file).
