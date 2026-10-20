#!/usr/bin/env bash
# Builds the Linux version of Quad Backup (Rust) and packs it into dist/QuadBackup-rust-linux-x64.tar.gz.
# Works on any x86_64 Linux (or WSL) with a C compiler; installs Rust with rustup if it's missing.
# Builds in a copy under ~/qb-build, so a source folder on a Windows drive doesn't slow it down.
set -euo pipefail

SRC="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$HOME/qb-build"
OUT="$SRC/dist"

if ! command -v cargo >/dev/null 2>&1; then
    [ -x "$HOME/.cargo/bin/cargo" ] || curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y --profile minimal
    export PATH="$HOME/.cargo/bin:$PATH"
fi

mkdir -p "$WORK"
rm -rf "$WORK/src" "$WORK/assets"
cp -r "$SRC/src" "$SRC/assets" "$SRC/Cargo.toml" "$SRC/Cargo.lock" "$SRC/build.rs" "$WORK/"
cd "$WORK"
cargo build --release
if [ "${1:-}" = "--test" ]; then
    cargo test --release
fi

PKG="$WORK/pkg/QuadBackup-rust-linux-x64"
rm -rf "$WORK/pkg"
mkdir -p "$PKG"
cp target/release/QuadBackup "$PKG/"
chmod 755 "$PKG/QuadBackup"
cp "$SRC/linux/README.txt" "$PKG/"
chmod 644 "$PKG/README.txt"
mkdir -p "$OUT"
tar -C "$WORK/pkg" -czf "$OUT/QuadBackup-rust-linux-x64.tar.gz" QuadBackup-rust-linux-x64
echo "Built $OUT/QuadBackup-rust-linux-x64.tar.gz"
