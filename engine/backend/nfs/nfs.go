// Package nfs is an rclone backend for NFSv3 servers (e.g. NAS devices),
// built on NasDrive's nfsclient. It lets NFS exports be mounted as drive
// letters through the same cmount + VFS pipeline as WebDAV.
package nfs

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"path"
	"strings"
	"sync"
	"time"
	"unicode/utf8"

	"github.com/nasdrive/nasdrive/engine/nfsclient"
	"github.com/rclone/rclone/fs"
	"github.com/rclone/rclone/fs/config/configmap"
	"github.com/rclone/rclone/fs/config/configstruct"
	"github.com/rclone/rclone/fs/fserrors"
	"github.com/rclone/rclone/fs/hash"
	"github.com/rclone/rclone/lib/pacer"
)

const (
	tmpPrefix   = ".nasdrive-tmp-"
	maxChunk    = 1 << 20
	inFlight    = 8 // concurrent READ/WRITE RPCs per transfer
	fileMode    = 0o644
	dirMode     = 0o755
	minSleep    = 50 * time.Millisecond
	maxSleep    = 2 * time.Second
	maxHandles  = 20000
	maxNameSize = 255
)

func init() {
	fs.Register(&fs.RegInfo{
		Name:        "nfs",
		Description: "NFSv3 server (NAS)",
		NewFs:       NewFs,
		Options: []fs.Option{{
			Name:      "host",
			Help:      "NFS server host name or IP address.",
			Required:  true,
			Sensitive: true,
		}, {
			Name:     "export",
			Help:     "Exported path on the server, e.g. /volume1/share (see showmount -e).",
			Required: true,
		}, {
			Name:     "port",
			Help:     "NFS service port. 0 asks the portmapper (port 111).",
			Default:  0,
			Advanced: true,
		}, {
			Name:     "mount_port",
			Help:     "MOUNT service port. 0 asks the portmapper (port 111).",
			Default:  0,
			Advanced: true,
		}, {
			Name:    "uid",
			Help:    "User ID sent with requests (AUTH_SYS).",
			Default: 0,
		}, {
			Name:    "gid",
			Help:    "Group ID sent with requests (AUTH_SYS).",
			Default: 0,
		}, {
			Name:     "privileged_port",
			Help:     "Connect from a source port below 1024, which most NAS servers require by default.",
			Default:  true,
			Advanced: true,
		}, {
			Name:     "rpc_timeout",
			Help:     "Timeout for each NFS request.",
			Default:  fs.Duration(15 * time.Second),
			Advanced: true,
		}},
	})
}

// Options defines the configuration for this backend.
type Options struct {
	Host           string      `config:"host"`
	Export         string      `config:"export"`
	Port           int         `config:"port"`
	MountPort      int         `config:"mount_port"`
	UID            int         `config:"uid"`
	GID            int         `config:"gid"`
	PrivilegedPort bool        `config:"privileged_port"`
	RPCTimeout     fs.Duration `config:"rpc_timeout"`
}

// Fs is an NFS export (or a directory within it).
type Fs struct {
	name     string
	root     string // path inside the export, no leading/trailing slash
	opt      Options
	features *fs.Features
	conn     *nfsclient.Conn
	pacer    *fs.Pacer

	hmu     sync.Mutex
	handles map[string][]byte // export-relative path -> file handle
}

// Object is a regular file.
type Object struct {
	fs      *Fs
	remote  string
	fh      []byte
	size    int64
	modTime time.Time
}

// NewFs connects to the server and mounts the export.
func NewFs(ctx context.Context, name, root string, m configmap.Mapper) (fs.Fs, error) {
	opt := new(Options)
	if err := configstruct.Set(m, opt); err != nil {
		return nil, err
	}
	if opt.Host == "" || opt.Export == "" {
		return nil, errors.New("nfs: host and export are required")
	}
	if !strings.HasPrefix(opt.Export, "/") {
		opt.Export = "/" + opt.Export
	}
	timeout := time.Duration(opt.RPCTimeout)
	if ci := fs.GetConfig(ctx); ci.ConnectTimeout > 0 && time.Duration(ci.ConnectTimeout) < timeout {
		timeout = time.Duration(ci.ConnectTimeout)
	}
	conn, err := nfsclient.Dial(ctx, nfsclient.Options{
		Host:       opt.Host,
		Export:     opt.Export,
		NFSPort:    opt.Port,
		MountPort:  opt.MountPort,
		UID:        uint32(opt.UID),
		GID:        uint32(opt.GID),
		Privileged: opt.PrivilegedPort,
		Timeout:    timeout,
	})
	if err != nil {
		return nil, err
	}
	f := &Fs{
		name:    name,
		root:    strings.Trim(path.Clean("/"+root), "/"),
		opt:     *opt,
		conn:    conn,
		pacer:   fs.NewPacer(ctx, retryCalculator{}),
		handles: map[string][]byte{"": conn.Root},
	}
	f.features = (&fs.Features{
		CanHaveEmptyDirectories: true,
		ReadMimeType:            false,
	}).Fill(ctx, f)

	if f.root != "" {
		_, attr, err := f.stat(ctx, f.root)
		if err == nil && !attr.IsDir() {
			f.root = parentOf(f.root)
			return f, fs.ErrorIsFile
		}
		if err != nil && !nfsclient.IsStatus(err, nfsclient.ErrNoEnt) {
			conn.Close()
			return nil, err
		}
	}
	return f, nil
}

func parentOf(p string) string {
	d := path.Dir(p)
	if d == "." || d == "/" {
		return ""
	}
	return d
}

// retryCalculator never delays successful calls (rclone's default pacer
// spaces every call by >= 10ms, capping a LAN NFS transfer at ~100 RPC/s) and
// backs off exponentially only while retrying.
type retryCalculator struct{}

func (retryCalculator) Calculate(state pacer.State) time.Duration {
	if state.ConsecutiveRetries == 0 {
		return 0
	}
	d := max(state.SleepTime*2, minSleep)
	return min(d, maxSleep)
}

// shouldRetry decides whether an idempotent operation is retried.
func shouldRetry(ctx context.Context, err error) (bool, error) {
	if err == nil {
		return false, nil
	}
	if fserrors.ContextError(ctx, &err) {
		return false, err
	}
	var ce *nfsclient.ErrConnection
	if errors.As(err, &ce) || errors.Is(err, context.DeadlineExceeded) || nfsclient.IsStatus(err, nfsclient.ErrJukebox) {
		return true, err
	}
	return fserrors.ShouldRetry(err), err
}

func (f *Fs) abs(remote string) string {
	return strings.Trim(path.Join(f.root, remote), "/")
}

// --- handle cache --------------------------------------------------------

func (f *Fs) cacheGet(p string) []byte {
	f.hmu.Lock()
	defer f.hmu.Unlock()
	return f.handles[p]
}

func (f *Fs) cachePut(p string, fh []byte) {
	if fh == nil {
		return
	}
	f.hmu.Lock()
	defer f.hmu.Unlock()
	if len(f.handles) > maxHandles {
		f.handles = map[string][]byte{"": f.conn.Root}
	}
	f.handles[p] = fh
}

// cacheDrop forgets p and everything below it.
func (f *Fs) cacheDrop(p string) {
	f.hmu.Lock()
	defer f.hmu.Unlock()
	if p == "" {
		f.handles = map[string][]byte{"": f.conn.Root}
		return
	}
	for k := range f.handles {
		if k == p || strings.HasPrefix(k, p+"/") {
			delete(f.handles, k)
		}
	}
}

// lookup resolves export-relative path p to a handle and fresh attributes.
func (f *Fs) lookupOnce(ctx context.Context, p string) ([]byte, nfsclient.Attr, error) {
	if p == "" {
		var a nfsclient.Attr
		err := f.pacer.Call(func() (bool, error) {
			var err error
			a, err = f.conn.GetAttr(ctx, f.conn.Root)
			return shouldRetry(ctx, err)
		})
		return f.conn.Root, a, err
	}
	dir, err := f.dirHandle(ctx, parentOf(p))
	if err != nil {
		return nil, nfsclient.Attr{}, err
	}
	var fh []byte
	var a nfsclient.Attr
	err = f.pacer.Call(func() (bool, error) {
		var err error
		fh, a, err = f.conn.Lookup(ctx, dir, path.Base(p))
		return shouldRetry(ctx, err)
	})
	if err != nil {
		return nil, a, err
	}
	f.cachePut(p, fh)
	return fh, a, nil
}

// stat looks p up, retrying once from scratch if a cached handle went stale.
func (f *Fs) stat(ctx context.Context, p string) ([]byte, nfsclient.Attr, error) {
	fh, a, err := f.lookupOnce(ctx, p)
	if isStale(err) {
		f.cacheDrop("")
		fh, a, err = f.lookupOnce(ctx, p)
	}
	return fh, a, err
}

func isStale(err error) bool {
	return nfsclient.IsStatus(err, nfsclient.ErrStale) || nfsclient.IsStatus(err, nfsclient.ErrBadHandle)
}

// dirHandle returns the handle of directory p, using the cache when possible.
func (f *Fs) dirHandle(ctx context.Context, p string) ([]byte, error) {
	if fh := f.cacheGet(p); fh != nil {
		return fh, nil
	}
	fh, a, err := f.lookupOnce(ctx, p)
	if err != nil {
		return nil, err
	}
	if !a.IsDir() {
		return nil, &nfsclient.Error{Status: nfsclient.ErrNotDir, Op: "LOOKUP"}
	}
	return fh, nil
}

// withStaleRetry runs op, and if it fails with a stale handle, clears the
// handle cache and runs it once more.
func (f *Fs) withStaleRetry(op func() error) error {
	err := op()
	if isStale(err) {
		f.cacheDrop("")
		err = op()
	}
	return err
}

// --- fs.Info ---------------------------------------------------------------

func (f *Fs) Name() string             { return f.name }
func (f *Fs) Root() string             { return f.root }
func (f *Fs) Features() *fs.Features   { return f.features }
func (f *Fs) Precision() time.Duration { return time.Microsecond }
func (f *Fs) Hashes() hash.Set         { return hash.Set(hash.None) }
func (f *Fs) String() string {
	return fmt.Sprintf("nfs://%s%s/%s", f.opt.Host, f.opt.Export, f.root)
}

// --- listing ---------------------------------------------------------------

// List the objects and directories in dir.
func (f *Fs) List(ctx context.Context, dir string) (fs.DirEntries, error) {
	p := f.abs(dir)
	var ents []nfsclient.DirEntry
	err := f.withStaleRetry(func() error {
		fh, err := f.dirHandle(ctx, p)
		if err != nil {
			return err
		}
		return f.pacer.Call(func() (bool, error) {
			var err error
			ents, err = f.conn.ReadDirPlus(ctx, fh)
			return shouldRetry(ctx, err)
		})
	})
	if err != nil {
		if nfsclient.IsStatus(err, nfsclient.ErrNoEnt) || nfsclient.IsStatus(err, nfsclient.ErrNotDir) {
			return nil, fs.ErrorDirNotFound
		}
		return nil, err
	}
	entries := make(fs.DirEntries, 0, len(ents))
	for _, e := range ents {
		if strings.HasPrefix(e.Name, tmpPrefix) {
			continue
		}
		childAbs := path.Join(p, e.Name)
		remote := path.Join(dir, e.Name)
		fh, attr := e.FH, e.Attr
		if fh == nil || attr == nil {
			var a nfsclient.Attr
			var err error
			fh, a, err = f.stat(ctx, childAbs)
			if err != nil {
				fs.Debugf(f, "skipping %q: %v", remote, err)
				continue
			}
			attr = &a
		}
		f.cachePut(childAbs, fh)
		switch attr.Type {
		case nfsclient.TypeDir:
			entries = append(entries, fs.NewDir(remote, attr.Mtime))
		case nfsclient.TypeReg:
			entries = append(entries, &Object{fs: f, remote: remote, fh: fh, size: int64(attr.Size), modTime: attr.Mtime})
		default:
			fs.Debugf(f, "skipping %q: not a regular file or directory (type %d)", remote, attr.Type)
		}
	}
	return entries, nil
}

// NewObject finds the object at remote.
func (f *Fs) NewObject(ctx context.Context, remote string) (fs.Object, error) {
	fh, a, err := f.stat(ctx, f.abs(remote))
	if err != nil {
		if nfsclient.IsStatus(err, nfsclient.ErrNoEnt) || nfsclient.IsStatus(err, nfsclient.ErrNotDir) {
			return nil, fs.ErrorObjectNotFound
		}
		return nil, err
	}
	if a.IsDir() {
		return nil, fs.ErrorIsDir
	}
	if a.Type != nfsclient.TypeReg {
		return nil, fs.ErrorObjectNotFound
	}
	return &Object{fs: f, remote: remote, fh: fh, size: int64(a.Size), modTime: a.Mtime}, nil
}

// --- directories -------------------------------------------------------------

// mkdirAll creates export-relative directory p and its parents.
func (f *Fs) mkdirAll(ctx context.Context, p string) ([]byte, error) {
	if p == "" {
		return f.conn.Root, nil
	}
	if fh := f.cacheGet(p); fh != nil {
		return fh, nil
	}
	fh, a, err := f.stat(ctx, p)
	if err == nil {
		if !a.IsDir() {
			return nil, fmt.Errorf("%q: %w", p, fs.ErrorIsFile)
		}
		return fh, nil
	}
	if !nfsclient.IsStatus(err, nfsclient.ErrNoEnt) {
		return nil, err
	}
	parent, err := f.mkdirAll(ctx, parentOf(p))
	if err != nil {
		return nil, err
	}
	fh, _, err = f.conn.Mkdir(ctx, parent, path.Base(p), dirMode)
	if nfsclient.IsStatus(err, nfsclient.ErrExist) {
		fh, _, err = f.stat(ctx, p)
	}
	if err != nil {
		return nil, err
	}
	f.cachePut(p, fh)
	return fh, nil
}

// Mkdir creates dir (and parents) if it doesn't exist.
func (f *Fs) Mkdir(ctx context.Context, dir string) error {
	_, err := f.mkdirAll(ctx, f.abs(dir))
	return err
}

// Rmdir removes an empty directory.
func (f *Fs) Rmdir(ctx context.Context, dir string) error {
	p := f.abs(dir)
	if p == "" {
		return errors.New("nfs: can't remove the export root")
	}
	err := f.withStaleRetry(func() error {
		parent, err := f.dirHandle(ctx, parentOf(p))
		if err != nil {
			return err
		}
		return f.conn.Rmdir(ctx, parent, path.Base(p))
	})
	f.cacheDrop(p)
	switch {
	case err == nil:
		return nil
	case nfsclient.IsStatus(err, nfsclient.ErrNoEnt):
		return fs.ErrorDirNotFound
	case nfsclient.IsStatus(err, nfsclient.ErrNotEmpty), nfsclient.IsStatus(err, nfsclient.ErrExist):
		return fs.ErrorDirectoryNotEmpty
	}
	return err
}

// --- upload ----------------------------------------------------------------

func tmpName(base string) string {
	b := make([]byte, 6)
	_, _ = rand.Read(b)
	n := tmpPrefix + hex.EncodeToString(b) + "-" + base
	for len(n) > maxNameSize {
		_, size := utf8.DecodeLastRuneInString(n)
		n = n[:len(n)-size]
	}
	return n
}

func (f *Fs) chunkSize(pref, max uint32) int {
	n := int(pref)
	if n <= 0 {
		n = int(max)
	}
	if n <= 0 || n > maxChunk {
		n = maxChunk
	}
	return n
}

// writeAll writes in to fh with up to inFlight concurrent WRITEs and returns
// the number of bytes written.
func (f *Fs) writeAll(ctx context.Context, fh []byte, in io.Reader) (int64, error) {
	cs := f.chunkSize(f.conn.Info.WTPref, f.conn.Info.WTMax)
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	sem := make(chan struct{}, inFlight)
	var wg sync.WaitGroup
	var emu sync.Mutex
	var firstErr error
	setErr := func(err error) {
		emu.Lock()
		if firstErr == nil {
			firstErr = err
			cancel()
		}
		emu.Unlock()
	}
	var off int64
	for {
		buf := make([]byte, cs)
		n, rerr := io.ReadFull(in, buf)
		if n > 0 {
			sem <- struct{}{}
			wg.Add(1)
			go func(data []byte, at int64) {
				defer func() { <-sem; wg.Done() }()
				for len(data) > 0 {
					var w uint32
					err := f.pacer.Call(func() (bool, error) {
						var err error
						w, err = f.conn.Write(ctx, fh, uint64(at), data, nfsclient.Unstable)
						return shouldRetry(ctx, err)
					})
					if err != nil {
						setErr(err)
						return
					}
					if w == 0 {
						setErr(errors.New("nfs WRITE: server wrote 0 bytes"))
						return
					}
					data, at = data[w:], at+int64(w)
				}
			}(buf[:n], off)
			off += int64(n)
		}
		if rerr == io.EOF || rerr == io.ErrUnexpectedEOF {
			break
		}
		if rerr != nil {
			setErr(rerr)
			break
		}
		emu.Lock()
		stop := firstErr != nil
		emu.Unlock()
		if stop {
			break
		}
	}
	wg.Wait()
	if firstErr != nil {
		return off, firstErr
	}
	err := f.pacer.Call(func() (bool, error) {
		err := f.conn.Commit(ctx, fh)
		return shouldRetry(ctx, err)
	})
	return off, err
}

// upload writes in to remote atomically via a temporary file and rename.
func (f *Fs) upload(ctx context.Context, remote string, in io.Reader, modTime time.Time, size int64) (*Object, error) {
	p := f.abs(remote)
	if p == "" {
		return nil, errors.New("nfs: can't upload to the export root")
	}
	dir, err := f.mkdirAll(ctx, parentOf(p))
	if err != nil {
		return nil, err
	}
	base := path.Base(p)
	tmp := tmpName(base)
	fh, _, err := f.conn.Create(ctx, dir, tmp, fileMode, false)
	if isStale(err) {
		f.cacheDrop("")
		if dir, err = f.mkdirAll(ctx, parentOf(p)); err == nil {
			fh, _, err = f.conn.Create(ctx, dir, tmp, fileMode, false)
		}
	}
	if err != nil {
		return nil, fmt.Errorf("create %q: %w", remote, err)
	}
	cleanup := func() { _ = f.conn.Remove(context.Background(), dir, tmp) }

	written, err := f.writeAll(ctx, fh, in)
	if err == nil && size >= 0 && written != size {
		err = fmt.Errorf("nfs: wrote %d bytes, expected %d", written, size)
	}
	if err == nil {
		mt := modTime
		err = f.conn.SetAttr(ctx, fh, nfsclient.SetAttrs{Mtime: &mt})
	}
	if err == nil {
		err = f.conn.Rename(ctx, dir, tmp, dir, base)
	}
	if err != nil {
		cleanup()
		return nil, err
	}
	// Some servers derive handles from paths, so the temporary file's handle
	// is not valid after the rename: look the final name up again.
	f.cacheDrop(p)
	fh, a, err := f.stat(ctx, p)
	if err != nil {
		return nil, err
	}
	return &Object{fs: f, remote: remote, fh: fh, size: int64(a.Size), modTime: a.Mtime}, nil
}

// Put uploads a new object.
func (f *Fs) Put(ctx context.Context, in io.Reader, src fs.ObjectInfo, options ...fs.OpenOption) (fs.Object, error) {
	return f.upload(ctx, src.Remote(), in, src.ModTime(ctx), src.Size())
}

// PutStream uploads an object of unknown size.
func (f *Fs) PutStream(ctx context.Context, in io.Reader, src fs.ObjectInfo, options ...fs.OpenOption) (fs.Object, error) {
	return f.upload(ctx, src.Remote(), in, src.ModTime(ctx), -1)
}

// --- server side operations ----------------------------------------------------

func (f *Fs) sameExport(o *Fs) bool {
	return o != nil && o.opt.Host == f.opt.Host && o.opt.Export == f.opt.Export
}

// Move renames an object on the server.
func (f *Fs) Move(ctx context.Context, src fs.Object, remote string) (fs.Object, error) {
	so, ok := src.(*Object)
	if !ok || !f.sameExport(so.fs) {
		return nil, fs.ErrorCantMove
	}
	from, to := so.fs.abs(so.remote), f.abs(remote)
	err := f.withStaleRetry(func() error {
		fromDir, err := f.dirHandle(ctx, parentOf(from))
		if err != nil {
			return err
		}
		toDir, err := f.mkdirAll(ctx, parentOf(to))
		if err != nil {
			return err
		}
		return f.conn.Rename(ctx, fromDir, path.Base(from), toDir, path.Base(to))
	})
	if err != nil {
		if nfsclient.IsStatus(err, nfsclient.ErrNoEnt) {
			return nil, fs.ErrorObjectNotFound
		}
		return nil, err
	}
	f.cacheDrop(from)
	so.fs.cacheDrop(from)
	f.cacheDrop(to)
	return f.NewObject(ctx, remote)
}

// DirMove renames a directory on the server.
func (f *Fs) DirMove(ctx context.Context, src fs.Fs, srcRemote, dstRemote string) error {
	sf, ok := src.(*Fs)
	if !ok || !f.sameExport(sf) {
		return fs.ErrorCantDirMove
	}
	from, to := sf.abs(srcRemote), f.abs(dstRemote)
	if from == "" || to == "" {
		return fs.ErrorCantDirMove
	}
	if _, _, err := f.stat(ctx, to); err == nil {
		return fs.ErrorDirExists
	} else if !nfsclient.IsStatus(err, nfsclient.ErrNoEnt) {
		return err
	}
	err := f.withStaleRetry(func() error {
		fromDir, err := f.dirHandle(ctx, parentOf(from))
		if err != nil {
			return err
		}
		toDir, err := f.mkdirAll(ctx, parentOf(to))
		if err != nil {
			return err
		}
		return f.conn.Rename(ctx, fromDir, path.Base(from), toDir, path.Base(to))
	})
	f.cacheDrop(from)
	sf.cacheDrop(from)
	if nfsclient.IsStatus(err, nfsclient.ErrNoEnt) {
		return fs.ErrorDirNotFound
	}
	return err
}

// About returns capacity information from FSSTAT.
func (f *Fs) About(ctx context.Context) (*fs.Usage, error) {
	var st nfsclient.FSStat
	err := f.pacer.Call(func() (bool, error) {
		var err error
		st, err = f.conn.FSStat(ctx, f.conn.Root)
		return shouldRetry(ctx, err)
	})
	if err != nil {
		return nil, err
	}
	total, used, free := int64(st.Total), int64(st.Total-st.Free), int64(st.Avail)
	return &fs.Usage{Total: &total, Used: &used, Free: &free}, nil
}

// Shutdown unmounts the export and closes connections.
func (f *Fs) Shutdown(ctx context.Context) error {
	f.conn.Close()
	return nil
}

// --- Object ------------------------------------------------------------------

func (o *Object) Fs() fs.Info    { return o.fs }
func (o *Object) Remote() string { return o.remote }
func (o *Object) String() string {
	if o == nil {
		return "<nil>"
	}
	return o.remote
}
func (o *Object) Size() int64                           { return o.size }
func (o *Object) ModTime(ctx context.Context) time.Time { return o.modTime }
func (o *Object) Storable() bool                        { return true }
func (o *Object) Hash(ctx context.Context, t hash.Type) (string, error) {
	return "", hash.ErrUnsupported
}

// handle returns a valid handle, re-resolving if the cached one is stale.
func (o *Object) refresh(ctx context.Context) error {
	fh, a, err := o.fs.stat(ctx, o.fs.abs(o.remote))
	if err != nil {
		if nfsclient.IsStatus(err, nfsclient.ErrNoEnt) {
			return fs.ErrorObjectNotFound
		}
		return err
	}
	o.fh, o.size, o.modTime = fh, int64(a.Size), a.Mtime
	return nil
}

// SetModTime sets the modification time.
func (o *Object) SetModTime(ctx context.Context, t time.Time) error {
	err := o.fs.conn.SetAttr(ctx, o.fh, nfsclient.SetAttrs{Mtime: &t})
	if isStale(err) {
		if err = o.refresh(ctx); err == nil {
			err = o.fs.conn.SetAttr(ctx, o.fh, nfsclient.SetAttrs{Mtime: &t})
		}
	}
	if err != nil {
		return err
	}
	a, err := o.fs.conn.GetAttr(ctx, o.fh)
	if err == nil {
		o.modTime = a.Mtime
	} else {
		o.modTime = t
	}
	return nil
}

// Open returns a reader for the object, honouring range/seek options.
func (o *Object) Open(ctx context.Context, options ...fs.OpenOption) (io.ReadCloser, error) {
	var offset, limit int64 = 0, -1
	for _, option := range options {
		switch x := option.(type) {
		case *fs.SeekOption:
			offset = x.Offset
		case *fs.RangeOption:
			offset, limit = x.Decode(o.size)
		default:
			if option.Mandatory() {
				fs.Logf(o, "Unsupported mandatory option: %v", option)
			}
		}
	}
	// Re-check the handle so a stale one fails here rather than mid-read.
	if _, err := o.fs.conn.GetAttr(ctx, o.fh); isStale(err) {
		if err := o.refresh(ctx); err != nil {
			return nil, err
		}
	}
	end := int64(-1)
	if limit >= 0 {
		end = offset + limit
	}
	return newReader(ctx, o.fs, o.fh, offset, end), nil
}

// Update replaces the object's content.
func (o *Object) Update(ctx context.Context, in io.Reader, src fs.ObjectInfo, options ...fs.OpenOption) error {
	no, err := o.fs.upload(ctx, o.remote, in, src.ModTime(ctx), src.Size())
	if err != nil {
		return err
	}
	*o = *no
	return nil
}

// Remove deletes the object.
func (o *Object) Remove(ctx context.Context) error {
	p := o.fs.abs(o.remote)
	err := o.fs.withStaleRetry(func() error {
		dir, err := o.fs.dirHandle(ctx, parentOf(p))
		if err != nil {
			return err
		}
		return o.fs.conn.Remove(ctx, dir, path.Base(p))
	})
	o.fs.cacheDrop(p)
	if nfsclient.IsStatus(err, nfsclient.ErrNoEnt) {
		return fs.ErrorObjectNotFound
	}
	return err
}

// Check interfaces
var (
	_ fs.Fs          = (*Fs)(nil)
	_ fs.PutStreamer = (*Fs)(nil)
	_ fs.Mover       = (*Fs)(nil)
	_ fs.DirMover    = (*Fs)(nil)
	_ fs.Abouter     = (*Fs)(nil)
	_ fs.Shutdowner  = (*Fs)(nil)
	_ fs.Object      = (*Object)(nil)
)
