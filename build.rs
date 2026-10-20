// Embeds the classic quad icon into QuadBackup.exe.
fn main() {
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows") {
        let mut res = winresource::WindowsResource::new();
        res.set_icon("assets/quad.ico");
        res.set("FileDescription", "Quad Backup");
        res.set("ProductName", "Quad Backup");
        if let Err(e) = res.compile() {
            // a missing resource compiler only costs us the exe icon
            println!("cargo:warning=couldn't embed the icon: {e}");
        }
    }
}
