// nasdrive-engine is the background engine of NasDrive.
//
// It embeds rclone as a library and links only the parts NasDrive needs (no
// other rclone backends or commands are compiled in): the WebDAV, SFTP and
// FTP backends, NasDrive's own NFS and Synology backends, WinFsp based mounting (cmount)
// and the rc remote control API, plus a few NasDrive specific rc calls (see
// rc_nasdrive.go).
//
// It is started by the NasDrive GUI as:
//
//	nasdrive-engine.exe engine --rc-addr 127.0.0.1:PORT --cache-dir DIR --log-file FILE
//
// The rc credentials are passed in the RCLONE_RC_USER / RCLONE_RC_PASS
// environment variables so they never appear on the command line.
package main

import (
	"os"

	_ "github.com/JoshuaHKU/NasDrive-For-Win11/engine/backend/nfs"      // the NFSv3 client
	_ "github.com/JoshuaHKU/NasDrive-For-Win11/engine/backend/synology" // Synology DSM File Station
	_ "github.com/rclone/rclone/backend/ftp"                            // FTP and FTPS
	_ "github.com/rclone/rclone/backend/local"                          // needed by the VFS cache
	_ "github.com/rclone/rclone/backend/sftp"                           // SFTP (SSH)
	_ "github.com/rclone/rclone/backend/webdav"                         // the WebDAV client (also Nextcloud)
	"github.com/rclone/rclone/cmd"
	_ "github.com/rclone/rclone/cmd/cmount" // WinFsp/FUSE mount (build with -tags cmount)
	_ "github.com/rclone/rclone/cmd/version"
)

// Version is set at build time with -ldflags "-X main.Version=x.y.z"
var Version = "0.5.0-dev"

func main() {
	// Default to the engine command when started without arguments so a
	// double click on the exe does something sensible.
	if len(os.Args) == 1 {
		os.Args = append(os.Args, "engine")
	}
	cmd.Main()
}
