#!/usr/bin/env bash
# Headless end-to-end test of the macOS app on this Mac.
# Starts throwaway local WebDAV/SFTP/FTP servers (rclone serve, random password),
# then runs gui/NasDrive.Mac.Smoke: renders every page to PNG and connects,
# exercises and disconnects each drive through the real view models and engine.
#
#   scripts/test-mac.sh                                  # local servers only
#   NFS_HOST=nas NFS_EXPORT=/volume1/share NFS_CHECK=/Volumes/share scripts/test-mac.sh
#       # also a real NAS; NFS_CHECK is the same share seen another way (to verify server-side results)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$DOTNET")}"
RCLONE="${RCLONE:-$(command -v rclone)}"
ENGINE_DIR="${ENGINE_DIR:-$ROOT/dist/osx-arm64/NasDrive.app/Contents/MacOS}"
[ -x "$ENGINE_DIR/nasdrive-engine" ] || { echo "build first: scripts/build-mac.sh" >&2; exit 1; }

W="$(cd "$(mktemp -d "${TMPDIR:-/tmp}/nasdrive-smoke.XXXXXX")" && pwd -P)"
PASS="$(openssl rand -hex 12)"
mkdir -p "$W/dav" "$W/sftp" "$W/ftp" "$W/home" "$W/out"
echo "existing file" > "$W/dav/readme.txt"
PIDS=()
cleanup() {
  for p in "${PIDS[@]}"; do kill "$p" 2>/dev/null || true; done
  pkill -f "nasdrive-engine engine .*$W/home" 2>/dev/null || true
  for m in $(/sbin/mount | awk -v h="$W/home" '$3 ~ "^"h {print $3}'); do /usr/sbin/diskutil umount force "$m" >/dev/null 2>&1 || true; done
}
trap cleanup EXIT

port() { python3 -c 'import socket;s=socket.socket();s.bind(("127.0.0.1",0));print(s.getsockname()[1])'; }
DAV_PORT=$(port); SFTP_PORT=$(port); FTP_PORT=$(port)
"$RCLONE" serve webdav "$W/dav" --addr "127.0.0.1:$DAV_PORT" --user tester --pass "$PASS" >"$W/dav.log" 2>&1 & PIDS+=($!)
"$RCLONE" serve sftp "$W/sftp" --addr "127.0.0.1:$SFTP_PORT" --user tester --pass "$PASS" >"$W/sftp.log" 2>&1 & PIDS+=($!)
"$RCLONE" serve ftp "$W/ftp" --addr "127.0.0.1:$FTP_PORT" --user tester --pass "$PASS" >"$W/ftp.log" 2>&1 & PIDS+=($!)
sleep 2

export NASDRIVE_HOME="$W/home" NASDRIVE_ENGINE_DIR="$ENGINE_DIR" SMOKE_OUT="$W/out" SMOKE_PASS="$PASS"
export SMOKE_DAV_PORT=$DAV_PORT SMOKE_DAV_DIR="$W/dav" SMOKE_SFTP_PORT=$SFTP_PORT SMOKE_SFTP_DIR="$W/sftp" SMOKE_FTP_PORT=$FTP_PORT SMOKE_FTP_DIR="$W/ftp"
if [ -n "${NFS_HOST:-}" ]; then
  SUB="nasdrive-mac-smoke-$(date +%s)"
  mkdir -p "$NFS_CHECK/$SUB"
  export SMOKE_NFS_HOST="$NFS_HOST" SMOKE_NFS_EXPORT="$NFS_EXPORT" SMOKE_NFS_SUBDIR="/$SUB" SMOKE_NFS_CHECK_DIR="$NFS_CHECK/$SUB"
fi

set +e
"$DOTNET" run --project "$ROOT/gui/NasDrive.Mac.Smoke" -c Debug
RC=$?
set -e
[ -n "${SUB:-}" ] && rmdir "$NFS_CHECK/$SUB" 2>/dev/null
echo "screenshots: $W/out   logs: $W/home/logs"
exit $RC
