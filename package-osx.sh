#!/bin/bash

Arch="$1"
OutputPath="$2"
Version="$3"

FileName="v2rayN-${Arch}.zip"
wget -nv -O $FileName "https://github.com/2dust/v2rayN-core-bin/raw/refs/heads/master/$FileName"
7z x $FileName
cp -rf v2rayN-${Arch}/* $OutputPath

PackagePath="v2rayN-Package-${Arch}"
mkdir -p "$PackagePath/Yvpn.app/Contents/Resources"
cp -rf "$OutputPath" "$PackagePath/Yvpn.app/Contents/MacOS"
cp -f "$PackagePath/Yvpn.app/Contents/MacOS/v2rayN.icns" "$PackagePath/Yvpn.app/Contents/Resources/AppIcon.icns"
echo "When this file exists, app will not store configs under this folder" > "$PackagePath/Yvpn.app/Contents/MacOS/NotStoreConfigHere.txt"
mv "$PackagePath/Yvpn.app/Contents/MacOS/v2rayN" "$PackagePath/Yvpn.app/Contents/MacOS/Yvpn"
chmod +x "$PackagePath/Yvpn.app/Contents/MacOS/Yvpn"

cat >"$PackagePath/Yvpn.app/Contents/Info.plist" <<-EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>English</string>
  <key>CFBundleDisplayName</key>
  <string>Yvpn</string>
  <key>CFBundleExecutable</key>
  <string>Yvpn</string>
  <key>CFBundleIconFile</key>
  <string>AppIcon</string>
  <key>CFBundleIconName</key>
  <string>AppIcon</string>
  <key>CFBundleIdentifier</key>
  <string>com.yvpn.v2rayN</string>
  <key>CFBundleName</key>
  <string>Yvpn</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>${Version}</string>
  <key>CSResourcesFileMapped</key>
  <true/>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>LSMinimumSystemVersion</key>
  <string>12.7</string>
  <key>CFBundleURLTypes</key>
  <array>
    <dict>
      <key>CFBundleURLName</key>
      <string>yvpn</string>
      <key>CFBundleURLSchemes</key>
      <array>
        <string>yvpn</string>
      </array>
    </dict>
  </array>
</dict>
</plist>
EOF

create-dmg \
    --volname "Yvpn Installer" \
    --window-size 700 420 \
    --icon-size 100 \
    --icon "Yvpn.app" 160 185 \
    --hide-extension "Yvpn.app" \
    --app-drop-link 500 185 \
    "Yvpn-${Arch}.dmg" \
    "$PackagePath/Yvpn.app"
