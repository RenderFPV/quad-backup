Quad Backup (Rust) for Linux - UNTESTED
=======================================

This build has never been run on a real Linux desktop. It should work, but expect rough edges.

Run it
------
    tar -xzf QuadBackup-rust-linux-x64.tar.gz
    cd QuadBackup-rust-linux-x64
    ./QuadBackup

It turns on "Start at login" the first time (an entry in ~/.config/autostart); switch that off in Settings if
you don't want it.

Serial port access
------------------
Flight controllers show up as /dev/ttyACM0 and so on. If backups fail with "permission denied", add yourself
to the dialout group (uucp on Arch), then log out and back in:

    sudo usermod -aG dialout $USER

Tray icon
---------
The icon uses the StatusNotifierItem tray, which KDE, XFCE, Cinnamon and most others show. On GNOME, install
the "AppIndicator and KStatusNotifierItem Support" extension, or just run ./QuadBackup again to open the window.

Where things live
-----------------
Settings, drone list and log:  ~/.config/quad-backup-rust/
Backups (default):             ~/Documents/Quad Backups/
