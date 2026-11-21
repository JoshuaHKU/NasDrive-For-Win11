//go:build !windows

package main

import (
	"os"
	"os/exec"
	"runtime"
	"strings"

	"golang.org/x/sys/unix"
)

type winfsp struct {
	Installed  bool
	InstallDir string
	Version    string
}

// winfspInfo always reports not installed on non Windows platforms.
func winfspInfo() winfsp { return winfsp{} }

// mountInfo describes how drives are mounted on this platform. macOS uses
// rclone's nfsmount (the system NFS client), which needs /sbin/mount_nfs.
func mountInfo() (backend string, ok bool, version string) {
	if runtime.GOOS != "darwin" {
		return "none", false, ""
	}
	_, err := os.Stat("/sbin/mount_nfs")
	if out, verr := exec.Command("sw_vers", "-productVersion").Output(); verr == nil {
		version = "macOS " + strings.TrimSpace(string(out))
	}
	return "nfs", err == nil, version
}

func diskFree(dir string) (int64, bool) {
	var st unix.Statfs_t
	if err := unix.Statfs(dir, &st); err != nil {
		return 0, false
	}
	return int64(st.Bavail) * int64(st.Bsize), true
}

func isWSAConnRefused(error) bool { return false }
