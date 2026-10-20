//! The main window: starry header with the mascot, and the Hangar / Settings / Flight log pages.

use super::app::{Dialog, DialogAction};
use super::art::{self, Pose, Sky};
use super::theme::*;
use super::widgets::{self, Pill};
use crate::core::engine::Engine;
use crate::core::store::{self, Drone, DroneDb, Settings};
use crate::platform;
use eframe::egui::{self, pos2, vec2, Align, Align2, Color32, Layout, Rect, Sense, Stroke, StrokeKind, Ui, UiBuilder};
use std::collections::HashMap;
use std::path::Path;
use std::time::Instant;

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Page {
    Hangar,
    Settings,
    Log,
}

/// Things the window needs the app to do (open a dialog, start a special backup...).
pub enum Request {
    Dialog(Dialog),
    Special(Option<String>),
    TestToast,
}

const DRONE_COLORS: [Color32; 6] = [SKY, MINT, PINK, AMBER, VIOLET, CORAL];

pub struct MainWindow {
    engine: Engine,
    page: Page,
    pub focus: bool,
    pub first_show: bool,
    sky: Sky,
    requests: Vec<Request>,
    db: DroneDb,
    db_at: Option<Instant>,
    counts: HashMap<String, (usize, usize)>,
    // settings page
    settings: Settings,
    vendors: String,
    grace: String,
    wait: String,
    launch: String,
    bad: [bool; 3],
    startup: bool,
    saved_at: f64,
    // log page
    log_filter: usize,
    log_sig: String,
    log_lines: Vec<(String, String, Color32)>,
}

impl MainWindow {
    pub fn new(engine: Engine) -> MainWindow {
        let mut w = MainWindow {
            engine, page: Page::Hangar, focus: false, first_show: true, sky: Sky::new(11, 34), requests: Vec::new(),
            db: DroneDb::default(), db_at: None, counts: HashMap::new(), settings: Settings::default(), vendors: String::new(),
            grace: String::new(), wait: String::new(), launch: String::new(), bad: [false; 3], startup: false, saved_at: -10.0,
            log_filter: 0, log_sig: String::new(), log_lines: Vec::new(),
        };
        w.load_settings();
        w
    }

    pub fn take_requests(&mut self) -> Vec<Request> { std::mem::take(&mut self.requests) }

    pub fn show_page(&mut self, page: Page) {
        self.page = page;
        if page == Page::Settings {
            self.load_settings();
        }
        self.refresh();
    }

    pub fn refresh(&mut self) {
        self.db_at = None;
        self.log_sig.clear();
    }

    fn load_settings(&mut self) {
        self.settings = Settings::load();
        self.vendors = self.settings.usb_vendor_ids.join(", ");
        self.grace = self.settings.reboot_grace_seconds.to_string();
        self.wait = self.settings.disconnect_wait_seconds.to_string();
        self.launch = self.settings.betaflight_launch_path.clone();
        self.bad = [false; 3];
        self.startup = platform::startup_enabled();
    }

    fn data(&mut self) {
        if self.db_at.map_or(true, |t| t.elapsed().as_secs_f32() > 1.5) {
            self.db = DroneDb::load();
            self.counts = self.db.drones.iter().filter_map(|d| d.folder.clone()).map(|f| { let c = store::count_backups(Some(&f)); (f, c) }).collect();
            self.db_at = Some(Instant::now());
        }
    }

    pub fn ui(&mut self, ctx: &egui::Context) {
        self.data();
        let now = ctx.input(|i| i.time);
        egui::CentralPanel::default().frame(egui::Frame::new().fill(BG)).show(ctx, |ui| {
            let full = ui.max_rect();
            self.header(ui, full, now as f32);
            self.tabs(ui, full, now);
            let content = Rect::from_min_max(pos2(full.left(), full.top() + 190.0), full.max);
            ui.scope_builder(UiBuilder::new().max_rect(content), |ui| match self.page {
                Page::Hangar => self.hangar(ui),
                Page::Settings => self.settings_page(ui, now),
                Page::Log => self.log_page(ui),
            });
        });
    }

    fn header(&mut self, ui: &mut Ui, full: Rect, t: f32) {
        let r = Rect::from_min_size(full.min, vec2(full.width(), 128.0));
        let p = ui.painter_at(r);
        self.sky.paint(&p, r, t);
        let bob = (t * 2.2).sin() * 3.0;
        art::glow(&p, pos2(r.left() + 72.0, r.top() + 116.0), 38.0 + bob, 5.0, alpha(INK, 140));
        art::quad(&p, pos2(r.left() + 72.0, r.top() + 62.0 + bob), 104.0, SKY,
            &Pose { prop_angle: t * 600.0, tilt: (t * 1.4).sin() * 5.0, blink: art::blink_at(t, 1.8, 3.9), face: true, dizzy: false });
        p.text(pos2(r.left() + 140.0, r.top() + 18.0), Align2::LEFT_TOP, "Quad Backup", black(28.0), TEXT);
        let pulse = 0.5 + 0.5 * (t * 3.0).sin();
        let dot = pos2(r.left() + 147.0, r.top() + 77.0);
        p.circle_filled(dot, 7.0, alpha(MINT, (70.0 * pulse) as u8));
        p.circle_filled(dot, 3.5, MINT);
        p.text(pos2(r.left() + 162.0, r.top() + 67.0), Align2::LEFT_TOP, "On patrol  \u{00B7}  watching USB for quads", semibold(13.5), TEXT);
        let latest = self.db.drones.iter().filter_map(|d| Some((d, parse_time(d.last_backup.as_deref()?)?))).max_by_key(|(_, t)| *t);
        let last = match latest {
            Some((d, when)) => format!("Last backup: {}  \u{00B7}  {}", d.name_or_default(), ago(when)),
            None => "No backups yet. Plug in a quad!".into(),
        };
        p.text(pos2(r.left() + 162.0, r.top() + 90.0), Align2::LEFT_TOP, last, regular(12.5), MUTED);
    }

    fn tabs(&mut self, ui: &mut Ui, full: Rect, now: f64) {
        let mut x = full.left() + 24.0;
        let count = self.db.drones.len();
        for (page, label) in [(Page::Hangar, "Hangar"), (Page::Settings, "Settings"), (Page::Log, "Flight log")] {
            let badged = page == Page::Hangar && count > 0;
            let w = text_width(ui.ctx(), label, semibold(13.5)) + if badged { 66.0 } else { 40.0 };
            let r = Rect::from_min_size(pos2(x, full.top() + 138.0), vec2(w, 36.0));
            let resp = ui.interact(r, ui.id().with(("tab", label)), Sense::click()).on_hover_cursor(egui::CursorIcon::PointingHand);
            let sel = self.page == page;
            let p = ui.painter();
            if sel {
                p.rect_filled(r, 18.0, VIOLET);
            } else if resp.hovered() {
                p.rect_filled(r, 18.0, PANEL);
            }
            let color = if sel { Color32::WHITE } else if resp.hovered() { TEXT } else { MUTED };
            if badged {
                let badge = Rect::from_min_size(pos2(r.right() - 40.0, r.top() + 8.0), vec2(24.0, 20.0));
                p.rect_filled(badge, 10.0, if sel { Color32::WHITE } else { PANEL_HI });
                p.text(badge.center(), Align2::CENTER_CENTER, count.to_string(), bold(11.0), if sel { VIOLET } else { TEXT });
                p.text(pos2(r.left() + 20.0, r.center().y), Align2::LEFT_CENTER, label, semibold(13.5), color);
            } else {
                p.text(r.center(), Align2::CENTER_CENTER, label, semibold(13.5), color);
            }
            if resp.clicked() {
                self.show_page(page);
            }
            x += w + 8.0;
        }
        // "Saved" flash after a settings change
        let fade = 1.0 - (now - self.saved_at) as f32 / 2.2;
        if fade > 0.0 {
            let a = (255.0 * (fade * 2.5).min(1.0)) as u8;
            let r = Rect::from_min_size(pos2(full.right() - 24.0 - 96.0, full.top() + 142.0), vec2(96.0, 28.0));
            ui.painter().rect_filled(r, 14.0, alpha(MINT, a / 6));
            ui.painter().text(r.center(), Align2::CENTER_CENTER, "\u{2713}  Saved", bold(11.5), alpha(MINT, a));
        }
        ui.painter().line_segment([pos2(full.left() + 24.0, full.top() + 182.0), pos2(full.right() - 24.0, full.top() + 182.0)], Stroke::new(1.0_f32, alpha(LINE, 150)));
    }

    // ------------------------------------------------------------------ hangar

    fn hangar(&mut self, ui: &mut Ui) {
        let area = ui.max_rect();
        let top = Rect::from_min_size(pos2(area.left() + 24.0, area.top()), vec2(area.width() - 48.0, 50.0));
        let total: usize = self.counts.values().map(|c| c.0).sum();
        let paused = self.db.drones.iter().filter(|d| d.ignored).count();
        let n = self.db.drones.len();
        let summary = if n == 0 { "Nobody's home yet.".to_string() } else {
            format!("{}  \u{00B7}  {}{}", plural(n, "quad"), plural(total, "backup"), if paused > 0 { format!("  \u{00B7}  {paused} paused") } else { String::new() })
        };
        let p = ui.painter();
        p.text(top.left_top(), Align2::LEFT_TOP, "Your fleet", black(20.0), TEXT);
        p.text(top.left_top() + vec2(1.0, 30.0), Align2::LEFT_TOP, summary, regular(12.5), MUTED);
        ui.scope_builder(UiBuilder::new().max_rect(top).layout(Layout::right_to_left(Align::Min)), |ui| {
            ui.spacing_mut().item_spacing.x = 10.0;
            if widgets::pill(ui, "\u{2605} Special backup", Pill::Special, 180.0, 42.0, true).clicked() {
                self.requests.push(Request::Special(None));
            }
            if widgets::pill(ui, "Back up now", Pill::Primary, 140.0, 42.0, true).clicked() {
                self.engine.backup_now();
            }
            if widgets::pill(ui, "Open folder", Pill::Secondary, 120.0, 42.0, true).clicked() {
                platform::open_folder(&self.engine.settings().backup_root);
            }
        });

        let list = Rect::from_min_max(pos2(area.left() + 24.0, top.bottom() + 14.0), pos2(area.right() - 24.0, area.bottom() - 12.0));
        ui.scope_builder(UiBuilder::new().max_rect(list), |ui| {
            egui::ScrollArea::vertical().auto_shrink([false; 2]).show(ui, |ui| {
                if self.db.drones.is_empty() {
                    empty_hangar(ui);
                }
                let mut drones = self.db.drones.clone();
                drones.sort_by_key(|d| (d.ignored, std::cmp::Reverse(d.last_backup.clone())));
                for d in drones {
                    if let Some(action) = self.card(ui, &d) {
                        self.card_action(&d, &action);
                    }
                    ui.add_space(12.0);
                }
            });
        });
    }

    fn card(&mut self, ui: &mut Ui, d: &Drone) -> Option<String> {
        let id = d.fc_id.clone().unwrap_or_default();
        let pending = self.engine.pending_special(&id);
        let plugged = self.engine.is_plugged_in(&id);
        let (backups, specials) = d.folder.as_ref().and_then(|f| self.counts.get(f).copied()).unwrap_or((0, 0));
        let folder_missing = !d.folder_exists();
        let height = if pending.is_some() { 140.0 } else { 116.0 };
        let (r, resp) = ui.allocate_exact_size(vec2(ui.available_width() - 4.0, height), Sense::hover());
        let hot = resp.hovered();
        let p = ui.painter().clone();
        p.rect(r, 16.0, if hot { PANEL_HI } else { PANEL }, Stroke::new(1.2_f32, if hot { alpha(VIOLET, 200) } else { LINE }), StrokeKind::Inside);

        let color = if d.ignored { MUTED } else { color_for(&id) };
        let badge = pos2(r.left() + 52.0, r.top() + 54.0);
        p.circle_filled(badge, 36.0, alpha(color, 34));
        art::quad(&p, badge, 64.0, color, &Pose { prop_angle: 25.0 + id.len() as f32 * 7.0, tilt: if d.ignored { 8.0 } else { -6.0 }, blink: if d.ignored { 1.0 } else { 0.0 }, face: true, dizzy: false });
        if d.ignored {
            p.text(pos2(r.left() + 70.0, r.top() + 8.0), Align2::LEFT_TOP, "z Z", bold(11.0), MUTED);
        }

        let x = r.left() + 104.0;
        let right_w = 170.0;
        let name = d.name_or_default();
        let name_rect = p.text(pos2(x, r.top() + 14.0), Align2::LEFT_TOP, &name, bold(17.0), TEXT);
        let (chip, chip_color) = if d.ignored { ("paused", AMBER) } else if folder_missing { ("folder missing", PINK) } else { ("on patrol", MINT) };
        let cw = text_width(ui.ctx(), chip, bold(11.0)) + 18.0;
        let chip_r = Rect::from_min_size(pos2(name_rect.right() + 12.0, r.top() + 18.0), vec2(cw, 21.0));
        p.rect_filled(chip_r, 10.5, alpha(chip_color, 40));
        p.text(chip_r.center(), Align2::CENTER_CENTER, chip, bold(11.0), chip_color);

        let mut parts: Vec<String> = [&d.board, &d.firmware].iter().filter_map(|v| (*v).clone()).filter(|v| !v.is_empty()).collect();
        if let Some(c) = d.craft_name.as_ref().filter(|c| !c.is_empty() && Some(*c) != d.name.as_ref()) {
            parts.push(format!("craft name \"{c}\""));
        }
        p.with_clip_rect(Rect::from_min_size(pos2(x, r.top() + 44.0), vec2(r.width() - 104.0 - right_w - 16.0, 22.0)))
            .text(pos2(x, r.top() + 47.0), Align2::LEFT_TOP, parts.join("  \u{00B7}  "), regular(12.0), MUTED);

        let rx = r.right() - 18.0;
        p.text(pos2(rx, r.top() + 14.0), Align2::RIGHT_TOP, "last backup", regular(12.0), MUTED);
        let last = parse_time(d.last_backup.as_deref().unwrap_or("")).map(ago).unwrap_or_else(|| "never".into());
        p.text(pos2(rx, r.top() + 32.0), Align2::RIGHT_TOP, last, bold(14.0), TEXT);
        let count = plural(backups, "backup");
        if specials > 0 {
            let star = p.text(pos2(rx, r.top() + 57.0), Align2::RIGHT_TOP, format!("\u{2605} {specials} special"), regular(12.0), GOLD);
            p.text(pos2(star.left(), r.top() + 57.0), Align2::RIGHT_TOP, format!("{count}  \u{00B7}  "), regular(12.0), MUTED);
        } else {
            p.text(pos2(rx, r.top() + 57.0), Align2::RIGHT_TOP, count, regular(12.0), MUTED);
        }

        let mut ly = r.top() + 82.0;
        if let Some(name) = &pending {
            let note = Rect::from_min_size(pos2(x, r.top() + 74.0), vec2(r.right() - 18.0 - x, 26.0));
            p.rect_filled(note, 13.0, alpha(GOLD, 34));
            p.with_clip_rect(note.shrink2(vec2(12.0, 0.0))).text(pos2(note.left() + 12.0, note.center().y), Align2::LEFT_CENTER,
                format!("\u{2605}  \"{name}\" is ready: disconnect in Betaflight and I'll save it"), bold(12.0), rgb(0xFFD86B));
            ly = r.top() + 108.0;
        }

        let mut items: Vec<(&str, String)> = Vec::new();
        if plugged && !d.ignored {
            items.push(("special", "\u{2605} Special backup".into()));
        }
        items.push(("open", "Open folder".into()));
        items.push(("rename", "Rename".into()));
        items.push(("move", "Change folder".into()));
        items.push(("pause", if d.ignored { "Resume backups".into() } else { "Pause backups".into() }));
        items.push(("forget", "Forget".into()));
        let mut lx = x;
        let mut clicked = None;
        for (key, label) in items {
            let w = text_width(ui.ctx(), &label, bold(12.0));
            let hit = Rect::from_min_size(pos2(lx - 4.0, ly - 3.0), vec2(w + 8.0, 22.0));
            let link = ui.interact(hit, ui.id().with(("card", &id, key)), Sense::click()).on_hover_cursor(egui::CursorIcon::PointingHand);
            let base = match key { "forget" => alpha(PINK, 220), "special" => GOLD, _ => SKY };
            let c = if link.hovered() { TEXT } else { base };
            let g = p.text(pos2(lx, ly), Align2::LEFT_TOP, &label, bold(12.0), c);
            if link.hovered() {
                p.line_segment([pos2(g.left(), g.bottom()), pos2(g.right(), g.bottom())], Stroke::new(1.0_f32, c));
            }
            if link.clicked() {
                clicked = Some(key.to_string());
            }
            lx += w + 22.0;
        }
        clicked
    }

    fn card_action(&mut self, d: &Drone, action: &str) {
        let id = d.fc_id.clone().unwrap_or_default();
        let name = d.name_or_default();
        match action {
            "special" => self.requests.push(Request::Special(Some(id))),
            "open" => match &d.folder {
                Some(f) if d.folder_exists() => platform::open_folder(f),
                _ => self.requests.push(Request::Dialog(Dialog::alert("That folder's gone missing", &format!("Use Change folder to point {name} somewhere new.")))),
            },
            "rename" => self.requests.push(Request::Dialog(Dialog::new(&format!("Rename {name}"),
                "This renames its backup folder too. Old backups keep their file names.", Some(name.clone()), "Rename", true, false, false,
                DialogAction::Rename { fc_id: id }))),
            "move" => {
                let start = d.folder.clone().unwrap_or_else(|| self.engine.settings().backup_root);
                if let Some(f) = rfd::FileDialog::new().set_title(format!("Pick the folder for {name}'s backups")).set_directory(start).pick_folder() {
                    let folder = f.to_string_lossy().to_string();
                    let leaf = f.file_name().map(|n| n.to_string_lossy().to_string());
                    DroneDb::mutate(|db| {
                        if let Some(x) = db.drones.iter_mut().find(|x| x.fc_id.as_deref() == Some(&id)) {
                            x.folder = Some(folder);
                            x.name = leaf;
                        }
                    });
                }
            }
            "pause" => DroneDb::set_paused(&id, !d.ignored),
            "forget" => self.requests.push(Request::Dialog(Dialog::new(&format!("Forget {name}?"),
                "Its backups stay right where they are, and you can restore it from Settings. Next time it's plugged in, I'll ask where to save it.",
                None, "Forget it", true, true, false, DialogAction::Forget { fc_id: id }))),
            _ => {}
        }
        self.refresh();
    }

    // ------------------------------------------------------------------ settings

    fn save_settings(&mut self, now: f64, change: impl FnOnce(&mut Settings)) {
        let mut s = Settings::load();
        change(&mut s);
        match s.save() {
            Ok(()) => {
                self.settings = s;
                self.engine.reload_settings();
                self.saved_at = now;
            }
            Err(e) => store::log(format!("Couldn't save settings: {e}")),
        }
    }

    fn settings_page(&mut self, ui: &mut Ui, now: f64) {
        let area = ui.max_rect();
        let list = Rect::from_min_max(pos2(area.left() + 24.0, area.top()), pos2(area.right() - 12.0, area.bottom()));
        ui.scope_builder(UiBuilder::new().max_rect(list), |ui| {
            egui::ScrollArea::vertical().auto_shrink([false; 2]).show(ui, |ui| {
                ui.set_width(list.width() - 18.0);
                let mut s = self.settings.clone();

                section(ui, "BACKUPS");
                row(ui, "Backups folder", &s.backup_root, 120.0, |ui| {
                    if widgets::pill(ui, "Change\u{2026}", Pill::Secondary, 112.0, 36.0, true).clicked() {
                        if let Some(f) = rfd::FileDialog::new().set_title("Where should new drone folders be created?").set_directory(&s.backup_root).pick_folder() {
                            let f = f.to_string_lossy().to_string();
                            self.save_settings(now, |x| x.backup_root = f);
                        }
                    }
                });
                let mut cmd = if s.cli_command == "dump all" { 1 } else { 0 };
                row(ui, "Backup command", "diff all saves only what you've changed (best for restoring). dump all saves every single setting.", 220.0, |ui| {
                    if widgets::segmented(ui, &["diff all", "dump all"], &mut cmd, 210.0).changed() {
                        self.save_settings(now, |x| x.cli_command = if cmd == 1 { "dump all".into() } else { "diff all".into() });
                    }
                });
                row(ui, "Firmware to back up", "Flight controllers running anything else are left alone.", 330.0, |ui| {
                    ui.spacing_mut().item_spacing.x = 8.0;
                    for (label, code) in [("Emuflight", "EMUF"), ("INAV", "INAV"), ("Betaflight", "BTFL")] {
                        let mut on = s.firmware_variants.iter().any(|v| v == code);
                        if widgets::chip_toggle(ui, label, &mut on).changed() {
                            let mut list: Vec<String> = s.firmware_variants.iter().filter(|v| v.as_str() != code).cloned().collect();
                            if on { list.push(code.into()); }
                            if !list.is_empty() {
                                s.firmware_variants = list.clone();
                                self.save_settings(now, |x| x.firmware_variants = list);
                            }
                        }
                    }
                });
                toggle_row(ui, "Skip backups that haven't changed", "If a quad's settings match its last backup, I won't save another copy. Special backups are always kept.",
                    s.skip_unchanged_backups, |v| self.save_settings(now, |x| x.skip_unchanged_backups = v));
                toggle_row(ui, "Back up when you disconnect in Betaflight", "Hit Disconnect with the quad still plugged in and I'll save that session's tune. Only kept if something changed. Also covers closing Betaflight.",
                    s.backup_when_disconnected, |v| self.save_settings(now, |x| x.backup_when_disconnected = v));
                toggle_row(ui, "Back up when Betaflight closes", "For when disconnect backups are off: close Betaflight with the quad still plugged in to save that session's tune.",
                    s.backup_when_betaflight_closes, |v| self.save_settings(now, |x| x.backup_when_betaflight_closes = v));

                section(ui, "TURNED-OFF DRONES");
                let paused: Vec<Drone> = self.db.drones.iter().filter(|d| d.ignored).cloned().collect();
                let forgotten = self.db.forgotten.clone();
                if paused.is_empty() && forgotten.is_empty() {
                    row(ui, "None right now", "Every drone you've plugged in is on patrol. Drones you set to \"never back up\" or Forget show up here, so you can bring them back.", 0.0, |_| {});
                }
                for d in paused {
                    let extra = if d.folder.as_deref().map_or(true, str::is_empty) { ". I'll ask where to save it next time it's plugged in." } else { "." };
                    row(ui, &d.name_or_default(), &format!("Set to never back up{}{extra}", details(&d)), 140.0, |ui| {
                        if widgets::pill(ui, "Back up again", Pill::Secondary, 132.0, 36.0, true).clicked() {
                            DroneDb::set_paused(d.fc_id.as_deref().unwrap_or(""), false);
                            self.refresh();
                        }
                    });
                }
                for d in forgotten {
                    let extra = d.folder.as_ref().map(|f| format!(". Its backups are still in {f}")).unwrap_or_else(|| ".".into());
                    row(ui, &d.name_or_default(), &format!("Forgotten{}{extra}", details(&d)), 112.0, |ui| {
                        if widgets::pill(ui, "Restore", Pill::Secondary, 104.0, 36.0, true).clicked() {
                            DroneDb::restore(d.fc_id.as_deref().unwrap_or(""));
                            self.refresh();
                        }
                    });
                }

                section(ui, "BETAFLIGHT");
                toggle_row(ui, "Open Betaflight after a backup", "Skipped if it's already open.", s.launch_betaflight, |v| self.save_settings(now, |x| x.launch_betaflight = v));
                let mut launch = std::mem::take(&mut self.launch);
                row(ui, "Betaflight app", if cfg!(windows) { "A shortcut, an .exe, or https://app.betaflight.com" } else { "An app, a .desktop file, or https://app.betaflight.com" }, 430.0, |ui| {
                    if widgets::pill(ui, "Auto-detect", Pill::Secondary, 116.0, 36.0, true).clicked() {
                        launch = platform::find_betaflight_shortcut().unwrap_or_else(|| "https://app.betaflight.com".into());
                        let l = launch.clone();
                        self.save_settings(now, |x| x.betaflight_launch_path = l);
                    }
                    let r = ui.add(egui::TextEdit::singleline(&mut launch).font(regular(12.5)).desired_width(300.0).margin(egui::Margin::symmetric(10, 8)));
                    if r.lost_focus() {
                        let l = launch.trim().to_string();
                        self.save_settings(now, |x| x.betaflight_launch_path = l);
                    }
                });
                self.launch = launch;

                section(ui, "POP-UPS & STARTUP");
                let mut notify = s.show_notifications;
                row(ui, "Pop-up notifications", "Little pop-ups when backups happen (or don't).", 140.0, |ui| {
                    if widgets::face_toggle(ui, &mut notify).changed() {
                        self.save_settings(now, |x| x.show_notifications = notify);
                    }
                    if widgets::pill(ui, "Test", Pill::Secondary, 76.0, 36.0, true).clicked() {
                        self.requests.push(Request::TestToast);
                    }
                });
                let mut startup = self.startup;
                toggle_row(ui, if cfg!(windows) { "Start with Windows" } else { "Start at login" }, "So I'm already on patrol when you plug in a quad.", startup, |v| {
                    startup = v;
                    match platform::set_startup(v) {
                        Ok(()) => self.saved_at = now,
                        Err(e) => store::log(format!("Couldn't change start at login: {e}")),
                    }
                });
                self.startup = startup;

                section(ui, "ADVANCED");
                let (mut vendors, mut grace, mut wait) = (std::mem::take(&mut self.vendors), std::mem::take(&mut self.grace), std::mem::take(&mut self.wait));
                let mut bad = self.bad;
                row(ui, "USB chip vendors", "Only USB devices from these vendors are ever opened. 0483 = STM32, 2E3C = AT32, 314B = APM32.", 210.0, |ui| {
                    if number_like_box(ui, &mut vendors, 200.0, bad[0]).lost_focus() {
                        let ids: Vec<String> = vendors.split(|c: char| c == ',' || c == ';' || c.is_whitespace()).filter(|v| !v.is_empty()).map(|v| v.to_uppercase()).collect();
                        bad[0] = ids.is_empty() || !ids.iter().all(|v| v.len() == 4 && v.chars().all(|c| c.is_ascii_hexdigit()));
                        if !bad[0] {
                            vendors = ids.join(", ");
                            self.save_settings(now, |x| x.usb_vendor_ids = ids);
                        }
                    }
                });
                row(ui, "Reboot grace (seconds)", "While Betaflight is open, a quad that reconnects this fast is treated as a reboot after Save, not a new plug-in. Also how long Betaflight gets to reconnect after a reboot. 2 to 60.", 90.0, |ui| {
                    if number_like_box(ui, &mut grace, 80.0, bad[1]).lost_focus() {
                        match grace.trim().parse::<i32>() {
                            Ok(n) if (2..=60).contains(&n) => { bad[1] = false; self.save_settings(now, |x| x.reboot_grace_seconds = n); }
                            _ => bad[1] = true,
                        }
                    }
                });
                row(ui, "Disconnect wait (seconds)", "How long after you hit Disconnect before I back up. When Betaflight reboots the quad itself (Save, CLI, Blackbox, flashing) I wait at least the reboot grace instead. 1 to 60.", 90.0, |ui| {
                    if number_like_box(ui, &mut wait, 80.0, bad[2]).lost_focus() {
                        match wait.trim().parse::<i32>() {
                            Ok(n) if (1..=60).contains(&n) => { bad[2] = false; self.save_settings(now, |x| x.disconnect_wait_seconds = n); }
                            _ => bad[2] = true,
                        }
                    }
                });
                self.vendors = vendors;
                self.grace = grace;
                self.wait = wait;
                self.bad = bad;

                ui.add_space(18.0);
                ui.horizontal(|ui| {
                    ui.label(egui::RichText::new("Changes save automatically.").font(regular(12.5)).color(MUTED));
                    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                        if widgets::pill(ui, "Open settings.json", Pill::Secondary, 168.0, 36.0, true).clicked() {
                            platform::open_text_file(&store::settings_path());
                        }
                    });
                });
                ui.add_space(24.0);
            });
        });
    }

    // ------------------------------------------------------------------ log

    fn load_log(&mut self) {
        let path = store::log_path();
        let sig = std::fs::metadata(&path).map(|m| format!("{}/{:?}/{}", m.len(), m.modified().ok(), self.log_filter)).unwrap_or_else(|_| format!("none/{}", self.log_filter));
        if sig == self.log_sig {
            return;
        }
        self.log_sig = sig;
        let text = std::fs::read_to_string(&path).unwrap_or_default();
        let lines: Vec<&str> = text.lines().filter(|l| !l.is_empty()).collect();
        self.log_lines = lines[lines.len().saturating_sub(800)..].iter().filter_map(|line| {
            let (time, msg) = if line.len() > 21 && line.is_char_boundary(21) { (line.get(5..19).unwrap_or(""), &line[21..]) } else { ("", *line) };
            let color = log_color(msg);
            let keep = match self.log_filter {
                1 => is_backup_line(msg),
                2 => color == PINK || color == AMBER,
                _ => true,
            };
            keep.then(|| (time.to_string(), msg.to_string(), color))
        }).collect();
    }

    fn log_page(&mut self, ui: &mut Ui) {
        self.load_log();
        let area = ui.max_rect();
        let top = Rect::from_min_size(pos2(area.left() + 24.0, area.top()), vec2(area.width() - 48.0, 50.0));
        let p = ui.painter();
        p.text(top.left_top(), Align2::LEFT_TOP, "Flight log", black(20.0), TEXT);
        p.text(top.left_top() + vec2(1.0, 30.0), Align2::LEFT_TOP, "Everything I've been up to, newest at the bottom.", regular(12.5), MUTED);
        ui.scope_builder(UiBuilder::new().max_rect(top).layout(Layout::right_to_left(Align::Min)), |ui| {
            ui.spacing_mut().item_spacing.x = 10.0;
            if widgets::pill(ui, "Clear", Pill::Secondary, 96.0, 42.0, true).clicked() {
                self.requests.push(Request::Dialog(Dialog::new("Clear the flight log?", "This wipes the log history. Your backups aren't touched.", None, "Clear it", true, true, false, DialogAction::ClearLog)));
            }
            if widgets::pill(ui, "Open file", Pill::Secondary, 120.0, 42.0, true).clicked() {
                platform::open_text_file(&store::log_path());
            }
        });
        let filter_rect = Rect::from_min_size(pos2(area.left() + 24.0, top.bottom() + 12.0), vec2(330.0, 36.0));
        ui.scope_builder(UiBuilder::new().max_rect(filter_rect), |ui| {
            if widgets::segmented(ui, &["Everything", "Backups", "Problems"], &mut self.log_filter, 330.0).changed() {
                self.log_sig.clear();
            }
        });
        let frame = Rect::from_min_max(pos2(area.left() + 24.0, filter_rect.bottom() + 12.0), pos2(area.right() - 24.0, area.bottom() - 20.0));
        ui.painter().rect(frame, 14.0, PANEL, Stroke::new(1.2_f32, LINE), StrokeKind::Inside);
        ui.scope_builder(UiBuilder::new().max_rect(frame.shrink2(vec2(14.0, 10.0))), |ui| {
            egui::ScrollArea::vertical().auto_shrink([false; 2]).stick_to_bottom(true).show(ui, |ui| {
                let mut job = egui::text::LayoutJob::default();
                job.wrap.max_width = ui.available_width();
                let fmt = |c: Color32| egui::TextFormat { font_id: mono(12.0), color: c, line_height: Some(20.0), ..Default::default() };
                if self.log_lines.is_empty() {
                    job.append(if self.log_filter == 2 { "No problems. Clear skies!" } else { "Nothing here yet. It's been a quiet day on the bench." }, 0.0, fmt(MUTED));
                }
                for (time, msg, color) in &self.log_lines {
                    job.append(&format!("{time}   "), 0.0, fmt(MUTED));
                    job.append(&format!("{msg}\n"), 0.0, fmt(*color));
                }
                ui.label(job);
            });
        });
    }
}

// ------------------------------------------------------------------------------------------------ helpers

fn section(ui: &mut Ui, title: &str) {
    ui.add_space(16.0);
    ui.label(egui::RichText::new(title).font(bold(11.0)).color(VIOLET));
    ui.add_space(2.0);
}

/// A settings row: title and description on the left, controls on the right (add them right-to-left).
fn row(ui: &mut Ui, title: &str, desc: &str, controls_w: f32, controls: impl FnOnce(&mut Ui)) {
    let width = ui.available_width();
    ui.add_space(8.0);
    ui.horizontal(|ui| {
        ui.vertical(|ui| {
            ui.set_width((width - controls_w - 24.0).max(200.0));
            ui.label(egui::RichText::new(title).font(semibold(14.5)).color(TEXT));
            ui.add(egui::Label::new(egui::RichText::new(desc).font(regular(12.5)).color(MUTED)).wrap());
        });
        ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
            ui.spacing_mut().item_spacing.x = 8.0;
            controls(ui);
        });
    });
    ui.add_space(8.0);
    let y = ui.cursor().top();
    ui.painter().line_segment([pos2(ui.max_rect().left(), y), pos2(ui.max_rect().left() + width, y)], Stroke::new(1.0_f32, rgb(0x24284E)));
    ui.add_space(1.0);
}

fn toggle_row(ui: &mut Ui, title: &str, desc: &str, value: bool, on_change: impl FnOnce(bool)) {
    let mut v = value;
    let mut changed = false;
    row(ui, title, desc, 60.0, |ui| {
        changed = widgets::face_toggle(ui, &mut v).changed();
    });
    if changed {
        on_change(v);
    }
}

fn number_like_box(ui: &mut Ui, text: &mut String, width: f32, bad: bool) -> egui::Response {
    let r = ui.add(egui::TextEdit::singleline(text).font(semibold(13.5)).desired_width(width).margin(egui::Margin::symmetric(10, 8)));
    if bad {
        ui.painter().rect_stroke(r.rect, 4.0, Stroke::new(1.8_f32, PINK), StrokeKind::Outside);
    }
    if r.has_focus() && ui.input(|i| i.key_pressed(egui::Key::Enter)) {
        r.surrender_focus();
    }
    r
}

fn empty_hangar(ui: &mut Ui) {
    let (r, _) = ui.allocate_exact_size(vec2(ui.available_width(), 260.0), Sense::hover());
    let p = ui.painter();
    let cx = r.center().x;
    art::glow(p, pos2(cx, r.top() + 156.0), 50.0, 6.0, alpha(INK, 150));
    art::quad(p, pos2(cx, r.top() + 92.0), 130.0, SKY, &Pose { prop_angle: 15.0, tilt: 0.0, blink: 1.0, face: true, dizzy: false });
    p.text(pos2(cx + 50.0, r.top() + 30.0), Align2::LEFT_TOP, "z", bold(20.0), MUTED);
    p.text(pos2(cx + 70.0, r.top() + 4.0), Align2::LEFT_TOP, "Z", bold(28.0), alpha(MUTED, 170));
    p.text(pos2(cx, r.top() + 182.0), Align2::CENTER_TOP, "The hangar's empty", black(20.0), TEXT);
    p.text(pos2(cx, r.top() + 214.0), Align2::CENTER_TOP, "Plug in a quad and I'll take it from there.", regular(13.5), MUTED);
}

fn details(d: &Drone) -> String {
    let parts: Vec<String> = [&d.board, &d.firmware].iter().filter_map(|v| (*v).clone()).filter(|v| !v.is_empty()).collect();
    if parts.is_empty() { String::new() } else { format!(" \u{00B7} {}", parts.join(" \u{00B7} ")) }
}

fn plural(n: usize, word: &str) -> String { format!("{n} {word}{}", if n == 1 { "" } else { "s" }) }

fn color_for(key: &str) -> Color32 {
    let mut h: i32 = 17;
    for c in key.chars() {
        h = h.wrapping_mul(31).wrapping_add(c as i32);
    }
    DRONE_COLORS[(h.rem_euclid(DRONE_COLORS.len() as i32)) as usize]
}

pub fn parse_time(s: &str) -> Option<chrono::NaiveDateTime> {
    chrono::NaiveDateTime::parse_from_str(s, "%Y-%m-%d %H:%M:%S").ok()
}

pub fn ago(t: chrono::NaiveDateTime) -> String {
    let d = chrono::Local::now().naive_local() - t;
    let (secs, mins, hours, days) = (d.num_seconds(), d.num_minutes(), d.num_hours(), d.num_days());
    if secs < 60 { "just now".into() }
    else if mins < 60 { format!("{mins} min ago") }
    else if hours < 24 { format!("{hours} hour{} ago", if hours == 1 { "" } else { "s" }) }
    else if days < 30 { if days == 1 { "yesterday".into() } else { format!("{days} days ago") } }
    else { t.format("%b %-d, %Y").to_string() }
}

fn log_color(msg: &str) -> Color32 {
    let m = msg.to_lowercase();
    if m.contains("saved ") { MINT }
    else if ["failed", "couldn't", "error", "incomplete", "invalid"].iter().any(|w| m.contains(w)) { PINK }
    else if m.contains("in use") || m.contains("stuck") { AMBER }
    else if m.contains("plugged in") { SKY }
    else if m.starts_with("started") || m.starts_with("exited") || m.contains("launched") || m.contains("betaflight closed") { VIOLET }
    else if ["came back", "ignoring", "not backed up", "skipped", "unchanged", "never back up"].iter().any(|w| m.contains(w)) { MUTED }
    else { TEXT }
}

fn is_backup_line(msg: &str) -> bool {
    let m = msg.to_lowercase();
    m.contains("saved ") || m.contains("plugged in") || m.contains("unchanged") || m.contains(": read ")
}

/// Renames a drone's backup folder (and every flight controller that shares it, e.g. after an FC swap).
pub fn rename_drone(fc_id: &str, new_name: &str) -> Result<(), String> {
    let db = DroneDb::load();
    let Some(d) = db.find(fc_id).cloned() else { return Ok(()) };
    let name = store::safe_name(new_name);
    if Some(&name) == d.name.as_ref() {
        return Ok(());
    }
    let old = d.folder.clone().unwrap_or_default();
    let parent = if old.is_empty() { std::path::PathBuf::from(Settings::load().backup_root) } else {
        Path::new(old.trim_end_matches(['\\', '/'])).parent().map(Path::to_path_buf).unwrap_or_default()
    };
    let new_folder = parent.join(&name);
    if !old.is_empty() && Path::new(&old).is_dir() {
        if old.eq_ignore_ascii_case(&new_folder.to_string_lossy()) {
            let temp = new_folder.with_extension("renaming"); // case-only rename needs a hop on Windows
            std::fs::rename(&old, &temp).map_err(|e| e.to_string())?;
            std::fs::rename(&temp, &new_folder).map_err(|e| e.to_string())?;
        } else if new_folder.exists() {
            return Err(format!("There's already a folder called \"{name}\" there. Pick another name, or use Change folder to point at it."));
        } else {
            std::fs::rename(&old, &new_folder).map_err(|e| e.to_string())?;
        }
    }
    let nf = new_folder.to_string_lossy().to_string();
    DroneDb::mutate(|db| {
        for x in db.drones.iter_mut().filter(|x| x.fc_id.as_deref() == Some(fc_id) || (!old.is_empty() && x.folder.as_deref().map_or(false, |f| f.eq_ignore_ascii_case(&old)))) {
            x.folder = Some(nf.clone());
            x.name = Some(name.clone());
        }
    });
    Ok(())
}
