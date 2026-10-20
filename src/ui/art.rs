//! Drawing: the quad mascot, sparkles, starry sky, confetti, and the classic tray/window icon.

use super::theme::*;
use eframe::egui::{self, epaint, pos2, vec2, Color32, Mesh, Painter, Pos2, Rect, Shape, Stroke};
use std::f32::consts::PI;

/// Maps the mascot's 100x100 design space onto the screen: scale, then rotate, then move.
#[derive(Clone, Copy)]
struct Xf {
    c: Pos2,
    s: f32,
    cos: f32,
    sin: f32,
}

impl Xf {
    fn new(c: Pos2, size: f32, tilt_deg: f32) -> Xf {
        let a = tilt_deg.to_radians();
        Xf { c, s: size / 100.0, cos: a.cos(), sin: a.sin() }
    }
    fn p(&self, x: f32, y: f32) -> Pos2 {
        let (x, y) = (x * self.s, y * self.s);
        pos2(self.c.x + x * self.cos - y * self.sin, self.c.y + x * self.sin + y * self.cos)
    }
    fn w(&self, v: f32) -> f32 { v * self.s }
}

fn thick_line(p: &Painter, a: Pos2, b: Pos2, width: f32, color: Color32) {
    p.line_segment([a, b], Stroke::new(width, color));
    p.circle_filled(a, width / 2.0, color);
    p.circle_filled(b, width / 2.0, color);
}

/// An ellipse (centre cx,cy, radii rx,ry, rotated by `rot` degrees) in mascot space.
fn ellipse(p: &Painter, xf: &Xf, cx: f32, cy: f32, rx: f32, ry: f32, rot: f32, fill: Color32) {
    let (s, c) = rot.to_radians().sin_cos();
    let pts: Vec<Pos2> = (0..36)
        .map(|i| {
            let t = i as f32 / 36.0 * 2.0 * PI;
            let (x, y) = (rx * t.cos(), ry * t.sin());
            xf.p(cx + x * c - y * s, cy + x * s + y * c)
        })
        .collect();
    p.add(Shape::convex_polygon(pts, fill, Stroke::NONE));
}

fn rounded_rect_points(xf: &Xf, x: f32, y: f32, w: f32, h: f32, r: f32) -> Vec<Pos2> {
    let mut pts = Vec::new();
    let corners = [(x + w - r, y + r, -90.0f32), (x + w - r, y + h - r, 0.0), (x + r, y + h - r, 90.0), (x + r, y + r, 180.0)];
    for (cx, cy, start) in corners {
        for i in 0..=8 {
            let a = (start + i as f32 * 90.0 / 8.0).to_radians();
            pts.push(xf.p(cx + r * a.cos(), cy + r * a.sin()));
        }
    }
    pts
}

fn bezier(p: &Painter, pts: [Pos2; 4], stroke: Stroke) {
    p.add(Shape::CubicBezier(epaint::CubicBezierShape::from_points_stroke(pts, false, Color32::TRANSPARENT, stroke)));
}

fn quad_bezier(p: &Painter, pts: [Pos2; 3], stroke: Stroke) {
    p.add(Shape::QuadraticBezier(epaint::QuadraticBezierShape::from_points_stroke(pts, false, Color32::TRANSPARENT, stroke)));
}

pub struct Pose {
    pub prop_angle: f32,
    pub tilt: f32,
    pub blink: f32,
    pub face: bool,
    pub dizzy: bool,
}

/// The quad with a face: top-down, four spinning props, an antenna with a bobble on top.
pub fn quad(p: &Painter, c: Pos2, size: f32, body: Color32, pose: &Pose) {
    let xf = Xf::new(c, size, pose.tilt);
    for (a, b) in [((-32.0, -32.0), (32.0, 32.0)), ((32.0, -32.0), (-32.0, 32.0))] {
        thick_line(p, xf.p(a.0, a.1), xf.p(b.0, b.1), xf.w(13.0), INK);
    }
    for (a, b) in [((-32.0, -32.0), (32.0, 32.0)), ((32.0, -32.0), (-32.0, 32.0))] {
        thick_line(p, xf.p(a.0, a.1), xf.p(b.0, b.1), xf.w(7.5), rgb(0x565CA8));
    }
    let motors = [(-32.0f32, -32.0f32), (32.0, -32.0), (32.0, 32.0), (-32.0, 32.0)];
    for (i, (mx, my)) in motors.iter().enumerate() {
        p.circle_filled(xf.p(*mx, *my), xf.w(20.0), alpha(AMBER, 60));
        let rot = if i % 2 == 0 { 1.0 } else { -1.0 } * pose.prop_angle + i as f32 * 45.0;
        ellipse(p, &xf, *mx, *my, 19.0, 3.6, rot, AMBER);
        p.circle_filled(xf.p(*mx, *my), xf.w(5.5), INK);
        p.circle_filled(xf.p(*mx, *my), xf.w(2.0), rgb(0xFFE08A));
    }
    // antenna
    bezier(p, [xf.p(7.0, -15.0), xf.p(9.0, -24.0), xf.p(15.0, -27.0), xf.p(17.0, -35.0)], Stroke::new(xf.w(2.4), MUTED));
    p.circle_filled(xf.p(17.0, -35.5), xf.w(3.5), PINK);
    // body: rounded square, lighter on top
    let outline = rounded_rect_points(&xf, -19.0, -16.0, 38.0, 32.0, 11.0);
    p.add(Shape::convex_polygon(outline.clone(), body, Stroke::new(xf.w(2.6), INK)));
    let top = rounded_rect_points(&xf, -16.5, -13.5, 33.0, 13.0, 8.0);
    p.add(Shape::convex_polygon(top, alpha(lighten(body, 0.35), 120), Stroke::NONE));

    if pose.face {
        let ink = Stroke::new(xf.w(2.2), INK);
        if pose.dizzy {
            for x in [-8.0f32, 8.0] {
                p.line_segment([xf.p(x - 3.0, -6.0), xf.p(x + 3.0, 0.0)], ink);
                p.line_segment([xf.p(x + 3.0, -6.0), xf.p(x - 3.0, 0.0)], ink);
            }
            bezier(p, [xf.p(-6.0, 7.0), xf.p(-3.0, 4.0), xf.p(3.0, 10.0), xf.p(6.0, 7.0)], ink);
        } else {
            let eye_h = 5.5 * (1.0 - 0.88 * pose.blink);
            for x in [-8.0f32, 8.0] {
                ellipse(p, &xf, x, -3.0, 4.5, eye_h, 0.0, Color32::WHITE);
                if pose.blink < 0.5 {
                    p.circle_filled(xf.p(x + 0.3, -0.7), xf.w(2.6), INK);
                    p.circle_filled(xf.p(x + 0.3, -1.9), xf.w(0.9), Color32::WHITE);
                }
            }
            quad_bezier(p, [xf.p(-4.8, 3.4), xf.p(0.0, 9.5), xf.p(4.8, 3.4)], ink);
        }
        ellipse(p, &xf, -14.0, 5.8, 3.0, 1.8, 0.0, alpha(PINK, 150));
        ellipse(p, &xf, 14.0, 5.8, 3.0, 1.8, 0.0, alpha(PINK, 150));
    }
}

/// Three twinkling four-point stars around a circle.
pub fn sparkles(p: &Painter, c: Pos2, r: f32, t: f32) {
    for (i, deg) in [-50.0f32, 35.0, 150.0].iter().enumerate() {
        let a = deg.to_radians();
        let twinkle = 0.55 + 0.45 * (t * 4.0 + i as f32 * 2.1).sin();
        let at = pos2(c.x + a.cos() * (r + 2.0), c.y + a.sin() * (r + 2.0));
        let z = (3.2 + i as f32 * 0.8) * twinkle;
        star(p, at, z, alpha(rgb(0xFFE58A), (255.0 * twinkle) as u8));
    }
}

pub fn star(p: &Painter, at: Pos2, z: f32, color: Color32) {
    let xf = Xf::new(at, 100.0, 0.0);
    ellipse(p, &xf, 0.0, 0.0, z * 2.0, z * 0.4, 0.0, color);
    ellipse(p, &xf, 0.0, 0.0, z * 0.4, z * 2.0, 0.0, color);
}

pub fn blink_at(t: f32, offset: f32, period: f32) -> f32 {
    let phase = (t + offset) % period;
    if phase < 0.14 { (phase / 0.14 * PI).sin() } else { 0.0 }
}

/// A vertical gradient rectangle.
pub fn gradient(p: &Painter, r: Rect, top: Color32, bottom: Color32) {
    let mut m = Mesh::default();
    m.colored_vertex(r.left_top(), top);
    m.colored_vertex(r.right_top(), top);
    m.colored_vertex(r.right_bottom(), bottom);
    m.colored_vertex(r.left_bottom(), bottom);
    m.add_triangle(0, 1, 2);
    m.add_triangle(0, 2, 3);
    p.add(Shape::mesh(m));
}

/// A soft round glow fading out to nothing.
pub fn glow(p: &Painter, c: Pos2, rx: f32, ry: f32, color: Color32) {
    let mut m = Mesh::default();
    m.colored_vertex(c, color);
    let edge = alpha(color, 0);
    for i in 0..=48 {
        let t = i as f32 / 48.0 * 2.0 * PI;
        m.colored_vertex(pos2(c.x + rx * t.cos(), c.y + ry * t.sin()), edge);
    }
    for i in 1..=48u32 {
        m.add_triangle(0, i, i + 1);
    }
    p.add(Shape::mesh(m));
}

pub struct Sky {
    stars: Vec<(f32, f32, f32, f32, f32)>, // x, y (0..1), size, speed, phase
}

impl Sky {
    pub fn new(seed: u64, count: usize) -> Sky {
        let mut s = seed.wrapping_mul(6364136223846793005).wrapping_add(1);
        let mut rnd = move || {
            s = s.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
            ((s >> 33) as f32) / (u32::MAX >> 1) as f32
        };
        let stars = (0..count).map(|_| (rnd(), rnd() * 0.85, 1.0 + rnd() * 2.4, 0.8 + rnd() * 2.2, rnd() * 6.28)).collect();
        Sky { stars }
    }

    pub fn paint(&self, p: &Painter, r: Rect, t: f32) {
        gradient(p, r, rgb(0x40308A), BG);
        glow(p, pos2(r.center().x, r.top() + 90.0), 150.0, 100.0, alpha(PINK, 75));
        for i in 0..3 {
            let travel = (i as f32 * 0.41 + t * 0.014 * (1.0 + i as f32 * 0.35)) % 1.4 - 0.2;
            cloud(p, pos2(r.left() + travel * r.width(), r.top() + r.height() * (0.62 + i as f32 * 0.12)), 60.0 + i as f32 * 18.0, 7 + i as u8 * 3);
        }
        for (x, y, size, speed, phase) in &self.stars {
            let a = (130.0 + 120.0 * (t * speed + phase).sin()).clamp(0.0, 255.0) as u8;
            let at = pos2(r.left() + x * r.width(), r.top() + y * r.height());
            if *size > 2.8 {
                star(p, at, size * 0.9, alpha(Color32::WHITE, a));
            } else {
                p.circle_filled(at, size / 2.0, alpha(Color32::WHITE, a));
            }
        }
    }
}

fn cloud(p: &Painter, c: Pos2, w: f32, a: u8) {
    let col = Color32::from_rgba_unmultiplied(255, 255, 255, a);
    let xf = Xf::new(c, 100.0, 0.0);
    ellipse(p, &xf, 0.0, 0.0, w / 2.0, w * 0.16, 0.0, col);
    ellipse(p, &xf, -w * 0.1, -w * 0.1, w * 0.2, w * 0.2, 0.0, col);
    ellipse(p, &xf, w * 0.14, -w * 0.08, w * 0.16, w * 0.16, 0.0, col);
}

#[derive(Default)]
pub struct Confetti {
    bits: Vec<(Pos2, egui::Vec2, f32, f32, f32, f32, Color32)>, // pos, vel, rot, spin, w, h, colour
    seed: u32,
}

impl Confetti {
    fn rnd(&mut self) -> f32 {
        self.seed = self.seed.wrapping_mul(1664525).wrapping_add(1013904223);
        (self.seed >> 8) as f32 / (1u32 << 24) as f32
    }

    pub fn burst(&mut self, at: Pos2, count: usize) {
        let colours = [PINK, AMBER, MINT, SKY, VIOLET, Color32::WHITE];
        self.seed ^= chrono::Local::now().timestamp_subsec_nanos();
        for _ in 0..count {
            let angle = self.rnd() * 2.0 * PI;
            let speed = 3.0 + self.rnd() * 8.0;
            let rot = self.rnd() * 360.0;
            let spin = (self.rnd() - 0.5) * 36.0;
            let (w, h) = (5.0 + self.rnd() * 6.0, 3.0 + self.rnd() * 3.0);
            let c = colours[(self.rnd() * 6.0) as usize % 6];
            self.bits.push((at, vec2(angle.cos() * speed, angle.sin() * speed - 7.0), rot, spin, w, h, c));
        }
    }

    pub fn step(&mut self, floor: f32) {
        for b in &mut self.bits {
            b.1.y += 0.38;
            b.1.x *= 0.985;
            b.0 += b.1;
            b.2 += b.3;
        }
        self.bits.retain(|b| b.0.y < floor + 30.0);
    }

    pub fn paint(&self, p: &Painter) {
        for (pos, _, rot, _, w, h, c) in &self.bits {
            let xf = Xf::new(*pos, 100.0, *rot);
            let pts = vec![xf.p(-w / 2.0, -h / 2.0), xf.p(w / 2.0, -h / 2.0), xf.p(w / 2.0, h / 2.0), xf.p(-w / 2.0, h / 2.0)];
            p.add(Shape::convex_polygon(pts, *c, Stroke::NONE));
        }
    }
}

/// The original logo (four amber props on a grey X, blue body) as RGBA pixels, for the tray and window icons.
pub fn classic_icon_rgba(px: u32) -> Vec<u8> {
    use tiny_skia::{FillRule, Paint, PathBuilder, Pixmap, Rect as SkRect, Stroke as SkStroke, Transform};
    let mut pm = Pixmap::new(px, px).unwrap();
    let s = px as f32 / 32.0;
    let (a, z, r) = (7.5 * s, 24.5 * s, 5.4 * s);
    let paint = |c: (u8, u8, u8)| {
        let mut p = Paint::default();
        p.set_color_rgba8(c.0, c.1, c.2, 255);
        p.anti_alias = true;
        p
    };
    let line = |pm: &mut Pixmap, w: f32, c: (u8, u8, u8)| {
        let mut pb = PathBuilder::new();
        pb.move_to(a, a);
        pb.line_to(z, z);
        pb.move_to(z, a);
        pb.line_to(a, z);
        let path = pb.finish().unwrap();
        pm.stroke_path(&path, &paint(c), &SkStroke { width: w, ..Default::default() }, Transform::identity(), None);
    };
    line(&mut pm, 5.4 * s, (40, 40, 40));
    line(&mut pm, 3.2 * s, (225, 225, 225));
    for (cx, cy) in [(a, a), (z, a), (a, z), (z, z)] {
        let circle = PathBuilder::from_circle(cx, cy, r).unwrap();
        pm.fill_path(&circle, &paint((255, 176, 0)), FillRule::Winding, Transform::identity(), None);
        pm.stroke_path(&circle, &paint((40, 40, 40)), &SkStroke { width: 1.4 * s, ..Default::default() }, Transform::identity(), None);
    }
    let body = SkRect::from_xywh(11.5 * s, 11.5 * s, 9.0 * s, 9.0 * s).unwrap();
    pm.fill_rect(body, &paint((30, 136, 229)), Transform::identity(), None);
    let outline = PathBuilder::from_rect(body);
    pm.stroke_path(&outline, &paint((40, 40, 40)), &SkStroke { width: 1.4 * s, ..Default::default() }, Transform::identity(), None);
    // tiny-skia stores premultiplied colour; icons want straight alpha
    pm.pixels().iter().flat_map(|p| { let c = p.demultiply(); [c.red(), c.green(), c.blue(), c.alpha()] }).collect()
}
