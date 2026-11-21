//go:build windows

package main

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"syscall"
	"unsafe"

	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/registry"
)

type winfsp struct {
	Installed  bool
	InstallDir string
	Version    string
}

// winfspInfo looks WinFsp up in the registry the same way cgofuse does.
func winfspInfo() (w winfsp) {
	for _, path := range []string{`SOFTWARE\WOW6432Node\WinFsp`, `SOFTWARE\WinFsp`} {
		k, err := registry.OpenKey(registry.LOCAL_MACHINE, path, registry.QUERY_VALUE)
		if err != nil {
			continue
		}
		dir, _, err := k.GetStringValue("InstallDir")
		if err == nil && dir != "" {
			w.InstallDir = dir
			if v, _, err := k.GetStringValue("Version"); err == nil {
				w.Version = v
			}
		}
		_ = k.Close()
		if w.InstallDir != "" {
			break
		}
	}
	if w.InstallDir == "" {
		return w
	}
	dll := "winfsp-x64.dll"
	switch arch() {
	case "arm64":
		dll = "winfsp-a64.dll"
	case "386":
		dll = "winfsp-x86.dll"
	}
	dllPath := filepath.Join(w.InstallDir, "bin", dll)
	if _, err := os.Stat(dllPath); err == nil {
		w.Installed = true
		if w.Version == "" {
			w.Version = fileVersion(dllPath)
		}
	}
	return w
}

// diskFree returns the bytes available to the caller on the volume of dir.
func diskFree(dir string) (int64, bool) {
	p, err := windows.UTF16PtrFromString(dir)
	if err != nil {
		return 0, false
	}
	var free, total, totalFree uint64
	if err := windows.GetDiskFreeSpaceEx(p, &free, &total, &totalFree); err != nil {
		return 0, false
	}
	return int64(free), true
}

// isWSAConnRefused reports whether err is the Windows "connection refused" error.
func isWSAConnRefused(err error) bool {
	var errno syscall.Errno
	return errors.As(err, &errno) && errno == windows.WSAECONNREFUSED
}

// fileVersion returns "major.minor.build" from a PE file's version resource.
// WinFsp does not record its version in the registry, so read it from the DLL.
func fileVersion(path string) string {
	size, err := windows.GetFileVersionInfoSize(path, nil)
	if err != nil || size == 0 {
		return ""
	}
	buf := make([]byte, size)
	if err := windows.GetFileVersionInfo(path, 0, size, unsafe.Pointer(&buf[0])); err != nil {
		return ""
	}
	var info *windows.VS_FIXEDFILEINFO
	var n uint32
	if err := windows.VerQueryValue(unsafe.Pointer(&buf[0]), `\`, unsafe.Pointer(&info), &n); err != nil || info == nil {
		return ""
	}
	return fmt.Sprintf("%d.%d.%d", info.FileVersionMS>>16, info.FileVersionMS&0xffff, info.FileVersionLS>>16)
}

// arch returns the CPU architecture, which selects the WinFsp DLL to check for.
func arch() string { return runtime.GOARCH }

// mountInfo describes how drives are mounted on this platform: WinFsp.
func mountInfo() (backend string, ok bool, version string) {
	w := winfspInfo()
	return "winfsp", w.Installed, w.Version
}
