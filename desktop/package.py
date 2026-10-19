"""Builds the downloadable Mac and Linux versions of Quad Backup into desktop/dist/.

    python package.py

Needs the .NET 8 SDK. Works on Windows, Mac or Linux. The zip/tar files are written by hand (rather than with
`zip`/`tar`) so the executable gets its Unix "run me" permission even when built on Windows.
"""
import os
import shutil
import subprocess
import tarfile
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
DIST = os.path.join(HERE, "dist")
PUBLISH = os.path.join(HERE, "bin", "publish")
APP_NAME = "Quad Backup"
EXE = "QuadBackup"
VERSION = "1.0.0"

INFO_PLIST = """<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>{name}</string>
  <key>CFBundleDisplayName</key><string>{name}</string>
  <key>CFBundleIdentifier</key><string>com.quadbackup.app</string>
  <key>CFBundleExecutable</key><string>{exe}</string>
  <key>CFBundleIconFile</key><string>QuadBackup.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>{version}</string>
  <key>CFBundleVersion</key><string>{version}</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
"""


def publish(rid):
    out = os.path.join(PUBLISH, rid)
    shutil.rmtree(out, ignore_errors=True)
    subprocess.run(["dotnet", "publish", os.path.join(HERE, "QuadBackup.Desktop.csproj"), "-c", "Release", "-r", rid,
                    "--self-contained", "true", "-p:DebugType=None", "-p:DebugSymbols=false", "-o", out], check=True)
    return out


def is_executable(name):
    return name == EXE or name.endswith(".sh") or name == "createdump"


def zip_tree(zip_path, root, prefix):
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for folder, _, files in os.walk(root):
            for f in sorted(files):
                full = os.path.join(folder, f)
                rel = os.path.relpath(full, root).replace(os.sep, "/")
                info = zipfile.ZipInfo(prefix + "/" + rel)
                info.compress_type = zipfile.ZIP_DEFLATED
                mode = 0o755 if is_executable(f) else 0o644
                info.external_attr = (0o100000 | mode) << 16  # regular file + permissions, read by macOS Archive Utility
                info.create_system = 3  # "made on Unix", so the permissions are honoured
                with open(full, "rb") as fh:
                    z.writestr(info, fh.read())


def mac(rid, label):
    out = publish(rid)
    bundle = os.path.join(PUBLISH, label, APP_NAME + ".app")
    shutil.rmtree(os.path.dirname(bundle), ignore_errors=True)
    macos = os.path.join(bundle, "Contents", "MacOS")
    resources = os.path.join(bundle, "Contents", "Resources")
    shutil.copytree(out, macos)
    os.makedirs(resources)
    shutil.copy(os.path.join(HERE, "packaging", "QuadBackup.icns"), resources)
    with open(os.path.join(bundle, "Contents", "Info.plist"), "w", newline="\n") as fh:
        fh.write(INFO_PLIST.format(name=APP_NAME, exe=EXE, version=VERSION))
    target = os.path.join(DIST, "QuadBackup-macOS-" + label + ".zip")
    zip_tree(target, bundle, APP_NAME + ".app")
    print("built", target)


def linux(rid):
    out = publish(rid)
    target = os.path.join(DIST, "QuadBackup-" + rid + ".tar.gz")

    def fix(info):
        info.uid = info.gid = 0
        info.uname = info.gname = ""
        if info.isdir():
            info.mode = 0o755
        else:
            info.mode = 0o755 if is_executable(os.path.basename(info.name)) else 0o644
        return info

    with tarfile.open(target, "w:gz") as tar:
        tar.add(out, arcname="quad-backup", filter=fix)
        readme = os.path.join(HERE, "packaging", "linux-README.txt")
        tar.add(readme, arcname="quad-backup/README.txt", filter=fix)
    print("built", target)


if __name__ == "__main__":
    shutil.rmtree(DIST, ignore_errors=True)
    os.makedirs(DIST)
    mac("osx-arm64", "AppleSilicon")
    mac("osx-x64", "Intel")
    linux("linux-x64")
