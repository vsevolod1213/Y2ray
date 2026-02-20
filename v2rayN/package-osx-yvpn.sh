#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

OUT="./Release/macos-arm64"
APP="$OUT/v2rayN.app"

SRC_BASE="./v2rayN.Desktop/bin/Release/net8.0"
if [ ! -d "$SRC_BASE" ]; then
  SRC_BASE="./v2rayN.Desktop/bin/Debug/net8.0"
fi

LIBSRC="$SRC_BASE/osx-arm64"

dotnet publish ./v2rayN.Desktop/v2rayN.Desktop.csproj \
  -c Release -r osx-arm64 -p:UseAppHost=true -p:SelfContained=true \
  -o "$OUT"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

cp "$OUT/v2rayN" "$APP/Contents/MacOS/v2rayN"
cp "./v2rayN.Desktop/v2rayN.icns" "$APP/Contents/Resources/v2rayN.icns"

for lib in libAvaloniaNative.dylib libHarfBuzzSharp.dylib libSkiaSharp.dylib libe_sqlite3.dylib; do
  if [ ! -f "$LIBSRC/$lib" ]; then
    echo "Missing $LIBSRC/$lib"
    exit 1
  fi
  cp "$LIBSRC/$lib" "$APP/Contents/MacOS/"
done

rsync -a "$SRC_BASE/bin" "$APP/Contents/MacOS/"
rsync -a "$SRC_BASE/guiConfigs" "$APP/Contents/MacOS/"
rsync -a "$SRC_BASE/binConfigs" "$APP/Contents/MacOS/"

# Remove stale generated files so the app rebuilds them on first run.
rm -f "$APP/Contents/MacOS/binConfigs/configPre.json"
rm -f "$APP/Contents/MacOS/binConfigs/run_as_sudo.sh"
rm -f "$APP/Contents/MacOS/binConfigs/kill_as_sudo.sh"

chmod +x "$APP/Contents/MacOS/v2rayN"
chmod +x "$APP/Contents/MacOS/bin/xray/xray"
chmod +x "$APP/Contents/MacOS/bin/sing_box/sing-box"

cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key><string>v2rayN</string>
  <key>CFBundleIdentifier</key><string>com.yvpn.v2rayN</string>
  <key>CFBundleName</key><string>Yvpn</string>
  <key>CFBundleDisplayName</key><string>Yvpn</string>
  <key>CFBundleIconFile</key><string>v2rayN</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>7.18.0</string>
  <key>CFBundleVersion</key><string>7.18.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>CFBundleURLTypes</key>
  <array>
    <dict>
      <key>CFBundleURLName</key><string>com.yvpn.v2rayN</string>
      <key>CFBundleURLSchemes</key>
      <array><string>yvpn</string></array>
    </dict>
  </array>
</dict>
</plist>
PLIST

echo "Built: $APP"
