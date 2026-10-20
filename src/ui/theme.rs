//! Colours, fonts and small text helpers, matching the C# version's look.

use eframe::egui::{self, Color32, FontData, FontDefinitions, FontFamily, FontId};
use std::sync::Arc;

pub const BG: Color32 = rgb(0x15172B);
pub const PANEL: Color32 = rgb(0x1F2244);
pub const PANEL_HI: Color32 = rgb(0x2A2E5A);
pub const LINE: Color32 = rgb(0x3A3F78);
pub const INK: Color32 = rgb(0x0E1024);
pub const TEXT: Color32 = rgb(0xF5F2FF);
pub const MUTED: Color32 = rgb(0xA0A5D0);
pub const AMBER: Color32 = rgb(0xFFB627);
pub const PINK: Color32 = rgb(0xFF5C8A);
pub const CORAL: Color32 = rgb(0xFF8A5C);
pub const MINT: Color32 = rgb(0x3DDC97);
pub const SKY: Color32 = rgb(0x5AC8FA);
pub const VIOLET: Color32 = rgb(0x8B7CFF);
pub const GOLD: Color32 = rgb(0xFFC93C);

pub const fn rgb(v: u32) -> Color32 {
    Color32::from_rgb((v >> 16) as u8, (v >> 8) as u8, v as u8)
}

pub fn alpha(c: Color32, a: u8) -> Color32 {
    Color32::from_rgba_unmultiplied(c.r(), c.g(), c.b(), a)
}

pub fn mix(a: Color32, b: Color32, t: f32) -> Color32 {
    let t = t.clamp(0.0, 1.0);
    let l = |x: u8, y: u8| (x as f32 + (y as f32 - x as f32) * t) as u8;
    Color32::from_rgb(l(a.r(), b.r()), l(a.g(), b.g()), l(a.b(), b.b()))
}

pub fn lighten(c: Color32, t: f32) -> Color32 { mix(c, Color32::WHITE, t) }

// font families: regular text, semibold, bold, black (headlines), mono
pub fn regular(size: f32) -> FontId { FontId::new(size, FontFamily::Proportional) }
pub fn semibold(size: f32) -> FontId { FontId::new(size, FontFamily::Name("semibold".into())) }
pub fn bold(size: f32) -> FontId { FontId::new(size, FontFamily::Name("bold".into())) }
pub fn black(size: f32) -> FontId { FontId::new(size, FontFamily::Name("black".into())) }
pub fn mono(size: f32) -> FontId { FontId::new(size, FontFamily::Monospace) }

/// Segoe UI from Windows' own fonts (with egui's built-in font as a fallback), plus Segoe UI Symbol for ★ ✓ ✕.
pub fn install_fonts(ctx: &egui::Context) {
    let mut fonts = FontDefinitions::default();
    // the first file of each list that exists wins; a missing weight falls back to the one below it, then egui's own
    let mut load = |key: &str, files: &[String]| -> Option<String> {
        let bytes = files.iter().find_map(|f| std::fs::read(f).ok())?;
        fonts.font_data.insert(key.to_string(), Arc::new(FontData::from_owned(bytes)));
        Some(key.to_string())
    };
    let [r, sb, b, bl, mo, sy] = font_files();
    let regular = load("regular", &r);
    let semibold = load("semibold", &sb);
    let bold = load("bold", &b);
    let black = load("black", &bl);
    let mono = load("mono", &mo);
    let symbols = load("symbols", &sy);

    let builtin = fonts.families.get(&FontFamily::Proportional).cloned().unwrap_or_default();
    let family = |first: &[Option<String>]| -> Vec<String> {
        let mut v: Vec<String> = first.iter().flatten().cloned().collect();
        v.extend(symbols.iter().cloned());
        v.extend(builtin.iter().cloned());
        v
    };
    fonts.families.insert(FontFamily::Proportional, family(&[regular.clone()]));
    fonts.families.insert(FontFamily::Name("semibold".into()), family(&[semibold, regular.clone()]));
    fonts.families.insert(FontFamily::Name("bold".into()), family(&[bold.clone(), regular.clone()]));
    fonts.families.insert(FontFamily::Name("black".into()), family(&[black, bold, regular]));
    let mut m: Vec<String> = mono.into_iter().collect();
    m.extend(fonts.families.get(&FontFamily::Monospace).cloned().unwrap_or_default());
    fonts.families.insert(FontFamily::Monospace, m);
    ctx.set_fonts(fonts);
}

/// Candidate files for regular, semibold, bold, black, monospace and symbols.
fn font_files() -> [Vec<String>; 6] {
    let list = |dir: &str, names: &[&str]| -> Vec<String> { names.iter().map(|n| format!("{dir}/{n}")).collect() };
    if cfg!(windows) {
        let win = std::env::var("WINDIR").unwrap_or_else(|_| "C:\\Windows".into());
        let d = format!("{win}\\Fonts");
        [list(&d, &["segoeui.ttf"]), list(&d, &["seguisb.ttf"]), list(&d, &["segoeuib.ttf"]), list(&d, &["seguibl.ttf"]), list(&d, &["consola.ttf"]), list(&d, &["seguisym.ttf"])]
    } else if cfg!(target_os = "macos") {
        let s = "/System/Library/Fonts/Supplemental";
        let l = "/Library/Fonts";
        let both = |n: &[&str]| -> Vec<String> { [list(s, n), list(l, n)].concat() };
        [
            both(&["Arial.ttf"]),
            both(&["Arial Bold.ttf"]),
            both(&["Arial Bold.ttf"]),
            both(&["Arial Black.ttf"]),
            [list("/System/Library/Fonts", &["Menlo.ttc"]), both(&["Courier New.ttf"])].concat(),
            list("/System/Library/Fonts", &["Apple Symbols.ttf"]),
        ]
    } else {
        // the common spots on Debian/Ubuntu, Fedora and Arch
        let noto = ["/usr/share/fonts/truetype/noto", "/usr/share/fonts/noto", "/usr/share/fonts/google-noto"];
        let dejavu = ["/usr/share/fonts/truetype/dejavu", "/usr/share/fonts/dejavu", "/usr/share/fonts/TTF", "/usr/share/fonts/dejavu-sans-fonts"];
        let many = |dirs: &[&str], n: &[&str]| -> Vec<String> { dirs.iter().flat_map(|d| list(d, n)).collect() };
        [
            [many(&noto, &["NotoSans-Regular.ttf"]), many(&dejavu, &["DejaVuSans.ttf"])].concat(),
            [many(&noto, &["NotoSans-SemiBold.ttf", "NotoSans-Medium.ttf"]), many(&dejavu, &["DejaVuSans-Bold.ttf"])].concat(),
            [many(&noto, &["NotoSans-Bold.ttf"]), many(&dejavu, &["DejaVuSans-Bold.ttf"])].concat(),
            [many(&noto, &["NotoSans-Black.ttf", "NotoSans-ExtraBold.ttf"]), many(&dejavu, &["DejaVuSans-Bold.ttf"])].concat(),
            [many(&noto, &["NotoSansMono-Regular.ttf"]), many(&dejavu, &["DejaVuSansMono.ttf"])].concat(),
            [many(&noto, &["NotoSansSymbols2-Regular.ttf"]), many(&dejavu, &["DejaVuSans.ttf"])].concat(),
        ]
    }
}

pub fn install_visuals(ctx: &egui::Context) {
    let mut v = egui::Visuals::dark();
    v.panel_fill = BG;
    v.window_fill = PANEL;
    v.extreme_bg_color = BG; // text edit background
    v.selection.bg_fill = alpha(VIOLET, 140);
    v.selection.stroke = egui::Stroke::new(1.0_f32, TEXT);
    v.widgets.inactive.bg_stroke = egui::Stroke::new(1.2_f32, LINE);
    v.widgets.hovered.bg_stroke = egui::Stroke::new(1.5_f32, VIOLET);
    v.widgets.active.bg_stroke = egui::Stroke::new(1.8_f32, VIOLET);
    v.text_cursor.stroke = egui::Stroke::new(2.0_f32, TEXT);
    ctx.set_visuals(v);
}

/// Width of a piece of text in this font.
pub fn text_width(ctx: &egui::Context, text: &str, font: FontId) -> f32 {
    ctx.fonts(|f| f.layout_no_wrap(text.to_string(), font, Color32::WHITE).size().x)
}
