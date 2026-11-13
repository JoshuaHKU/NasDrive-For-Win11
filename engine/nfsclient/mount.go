package nfsclient

import (
	"context"
	"errors"
	"fmt"
	"net"
	"strconv"
)

const (
	pmapProg    = 100000
	pmapVers    = 2
	pmapGetPort = 3
	ipprotoTCP  = 6

	MountProg   = 100005
	mountVers   = 3
	mountMnt    = 1
	mountUmnt   = 3
	mountExport = 5

	NFSProg = 100003
	nfsVers = 3
)

// MountError is a MOUNT protocol failure status (mountstat3).
type MountError struct {
	Status uint32
	Path   string
}

var mountStatNames = map[uint32]string{
	1: "MNT3ERR_PERM", 2: "MNT3ERR_NOENT", 5: "MNT3ERR_IO", 13: "MNT3ERR_ACCES",
	20: "MNT3ERR_NOTDIR", 22: "MNT3ERR_INVAL", 63: "MNT3ERR_NAMETOOLONG",
	10004: "MNT3ERR_NOTSUPP", 10006: "MNT3ERR_SERVERFAULT",
}

// Error describes the failure, with a hint for permission errors.
func (e *MountError) Error() string {
	name := mountStatNames[e.Status]
	if name == "" {
		name = "status " + strconv.Itoa(int(e.Status))
	}
	switch e.Status {
	case 1, 13:
		return fmt.Sprintf("mount %q: %s (the server does not allow this client: check the export's host/IP rules and non-privileged port setting)", e.Path, name)
	case 2:
		return fmt.Sprintf("mount %q: %s (no such export)", e.Path, name)
	}
	return fmt.Sprintf("mount %q: %s", e.Path, name)
}

// GetPort asks the portmapper on host for the TCP port of prog/vers.
func GetPort(ctx context.Context, host string, prog, vers uint32, opt DialOptions) (int, error) {
	opt.Privileged = false
	cl := NewClient(net.JoinHostPort(host, "111"), opt)
	defer cl.Close()
	var e enc
	e.u32(prog)
	e.u32(vers)
	e.u32(ipprotoTCP)
	e.u32(0)
	res, err := cl.Call(ctx, pmapProg, pmapVers, pmapGetPort, Auth{}, e.b, true)
	if err != nil {
		return 0, fmt.Errorf("portmapper on %s: %w", host, err)
	}
	d := dec{b: res}
	port := d.u32()
	if d.err != nil {
		return 0, d.err
	}
	if port == 0 {
		return 0, &RPCError{prog, vers, 0, fmt.Sprintf("program %d version %d is not registered with the portmapper on %s", prog, vers, host)}
	}
	return int(port), nil
}

// Export is one entry of the server's export list.
type Export struct {
	Path   string   `json:"path"`
	Groups []string `json:"groups"`
}

func mountClient(ctx context.Context, host string, port int, opt DialOptions) (*Client, error) {
	if port == 0 {
		p, err := GetPort(ctx, host, MountProg, mountVers, opt)
		if err != nil {
			return nil, err
		}
		port = p
	}
	return NewClient(net.JoinHostPort(host, strconv.Itoa(port)), opt), nil
}

// Exports lists the server's exports (like showmount -e).
func Exports(ctx context.Context, host string, mountPort int, opt DialOptions) ([]Export, error) {
	cl, err := mountClient(ctx, host, mountPort, opt)
	if err != nil {
		return nil, err
	}
	defer cl.Close()
	res, err := cl.Call(ctx, MountProg, mountVers, mountExport, Auth{}, nil, true)
	if err != nil {
		return nil, err
	}
	d := dec{b: res}
	var out []Export
	for d.bool() && len(out) < 10000 {
		ex := Export{Path: d.str(1024)}
		for d.bool() && len(ex.Groups) < 1000 {
			ex.Groups = append(ex.Groups, d.str(1024))
		}
		out = append(out, ex)
	}
	return out, d.err
}

// Mount resolves exportPath to its root file handle.
func Mount(ctx context.Context, host string, mountPort int, exportPath string, auth Auth, opt DialOptions) ([]byte, error) {
	cl, err := mountClient(ctx, host, mountPort, opt)
	if err != nil {
		return nil, err
	}
	defer cl.Close()
	var e enc
	e.str(exportPath)
	res, err := cl.Call(ctx, MountProg, mountVers, mountMnt, auth, e.b, true)
	if err != nil {
		var re *RPCError
		if errors.As(err, &re) {
			return nil, fmt.Errorf("mount %q: %w", exportPath, err)
		}
		return nil, err
	}
	d := dec{b: res}
	if st := d.u32(); st != 0 {
		if d.err != nil {
			return nil, d.err
		}
		return nil, &MountError{Status: st, Path: exportPath}
	}
	fh := d.opaque(64)
	return fh, d.err
}

// Unmount tells the server the export is no longer in use (best effort).
func Unmount(ctx context.Context, host string, mountPort int, exportPath string, auth Auth, opt DialOptions) {
	cl, err := mountClient(ctx, host, mountPort, opt)
	if err != nil {
		return
	}
	defer cl.Close()
	var e enc
	e.str(exportPath)
	_, _ = cl.Call(ctx, MountProg, mountVers, mountUmnt, auth, e.b, true)
}
