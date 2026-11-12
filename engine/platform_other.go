//go:build !windows

package main

import "golang.org/x/sys/unix"

type winfsp struct {
	Installed  bool
	InstallDir string
	Version    string
}

// winfspInfo always reports not installed on non Windows platforms, which
// are only supported for development and testing of the engine.
func winfspInfo() winfsp { return winfsp{} }

func diskFree(dir string) (int64, bool) {
	var st unix.Statfs_t
	if err := unix.Statfs(dir, &st); err != nil {
		return 0, false
	}
	return int64(st.Bavail) * int64(st.Bsize), true
}

func isWSAConnRefused(error) bool { return false }
