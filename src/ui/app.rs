//! The app: tray icon, the engine, and every window. egui draws all windows as "viewports" of one hidden 1x1
//! root window, which keeps running (so pop-ups and the new-drone window can appear) while nothing else is open.

use super::art::{self, Confetti, Pose, Sky};
use super::main_window::{MainWindow, Page};
use super::theme::*;
use super::widgets::{self, Pill};
use crate::core::engine::{AskRequest, Engine, EngineOptions, EngineUi};
use crate::core::store::{self, log, DroneMood, NewDroneChoice, Settings, ToastKind};
use crate::platform;
use super::tray::{self, TrayAction};
use eframe::egui::{self, pos2, vec2, Align2, Color32, IconData, Pos2, Rect, Sense, ViewportBuilder, ViewportCommand, ViewportId};
use std::path::Path;
use std::sync::mpsc::{channel, Receiver, Sender};
use std::sync::Arc;
use std::time::Duration;

pub enum UiMsg {
    Toast { kind: ToastKind, title: String, text: String, folder: Option<String> },
    Ask { request: AskRequest, reply: Sender<Option<NewDroneChoice>> },
    ShowMain(Page),
    Tray(TrayAction),
}

/// The engine's way onto the screen (it runs on its own threads).
struct Bridge {
    tx: Sender<UiMsg>,
    ctx: egui::Context,
}

impl EngineUi for Bridge {
    fn notify(&self, kind: ToastKind, title: &str, text: &str, folder: Option<String>) {
        let _ = self.tx.send(UiMsg::Toast { kind, title: title.into(), text: text.into(), folder });
        self.ctx.request_repaint();
    }

    fn ask_where_to_save(&self, request: AskRequest) -> Option<NewDroneChoice> {
        let (reply, answer) = channel();
        let _ = self.tx.send(UiMsg::Ask { request, reply });
        self.ctx.request_repaint();
        answer.recv().ok().flatten()
    }
}

// ------------------------------------------------------------------------------------------------ dialogs

pub enum DialogAction {
    Alert,
    Rename { fc_id: String },
    Forget { fc_id: String },
    ClearLog,
    Special { port: String },
}

pub struct Dialog {
    id: u64,
    title: String,
    message: String,
    input: Option<String>,
    ok_text: String,
    cancel: bool,
    danger: bool,
    special: bool,
    action: DialogAction,
    focused: bool,
}

impl Dialog {
    pub fn alert(title: &str, message: &str) -> Dialog {
        Dialog::new(title, message, None, "Got it", false, true, false, DialogAction::Alert)
    }

    #[allow(clippy::too_many_arguments)]
    pub fn new(title: &str, message: &str, input: Option<String>, ok_text: &str, cancel: bool, danger: bool, special: bool, action: DialogAction) -> Dialog {
        Dialog { id: 0, title: title.into(), message: message.into(), input, ok_text: ok_text.into(), cancel, danger, special, action, focused: false }
    }
}

// ------------------------------------------------------------------------------------------------ pop-ups

struct Toast {
    id: u64,
    kind: ToastKind,
    title: String,
    text: String,
    folder: Option<String>,
    born: f64,
    lived: f32,
    closing_at: Option<f64>,
    y: f32,
    height: f32,
}

const TOAST_W: f32 = 380.0;
const TOAST_LIFE: f32 = 7.0;

fn accent(kind: ToastKind) -> Color32 {
    match kind {
        ToastKind::Success => MINT,
        ToastKind::Warning => AMBER,
        ToastKind::Error => PINK,
        ToastKind::Special => GOLD,
        ToastKind::Info => SKY,
    }
}

// ------------------------------------------------------------------------------------------------ new-drone window

struct NewDrone {
    id: u64,
    request: AskRequest,
    reply: Option<Sender<Option<NewDroneChoice>>>,
    name: String,
    location: String,
    headline: String,
    subline: String,
    celebrate_at: Option<f64>,
    close_at: Option<f64>,
    confetti: Confetti,
    sky: Sky,
    shake_at: Option<f64>,
    focused: bool,
    name_focused: bool,
}

impl NewDrone {
    fn new(id: u64, request: AskRequest, reply: Sender<Option<NewDroneChoice>>) -> NewDrone {
        let hellos = ["A wild quad appears!", "New quad on the bench!", "Ooh, a new one!", "Well hello there, flyer!"];
        let n = request.suggested_name.clone();
        let craft = request.backup.craft_name.clone().filter(|c| !c.is_empty());
        let (headline, subline) = match request.mood {
            DroneMood::NewFlightController => (format!("Is that you, {n}?"), "Same name, new flight controller. Keep the name to add to its history.".to_string()),
            DroneMood::MissingFolder => ("Hey, where'd my folder go?".to_string(), format!("{n}'s backup folder vanished. Let's give it a new one.")),
            DroneMood::Returning => (format!("Welcome back, {n}!"), "You forgot me, but I kept my folder. Keep the name to pick up where we left off.".to_string()),
            DroneMood::New => (
                hellos[(id as usize + chrono::Local::now().timestamp_subsec_millis() as usize) % hellos.len()].to_string(),
                match craft {
                    Some(c) => format!("Meet {c}. Let's give it a home for its backups."),
                    None => "No craft name set, so let's call it something nice.".to_string(),
                },
            ),
        };
        NewDrone {
            id, name: request.suggested_name.clone(), location: request.location.clone(), request, reply: Some(reply), headline, subline,
            celebrate_at: None, close_at: None, confetti: Confetti::default(), sky: Sky::new(7, 30), shake_at: None, focused: false, name_focused: false,
        }
    }

    fn answer(&mut self, choice: Option<NewDroneChoice>) {
        if let Some(reply) = self.reply.take() {
            let _ = reply.send(choice);
        }
    }
}

// ------------------------------------------------------------------------------------------------ the app

pub struct App {
    engine: Engine,
    rx: Receiver<UiMsg>,
    _tray: Option<tray::Tray>,
    icon: Arc<IconData>,
    main: MainWindow,
    main_open: bool,
    /// The main window is in front or under the mouse, so it animates at full speed.
    main_active: bool,
    main_minimized: bool,
    toasts: Vec<Toast>,
    dialogs: Vec<Dialog>,
    drones: Vec<NewDrone>,
    next_id: u64,
    ppp: f32,
    monitor: Option<egui::Vec2>,
}

impl App {
    pub fn new(cc: &eframe::CreationContext<'_>, instance: Arc<platform::Instance>, show_window: bool) -> App {
        install_fonts(&cc.egui_ctx);
        install_visuals(&cc.egui_ctx);
        let (tx, rx) = channel();

        let (settings, first_run) = Settings::load_or_create(|| platform::find_betaflight_shortcut().unwrap_or_else(|| "https://app.betaflight.com".into()));
        let _ = std::fs::create_dir_all(&settings.backup_root);
        if (first_run || platform::startup_enabled()) && std::env::var_os("QB_DEMO").is_none() {
            // first run turns on start at login; later runs refresh the entry in case the app moved
            if let Err(e) = platform::set_startup(true) {
                log(format!("Couldn't update start at login: {e}"));
            }
        }

        let engine = Engine::new(Arc::new(platform::NativePlatform), Arc::new(Bridge { tx: tx.clone(), ctx: cc.egui_ctx.clone() }), EngineOptions::default());
        if std::env::var_os("QB_DEMO").is_none() {
            engine.start();
        }

        // a second copy of the exe was launched: open the window here
        {
            let (tx, ctx) = (tx.clone(), cc.egui_ctx.clone());
            std::thread::spawn(move || loop {
                if instance.wait_for_show(Duration::from_secs(3600)) {
                    let _ = tx.send(UiMsg::ShowMain(Page::Hangar));
                    ctx.request_repaint();
                }
            });
        }

        let tray = tray::build(tx.clone(), cc.egui_ctx.clone());
        let icon = Arc::new(IconData { rgba: art::classic_icon_rgba(64), width: 64, height: 64 });
        log(format!("Started (Rust). Backups go to {}", settings.backup_root));

        let mut app = App {
            main: MainWindow::new(engine.clone()),
            engine, rx, _tray: tray, icon, main_open: show_window, main_active: show_window, main_minimized: false, toasts: Vec::new(), dialogs: Vec::new(), drones: Vec::new(), next_id: 1, ppp: 1.0, monitor: None,
        };
        if !show_window && first_run {
            app.toast(ToastKind::Info, "Quad Backup is on patrol!", &format!("Plug in a drone and I'll back it up. My icon is in the {}.", platform::TRAY_WORD), Some(settings.backup_root));
        }
        if let Ok(demo) = std::env::var("QB_DEMO") {
            app.demo(&demo);
        }
        app
    }

    fn id(&mut self) -> u64 {
        self.next_id += 1;
        self.next_id
    }

    fn toast(&mut self, kind: ToastKind, title: &str, text: &str, folder: Option<String>) {
        let id = self.id();
        self.toasts.push(Toast { id, kind, title: title.into(), text: text.into(), folder, born: -1.0, lived: 0.0, closing_at: None, y: f32::NAN, height: 92.0 });
        let open: Vec<usize> = (0..self.toasts.len()).filter(|&i| self.toasts[i].closing_at.is_none()).collect();
        if open.len() > 3 {
            self.toasts[open[0]].closing_at = Some(0.0);
        }
    }

    pub fn open_dialog(&mut self, mut d: Dialog) {
        d.id = self.id();
        self.dialogs.push(d);
    }

    fn special_backup(&mut self, fc_id: Option<String>) {
        match self.engine.find_special_target(fc_id.as_deref()) {
            None => self.open_dialog(Dialog::alert("No quad plugged in", "Plug in the quad you're tuning, then hit Special backup again.")),
            Some((port, name)) => self.open_dialog(Dialog::new(
                &format!("\u{2605} Special backup{}", name.map(|n| format!(" for {n}")).unwrap_or_default()),
                "Name this one so you can find it later, like \"locked in freestyle\" or \"before new props\". It's kept even if nothing changed.",
                Some(String::new()), "\u{2605} Save it", true, false, true, DialogAction::Special { port },
            )),
        }
    }

    fn handle_messages(&mut self, ctx: &egui::Context) {
        while let Ok(msg) = self.rx.try_recv() {
            match msg {
                UiMsg::Toast { kind, title, text, folder } => self.toast(kind, &title, &text, folder),
                UiMsg::Ask { request, reply } => {
                    let id = self.id();
                    self.drones.push(NewDrone::new(id, request, reply));
                }
                UiMsg::ShowMain(page) => self.show_main(page),
                UiMsg::Tray(action) => self.tray_action(ctx, action),
            }
        }
        // things the main window asked for
        for req in self.main.take_requests() {
            match req {
                super::main_window::Request::Dialog(d) => self.open_dialog(d),
                super::main_window::Request::Special(fc) => self.special_backup(fc),
                super::main_window::Request::TestToast => self.toast(ToastKind::Success, "Test pop-up!", store::quips::saved(), None),
            }
        }
    }

    fn show_main(&mut self, page: Page) {
        self.main.show_page(page);
        self.main_open = true;
        self.main.focus = true;
    }

    fn tray_action(&mut self, ctx: &egui::Context, action: TrayAction) {
        match action {
            TrayAction::Open | TrayAction::Hangar => self.show_main(Page::Hangar),
            TrayAction::Settings => self.show_main(Page::Settings),
            TrayAction::Log => self.show_main(Page::Log),
            TrayAction::Backup => self.engine.backup_now(),
            TrayAction::Special => self.special_backup(None),
            TrayAction::Folder => platform::open_folder(&self.engine.settings().backup_root),
            TrayAction::Launch => self.engine.launch_betaflight(),
            TrayAction::Exit => {
                log("Exited");
                self.engine.stop();
                for d in &mut self.drones {
                    d.answer(None);
                }
                ctx.send_viewport_cmd(ViewportCommand::Close);
            }
        }
    }

    /// QB_DEMO=hangar|settings|log|drone|toasts|special opens that screen with sample data.
    fn demo(&mut self, what: &str) {
        match what {
            "settings" => self.show_main(Page::Settings),
            "log" => self.show_main(Page::Log),
            "drone" => {
                let backup = crate::core::fc::FcBackup { board: Some("SPEEDYBEEF405V4".into()), firmware: Some("Betaflight 4.5.1".into()), craft_name: Some("Chimera7".into()), ..Default::default() };
                let (reply, _keep) = channel();
                std::mem::forget(_keep);
                let id = self.id();
                let request = AskRequest { backup, port: "COM7".into(), suggested_name: "Chimera7".into(), location: self.engine.settings().backup_root, note: None, mood: DroneMood::New };
                self.drones.push(NewDrone::new(id, request, reply));
            }
            "toasts" => {
                self.toast(ToastKind::Success, "Chimera7 is backed up!", &format!("{}\nChimera7_2026-10-19_14-32-05.txt", store::quips::saved()), None);
                self.toast(ToastKind::Special, "\u{2605} \"locked in freestyle\" saved!", store::quips::special(), None);
                self.toast(ToastKind::Error, "Oof. Backup failed on COM15", "Flight controller didn't enter CLI mode", None);
            }
            "special" => self.open_dialog(Dialog::new("\u{2605} Special backup for Chimera7",
                "Name this one so you can find it later, like \"locked in freestyle\" or \"before new props\". It's kept even if nothing changed.",
                Some("locked in freestyle".into()), "\u{2605} Save it", true, false, true, DialogAction::Alert)),
            _ => self.show_main(Page::Hangar),
        }
    }

    /// The usable screen area in points: Windows' work area (minus the taskbar), otherwise the monitor size with
    /// room left for a menu bar / panels.
    fn work_area_points(&self) -> Rect {
        if let Some((l, t, r, b)) = platform::work_area() {
            let s = self.ppp;
            return Rect::from_min_max(pos2(l as f32 / s, t as f32 / s), pos2(r as f32 / s, b as f32 / s));
        }
        let size = self.monitor.unwrap_or(vec2(1440.0, 900.0));
        Rect::from_min_max(pos2(0.0, 30.0), pos2(size.x, size.y - 48.0))
    }

    fn centered(&self, size: egui::Vec2) -> Pos2 {
        let a = self.work_area_points();
        pos2(a.center().x - size.x / 2.0, (a.center().y - size.y / 2.0).max(a.top()))
    }
}

impl eframe::App for App {
    fn clear_color(&self, _: &egui::Visuals) -> [f32; 4] {
        [0.0; 4] // the root window is an invisible 1x1 speck
    }

    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        self.ppp = ctx.input(|i| i.viewport().native_pixels_per_point).unwrap_or(ctx.pixels_per_point());
        if let Some(m) = ctx.input(|i| i.viewport().monitor_size) {
            self.monitor = Some(m);
        }
        self.handle_messages(ctx);
        let now = ctx.input(|i| i.time);

        if self.main_open {
            self.show_main_window(ctx);
        }
        self.show_new_drones(ctx, now);
        self.show_dialogs(ctx);
        self.show_toasts(ctx, now);

        // Smooth (30 fps) only while something is worth watching: pop-ups, the new-drone window, dialogs, or the main
        // window when it's in front or under the mouse. Behind other windows it ticks along at 10 fps; minimized or
        // closed, once a second. Each frame costs a few ms of CPU, so this is what keeps a laptop's fan quiet.
        let lively = !self.toasts.is_empty() || !self.drones.is_empty() || !self.dialogs.is_empty() || (self.main_open && self.main_active);
        let every = if lively {
            Duration::from_millis(33)
        } else if self.main_open && !self.main_minimized {
            Duration::from_millis(100)
        } else {
            Duration::from_secs(1)
        };
        ctx.request_repaint_after(every);
    }
}

impl App {
    fn show_main_window(&mut self, ctx: &egui::Context) {
        let mut builder = ViewportBuilder::default()
            .with_title("Quad Backup")
            .with_inner_size([780.0, 700.0])
            .with_resizable(false)
            .with_maximize_button(false)
            .with_icon(self.icon.clone());
        if self.main.first_show {
            builder = builder.with_position(self.centered(vec2(780.0, 700.0)));
        }
        let main = &mut self.main;
        let mut close = false;
        let (mut active, mut minimized) = (false, false);
        ctx.show_viewport_immediate(ViewportId::from_hash_of("main"), builder, |ctx, _| {
            if main.focus {
                ctx.send_viewport_cmd(ViewportCommand::Minimized(false));
                ctx.send_viewport_cmd(ViewportCommand::Focus);
                main.focus = false;
            }
            main.first_show = false;
            main.ui(ctx);
            (active, minimized) = ctx.input(|i| (i.viewport().focused.unwrap_or(false) || i.pointer.has_pointer(), i.viewport().minimized.unwrap_or(false)));
            if ctx.input(|i| i.viewport().close_requested()) {
                close = true; // closing just tucks it away; Exit in the tray menu really quits
            }
        });
        self.main_active = active;
        self.main_minimized = minimized;
        if close {
            self.main_open = false;
        }
    }

    // ---------------------------------------------------------------- new-drone window

    fn show_new_drones(&mut self, ctx: &egui::Context, now: f64) {
        let mut drones = std::mem::take(&mut self.drones);
        let size = vec2(540.0, 600.0);
        let pos = self.centered(size);
        for d in &mut drones {
            let builder = ViewportBuilder::default()
                .with_title("Quad Backup - new drone")
                .with_inner_size(size)
                .with_position(pos)
                .with_resizable(false)
                .with_maximize_button(false)
                .with_minimize_button(false)
                .with_always_on_top()
                .with_icon(self.icon.clone());
            ctx.show_viewport_immediate(ViewportId::from_hash_of(("drone", d.id)), builder, |ctx, _| new_drone_ui(ctx, d, now));
        }
        drones.retain(|d| d.close_at.map_or(true, |t| now < t));
        self.drones = drones;
    }

    // ---------------------------------------------------------------- dialogs

    fn show_dialogs(&mut self, ctx: &egui::Context) {
        let mut dialogs = std::mem::take(&mut self.dialogs);
        let mut finished: Vec<(u64, bool, Option<String>)> = Vec::new();
        for d in &mut dialogs {
            let h = 170.0 + if d.input.is_some() { 56.0 } else { 0.0 } + (d.message.len() as f32 / 52.0).ceil() * 18.0;
            let builder = ViewportBuilder::default()
                .with_title(d.title.clone())
                .with_inner_size([460.0, h])
                .with_position(self.centered(vec2(460.0, h)))
                .with_resizable(false)
                .with_maximize_button(false)
                .with_minimize_button(false)
                .with_always_on_top()
                .with_icon(self.icon.clone());
            ctx.show_viewport_immediate(ViewportId::from_hash_of(("dialog", d.id)), builder, |ctx, _| {
                if let Some(ok) = dialog_ui(ctx, d) {
                    finished.push((d.id, ok, d.input.clone()));
                }
            });
        }
        for (id, ok, input) in finished {
            if let Some(i) = dialogs.iter().position(|d| d.id == id) {
                let d = dialogs.remove(i);
                if ok {
                    self.dialog_done(d.action, input.unwrap_or_default());
                }
            }
        }
        dialogs.extend(std::mem::take(&mut self.dialogs));
        self.dialogs = dialogs;
    }

    fn dialog_done(&mut self, action: DialogAction, input: String) {
        let input = input.trim().to_string();
        match action {
            DialogAction::Alert => {}
            DialogAction::Special { port } => self.engine.queue_special(port, if input.is_empty() { "special".into() } else { input }),
            DialogAction::Forget { fc_id } => store::DroneDb::forget(&fc_id),
            DialogAction::ClearLog => {
                let _ = std::fs::write(store::log_path(), "");
            }
            DialogAction::Rename { fc_id } => {
                if let Err(e) = super::main_window::rename_drone(&fc_id, &input) {
                    self.open_dialog(Dialog::alert("Hmm, that didn't work", &e));
                }
            }
        }
        self.main.refresh();
    }

    // ---------------------------------------------------------------- pop-ups

    fn show_toasts(&mut self, ctx: &egui::Context, now: f64) {
        if self.toasts.is_empty() {
            return;
        }
        let area = self.work_area_points();
        let mut toasts = std::mem::take(&mut self.toasts);
        // newest nearest the corner: stacking down from the top-right on a Mac, up from the bottom-right elsewhere
        let mut y = if platform::TOASTS_FROM_TOP { area.top() + 8.0 } else { area.bottom() };
        for t in toasts.iter_mut().rev() {
            let target = if platform::TOASTS_FROM_TOP { let at = y; y += t.height + 12.0; at } else { y -= t.height + 12.0; y };
            if t.y.is_nan() {
                t.y = target + if platform::TOASTS_FROM_TOP { -24.0 } else { 24.0 };
            }
            t.y += (target - t.y) * 0.25;
        }
        let mut clicked_folder = None;
        for t in &mut toasts {
            if t.born < 0.0 {
                t.born = now;
            }
            let pos = pos2(area.right() - TOAST_W - 14.0, t.y);
            let builder = ViewportBuilder::default()
                .with_title("Quad Backup")
                .with_inner_size([TOAST_W, t.height])
                .with_position(pos)
                .with_decorations(false)
                .with_resizable(false)
                .with_always_on_top()
                .with_taskbar(false)
                .with_active(false);
            let id = ViewportId::from_hash_of(("toast", t.id));
            ctx.show_viewport_immediate(id, builder, |ctx, _| {
                ctx.send_viewport_cmd(ViewportCommand::OuterPosition(pos));
                if let Some(folder) = toast_ui(ctx, t, now) {
                    clicked_folder = Some(folder);
                }
            });
        }
        toasts.retain(|t| t.closing_at.is_none());
        if let Some(f) = clicked_folder {
            platform::open_folder(&f);
        }
        toasts.extend(std::mem::take(&mut self.toasts));
        self.toasts = toasts;
    }
}

fn toast_ui(ctx: &egui::Context, t: &mut Toast, now: f64) -> Option<String> {
    let mut open_folder = None;
    let accent = accent(t.kind);
    egui::CentralPanel::default().frame(egui::Frame::new().fill(PANEL).stroke(egui::Stroke::new(1.0_f32, LINE))).show(ctx, |ui| {
        let full = ui.max_rect();
        let resp = ui.interact(full, ui.id().with("toast"), Sense::click());
        let hovering = resp.hovered();
        if !hovering && t.closing_at.is_none() {
            t.lived += ctx.input(|i| i.stable_dt).min(0.1);
        }
        if t.lived >= TOAST_LIFE && t.closing_at.is_none() {
            t.closing_at = Some(now);
        }
        let p = ui.painter();
        let tsec = (now - t.born) as f32;
        let c = pos2(full.left() + 46.0, full.top() + (full.height() / 2.0).min(46.0));
        p.circle(c, 30.0, alpha(accent, 40), egui::Stroke::new(1.5_f32, alpha(accent, 140)));
        let (tilt, bob) = match t.kind {
            ToastKind::Success => ((tsec * 3.0).sin() * 8.0, -(tsec * 4.0).sin().abs() * 3.0),
            ToastKind::Error => (22.0 + (tsec * 11.0).sin() * 5.0, 0.0),
            ToastKind::Warning => ((tsec * 5.0).sin() * 12.0, 0.0),
            ToastKind::Special => ((tsec * 140.0) % 360.0, -(tsec * 3.0).sin() * 3.0),
            ToastKind::Info => ((tsec * 1.6).sin() * 5.0, 0.0),
        };
        let pose = Pose { prop_angle: tsec * 600.0, tilt, blink: art::blink_at(tsec, 1.6, 3.3), face: true, dizzy: t.kind == ToastKind::Error };
        art::quad(p, c + vec2(0.0, bob), 50.0, accent, &pose);
        if t.kind == ToastKind::Special {
            art::sparkles(p, c, 30.0, tsec);
        }
        let x = full.left() + 92.0;
        let w = full.width() - 92.0 - 34.0;
        p.text(pos2(x, full.top() + 16.0), Align2::LEFT_TOP, &t.title, bold(14.0), TEXT);
        let body = p.layout(t.text.clone(), regular(12.5), MUTED, w);
        let body_h = body.size().y;
        p.galley(pos2(x, full.top() + 42.0), body, MUTED);
        t.height = (16.0 + 26.0 + body_h + 22.0).max(92.0);
        // close "x" and the time-left fuse
        let xr = Rect::from_center_size(pos2(full.right() - 20.0, full.top() + 20.0), vec2(20.0, 20.0));
        let x_hot = ui.rect_contains_pointer(xr);
        let k = 4.5;
        let stroke = egui::Stroke::new(1.6_f32, if x_hot { TEXT } else { MUTED });
        p.line_segment([xr.center() + vec2(-k, -k), xr.center() + vec2(k, k)], stroke);
        p.line_segment([xr.center() + vec2(k, -k), xr.center() + vec2(-k, k)], stroke);
        let left = if t.closing_at.is_some() { 0.0 } else { 1.0 - (t.lived / TOAST_LIFE).min(1.0) };
        p.rect_filled(Rect::from_min_size(pos2(full.left(), full.bottom() - 3.0), vec2(full.width() * left, 3.0)), 0.0, accent);
        if resp.clicked() {
            if !x_hot {
                open_folder = t.folder.clone();
            }
            t.closing_at = Some(now);
        }
        if t.folder.is_some() {
            resp.on_hover_cursor(egui::CursorIcon::PointingHand);
        }
    });
    open_folder
}

fn new_drone_ui(ctx: &egui::Context, d: &mut NewDrone, now: f64) {
    if ctx.input(|i| i.viewport().close_requested()) && d.close_at.is_none() {
        d.answer(None);
        d.close_at = Some(now);
    }
    if !d.focused {
        ctx.send_viewport_cmd(ViewportCommand::Focus);
        d.focused = true;
    }
    egui::CentralPanel::default().frame(egui::Frame::new().fill(BG)).show(ctx, |ui| {
        let full = ui.max_rect();
        let t = now as f32;
        // header: sky + mascot (does a flip when celebrating) + confetti
        let header = Rect::from_min_size(full.min, vec2(full.width(), 178.0));
        let p = ui.painter_at(full);
        d.sky.paint(&p, header, t);
        let (mut bob, mut tilt) = ((t * 2.2).sin() * 4.0, (t * 1.4).sin() * 5.0);
        if let Some(c) = d.celebrate_at {
            let k = ((now - c) as f32 / 0.75).min(1.0);
            tilt = (1.0 - (1.0 - k).powi(3)) * 360.0;
            bob = -(k * std::f32::consts::PI).sin() * 30.0;
        }
        let mc = pos2(header.center().x, header.top() + 90.0 + bob);
        let shadow = 37.0 + bob * 0.6;
        art::glow(&p, pos2(header.center().x, header.top() + 162.0), shadow, 5.0, alpha(INK, 140));
        let pose = Pose { prop_angle: t * if d.celebrate_at.is_some() { 1200.0 } else { 690.0 }, tilt, blink: if d.celebrate_at.is_some() { 0.0 } else { art::blink_at(t, 1.8, 3.7) }, face: true, dizzy: false };
        art::quad(&p, mc, 122.0, SKY, &pose);

        ui.add_space(178.0 + 10.0);
        ui.vertical_centered(|ui| {
            ui.label(egui::RichText::new(&d.headline).font(black(25.0)).color(TEXT));
            ui.add_space(4.0);
            ui.label(egui::RichText::new(&d.subline).font(regular(14.0)).color(MUTED));
            ui.add_space(10.0);
        });
        // board / firmware / port chips, centred
        let b = &d.request.backup;
        let chips = [(AMBER, b.board.clone().unwrap_or_else(|| "unknown board".into())),
                     (MINT, b.firmware.clone().or(b.variant.clone()).unwrap_or_else(|| "firmware?".into())),
                     (SKY, d.request.port.clone())];
        let total: f32 = chips.iter().map(|(_, s)| text_width(ctx, s, semibold(12.0)) + 32.0).sum::<f32>() + 16.0;
        ui.horizontal(|ui| {
            ui.add_space(((ui.available_width() - total) / 2.0).max(0.0));
            ui.spacing_mut().item_spacing.x = 8.0;
            for (dot, s) in &chips {
                widgets::info_chip(ui, *dot, s);
            }
        });
        ui.add_space(12.0);

        let margin = 26.0;
        ui.horizontal(|ui| {
            ui.add_space(margin);
            ui.vertical(|ui| {
                ui.set_width(full.width() - margin * 2.0);
                if let Some(note) = &d.request.note {
                    egui::Frame::new().fill(alpha(AMBER, 28)).stroke(egui::Stroke::new(1.0_f32, alpha(AMBER, 110))).corner_radius(10.0).inner_margin(egui::Margin::symmetric(14, 10)).show(ui, |ui| {
                        ui.set_width(ui.available_width());
                        ui.label(egui::RichText::new(note).font(regular(12.5)).color(rgb(0xFFD27A)));
                    });
                    ui.add_space(10.0);
                }
                ui.label(egui::RichText::new("FOLDER NAME").font(bold(11.0)).color(MUTED));
                ui.add_space(2.0);
                let mut shake = 0.0;
                if let Some(s) = d.shake_at {
                    let k = ((now - s) as f32 / 0.42).min(1.0);
                    shake = (k * std::f32::consts::PI * 7.0).sin() * 9.0 * (1.0 - k);
                    if k >= 1.0 { d.shake_at = None; }
                }
                ui.horizontal(|ui| {
                    ui.add_space(shake.max(0.0));
                    let edit = egui::TextEdit::singleline(&mut d.name).font(semibold(15.0)).desired_width(full.width() - margin * 2.0 - shake.abs())
                        .margin(egui::Margin::symmetric(12, 9)).interactive(d.celebrate_at.is_none());
                    let r = ui.add(edit);
                    if !d.name_focused { r.request_focus(); d.name_focused = true; }
                });
                ui.add_space(6.0);
                ui.horizontal(|ui| {
                    ui.label(egui::RichText::new(format!("in  {}", d.location)).font(regular(12.5)).color(MUTED));
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        if widgets::link(ui, "change", SKY, regular(12.5)).clicked() {
                            if let Some(f) = rfd::FileDialog::new().set_title("Where should this drone's backup folder be created?").set_directory(&d.location).pick_folder() {
                                d.location = f.to_string_lossy().into();
                            }
                        }
                    });
                });
                ui.add_space(4.0);
                let preview = format!("saves as {}_{}.txt", store::safe_name(d.name.trim()), chrono::Local::now().format("%Y-%m-%d_%H-%M-%S"));
                ui.label(egui::RichText::new(preview).font(mono(12.0)).color(MINT));
                ui.add_space(14.0);

                let enabled = d.celebrate_at.is_none();
                let mut choice: Option<NewDroneChoice> = None;
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    ui.spacing_mut().item_spacing.x = 12.0;
                    let enter = ctx.input(|i| i.key_pressed(egui::Key::Enter));
                    if widgets::pill(ui, "Give it a home!", Pill::Primary, 176.0, 44.0, enabled).clicked() || (enter && enabled) {
                        let name = d.name.trim().to_string();
                        if name.is_empty() {
                            d.shake_at = Some(now);
                        } else {
                            choice = Some(NewDroneChoice::Folder(Path::new(&d.location).join(store::safe_name(&name)).to_string_lossy().into()));
                        }
                    }
                    if widgets::pill(ui, "Use existing folder\u{2026}", Pill::Secondary, 196.0, 44.0, enabled).clicked() {
                        if let Some(f) = rfd::FileDialog::new().set_title("Pick the folder to save this drone's backups in").set_directory(&d.location).pick_folder() {
                            choice = Some(NewDroneChoice::Folder(f.to_string_lossy().into()));
                        }
                    }
                });
                if let Some(NewDroneChoice::Folder(folder)) = &choice {
                    // the quad does a happy flip, confetti flies, then the window closes
                    let leaf = Path::new(folder.trim_end_matches(['\\', '/'])).file_name().map(|n| n.to_string_lossy().to_string()).unwrap_or_default();
                    d.headline = format!("Welcome home, {leaf}!");
                    d.subline = "First backup going in now. Every plug-in adds another.".into();
                    d.celebrate_at = Some(now);
                    d.close_at = Some(now + 1.25);
                    d.confetti.burst(pos2(header.center().x, header.top() + 90.0), 110);
                    d.answer(choice.clone());
                }
                ui.add_space(14.0);
                ui.horizontal(|ui| {
                    let not_now = "not now";
                    let never = "never back up this quad";
                    let w = text_width(ctx, not_now, regular(12.5)) + text_width(ctx, never, regular(12.5)) + 40.0;
                    ui.add_space(((ui.available_width() - w) / 2.0).max(0.0));
                    if enabled && widgets::link(ui, not_now, MUTED, regular(12.5)).clicked() {
                        d.answer(None);
                        d.close_at = Some(now);
                    }
                    ui.label(egui::RichText::new("\u{00B7}").font(regular(12.5)).color(MUTED));
                    if enabled && widgets::link(ui, never, MUTED, regular(12.5)).clicked() {
                        d.answer(Some(NewDroneChoice::Never));
                        d.close_at = Some(now);
                    }
                });
                if ctx.input(|i| i.key_pressed(egui::Key::Escape)) && enabled {
                    d.answer(None);
                    d.close_at = Some(now);
                }
            });
        });
        d.confetti.step(full.bottom());
        d.confetti.paint(&ui.painter_at(full));
    });
}

/// Returns Some(true) for OK, Some(false) for cancel/close, None while still open.
fn dialog_ui(ctx: &egui::Context, d: &mut Dialog) -> Option<bool> {
    let mut result = None;
    if ctx.input(|i| i.viewport().close_requested()) {
        result = Some(false);
    }
    egui::CentralPanel::default().frame(egui::Frame::new().fill(PANEL).inner_margin(egui::Margin::symmetric(22, 18))).show(ctx, |ui| {
        let t = ctx.input(|i| i.time) as f32;
        ui.horizontal(|ui| {
            let (r, _) = ui.allocate_exact_size(vec2(70.0, 70.0), Sense::hover());
            let body = if d.special { GOLD } else if d.danger { PINK } else { SKY };
            let pose = Pose { prop_angle: t * 660.0, tilt: if d.danger { 14.0 } else { -6.0 }, blink: 0.0, face: true, dizzy: false };
            art::quad(ui.painter(), r.center(), 58.0, body, &pose);
            if d.special {
                art::sparkles(ui.painter(), r.center(), 30.0, t);
            }
            ui.vertical(|ui| {
                ui.label(egui::RichText::new(&d.title).font(black(18.0)).color(TEXT));
                ui.add(egui::Label::new(egui::RichText::new(&d.message).font(regular(13.5)).color(MUTED)).wrap());
            });
        });
        ui.add_space(14.0);
        if let Some(input) = &mut d.input {
            let r = ui.add(egui::TextEdit::singleline(input).font(semibold(15.0)).desired_width(ui.available_width()).margin(egui::Margin::symmetric(12, 9)));
            if !d.focused {
                r.request_focus();
                d.focused = true;
            }
            ui.add_space(14.0);
        }
        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
            ui.spacing_mut().item_spacing.x = 10.0;
            let kind = if d.special { Pill::Special } else { Pill::Primary };
            if widgets::pill(ui, &d.ok_text, kind, 140.0, 42.0, true).clicked() || ctx.input(|i| i.key_pressed(egui::Key::Enter)) {
                result = Some(true);
            }
            if d.cancel && widgets::pill(ui, "Cancel", Pill::Secondary, 112.0, 42.0, true).clicked() {
                result = Some(false);
            }
        });
        if ctx.input(|i| i.key_pressed(egui::Key::Escape)) {
            result = Some(false);
        }
    });
    result
}
