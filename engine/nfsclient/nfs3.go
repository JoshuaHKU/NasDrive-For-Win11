package nfsclient

import (
	"context"
	"fmt"
	"net"
	"os"
	"strconv"
	"time"
)

// NFSv3 procedures
const (
	procGetAttr     = 1
	procSetAttr     = 2
	procLookup      = 3
	procRead        = 6
	procWrite       = 7
	procCreate      = 8
	procMkdir       = 9
	procRemove      = 12
	procRmdir       = 13
	procRename      = 14
	procReadDirPlus = 17
	procFSStat      = 18
	procFSInfo      = 19
	procCommit      = 21
)

// File types (ftype3)
const (
	TypeReg = 1
	TypeDir = 2
	TypeLnk = 5
)

// Stable how (stable_how)
const (
	Unstable = 0
	FileSync = 2
)

const maxFH = 64

// Error is an NFSv3 status (nfsstat3) other than NFS3_OK.
type Error struct {
	Status uint32
	Op     string
}

// Common nfsstat3 values
const (
	ErrPerm        = 1
	ErrNoEnt       = 2
	ErrIO          = 5
	ErrAcces       = 13
	ErrExist       = 17
	ErrNotDir      = 20
	ErrIsDir       = 21
	ErrNoSpc       = 28
	ErrROFS        = 30
	ErrNameTooLong = 63
	ErrNotEmpty    = 66
	ErrDQuot       = 69
	ErrStale       = 70
	ErrBadHandle   = 10001
	ErrNotSupp     = 10004
	ErrServerFault = 10006
	ErrJukebox     = 10008
)

var statNames = map[uint32]string{
	1: "NFS3ERR_PERM", 2: "NFS3ERR_NOENT", 5: "NFS3ERR_IO", 6: "NFS3ERR_NXIO", 13: "NFS3ERR_ACCES",
	17: "NFS3ERR_EXIST", 18: "NFS3ERR_XDEV", 20: "NFS3ERR_NOTDIR", 21: "NFS3ERR_ISDIR",
	22: "NFS3ERR_INVAL", 27: "NFS3ERR_FBIG", 28: "NFS3ERR_NOSPC", 30: "NFS3ERR_ROFS",
	63: "NFS3ERR_NAMETOOLONG", 66: "NFS3ERR_NOTEMPTY", 69: "NFS3ERR_DQUOT", 70: "NFS3ERR_STALE",
	10001: "NFS3ERR_BADHANDLE", 10004: "NFS3ERR_NOTSUPP", 10006: "NFS3ERR_SERVERFAULT", 10008: "NFS3ERR_JUKEBOX",
}

func (e *Error) Error() string {
	name := statNames[e.Status]
	if name == "" {
		name = "status " + strconv.Itoa(int(e.Status))
	}
	return fmt.Sprintf("nfs %s: %s", e.Op, name)
}

// Is lets errors.Is(err, os.ErrNotExist) and friends work.
func (e *Error) Is(target error) bool {
	switch target {
	case os.ErrNotExist:
		return e.Status == ErrNoEnt
	case os.ErrExist:
		return e.Status == ErrExist
	case os.ErrPermission:
		return e.Status == ErrPerm || e.Status == ErrAcces
	}
	return false
}

// IsStatus reports whether err is an NFS error with the given status.
func IsStatus(err error, status uint32) bool {
	e, ok := err.(*Error)
	return ok && e.Status == status
}

// Attr is a decoded fattr3.
type Attr struct {
	Type   uint32
	Mode   uint32
	UID    uint32
	GID    uint32
	Size   uint64
	FileID uint64
	Mtime  time.Time
}

func (a *Attr) IsDir() bool { return a.Type == TypeDir }

func decTime(d *dec) time.Time {
	s, ns := d.u32(), d.u32()
	return time.Unix(int64(s), int64(ns))
}

func decFattr(d *dec) Attr {
	var a Attr
	a.Type = d.u32()
	a.Mode = d.u32()
	d.u32() // nlink
	a.UID = d.u32()
	a.GID = d.u32()
	a.Size = d.u64()
	d.u64()   // used
	d.skip(8) // rdev
	d.u64()   // fsid
	a.FileID = d.u64()
	decTime(d) // atime
	a.Mtime = decTime(d)
	decTime(d) // ctime
	return a
}

func decPostOpAttr(d *dec) *Attr {
	if !d.bool() {
		return nil
	}
	a := decFattr(d)
	return &a
}

func decWcc(d *dec) *Attr {
	if d.bool() { // pre_op_attr: size, mtime, ctime
		d.skip(8 + 8 + 8)
	}
	return decPostOpAttr(d)
}

func decPostOpFH(d *dec) []byte {
	if !d.bool() {
		return nil
	}
	return d.opaque(maxFH)
}

// SetAttrs selects attributes for SETATTR/CREATE/MKDIR; nil fields are unchanged.
type SetAttrs struct {
	Mode  *uint32
	Size  *uint64
	Mtime *time.Time
}

func (s SetAttrs) encode(e *enc) {
	if s.Mode != nil {
		e.bool(true)
		e.u32(*s.Mode)
	} else {
		e.bool(false)
	}
	e.bool(false) // uid
	e.bool(false) // gid
	if s.Size != nil {
		e.bool(true)
		e.u64(*s.Size)
	} else {
		e.bool(false)
	}
	e.u32(0) // atime: DONT_CHANGE
	if s.Mtime != nil {
		e.u32(2) // SET_TO_CLIENT_TIME
		t := *s.Mtime
		e.u32(uint32(t.Unix()))
		e.u32(uint32(t.Nanosecond()))
	} else {
		e.u32(0)
	}
}

// FSInfo is the subset of FSINFO results used for I/O sizing.
type FSInfo struct {
	RTMax, RTPref, WTMax, WTPref uint32
}

// FSStat holds FSSTAT results in bytes.
type FSStat struct {
	Total, Free, Avail uint64
}

// DirEntry is one READDIRPLUS entry.
type DirEntry struct {
	Name string
	FH   []byte // may be nil if the server did not return it
	Attr *Attr  // may be nil
}

// Options configure a Conn.
type Options struct {
	Host       string
	Export     string
	NFSPort    int // 0 = ask the portmapper
	MountPort  int // 0 = ask the portmapper
	UID, GID   uint32
	Privileged bool
	Timeout    time.Duration // per call; also connect timeout
	Machine    string
}

// Conn is a mounted NFSv3 export.
type Conn struct {
	opt  Options
	auth Auth
	rpc  *Client
	Root []byte
	Info FSInfo
}

// Dial mounts the export and connects to the NFS service.
func Dial(ctx context.Context, opt Options) (*Conn, error) {
	if opt.Timeout <= 0 {
		opt.Timeout = 15 * time.Second
	}
	if opt.Machine == "" {
		opt.Machine, _ = os.Hostname()
		if opt.Machine == "" {
			opt.Machine = "nasdrive"
		}
	}
	dopt := DialOptions{Timeout: opt.Timeout, Privileged: opt.Privileged}
	auth := AuthSys(opt.Machine, opt.UID, opt.GID)
	cctx, cancel := context.WithTimeout(ctx, opt.Timeout)
	defer cancel()
	root, err := Mount(cctx, opt.Host, opt.MountPort, opt.Export, auth, dopt)
	if err != nil {
		return nil, err
	}
	port := opt.NFSPort
	if port == 0 {
		if port, err = GetPort(cctx, opt.Host, NFSProg, nfsVers, dopt); err != nil {
			return nil, err
		}
	}
	c := &Conn{
		opt:  opt,
		auth: auth,
		rpc:  NewClient(net.JoinHostPort(opt.Host, strconv.Itoa(port)), dopt),
		Root: root,
	}
	info, err := c.FSInfo(ctx, root)
	if err != nil {
		c.rpc.Close()
		return nil, err
	}
	c.Info = info
	return c, nil
}

// Close unmounts (best effort) and closes connections.
func (c *Conn) Close() {
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	Unmount(ctx, c.opt.Host, c.opt.MountPort, c.opt.Export, c.auth, DialOptions{Timeout: 3 * time.Second, Privileged: c.opt.Privileged})
	c.rpc.Close()
}

// call runs proc with a per-call timeout and returns the decoder positioned
// after a successful nfsstat3. On an NFS error the decoder is returned too so
// callers can skip failure data; err is *Error.
func (c *Conn) call(ctx context.Context, op string, proc uint32, args []byte, idempotent bool) (*dec, error) {
	cctx, cancel := context.WithTimeout(ctx, c.opt.Timeout)
	defer cancel()
	res, err := c.rpc.Call(cctx, NFSProg, nfsVers, proc, c.auth, args, idempotent)
	if err != nil {
		if cctx.Err() == context.DeadlineExceeded && ctx.Err() == nil {
			return nil, fmt.Errorf("nfs %s: %w", op, context.DeadlineExceeded)
		}
		return nil, err
	}
	d := &dec{b: res}
	st := d.u32()
	if d.err != nil {
		return nil, d.err
	}
	if st != 0 {
		return d, &Error{Status: st, Op: op}
	}
	return d, nil
}

func dirop(e *enc, dir []byte, name string) {
	e.opaque(dir)
	e.str(name)
}

func (c *Conn) GetAttr(ctx context.Context, fh []byte) (Attr, error) {
	var e enc
	e.opaque(fh)
	d, err := c.call(ctx, "GETATTR", procGetAttr, e.b, true)
	if err != nil {
		return Attr{}, err
	}
	a := decFattr(d)
	return a, d.err
}

func (c *Conn) SetAttr(ctx context.Context, fh []byte, s SetAttrs) error {
	var e enc
	e.opaque(fh)
	s.encode(&e)
	e.bool(false) // no guard
	_, err := c.call(ctx, "SETATTR", procSetAttr, e.b, true)
	return err
}

func (c *Conn) Lookup(ctx context.Context, dir []byte, name string) ([]byte, Attr, error) {
	var e enc
	dirop(&e, dir, name)
	d, err := c.call(ctx, "LOOKUP", procLookup, e.b, true)
	if err != nil {
		return nil, Attr{}, err
	}
	fh := d.opaque(maxFH)
	a := decPostOpAttr(d)
	if d.err != nil {
		return nil, Attr{}, d.err
	}
	if a == nil {
		at, err := c.GetAttr(ctx, fh)
		return fh, at, err
	}
	return fh, *a, nil
}

// Read reads up to count bytes at off.
func (c *Conn) Read(ctx context.Context, fh []byte, off uint64, count uint32) ([]byte, bool, error) {
	var e enc
	e.opaque(fh)
	e.u64(off)
	e.u32(count)
	d, err := c.call(ctx, "READ", procRead, e.b, true)
	if err != nil {
		return nil, false, err
	}
	decPostOpAttr(d)
	d.u32() // count
	eof := d.bool()
	data := d.opaque(int(count) + 4096)
	return data, eof, d.err
}

// Write writes data at off and returns the number of bytes written.
func (c *Conn) Write(ctx context.Context, fh []byte, off uint64, data []byte, stable uint32) (uint32, error) {
	var e enc
	e.opaque(fh)
	e.u64(off)
	e.u32(uint32(len(data)))
	e.u32(stable)
	e.opaque(data)
	d, err := c.call(ctx, "WRITE", procWrite, e.b, true)
	if err != nil {
		return 0, err
	}
	decWcc(d)
	n := d.u32()
	return n, d.err
}

func (c *Conn) Commit(ctx context.Context, fh []byte) error {
	var e enc
	e.opaque(fh)
	e.u64(0)
	e.u32(0)
	_, err := c.call(ctx, "COMMIT", procCommit, e.b, true)
	return err
}

// createLike handles CREATE and MKDIR replies, falling back to LOOKUP when
// the server omits the new handle.
func (c *Conn) createLike(ctx context.Context, d *dec, dir []byte, name string) ([]byte, Attr, error) {
	fh := decPostOpFH(d)
	a := decPostOpAttr(d)
	if d.err != nil {
		return nil, Attr{}, d.err
	}
	if fh == nil {
		return c.Lookup(ctx, dir, name)
	}
	if a == nil {
		at, err := c.GetAttr(ctx, fh)
		return fh, at, err
	}
	return fh, *a, nil
}

// Create creates (or truncates, if exclusive is false) a regular file.
func (c *Conn) Create(ctx context.Context, dir []byte, name string, mode uint32, guarded bool) ([]byte, Attr, error) {
	var e enc
	dirop(&e, dir, name)
	if guarded {
		e.u32(1) // GUARDED
	} else {
		e.u32(0) // UNCHECKED
	}
	SetAttrs{Mode: &mode}.encode(&e)
	d, err := c.call(ctx, "CREATE", procCreate, e.b, false)
	if err != nil {
		return nil, Attr{}, err
	}
	return c.createLike(ctx, d, dir, name)
}

func (c *Conn) Mkdir(ctx context.Context, dir []byte, name string, mode uint32) ([]byte, Attr, error) {
	var e enc
	dirop(&e, dir, name)
	SetAttrs{Mode: &mode}.encode(&e)
	d, err := c.call(ctx, "MKDIR", procMkdir, e.b, false)
	if err != nil {
		return nil, Attr{}, err
	}
	return c.createLike(ctx, d, dir, name)
}

func (c *Conn) Remove(ctx context.Context, dir []byte, name string) error {
	var e enc
	dirop(&e, dir, name)
	_, err := c.call(ctx, "REMOVE", procRemove, e.b, false)
	return err
}

func (c *Conn) Rmdir(ctx context.Context, dir []byte, name string) error {
	var e enc
	dirop(&e, dir, name)
	_, err := c.call(ctx, "RMDIR", procRmdir, e.b, false)
	return err
}

func (c *Conn) Rename(ctx context.Context, fromDir []byte, fromName string, toDir []byte, toName string) error {
	var e enc
	dirop(&e, fromDir, fromName)
	dirop(&e, toDir, toName)
	_, err := c.call(ctx, "RENAME", procRename, e.b, false)
	return err
}

// ReadDirPlus lists a directory with attributes and handles.
func (c *Conn) ReadDirPlus(ctx context.Context, dir []byte) ([]DirEntry, error) {
	var out []DirEntry
	var cookie uint64
	verf := make([]byte, 8)
	for page := 0; page < 100000; page++ {
		var e enc
		e.opaque(dir)
		e.u64(cookie)
		e.fixed(verf)
		e.u32(64 << 10)  // dircount
		e.u32(512 << 10) // maxcount
		d, err := c.call(ctx, "READDIRPLUS", procReadDirPlus, e.b, true)
		if err != nil {
			return nil, err
		}
		decPostOpAttr(d)
		copy(verf, d.fixed(8))
		n := 0
		for d.bool() {
			d.u64() // fileid
			name := d.str(4096)
			cookie = d.u64()
			a := decPostOpAttr(d)
			fh := decPostOpFH(d)
			if d.err != nil {
				break
			}
			n++
			if name == "." || name == ".." {
				continue
			}
			out = append(out, DirEntry{Name: name, FH: fh, Attr: a})
		}
		eof := d.bool()
		if d.err != nil {
			return nil, d.err
		}
		if eof || n == 0 {
			return out, nil
		}
	}
	return out, fmt.Errorf("nfs READDIRPLUS: too many pages")
}

func (c *Conn) FSInfo(ctx context.Context, fh []byte) (FSInfo, error) {
	var e enc
	e.opaque(fh)
	d, err := c.call(ctx, "FSINFO", procFSInfo, e.b, true)
	if err != nil {
		return FSInfo{}, err
	}
	decPostOpAttr(d)
	var i FSInfo
	i.RTMax = d.u32()
	i.RTPref = d.u32()
	d.u32() // rtmult
	i.WTMax = d.u32()
	i.WTPref = d.u32()
	return i, d.err
}

func (c *Conn) FSStat(ctx context.Context, fh []byte) (FSStat, error) {
	var e enc
	e.opaque(fh)
	d, err := c.call(ctx, "FSSTAT", procFSStat, e.b, true)
	if err != nil {
		return FSStat{}, err
	}
	decPostOpAttr(d)
	var s FSStat
	s.Total = d.u64()
	s.Free = d.u64()
	s.Avail = d.u64()
	return s, d.err
}
