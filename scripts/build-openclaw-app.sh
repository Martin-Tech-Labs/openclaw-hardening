#!/bin/sh
set -eu

# Usage:
#   ./build-openclaw-app.sh /absolute/path/to/openclaw /absolute/path/to/node
#
# Example:
#   ./build-openclaw-app.sh ~/src/openclaw "$(node -p 'process.execPath')"

if [ "$#" -ne 2 ]; then
  echo "Usage: $0 <openclaw-source-dir> <node-binary>"
  exit 1
fi

SRC_DIR="$1"
NODE_BIN="$2"

if [ ! -d "$SRC_DIR" ]; then
  echo "Source directory not found: $SRC_DIR"
  exit 1
fi

if [ ! -x "$NODE_BIN" ]; then
  echo "Node binary not executable: $NODE_BIN"
  exit 1
fi

APP_NAME="OpenClaw.app"
CONTENTS_DIR="$APP_NAME/Contents"
MACOS_DIR="$CONTENTS_DIR/MacOS"
RESOURCES_DIR="$CONTENTS_DIR/Resources"
APP_RES_DIR="$RESOURCES_DIR/app"

echo "Cleaning old app bundle..."
rm -rf "$APP_NAME"  

echo "Creating app bundle structure..."
mkdir -p "$MACOS_DIR" "$APP_RES_DIR"

echo "Copying OpenClaw app files..."
# Adjust this list if needed
for item in dist node_modules package.json package-lock.json openclaw.mjs; do
  if [ -e "$SRC_DIR/$item" ]; then
    cp -R "$SRC_DIR/$item" "$APP_RES_DIR/"
  fi
done

echo "Creating a custom Node runtime as SEA with dist/index.js entry point..."

cat > "$APP_RES_DIR/loader.cjs" <<LOADER
const { pathToFileURL } = require("node:url");
const path = require("node:path");

(async () => {
  const target = path.join(__dirname, "node_modules/openclaw/dist/index.js");

  process.argv = [
    process.argv[0],
    target,
    ...process.argv.slice(2),
  ];

  console.log("loading:", target);
  console.log("argv:", process.argv);

  await import(pathToFileURL(target).href);
})();
LOADER

mkdir -p "$APP_NAME/tmp"

cat > "$APP_NAME/tmp/entry.cjs" <<EOF
const { createRequire } = require("node:module");
const r = createRequire(__filename);
r("$PWD/OpenClaw.app/Contents/Resources/app/loader.cjs");
EOF

cat > "$APP_NAME/tmp/sea-config.json" <<EOF
{
  "main": "$APP_NAME/tmp/entry.cjs",
  "output": "$MACOS_DIR/openclaw-bundle"
}
EOF

"$NODE_BIN" --build-sea "$APP_NAME/tmp/sea-config.json"
rm -rf "$APP_NAME/tmp"

codesign --sign - --force "$MACOS_DIR/openclaw-bundle"

echo "Creating Info.plist..."
cat > "$CONTENTS_DIR/Info.plist" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>
  <string>OpenClaw Bundle</string>
  <key>CFBundleDisplayName</key>
  <string>OpenClaw Bundle</string>
  <key>CFBundleIdentifier</key>
  <string>ai.openclaw.bundle</string>
  <key>CFBundleVersion</key>
  <string>1</string>
  <key>CFBundleShortVersionString</key>
  <string>1.0</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleExecutable</key>
  <string>openclaw-bundle</string>
  <key>LSUIElement</key>
  <true/>
</dict>
</plist>
EOF

echo "Signing app bundle ad-hoc..."
codesign --sign - --force --deep "$APP_NAME"

echo "Done."
echo "App bundle created at: $APP_NAME"
echo
echo "Try launching with:"
echo "  open \"$APP_NAME\" --args gateway --port 18789"
echo
echo "Or run launcher directly:"
echo "  \"$APP_NAME/Contents/MacOS/openclaw-bundle\" --args gateway --port 18789"