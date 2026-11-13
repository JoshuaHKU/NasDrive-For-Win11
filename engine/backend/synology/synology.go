// Package synology is an rclone backend for Synology DSM using the File
// Station Web API (the same API as DSM's File Station app). Unlike WebDAV or
// NFS it needs nothing enabled on the NAS beyond DSM itself, supports
// 2-step verification via device tokens, and reports real volume capacity.
package synology

import (
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"path"
	"strconv"
	"strings"
	"time"

	"github.com/rclone/rclone/fs"
	"github.com/rclone/rclone/fs/config/configmap"
	"github.com/rclone/rclone/fs/config/configstruct"
	"github.com/rclone/rclone/fs/config/obscure"
	"github.com/rclone/rclone/fs/fserrors"
	"github.com/rclone/rclone/fs/fshttp"
	"github.com/rclone/rclone/fs/hash"
	"github.com/rclone/rclone/lib/pacer"
)

const listLimit = 1000

func init() {
	fs.Register(&fs.RegInfo{
		Name:        "synology",
		Description: "Synology DSM (File Station API)",
		NewFs:       NewFs,
		Options: []fs.Option{{
			Name:     "url",
			Help:     "DSM address, e.g. https://nas:5001",
			Required: true,
		}, {
			Name:      "user",
			Help:      "DSM user name.",
			Required:  true,
			Sensitive: true,
		}, {
			Name:       "pass",
			Help:       "DSM password.",
			IsPassword: true,
			Required:   true,
		}, {
			Name:       "device_id",
			Help:       "Device token from a previous login with a 2-step verification code; skips the code.",
			IsPassword: true,
			Advanced:   true,
		}, {
			Name:     "device_name",
			Help:     "Device name shown in DSM's trusted devices list.",
			Default:  "NasDrive",
			Advanced: true,
		}},
	})
}

// Options defines the configuration for this backend.
type Options struct {
	URL        string `config:"url"`
	User       string `config:"user"`
	Pass       string `config:"pass"`
	DeviceID   string `config:"device_id"`
	DeviceName string `config:"device_name"`
}

// Fs is a DSM server, rooted at "share/folder" (empty root = list of shares).
type Fs struct {
	name     string
	root     string
	opt      Options
	features *fs.Features
	c        *client
	pacer    *fs.Pacer
}

// Object is a file on the DSM.
type Object struct {
	fs      *Fs
	remote  string
	size    int64
	modTime time.Time
}

// newClient builds the API client from options (shared with the rc login call).
func newAPIClient(ctx context.Context, opt *Options) (*client, error) {
	pass, err := obscure.Reveal(opt.Pass)
	if err != nil {
		return nil, fmt.Errorf("synology: couldn't decrypt password: %w", err)
	}
	did := ""
	if opt.DeviceID != "" {
		if did, err = obscure.Reveal(opt.DeviceID); err != nil {
			return nil, fmt.Errorf("synology: couldn't decrypt device_id: %w", err)
		}
	}
	u := strings.TrimRight(opt.URL, "/")
	if !strings.Contains(u, "://") {
		u = "https://" + u
	}
	return newClient(u, fshttp.NewClient(ctx), opt.User, pass, did, opt.DeviceName), nil
}

// deviceName returns the name DSM records for the trusted device. DSM binds
// a device token to this name, so every login must use the same value.
func deviceName(opt *Options) string {
	if opt.DeviceName != "" && opt.DeviceName != "NasDrive" {
		return opt.DeviceName
	}
	if host, _ := os.Hostname(); host != "" {
		return "NasDrive-" + host
	}
	return "NasDrive"
}

// Login performs an interactive login with a 2-step verification code for the
// remote string fsString and returns the (unobscured) device token to store.
func Login(ctx context.Context, m configmap.Mapper, otp string) (string, error) {
	opt := new(Options)
	if err := configstruct.Set(m, opt); err != nil {
		return "", err
	}
	opt.DeviceID = ""
	opt.DeviceName = deviceName(opt)
	c, err := newAPIClient(ctx, opt)
	if err != nil {
		return "", err
	}
	did, err := c.Login(ctx, otp)
	if err != nil {
		return "", err
	}
	c.Logout(ctx)
	if did == "" {
		return "", fmt.Errorf("synology: DSM did not issue a device token (%s)", LoginDiagnostics)
	}
	// Prove the token works the way later connections will use it.
	opt.DeviceID = obscure.MustObscure(did)
	check, err := newAPIClient(ctx, opt)
	if err != nil {
		return "", err
	}
	if _, err := check.Login(ctx, ""); err != nil {
		return "", fmt.Errorf("synology: the new device token was not accepted: %w", err)
	}
	check.Logout(ctx)
	return did, nil
}

// NewFs logs in to DSM.
func NewFs(ctx context.Context, name, root string, m configmap.Mapper) (fs.Fs, error) {
	opt := new(Options)
	if err := configstruct.Set(m, opt); err != nil {
		return nil, err
	}
	if opt.URL == "" || opt.User == "" {
		return nil, errors.New("synology: url and user are required")
	}
	opt.DeviceName = deviceName(opt)
	c, err := newAPIClient(ctx, opt)
	if err != nil {
		return nil, err
	}
	if _, err := c.Login(ctx, ""); err != nil {
		fs.Logf(nil, "synology: login to %s as %q (device %q, token %v) failed: %v", hostOf(opt.URL), opt.User, opt.DeviceName, opt.DeviceID != "", err)
		return nil, err
	}
	f := &Fs{
		name:  name,
		root:  strings.Trim(path.Clean("/"+root), "/"),
		opt:   *opt,
		c:     c,
		pacer: fs.NewPacer(ctx, pacer.NewDefault(pacer.MinSleep(10*time.Millisecond), pacer.MaxSleep(2*time.Second))),
	}
	f.features = (&fs.Features{CanHaveEmptyDirectories: true}).Fill(ctx, f)
	if f.root != "" && strings.Contains(f.root, "/") {
		info, err := f.getinfo(ctx, f.abs(""))
		if err == nil && !info.IsDir {
			f.root = parentOf(f.root)
			return f, fs.ErrorIsFile
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

// abs returns the DSM path ("/share/dir/file") of remote, or "/" for the share list.
func (f *Fs) abs(remote string) string {
	return "/" + strings.Trim(path.Join(f.root, remote), "/")
}

func shouldRetry(ctx context.Context, err error) (bool, error) {
	if err == nil {
		return false, nil
	}
	if fserrors.ContextError(ctx, &err) {
		return false, err
	}
	if IsCode(err, 402, 421) { // busy
		return true, err
	}
	var e *Error
	if errors.As(err, &e) {
		return false, err
	}
	return fserrors.ShouldRetry(err), err
}

func (f *Fs) call(ctx context.Context, api string, version int, method string, params map[string]string, out any) error {
	return f.pacer.Call(func() (bool, error) {
		return shouldRetry(ctx, f.c.Call(ctx, api, version, method, params, out))
	})
}

// --- fs.Info --------------------------------------------------------------

// fs.Info implementation: name, root, features and capabilities of the remote.
func (f *Fs) Name() string             { return f.name }
func (f *Fs) Root() string             { return f.root }
func (f *Fs) Features() *fs.Features   { return f.features }
func (f *Fs) Precision() time.Duration { return time.Second }
func (f *Fs) Hashes() hash.Set         { return hash.Set(hash.None) }
func (f *Fs) String() string           { return fmt.Sprintf("synology://%s/%s", hostOf(f.opt.URL), f.root) }

func hostOf(u string) string {
	u = strings.TrimPrefix(strings.TrimPrefix(u, "https://"), "http://")
	return strings.TrimRight(u, "/")
}

// --- API types --------------------------------------------------------------

type fileInfo struct {
	Name       string `json:"name"`
	Path       string `json:"path"`
	IsDir      bool   `json:"isdir"`
	Code       int    `json:"code"`
	Additional struct {
		Size int64 `json:"size"`
		Time struct {
			Mtime int64 `json:"mtime"`
		} `json:"time"`
		VolumeStatus struct {
			Free     int64 `json:"freespace"`
			Total    int64 `json:"totalspace"`
			ReadOnly bool  `json:"readonly"`
		} `json:"volume_status"`
	} `json:"additional"`
}

func (fi *fileInfo) mtime() time.Time { return time.Unix(fi.Additional.Time.Mtime, 0) }

func (f *Fs) listShares(ctx context.Context) ([]fileInfo, error) {
	var out struct {
		Shares []fileInfo `json:"shares"`
	}
	err := f.call(ctx, "SYNO.FileStation.List", 2, "list_share", map[string]string{
		// No "limit": DSM 7.2 answers limit=-1 with HTTP 502; the default lists all shares.
		"additional": qa("time", "volume_status"),
	}, &out)
	return out.Shares, err
}

func (f *Fs) listDir(ctx context.Context, dir string) ([]fileInfo, error) {
	var all []fileInfo
	for offset := 0; ; {
		var out struct {
			Total int        `json:"total"`
			Files []fileInfo `json:"files"`
		}
		err := f.call(ctx, "SYNO.FileStation.List", 2, "list", map[string]string{
			"folder_path": qs(dir), "additional": qa("size", "time"),
			"offset": strconv.Itoa(offset), "limit": strconv.Itoa(listLimit),
		}, &out)
		if err != nil {
			return nil, err
		}
		all = append(all, out.Files...)
		offset += len(out.Files)
		if len(out.Files) == 0 || offset >= out.Total {
			return all, nil
		}
	}
}

var errNotFound = errors.New("not found")

func (f *Fs) getinfo(ctx context.Context, p string) (*fileInfo, error) {
	if p == "/" {
		return &fileInfo{Name: "", Path: "/", IsDir: true}, nil
	}
	var out struct {
		Files []fileInfo `json:"files"`
	}
	err := f.call(ctx, "SYNO.FileStation.List", 2, "getinfo", map[string]string{
		"path": qa(p), "additional": qa("size", "time"),
	}, &out)
	if IsCode(err, 408) {
		return nil, errNotFound
	}
	if err != nil {
		return nil, err
	}
	if len(out.Files) == 0 || out.Files[0].Code == 408 || out.Files[0].Code != 0 && out.Files[0].Path == "" {
		return nil, errNotFound
	}
	if out.Files[0].Code != 0 {
		return nil, &Error{Code: out.Files[0].Code, API: "SYNO.FileStation.List", Method: "getinfo"}
	}
	return &out.Files[0], nil
}

// --- listing ----------------------------------------------------------------

// List the objects and directories in dir.
func (f *Fs) List(ctx context.Context, dir string) (fs.DirEntries, error) {
	p := f.abs(dir)
	var files []fileInfo
	var err error
	if p == "/" {
		files, err = f.listShares(ctx)
	} else {
		files, err = f.listDir(ctx, p)
		if IsCode(err, 408) {
			return nil, fs.ErrorDirNotFound
		}
		if IsCode(err, 400, 401) {
			// Listing a file (or a missing share) - distinguish.
			if info, ierr := f.getinfo(ctx, p); ierr != nil || !info.IsDir {
				return nil, fs.ErrorDirNotFound
			}
		}
	}
	if err != nil {
		return nil, err
	}
	entries := make(fs.DirEntries, 0, len(files))
	for i := range files {
		fi := &files[i]
		remote := path.Join(dir, fi.Name)
		if fi.IsDir {
			entries = append(entries, fs.NewDir(remote, fi.mtime()))
		} else {
			entries = append(entries, &Object{fs: f, remote: remote, size: fi.Additional.Size, modTime: fi.mtime()})
		}
	}
	return entries, nil
}

// NewObject finds the object at remote.
func (f *Fs) NewObject(ctx context.Context, remote string) (fs.Object, error) {
	info, err := f.getinfo(ctx, f.abs(remote))
	if errors.Is(err, errNotFound) {
		return nil, fs.ErrorObjectNotFound
	}
	if err != nil {
		return nil, err
	}
	if info.IsDir {
		return nil, fs.ErrorIsDir
	}
	return &Object{fs: f, remote: remote, size: info.Additional.Size, modTime: info.mtime()}, nil
}

// --- directories ------------------------------------------------------------

// Mkdir creates dir (and parents).
func (f *Fs) Mkdir(ctx context.Context, dir string) error {
	p := f.abs(dir)
	if p == "/" {
		return nil
	}
	parent, name := path.Split(p)
	parent = strings.TrimRight(parent, "/")
	if parent == "" {
		// A share: can't be created through File Station, just check it exists.
		if _, err := f.getinfo(ctx, p); err != nil {
			return fmt.Errorf("synology: shared folder %q does not exist (create it in DSM)", name)
		}
		return nil
	}
	if info, err := f.getinfo(ctx, p); err == nil {
		if !info.IsDir {
			return fs.ErrorIsFile
		}
		return nil
	}
	err := f.call(ctx, "SYNO.FileStation.CreateFolder", 2, "create", map[string]string{
		"folder_path": qa(parent), "name": qa(name), "force_parent": "true",
	}, nil)
	if IsCode(err, 414, 1100) {
		if info, ierr := f.getinfo(ctx, p); ierr == nil && info.IsDir {
			return nil
		}
	}
	return err
}

func (f *Fs) delete(ctx context.Context, p string) error {
	var out struct {
		TaskID string `json:"taskid"`
	}
	if err := f.call(ctx, "SYNO.FileStation.Delete", 2, "start", map[string]string{
		"path": qa(p), "recursive": "true",
	}, &out); err != nil {
		return err
	}
	return f.c.waitTask(ctx, "SYNO.FileStation.Delete", 2, out.TaskID)
}

// Rmdir removes an empty directory.
func (f *Fs) Rmdir(ctx context.Context, dir string) error {
	p := f.abs(dir)
	if strings.Count(p, "/") <= 1 {
		return errors.New("synology: can't remove a shared folder")
	}
	ents, err := f.listDir(ctx, p)
	if IsCode(err, 408) {
		return fs.ErrorDirNotFound
	}
	if err != nil {
		return err
	}
	if len(ents) > 0 {
		return fs.ErrorDirectoryNotEmpty
	}
	return f.delete(ctx, p)
}

// --- upload -------------------------------------------------------------------

func (f *Fs) upload(ctx context.Context, remote string, in io.Reader, size int64, modTime time.Time) (*Object, error) {
	p := f.abs(remote)
	dir, name := path.Split(p)
	dir = strings.TrimRight(dir, "/")
	if dir == "" {
		return nil, errors.New("synology: files can't be stored outside a shared folder")
	}
	if size < 0 {
		// DSM needs a Content-Length: spool unknown-size streams to disk.
		tmp, err := os.CreateTemp("", "nasdrive-syno-*")
		if err != nil {
			return nil, err
		}
		defer func() { _ = tmp.Close(); _ = os.Remove(tmp.Name()) }()
		if size, err = io.Copy(tmp, in); err != nil {
			return nil, err
		}
		if _, err = tmp.Seek(0, io.SeekStart); err != nil {
			return nil, err
		}
		in = tmp
	}
	if err := f.c.Upload(ctx, dir, name, in, size, modTime); err != nil {
		return nil, fmt.Errorf("upload %q: %w", remote, err)
	}
	info, err := f.getinfo(ctx, p)
	if err != nil {
		return nil, err
	}
	return &Object{fs: f, remote: remote, size: info.Additional.Size, modTime: info.mtime()}, nil
}

// Put uploads a new object.
func (f *Fs) Put(ctx context.Context, in io.Reader, src fs.ObjectInfo, options ...fs.OpenOption) (fs.Object, error) {
	return f.upload(ctx, src.Remote(), in, src.Size(), src.ModTime(ctx))
}

// PutStream uploads an object of unknown size.
func (f *Fs) PutStream(ctx context.Context, in io.Reader, src fs.ObjectInfo, options ...fs.OpenOption) (fs.Object, error) {
	return f.upload(ctx, src.Remote(), in, -1, src.ModTime(ctx))
}

// --- server side operations ----------------------------------------------------

func (f *Fs) sameServer(o *Fs) bool {
	return o != nil && o.opt.URL == f.opt.URL && o.opt.User == f.opt.User
}

// rename moves from (absolute) to to (absolute) on the server.
func (f *Fs) rename(ctx context.Context, from, to string) error {
	fromDir, fromName := path.Split(from)
	toDir, toName := path.Split(to)
	fromDir, toDir = strings.TrimRight(fromDir, "/"), strings.TrimRight(toDir, "/")
	if fromDir == toDir {
		return f.call(ctx, "SYNO.FileStation.Rename", 2, "rename", map[string]string{
			"path": qa(from), "name": qa(toName),
		}, nil)
	}
	if strings.Count(to, "/") <= 1 {
		return fs.ErrorCantMove
	}
	if _, err := f.getinfo(ctx, toDir); err != nil {
		// create the destination folder
		parent, name := path.Split(toDir)
		if err := f.call(ctx, "SYNO.FileStation.CreateFolder", 2, "create", map[string]string{
			"folder_path": qa(strings.TrimRight(parent, "/")), "name": qa(name), "force_parent": "true",
		}, nil); err != nil && !IsCode(err, 414, 1100) {
			return err
		}
	}
	src := from
	if fromName != toName {
		// Rename in place first (to a name unlikely to clash), then move.
		tmpName := ".nasdrive-mv-" + strconv.FormatInt(time.Now().UnixNano(), 36) + "-" + toName
		if err := f.call(ctx, "SYNO.FileStation.Rename", 2, "rename", map[string]string{
			"path": qa(from), "name": qa(tmpName),
		}, nil); err != nil {
			return err
		}
		src = path.Join(fromDir, tmpName)
		defer func() {
			// If the move failed, try to restore the original name.
			if _, err := f.getinfo(ctx, src); err == nil {
				_ = f.call(ctx, "SYNO.FileStation.Rename", 2, "rename", map[string]string{"path": qa(src), "name": qa(fromName)}, nil)
			}
		}()
		if err := f.moveInto(ctx, src, toDir); err != nil {
			return err
		}
		return f.call(ctx, "SYNO.FileStation.Rename", 2, "rename", map[string]string{
			"path": qa(path.Join(toDir, tmpName)), "name": qa(toName),
		}, nil)
	}
	return f.moveInto(ctx, src, toDir)
}

func (f *Fs) moveInto(ctx context.Context, src, destDir string) error {
	var out struct {
		TaskID string `json:"taskid"`
	}
	if err := f.call(ctx, "SYNO.FileStation.CopyMove", 3, "start", map[string]string{
		"path": qa(src), "dest_folder_path": qs(destDir), "overwrite": "true", "remove_src": "true",
	}, &out); err != nil {
		return err
	}
	return f.c.waitTask(ctx, "SYNO.FileStation.CopyMove", 3, out.TaskID)
}

// Move renames/moves an object on the server.
func (f *Fs) Move(ctx context.Context, src fs.Object, remote string) (fs.Object, error) {
	so, ok := src.(*Object)
	if !ok || !f.sameServer(so.fs) {
		return nil, fs.ErrorCantMove
	}
	from, to := so.fs.abs(so.remote), f.abs(remote)
	if strings.Count(to, "/") <= 1 {
		return nil, fs.ErrorCantMove
	}
	// Overwrite semantics: remove an existing destination file first.
	if info, err := f.getinfo(ctx, to); err == nil {
		if info.IsDir {
			return nil, fs.ErrorIsDir
		}
		if err := f.delete(ctx, to); err != nil {
			return nil, err
		}
	}
	if err := f.rename(ctx, from, to); err != nil {
		if IsCode(err, 408) {
			return nil, fs.ErrorObjectNotFound
		}
		return nil, err
	}
	return f.NewObject(ctx, remote)
}

// DirMove moves a directory on the server.
func (f *Fs) DirMove(ctx context.Context, src fs.Fs, srcRemote, dstRemote string) error {
	sf, ok := src.(*Fs)
	if !ok || !f.sameServer(sf) {
		return fs.ErrorCantDirMove
	}
	from, to := sf.abs(srcRemote), f.abs(dstRemote)
	if strings.Count(from, "/") <= 1 || strings.Count(to, "/") <= 1 {
		return fs.ErrorCantDirMove
	}
	if _, err := f.getinfo(ctx, to); err == nil {
		return fs.ErrorDirExists
	}
	err := f.rename(ctx, from, to)
	if IsCode(err, 408) {
		return fs.ErrorDirNotFound
	}
	return err
}

// About returns the capacity of the volume holding the root's shared folder.
func (f *Fs) About(ctx context.Context) (*fs.Usage, error) {
	shares, err := f.listShares(ctx)
	if err != nil {
		return nil, err
	}
	want := strings.SplitN(f.root, "/", 2)[0]
	var total, free int64
	seen := map[int64]bool{}
	for _, s := range shares {
		vs := s.Additional.VolumeStatus
		if want != "" {
			if s.Name == want {
				total, free = vs.Total, vs.Free
				break
			}
			continue
		}
		// Share list root: sum distinct volumes (approximated by total size).
		if vs.Total > 0 && !seen[vs.Total] {
			seen[vs.Total] = true
			total += vs.Total
			free += vs.Free
		}
	}
	if total <= 0 {
		return nil, errors.New("synology: capacity not available")
	}
	used := total - free
	return &fs.Usage{Total: &total, Used: &used, Free: &free}, nil
}

// Shutdown logs out.
func (f *Fs) Shutdown(ctx context.Context) error {
	f.c.Logout(ctx)
	return nil
}

// --- Object -------------------------------------------------------------------

// fs.ObjectInfo implementation: metadata cached from the last listing or lookup.
func (o *Object) Fs() fs.Info                           { return o.fs }
func (o *Object) Remote() string                        { return o.remote }
func (o *Object) Size() int64                           { return o.size }
func (o *Object) ModTime(ctx context.Context) time.Time { return o.modTime }
func (o *Object) Storable() bool                        { return true }
func (o *Object) String() string {
	if o == nil {
		return "<nil>"
	}
	return o.remote
}
func (o *Object) Hash(ctx context.Context, t hash.Type) (string, error) {
	return "", hash.ErrUnsupported
}

// SetModTime is not supported by File Station.
func (o *Object) SetModTime(ctx context.Context, t time.Time) error {
	return fs.ErrorCantSetModTime
}

// Open downloads the object, honouring range/seek options.
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
	if limit == 0 || (o.size >= 0 && offset >= o.size) {
		return io.NopCloser(strings.NewReader("")), nil
	}
	var rc io.ReadCloser
	err := o.fs.pacer.Call(func() (bool, error) {
		var err error
		rc, err = o.fs.c.Download(ctx, o.fs.abs(o.remote), offset, limit)
		return shouldRetry(ctx, err)
	})
	if IsCode(err, 408) {
		return nil, fs.ErrorObjectNotFound
	}
	return rc, err
}

// Update replaces the object's content.
func (o *Object) Update(ctx context.Context, in io.Reader, src fs.ObjectInfo, options ...fs.OpenOption) error {
	no, err := o.fs.upload(ctx, o.remote, in, src.Size(), src.ModTime(ctx))
	if err != nil {
		return err
	}
	*o = *no
	return nil
}

// Remove deletes the object.
func (o *Object) Remove(ctx context.Context) error {
	err := o.fs.delete(ctx, o.fs.abs(o.remote))
	if IsCode(err, 408) {
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
