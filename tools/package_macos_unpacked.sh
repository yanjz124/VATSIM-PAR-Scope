#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP_NAME="PARScopeDisplay_Avalonia"
PROJECT_PATH="$ROOT/PARScopeDisplay_Avalonia/PARScopeDisplay_Avalonia.csproj"
OUT_DIR="$ROOT/publish_unpacked"
VERSION="1.0"
RID="osx-arm64"

mkdir -p "$OUT_DIR"

PUBLISH_DIR="$OUT_DIR/${APP_NAME}-${RID}"
rm -rf "$PUBLISH_DIR"
mkdir -p "$PUBLISH_DIR"

echo "Publishing unpacked self-contained app for $RID to $PUBLISH_DIR..."
dotnet publish "$PROJECT_PATH" -c Release -r $RID -o "$PUBLISH_DIR" -p:SelfContained=true -p:PublishSingleFile=false --nologo

# Create .app bundle
APP_BUNDLE="$OUT_DIR/${APP_NAME}.app"
rm -rf "$APP_BUNDLE"
mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Resources"

# Copy all publish files into MacOS folder (Avalonia expects libs next to executable)
cp -R "$PUBLISH_DIR/"* "$APP_BUNDLE/Contents/MacOS/"
chmod -R 755 "$APP_BUNDLE/Contents/MacOS"

# Ensure executable bit on main executable
MAIN_EXE="$(find "$APP_BUNDLE/Contents/MacOS" -maxdepth 1 -type f -perm +111 -print | head -n1)"
if [ -z "$MAIN_EXE" ]; then
  echo "ERROR: No executable found in publish output"
  ls -la "$APP_BUNDLE/Contents/MacOS"
  exit 1
fi
MAIN_NAME="$(basename "$MAIN_EXE")"

# Write a minimal Info.plist
cat > "$APP_BUNDLE/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple Computer//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>
  <string>$APP_NAME</string>
  <key>CFBundleDisplayName</key>
  <string>$APP_NAME</string>
  <key>CFBundleIdentifier</key>
  <string>com.example.parscope</string>
  <key>CFBundleVersion</key>
  <string>$VERSION</string>
  <key>CFBundleExecutable</key>
  <string>$MAIN_NAME</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
</dict>
</plist>
EOF

# Optionally add an icns if present in repo
if [ -f "$ROOT/appicon.icns" ]; then
  cp "$ROOT/appicon.icns" "$APP_BUNDLE/Contents/Resources/appicon.icns"
fi

# Create DMG
DMG_FILE="$ROOT/${APP_NAME}-${RID}-unpacked-$VERSION.dmg"
rm -f "$DMG_FILE"
echo "Creating dmg $DMG_FILE from $APP_BUNDLE"
hdiutil create -volname "$APP_NAME" -srcfolder "$APP_BUNDLE" -ov -format UDZO "$DMG_FILE"

echo "Created $DMG_FILE"

echo "Packaging complete. DMG located at: $DMG_FILE"