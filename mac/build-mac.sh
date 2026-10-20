#!/usr/bin/env bash
# Builds "Quad Backup Rust.app" (a universal binary for Apple Silicon and Intel) into dist/.
# Run it on a Mac from the unzipped source folder:   bash mac/build-mac.sh
# Needs Apple's command line tools (it offers to install them) and installs Rust with rustup if it's missing.
set -euo pipefail

SRC="$(cd "$(dirname "$0")/.." && pwd)"
cd "$SRC"
APP_NAME="Quad Backup Rust"
VERSION="$(sed -n 's/^version = "\(.*\)"/\1/p' Cargo.toml | head -1)"

if ! xcode-select -p >/dev/null 2>&1; then
    echo "Apple's command line tools are needed to build. Starting their installer; run this script again when it's done."
    xcode-select --install || true
    exit 1
fi
if ! command -v cargo >/dev/null 2>&1; then
    [ -x "$HOME/.cargo/bin/cargo" ] || curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y --profile minimal
    export PATH="$HOME/.cargo/bin:$PATH"
fi
rustup target add aarch64-apple-darwin x86_64-apple-darwin

export MACOSX_DEPLOYMENT_TARGET=11.0
cargo build --release --target aarch64-apple-darwin
cargo build --release --target x86_64-apple-darwin
if [ "${1:-}" = "--test" ]; then
    cargo test --release
fi

APP="dist/$APP_NAME.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
lipo -create -output "$APP/Contents/MacOS/QuadBackup" \
    target/aarch64-apple-darwin/release/QuadBackup target/x86_64-apple-darwin/release/QuadBackup
cp mac/QuadBackup.icns "$APP/Contents/Resources/"
cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>$APP_NAME</string>
  <key>CFBundleDisplayName</key><string>$APP_NAME</string>
  <key>CFBundleIdentifier</key><string>com.quadbackup.rust</string>
  <key>CFBundleExecutable</key><string>QuadBackup</string>
  <key>CFBundleIconFile</key><string>QuadBackup.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
EOF
# an ad-hoc signature is enough for an app built on this Mac
codesign --force --deep -s - "$APP"
(cd dist && rm -f "QuadBackup-rust-mac.zip" && ditto -c -k --keepParent "$APP_NAME.app" "QuadBackup-rust-mac.zip")

echo
echo "Built $SRC/$APP"
echo "Drag it into Applications and open it. It lives in the menu bar (no Dock icon)."
