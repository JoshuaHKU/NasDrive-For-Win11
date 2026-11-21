#!/usr/bin/env bash
# Builds NasDrive.app and a .dmg for macOS into dist/osx-<arch>/.
# Requires macOS with Go and the .NET 8 SDK (Xcode command line tools for codesign).
#
#   scripts/build-mac.sh               # Apple silicon (arm64)
#   ARCH=amd64 scripts/build-mac.sh    # Intel
#   SIGN_ID="Developer ID Application: …" scripts/build-mac.sh   # real signature (default: ad-hoc)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ARCH="${ARCH:-arm64}"
VERSION="${VERSION:-0.5.0}"
SIGN_ID="${SIGN_ID:--}"
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
case "$ARCH" in
  arm64) RID=osx-arm64 ;;
  amd64) RID=osx-x64 ;;
  *) echo "unsupported ARCH $ARCH" >&2; exit 1 ;;
esac
OUT="$ROOT/dist/$RID"
APP="$OUT/NasDrive.app"
WORK="$OUT/.work"
rm -rf "$OUT"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$WORK"

RCLONE_VERSION="$(awk '$1=="github.com/rclone/rclone"{print $2; exit}' "$ROOT/engine/go.mod")"
echo "==> engine (darwin/$ARCH, rclone $RCLONE_VERSION)"
(
  cd "$ROOT/engine"
  GOOS=darwin GOARCH="$ARCH" CGO_ENABLED=0 go build -trimpath \
    -ldflags "-s -w -X main.Version=$VERSION -X github.com/rclone/rclone/fs.Version=$RCLONE_VERSION" \
    -o "$APP/Contents/MacOS/nasdrive-engine" .
)

echo "==> gui ($RID)"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
"$DOTNET" publish "$ROOT/gui/NasDrive.Mac/NasDrive.Mac.csproj" -c Release -r "$RID" \
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false \
  -p:EnableCompressionInSingleFile=true -p:Version="$VERSION" -o "$WORK/publish" --nologo
rm -f "$WORK/publish"/*.pdb
cp "$WORK/publish"/* "$APP/Contents/MacOS/"

echo "==> icon"
ICONSET="$WORK/NasDrive.iconset"
mkdir -p "$ICONSET"
sips -s format png "$ROOT/gui/NasDrive.Mac/Assets/NasDrive.ico" --out "$WORK/icon256.png" >/dev/null
for s in 16 32 128 256 512; do
  sips -z $s $s "$WORK/icon256.png" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  d=$((s * 2)); [ $d -le 512 ] && sips -z $d $d "$WORK/icon256.png" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/NasDrive.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>NasDrive</string>
  <key>CFBundleDisplayName</key><string>NasDrive</string>
  <key>CFBundleIdentifier</key><string>io.github.joshuahku.nasdrive</string>
  <key>CFBundleExecutable</key><string>NasDrive</string>
  <key>CFBundleIconFile</key><string>NasDrive</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>MIT License. Built on rclone.</string>
</dict>
</plist>
PLIST
cp "$ROOT/LICENSE" "$ROOT/THIRD-PARTY-NOTICES.md" "$APP/Contents/Resources/"

echo "==> codesign ($SIGN_ID)"
xattr -cr "$APP"
OPTS=(--force --timestamp=none)
[ "$SIGN_ID" != "-" ] && OPTS=(--force --timestamp --options runtime)
find "$APP/Contents/MacOS" -type f \( -name '*.dylib' -o -name 'nasdrive-engine' \) -exec codesign "${OPTS[@]}" --sign "$SIGN_ID" {} \;
codesign "${OPTS[@]}" --sign "$SIGN_ID" "$APP"
codesign --verify --deep --strict "$APP"

echo "==> dmg"
DMG="$OUT/NasDrive-$VERSION-$RID.dmg"
STAGE="$WORK/dmg"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "NasDrive $VERSION" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
rm -rf "$WORK"
echo "==> done: $DMG"
ls -la "$OUT"
