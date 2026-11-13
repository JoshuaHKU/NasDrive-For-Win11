package nfsclient

import (
	"errors"
	"syscall"
)

// isAddrInUse reports a local port collision. WSAEADDRINUSE is 10048 on
// Windows, which Go maps to syscall.Errno(10048) rather than EADDRINUSE.
func isAddrInUse(err error) bool {
	var errno syscall.Errno
	if errors.As(err, &errno) {
		return errno == syscall.EADDRINUSE || errno == 10048 || errno == syscall.EADDRNOTAVAIL
	}
	return false
}
