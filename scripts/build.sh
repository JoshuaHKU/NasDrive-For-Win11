#!/usr/bin/env bash
# Builds nasdrive-engine.exe and NasDrive.exe for Windows into dist/win-<arch>/.
# Works on macOS, Linux and Windows (Git Bash). Requires Go and the .NET 8 SDK.
#
#   scripts/build.sh            # x64
#   ARCH=arm64 scripts/build.sh # Windows on ARM
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ARCH="${ARCH:-amd64}"
VERSION="${VERSION:-0.2.0}"
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
case "$ARCH" in
  amd64) RID=win-x64 ;;
  arm64) RID=win-arm64 ;;
  *) echo "unsupported ARCH $ARCH" >&2; exit 1 ;;
esac
OUT="$ROOT/dist/$RID"
rm -rf "$OUT"
mkdir -p "$OUT"

RCLONE_VERSION="$(awk '$1=="github.com/rclone/rclone"{print $2; exit}' "$ROOT/engine/go.mod")"
echo "==> engine ($ARCH, rclone $RCLONE_VERSION)"
(
  cd "$ROOT/engine"
  GOOS=windows GOARCH="$ARCH" CGO_ENABLED=0 go build -tags cmount -trimpath \
    -ldflags "-s -w -H windowsgui -X main.Version=$VERSION -X github.com/rclone/rclone/fs.Version=$RCLONE_VERSION" \
    -o "$OUT/nasdrive-engine.exe" .
)

echo "==> gui ($RID)"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
"$DOTNET" publish "$ROOT/gui/NasDrive/NasDrive.csproj" -c Release -r "$RID" \
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:Version="$VERSION" -o "$OUT" --nologo

rm -f "$OUT"/*.pdb
cp "$ROOT/LICENSE" "$ROOT/THIRD-PARTY-NOTICES.md" "$OUT/"
echo "==> done: $OUT"
ls -la "$OUT"
