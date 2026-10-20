//! The tray / menu bar icon and its menu. Windows and Mac use tray-icon; Linux uses ksni (StatusNotifierItem
//! over D-Bus, no GTK), which shows up on KDE, and on GNOME with the AppIndicator extension.

use super::app::UiMsg;
use super::art;
use eframe::egui;
use std::sync::mpsc::Sender;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TrayAction {
    Open,
    Backup,
    Special,
    Folder,
    Launch,
    Hangar,
    Settings,
    Log,
    Exit,
}

/// The menu, top to bottom; None is a separator.
fn items() -> Vec<Option<(TrayAction, &'static str)>> {
    vec![
        Some((TrayAction::Open, "Open Quad Backup")),
        None,
        Some((TrayAction::Backup, "Back up the plugged-in quad now")),
        Some((TrayAction::Special, "\u{2605} Special backup\u{2026}")),
        Some((TrayAction::Folder, "Open backups folder")),
        Some((TrayAction::Launch, "Launch Betaflight")),
        None,
        Some((TrayAction::Hangar, "Hangar (drone list)")),
        Some((TrayAction::Settings, "Settings")),
        Some((TrayAction::Log, "Flight log")),
        None,
        Some((TrayAction::Exit, if cfg!(target_os = "macos") { "Quit (your quads will miss me)" } else { "Exit (your quads will miss me)" })),
    ]
}

fn send(tx: &Sender<UiMsg>, ctx: &egui::Context, action: TrayAction) {
    let _ = tx.send(UiMsg::Tray(action));
    ctx.request_repaint();
}

/// Keeps the tray alive for as long as it's held.
pub struct Tray(#[allow(dead_code)] Box<dyn std::any::Any>);

#[cfg(not(target_os = "linux"))]
pub fn build(tx: Sender<UiMsg>, ctx: egui::Context) -> Option<Tray> {
    use tray_icon::menu::{Menu, MenuEvent, MenuItem, PredefinedMenuItem};
    use tray_icon::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};

    let menu = Menu::new();
    let _ = menu.append(&MenuItem::new("Quad Backup \u{00B7} on patrol", false, None));
    let _ = menu.append(&PredefinedMenuItem::separator());
    let mut ids = Vec::new();
    for item in items() {
        match item {
            None => { let _ = menu.append(&PredefinedMenuItem::separator()); }
            Some((action, label)) => {
                let m = MenuItem::new(label, true, None);
                ids.push((m.id().clone(), action));
                let _ = menu.append(&m);
            }
        }
    }
    {
        let (tx, ctx) = (tx.clone(), ctx.clone());
        MenuEvent::set_event_handler(Some(move |e: MenuEvent| {
            if let Some((_, action)) = ids.iter().find(|(id, _)| *id == e.id) {
                send(&tx, &ctx, *action);
            }
        }));
    }
    {
        let (tx, ctx) = (tx.clone(), ctx.clone());
        TrayIconEvent::set_event_handler(Some(move |e: TrayIconEvent| {
            if let TrayIconEvent::Click { button: MouseButton::Left, button_state: MouseButtonState::Up, .. } = e {
                send(&tx, &ctx, TrayAction::Open);
            }
        }));
    }
    let mut builder = TrayIconBuilder::new()
        .with_menu(Box::new(menu))
        .with_tooltip("Quad Backup - click to open")
        // Windows: left click opens the window, right click the menu. Mac: clicking shows the menu, as usual there.
        .with_menu_on_left_click(cfg!(target_os = "macos"));
    if let Ok(icon) = tray_icon::Icon::from_rgba(art::classic_icon_rgba(32), 32, 32) {
        builder = builder.with_icon(icon);
    }
    match builder.build() {
        Ok(t) => Some(Tray(Box::new(t))),
        Err(e) => {
            crate::core::store::log(format!("Couldn't create the tray icon: {e}"));
            None
        }
    }
}

#[cfg(target_os = "linux")]
pub fn build(tx: Sender<UiMsg>, ctx: egui::Context) -> Option<Tray> {
    use ksni::blocking::TrayMethods;

    struct LinuxTray {
        tx: Sender<UiMsg>,
        ctx: egui::Context,
    }

    impl ksni::Tray for LinuxTray {
        fn id(&self) -> String { "quad-backup".into() }
        fn title(&self) -> String { "Quad Backup".into() }
        fn activate(&mut self, _x: i32, _y: i32) { send(&self.tx, &self.ctx, TrayAction::Open); }
        fn icon_pixmap(&self) -> Vec<ksni::Icon> {
            // RGBA -> ARGB, as the spec wants
            let rgba = art::classic_icon_rgba(32);
            let data = rgba.chunks(4).flat_map(|p| [p[3], p[0], p[1], p[2]]).collect();
            vec![ksni::Icon { width: 32, height: 32, data }]
        }
        fn menu(&self) -> Vec<ksni::MenuItem<Self>> {
            items()
                .into_iter()
                .map(|item| match item {
                    None => ksni::MenuItem::Separator,
                    Some((action, label)) => ksni::menu::StandardItem {
                        label: label.into(),
                        activate: Box::new(move |t: &mut Self| send(&t.tx, &t.ctx, action)),
                        ..Default::default()
                    }
                    .into(),
                })
                .collect()
        }
    }

    match (LinuxTray { tx, ctx }).spawn() {
        Ok(handle) => Some(Tray(Box::new(handle))),
        Err(e) => {
            crate::core::store::log(format!("Couldn't create the tray icon (no tray on this desktop?): {e}"));
            None
        }
    }
}
