#![cfg_attr(not(test), windows_subsystem = "windows")]
//! Quad Backup - backs up Betaflight flight controllers when they're plugged in, then opens
//! Betaflight. Lives in the system tray.

mod core;
mod ui;
mod platform;

use std::sync::Arc;
use std::time::{Duration, Instant};

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
    // after a crash the app relaunches itself with --restarted; that copy waits for the old one to finish exiting
    let restarted = std::env::args().any(|a| a == "--restarted");
    let Some(instance) = claim_instance(restarted) else {
        return Ok(()); // the running copy opens its window instead
    };
    let instance = Arc::new(instance);
    let started = Instant::now();
    std::panic::set_hook(Box::new(move |info| {
        // A crash anywhere (the window code, the backup engine) would otherwise leave no app, or an app that no longer
        // backs anything up. So: note it, start a fresh copy quietly in the tray, and get out of its way.
        core::store::log(format!("Crashed: {info}"));
        if started.elapsed() < Duration::from_secs(60) {
            core::store::log("That was less than a minute after starting, so I'm not restarting (to avoid a crash loop)");
        } else if !demo {
            match std::env::current_exe().and_then(|exe| std::process::Command::new(exe).args(["--tray", "--restarted"]).spawn()) {
                Ok(_) => core::store::log("Restarting after the crash"),
                Err(e) => core::store::log(format!("Couldn't restart after the crash: {e}")),
            }
        }
        std::process::exit(1);
    }));
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

/// Takes the single-instance lock. Normally it gives up at once (poking the running copy to open its window); after a
/// crash restart it waits up to 20 s for the old copy to exit instead.
fn claim_instance(restarted: bool) -> Option<platform::Instance> {
    let deadline = Instant::now() + Duration::from_secs(if restarted { 20 } else { 0 });
    loop {
        match platform::claim_instance(!restarted) {
            platform::Claim::First(i) => return Some(i),
            platform::Claim::AlreadyRunning if Instant::now() < deadline => std::thread::sleep(Duration::from_millis(250)),
            platform::Claim::AlreadyRunning => {
                if restarted {
                    core::store::log("The old copy didn't exit after its crash, so this restart gave up");
                }
                return None;
            }
        }
    }
}
