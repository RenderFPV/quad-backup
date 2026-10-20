//! Hand-drawn controls: pill buttons (with the shiny gold one), face toggles, segmented pickers, chips and links.

use super::theme::*;
use eframe::egui::{self, pos2, vec2, Align2, Color32, CursorIcon, Mesh, Rect, Response, Sense, Shape, Stroke, StrokeKind, Ui};

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum Pill {
    Primary,
    Secondary,
    Special,
}

/// A rounded "pill" fill that blends from `a` on the left to `b` on the right.
fn pill_gradient(p: &egui::Painter, r: Rect, a: Color32, b: Color32) {
    let rad = r.height() / 2.0;
    p.circle_filled(pos2(r.left() + rad, r.center().y), rad, a);
    p.circle_filled(pos2(r.right() - rad, r.center().y), rad, b);
    let mid = Rect::from_min_max(pos2(r.left() + rad, r.top()), pos2(r.right() - rad, r.bottom()));
    let mut m = Mesh::default();
    m.colored_vertex(mid.left_top(), a);
    m.colored_vertex(mid.right_top(), b);
    m.colored_vertex(mid.right_bottom(), b);
    m.colored_vertex(mid.left_bottom(), a);
    m.add_triangle(0, 1, 2);
    m.add_triangle(0, 2, 3);
    p.add(Shape::mesh(m));
}

pub fn pill(ui: &mut Ui, text: &str, kind: Pill, width: f32, height: f32, enabled: bool) -> Response {
    let (rect, resp) = ui.allocate_exact_size(vec2(width, height), if enabled { Sense::click() } else { Sense::hover() });
    let hover = enabled && resp.hovered();
    let down = enabled && resp.is_pointer_button_down_on();
    let lift = if down { 1.0 } else if hover { -1.0 } else { 0.0 };
    let r = Rect::from_min_size(rect.min + vec2(1.0, 2.0 + lift), vec2(width - 2.0, height - 4.0));
    let rad = r.height() / 2.0;
    let p = ui.painter();
    match kind {
        Pill::Secondary => {
            p.rect(r, rad, if hover { PANEL_HI } else { PANEL }, Stroke::new(1.5_f32, if hover { VIOLET } else { LINE }), StrokeKind::Inside);
        }
        Pill::Primary | Pill::Special => {
            let special = kind == Pill::Special;
            let shade = Rect::from_min_size(rect.min + vec2(1.0, 4.0), vec2(width - 2.0, height - 5.0));
            p.rect_filled(shade, rad, if special { rgb(0xB06A00) } else { rgb(0xA8325A) });
            let (a, b) = match (special, hover) {
                (true, false) => (rgb(0xFFD45C), rgb(0xFFA51F)),
                (true, true) => (rgb(0xFFE58A), rgb(0xFFBE55)),
                (false, false) => (PINK, CORAL),
                (false, true) => (rgb(0xFF7AA0), rgb(0xFFA478)),
            };
            if enabled { pill_gradient(p, r, a, b) } else { p.rect_filled(r, rad, PANEL_HI); }
            if special && enabled {
                // a diagonal glint sweeping across every couple of seconds
                let t = ui.input(|i| i.time) as f32;
                let gx = r.left() - 40.0 + (t % 2.4) / 2.4 * (r.width() + 120.0);
                let glint = vec![pos2(gx, r.bottom()), pos2(gx + 14.0, r.bottom()), pos2(gx + 34.0, r.top()), pos2(gx + 20.0, r.top())];
                p.with_clip_rect(r.shrink2(vec2(rad * 0.4, 0.0))).add(Shape::convex_polygon(glint, alpha(Color32::WHITE, 110), Stroke::NONE));
                // no repaint request here: App::update paces every animation, so this never spins a CPU core
            }
        }
    }
    if resp.has_focus() {
        p.rect_stroke(r.shrink(3.0), rad - 3.0, Stroke::new(1.2_f32, alpha(Color32::WHITE, 140)), StrokeKind::Inside);
    }
    let color = if !enabled { MUTED } else if kind == Pill::Special { INK } else if kind == Pill::Primary { Color32::WHITE } else { TEXT };
    p.text(r.center(), Align2::CENTER_CENTER, text, bold(13.5), color);
    if enabled { resp.on_hover_cursor(CursorIcon::PointingHand) } else { resp }
}

/// On/off switch whose knob is a tiny face: awake when on, asleep when off.
pub fn face_toggle(ui: &mut Ui, on: &mut bool) -> Response {
    let (rect, mut resp) = ui.allocate_exact_size(vec2(50.0, 28.0), Sense::click());
    if resp.clicked() {
        *on = !*on;
        resp.mark_changed();
    }
    let k = ui.ctx().animate_bool_with_time(resp.id, *on, 0.12);
    let p = ui.painter();
    let r = rect.shrink(1.0);
    p.rect_filled(r, r.height() / 2.0, mix(LINE, MINT, k));
    let d = r.height() - 6.0;
    let c = pos2(r.left() + 3.0 + k * (r.width() - d - 6.0) + d / 2.0, r.center().y);
    p.circle_filled(c, d / 2.0, Color32::WHITE);
    let ink = Stroke::new(1.3_f32, INK);
    if k > 0.5 {
        p.circle_filled(c + vec2(-2.7, -1.7), 1.3, INK);
        p.circle_filled(c + vec2(2.7, -1.7), 1.3, INK);
        p.add(Shape::QuadraticBezier(egui::epaint::QuadraticBezierShape::from_points_stroke(
            [c + vec2(-2.8, 1.2), c + vec2(0.0, 4.2), c + vec2(2.8, 1.2)], false, Color32::TRANSPARENT, ink)));
    } else {
        p.line_segment([c + vec2(-4.2, -1.2), c + vec2(-1.4, -1.2)], ink);
        p.line_segment([c + vec2(1.4, -1.2), c + vec2(4.2, -1.2)], ink);
    }
    resp.on_hover_cursor(CursorIcon::PointingHand)
}

/// A row of options, one selected. `changed()` on the response when the choice changes.
pub fn segmented(ui: &mut Ui, options: &[&str], selected: &mut usize, width: f32) -> Response {
    let (rect, mut resp) = ui.allocate_exact_size(vec2(width, 36.0), Sense::click());
    let w = rect.width() / options.len() as f32;
    let hover_index = resp.hover_pos().map(|pos| (((pos.x - rect.left()) / w) as usize).min(options.len() - 1));
    if resp.clicked() {
        if let Some(i) = hover_index {
            if i != *selected {
                *selected = i;
                resp.mark_changed();
            }
        }
    }
    let p = ui.painter();
    p.rect(rect.shrink(0.5), rect.height() / 2.0, PANEL, Stroke::new(1.2_f32, LINE), StrokeKind::Inside);
    for (i, opt) in options.iter().enumerate() {
        let seg = Rect::from_min_size(pos2(rect.left() + i as f32 * w, rect.top()), vec2(w, rect.height()));
        if i == *selected {
            let pill = seg.shrink(3.0);
            p.rect_filled(pill, pill.height() / 2.0, VIOLET);
        }
        let c = if i == *selected { Color32::WHITE } else if hover_index == Some(i) { TEXT } else { MUTED };
        p.text(seg.center(), Align2::CENTER_CENTER, *opt, bold(12.5), c);
    }
    resp.on_hover_cursor(CursorIcon::PointingHand)
}

pub fn chip_toggle(ui: &mut Ui, text: &str, selected: &mut bool) -> Response {
    let w = text_width(ui.ctx(), text, bold(12.5)) + 32.0;
    let (rect, mut resp) = ui.allocate_exact_size(vec2(w, 32.0), Sense::click());
    if resp.clicked() {
        *selected = !*selected;
        resp.mark_changed();
    }
    let fill = if *selected { alpha(VIOLET, 70) } else if resp.hovered() { PANEL_HI } else { PANEL };
    ui.painter().rect(rect.shrink(0.5), 16.0, fill, Stroke::new(1.3_f32, if *selected { VIOLET } else { LINE }), StrokeKind::Inside);
    ui.painter().text(rect.center(), Align2::CENTER_CENTER, text, bold(12.5), if *selected { TEXT } else { MUTED });
    resp.on_hover_cursor(CursorIcon::PointingHand)
}

/// Clickable text that brightens and underlines on hover.
pub fn link(ui: &mut Ui, text: &str, color: Color32, font: egui::FontId) -> Response {
    let galley = ui.painter().layout_no_wrap(text.to_string(), font.clone(), color);
    let (rect, resp) = ui.allocate_exact_size(galley.size() + vec2(0.0, 2.0), Sense::click());
    let hot = resp.hovered();
    let c = if hot { TEXT } else { color };
    ui.painter().text(rect.left_top(), Align2::LEFT_TOP, text, font, c);
    if hot {
        let y = rect.top() + galley.size().y;
        ui.painter().line_segment([pos2(rect.left(), y), pos2(rect.right(), y)], Stroke::new(1.0_f32, c));
    }
    resp.on_hover_cursor(CursorIcon::PointingHand)
}

/// A small coloured-dot label in a rounded pill (board / firmware / port on the new-drone window).
pub fn info_chip(ui: &mut Ui, dot: Color32, text: &str) {
    let w = text_width(ui.ctx(), text, semibold(12.0)) + 32.0;
    let (rect, _) = ui.allocate_exact_size(vec2(w, 26.0), Sense::hover());
    let p = ui.painter();
    p.rect(rect, 13.0, PANEL, Stroke::new(1.2_f32, LINE), StrokeKind::Inside);
    p.circle_filled(pos2(rect.left() + 14.0, rect.center().y), 3.5, dot);
    p.text(pos2(rect.left() + 24.0, rect.center().y), Align2::LEFT_CENTER, text, semibold(12.0), TEXT);
}
