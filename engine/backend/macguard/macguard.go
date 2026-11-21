// Package macguard wraps another remote and keeps macOS metadata files off it.
//
// The macOS NFS client stores extended attributes in AppleDouble "._name"
// files when the server (here: the engine's own NFS server, NFSv3) has no
// named attributes, and Finder writes ".DS_Store" and other bookkeeping files.
// On a NAS share these litter every folder. Like macFUSE's "noappledouble",
// macguard discards them: uploads are accepted but not stored, and such files
// are hidden from listings. Extended attributes are therefore not kept on the
// server.
package macguard

import (
	"context"
	"errors"
	"io"
	"path"
	"strings"
	"time"

	"github.com/rclone/rclone/fs"
	"github.com/rclone/rclone/fs/config/configmap"
	"github.com/rclone/rclone/fs/config/configstruct"
	"github.com/rclone/rclone/fs/fspath"
	"github.com/rclone/rclone/fs/hash"
)

func init() {
	fs.Register(&fs.RegInfo{
		Name:        "macguard",
		Description: "Keeps macOS metadata files (._*, .DS_Store) off the wrapped remote",
		NewFs:       NewFs,
		Options: []fs.Option{{
			Name:     "remote",
			Help:     "The remote to wrap, e.g. a :webdav,... connection string.",
			Required: true,
		}},
	})
}

// Blocked reports whether a file or folder name is macOS metadata that must
// not be stored on the server.
func Blocked(name string) bool {
	switch name {
	case ".DS_Store", ".localized", "Icon\r", ".Spotlight-V100", ".Trashes", ".fseventsd",
		".TemporaryItems", ".DocumentRevisions-V100", ".VolumeIcon.icns", ".apdisk":
		return true
	}
	return strings.HasPrefix(name, "._")
}

// Fs wraps another Fs.
type Fs struct {
	fs.Fs
}

// NewFs creates the wrapped remote at root.
func NewFs(ctx context.Context, name, root string, m configmap.Mapper) (fs.Fs, error) {
	var opt struct {
		Remote string `config:"remote"`
	}
	if err := configstruct.Set(m, &opt); err != nil {
		return nil, err
	}
	if strings.HasPrefix(opt.Remote, name+":") {
		return nil, errors.New("macguard: can't wrap itself")
	}
	inner, err := fs.NewFs(ctx, fspath.JoinRootPath(opt.Remote, root))
	if inner == nil {
		return nil, err
	}
	return &Fs{Fs: inner}, err // err may be fs.ErrorIsFile
}

func blockedPath(remote string) bool {
	for _, part := range strings.Split(remote, "/") {
		if Blocked(part) {
			return true
		}
	}
	return false
}

// List hides metadata files that already exist on the server.
func (f *Fs) List(ctx context.Context, dir string) (fs.DirEntries, error) {
	if blockedPath(dir) {
		return nil, fs.ErrorDirNotFound
	}
	entries, err := f.Fs.List(ctx, dir)
	if err != nil {
		return nil, err
	}
	out := entries[:0]
	for _, e := range entries {
		if !Blocked(path.Base(e.Remote())) {
			out = append(out, e)
		}
	}
	return out, nil
}

// NewObject reports metadata files as absent.
func (f *Fs) NewObject(ctx context.Context, remote string) (fs.Object, error) {
	if blockedPath(remote) {
		return nil, fs.ErrorObjectNotFound
	}
	return f.Fs.NewObject(ctx, remote)
}

// Put discards metadata files and stores everything else.
func (f *Fs) Put(ctx context.Context, in io.Reader, src fs.ObjectInfo, options ...fs.OpenOption) (fs.Object, error) {
	if blockedPath(src.Remote()) {
		n, _ := io.Copy(io.Discard, in)
		return &discarded{f: f, remote: src.Remote(), size: n, modTime: src.ModTime(ctx)}, nil
	}
	return f.Fs.Put(ctx, in, src, options...)
}

// Mkdir silently skips metadata folders.
func (f *Fs) Mkdir(ctx context.Context, dir string) error {
	if blockedPath(dir) {
		return nil
	}
	return f.Fs.Mkdir(ctx, dir)
}

// Rmdir silently skips metadata folders.
func (f *Fs) Rmdir(ctx context.Context, dir string) error {
	if blockedPath(dir) {
		return nil
	}
	return f.Fs.Rmdir(ctx, dir)
}

// discarded stands in for a metadata file that was not stored.
type discarded struct {
	f       *Fs
	remote  string
	size    int64
	modTime time.Time
}

func (o *discarded) Fs() fs.Info                           { return o.f }
func (o *discarded) Remote() string                        { return o.remote }
func (o *discarded) String() string                        { return o.remote }
func (o *discarded) Size() int64                           { return o.size }
func (o *discarded) ModTime(ctx context.Context) time.Time { return o.modTime }
func (o *discarded) Storable() bool                        { return false }
func (o *discarded) SetModTime(ctx context.Context, t time.Time) error {
	o.modTime = t
	return nil
}
func (o *discarded) Hash(ctx context.Context, t hash.Type) (string, error) {
	return "", hash.ErrUnsupported
}
func (o *discarded) Open(ctx context.Context, options ...fs.OpenOption) (io.ReadCloser, error) {
	return nil, fs.ErrorObjectNotFound
}
func (o *discarded) Update(ctx context.Context, in io.Reader, src fs.ObjectInfo, options ...fs.OpenOption) error {
	n, _ := io.Copy(io.Discard, in)
	o.size, o.modTime = n, src.ModTime(ctx)
	return nil
}
func (o *discarded) Remove(ctx context.Context) error { return nil }

var (
	_ fs.Fs     = (*Fs)(nil)
	_ fs.Object = (*discarded)(nil)
)
