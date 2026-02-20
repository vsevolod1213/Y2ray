#!/bin/bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$ROOT_DIR/v2rayN"
PUBLISH_DIR="$PROJECT_DIR/Release/macos-arm64"
APP_DIR="$PUBLISH_DIR/v2rayN.app"
BIN_DEBUG="$PROJECT_DIR/v2rayN.Desktop/bin/Debug/net8.0"
NATIVE_DIR="$PROJECT_DIR/v2rayN.Desktop/bin/Release/net8.0/osx-arm64"
ICON_SRC="$PROJECT_DIR/v2rayN.Desktop/v2rayN.icns"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT_DIR/Directory.Build.props" | head -n 1)"

if [[ -z "${VERSION:-}" ]]; then
  VERSION="0.0.0"
fi

if [[ ! -d "$PROJECT_DIR" ]]; then
  echo "Project folder not found: $PROJECT_DIR" >&2
  exit 1
fi

echo "Publishing..."
dotnet publish "$PROJECT_DIR/v2rayN.Desktop/v2rayN.Desktop.csproj" \
  -c Release -r osx-arm64 -p:UseAppHost=true -p:SelfContained=true \
  -o "$PUBLISH_DIR"

echo "Building .app..."
rm -rf "$APP_DIR"
mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"

cp "$PUBLISH_DIR/v2rayN" "$APP_DIR/Contents/MacOS/v2rayN"
cp "$ICON_SRC" "$APP_DIR/Contents/Resources/v2rayN.icns"

for lib in libAvaloniaNative.dylib libHarfBuzzSharp.dylib libSkiaSharp.dylib libe_sqlite3.dylib; do
  if [[ -f "$PUBLISH_DIR/$lib" ]]; then
    cp "$PUBLISH_DIR/$lib" "$APP_DIR/Contents/MacOS/"
  elif [[ -f "$NATIVE_DIR/$lib" ]]; then
    cp "$NATIVE_DIR/$lib" "$APP_DIR/Contents/MacOS/"
  else
    echo "Missing native library: $lib" >&2
    exit 1
  fi
done

if [[ ! -d "$BIN_DEBUG/bin" ]]; then
  echo "Missing cores directory: $BIN_DEBUG/bin" >&2
  exit 1
fi
if [[ ! -d "$BIN_DEBUG/binConfigs" ]]; then
  echo "Missing binConfigs directory: $BIN_DEBUG/binConfigs" >&2
  exit 1
fi
if [[ ! -d "$BIN_DEBUG/guiConfigs" ]]; then
  echo "Missing guiConfigs directory: $BIN_DEBUG/guiConfigs" >&2
  exit 1
fi

rsync -a "$BIN_DEBUG/bin" "$APP_DIR/Contents/MacOS/"
rsync -a "$BIN_DEBUG/guiConfigs" "$APP_DIR/Contents/MacOS/"
rsync -a "$BIN_DEBUG/binConfigs" "$APP_DIR/Contents/MacOS/"

chmod +x "$APP_DIR/Contents/MacOS/bin/xray/xray"
chmod +x "$APP_DIR/Contents/MacOS/bin/sing_box/sing-box"

# Remove generated files so the app recreates them with correct paths.
rm -f "$APP_DIR/Contents/MacOS/binConfigs/configPre.json"
rm -f "$APP_DIR/Contents/MacOS/binConfigs/run_as_sudo.sh"
rm -f "$APP_DIR/Contents/MacOS/binConfigs/kill_as_sudo.sh"

cat > "$APP_DIR/Contents/Info.plist" <<EOF
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
  <key>CFBundleShortVersionString</key><string>${VERSION}</string>
  <key>CFBundleVersion</key><string>${VERSION}</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>CFBundleURLTypes</key>
  <array>
    <dict>
      <key>CFBundleURLName</key><string>com.yvpn.v2rayN</string>
      <key>CFBundleURLSchemes</key>
      <array>
        <string>yvpn</string>
      </array>
    </dict>
  </array>
</dict>
</plist>
EOF

echo "Done."
echo "App path: $APP_DIR"
