#![cfg_attr(not(test), windows_subsystem = "windows")]
//! Quad Backup - backs up Betaflight flight controllers when they're plugged in, then opens
//! Betaflight. Lives in the system tray.

mod core;
mod ui;
mod platform;

use std::sync::Arc;

fn main() -> eframe::Result<()> {
    // the C# version and this one would fight over the same quad
    // QB_DEMO only shows screens with sample data; it never starts the backup engine or touches a port
    let demo = std::env::var_os("QB_DEMO").is_some();
    let at_login = std::env::args().any(|a| a == "--tray");
    if !demo && platform::other_version_running() {
        if at_login {
            core::store::log("The C# Quad Backup is running, so this copy stepped aside");
        } else {
            platform::message_box("Quad Backup", "The original (C#) Quad Backup is already running.\n\nExit it from its tray icon first, so the two don't fight over your quad.");
        }
        return Ok(());
    }
    let instance = match platform::claim_instance() {
        platform::Claim::First(i) => Arc::new(i),
        platform::Claim::AlreadyRunning => return Ok(()), // the running copy opens its window instead
    };
    std::panic::set_hook(Box::new(|info| core::store::log(format!("Crashed: {info}"))));
    // starting at login passes --tray so the window doesn't pop up every time
    let show_window = !std::env::args().any(|a| a == "--tray");

    // The root window is an invisible 1x1 speck that keeps the app (and its pop-ups) alive; the real windows are
    // extra viewports it opens.
    let options = eframe::NativeOptions {
        viewport: eframe::egui::ViewportBuilder::default()
            .with_title("Quad Backup")
            .with_inner_size([1.0, 1.0])
            .with_position([0.0, 0.0])
            .with_decorations(false)
            .with_transparent(true)
            .with_taskbar(false)
            .with_mouse_passthrough(true)
            .with_resizable(false)
            .with_icon(std::sync::Arc::new(eframe::egui::IconData { rgba: ui::art::classic_icon_rgba(64), width: 64, height: 64 })),
        ..Default::default()
    };
    eframe::run_native("Quad Backup", options, Box::new(move |cc| Ok(Box::new(ui::app::App::new(cc, instance, show_window)))))
}
