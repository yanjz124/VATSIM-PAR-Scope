#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP_NAME="PARScopeDisplay_Avalonia"
PROJECT_PATH="$ROOT/PARScopeDisplay_Avalonia/PARScopeDisplay_Avalonia.csproj"
OUT_DIR="$ROOT/publish"
VERSION="1.0"

mkdir -p "$OUT_DIR"

# helper to create .app bundle
create_app() {
  local arch=$1
  local rid=$2
  local publish_dir="$OUT_DIR/${APP_NAME}-${rid}"
  local app_bundle="$OUT_DIR/${APP_NAME}-${rid}.app"
  local dmg_file="$ROOT/${APP_NAME}-${rid}-$VERSION.dmg"

  echo "Publishing for $rid..."
  dotnet publish "$PROJECT_PATH" -c Release -r $rid -o "$publish_dir" -p:PublishSingleFile=true -p:SelfContained=true --nologo

  echo "Creating .app bundle at $app_bundle"
  rm -rf "$app_bundle"
  mkdir -p "$app_bundle/Contents/MacOS"
  mkdir -p "$app_bundle/Contents/Resources"

  # copy single-file executable (find the executable file)
  exe=$(ls "$publish_dir" | grep -v "\.deps.json$" | grep -v "\.pdb$" | grep -v "\.runtimeconfig.json$" | head -n1)
  if [ -z "$exe" ]; then
    echo "No executable found in $publish_dir"; exit 1
  fi
  cp "$publish_dir/$exe" "$app_bundle/Contents/MacOS/$APP_NAME"
  chmod +x "$app_bundle/Contents/MacOS/$APP_NAME"

  # minimal Info.plist
  cat > "$app_bundle/Contents/Info.plist" <<EOF
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
  <string>$APP_NAME</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
</dict>
</plist>
EOF

  echo "Creating dmg $dmg_file"
  # create dmg from the .app bundle
  rm -f "$dmg_file"
  hdiutil create -volname "$APP_NAME" -srcfolder "$app_bundle" -ov -format UDZO "$dmg_file"
  echo "Created $dmg_file"
}

# Publish for Intel x64
create_app "x64" "osx-x64"
# Publish for Apple Silicon
create_app "arm64" "osx-arm64"

echo "All done. DMGs are in $ROOT"