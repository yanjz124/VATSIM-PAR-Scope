#!/usr/bin/env bash
set -euo pipefail

# Simple macOS packaging: publish arm64-only self-contained unpacked app,
# assemble a minimal .app bundle, and create an unsigned DMG for local testing.

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT_REL="PARScopeDisplay_Avalonia/PARScopeDisplay_Avalonia.csproj"
PROJECT_PATH="$ROOT_DIR/$PROJECT_REL"
OUT_PUBLISH="/tmp/par_publish_arm64_for_packaging"
APP_NAME="PARScopeDisplay_Avalonia"
APP_BUNDLE_NAME="$APP_NAME-osx-arm64-unpacked.app"
APP_DIR="$ROOT_DIR/$APP_BUNDLE_NAME"
DMG_NAME="$ROOT_DIR/${APP_NAME}-osx-arm64-unpacked-simple-1.0.dmg"
EXECUTABLE_NAME="$APP_NAME"

echo "Root: $ROOT_DIR"
echo "Project: $PROJECT_PATH"

rm -rf "$OUT_PUBLISH" "$APP_DIR" "$DMG_NAME"
mkdir -p "$OUT_PUBLISH"

echo "Publishing app (arm64, self-contained, unpacked)..."
dotnet publish "$PROJECT_PATH" -c Release -r osx-arm64 -p:SelfContained=true -p:PublishSingleFile=false -o "$OUT_PUBLISH"

echo "Assembling .app bundle at: $APP_DIR"
mkdir -p "$APP_DIR/Contents/MacOS"
mkdir -p "$APP_DIR/Contents/Resources"

# Create a minimal Info.plist
cat > "$APP_DIR/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDisplayName</key>
  <string>${APP_NAME}</string>
  <key>CFBundleExecutable</key>
  <string>${EXECUTABLE_NAME}</string>
  <key>CFBundleIdentifier</key>
  <string>com.example.parscope</string>
  <key>CFBundleName</key>
  <string>${APP_NAME}</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleVersion</key>
  <string>1.0</string>
</dict>
</plist>
EOF

echo "Copying publish output into Contents/MacOS"
cp -R "$OUT_PUBLISH"/* "$APP_DIR/Contents/MacOS/"

# Ensure the main executable is named correctly (some publish setups place the exe without extension)
if [ -f "$APP_DIR/Contents/MacOS/${EXECUTABLE_NAME}" ]; then
  echo "Executable already present: $EXECUTABLE_NAME"
else
  # Try to find the main executable file (first Mach-O EXECUTABLE)
  exec_candidate=""
  for f in "$APP_DIR/Contents/MacOS"/*; do
    if [ -f "$f" ]; then
      type=$(file -b "$f" || true)
      if echo "$type" | grep -q "Mach-O .* executable"; then
        exec_candidate="$f"
        break
      fi
    fi
  done
  if [ -n "$exec_candidate" ]; then
    mv "$exec_candidate" "$APP_DIR/Contents/MacOS/${EXECUTABLE_NAME}"
    echo "Renamed $exec_candidate -> ${EXECUTABLE_NAME}"
  else
    echo "ERROR: could not locate main executable in publish output" >&2
    exit 2
  fi
fi

cd "$APP_DIR/Contents/MacOS"

# Create compatibility symlink for Avalonia native naming if present
if [ -f libAvaloniaNative.dylib ] && [ ! -f libAvalonia.Native.OSX.dylib ]; then
  ln -sf libAvaloniaNative.dylib libAvalonia.Native.OSX.dylib || true
fi

cd "$ROOT_DIR"

echo "Set ownership and permissions"
sudo chown -R $(whoami):staff "$APP_DIR"
chmod -R u+rwX,go+rX "$APP_DIR/Contents"
chmod +x "$APP_DIR/Contents/MacOS/${EXECUTABLE_NAME}"

echo "(Optional) Ad-hoc codesign to make gatekeeper messages clearer (not a substitute for a Developer ID)"
codesign --force --deep --sign - "$APP_DIR" || true

echo "Creating DMG: $DMG_NAME"
hdiutil create -ov -format UDZO -srcfolder "$APP_DIR" -volname "$APP_NAME" "$DMG_NAME"

echo "Done. DMG created at: $DMG_NAME"
echo "You can install by mounting the DMG and copying the .app to /Applications, or run binary directly from the DMG for quick test."

exit 0
