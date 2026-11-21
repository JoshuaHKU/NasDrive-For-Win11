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
	"regexp"
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
//
// Only the methods of the fs.Fs interface are promoted from the embedded
// remote, so the optional ones (Move, Copy, PutStream, ...) are implemented
// below with the same filtering and advertised through our own Features;
// handing out the inner remote's Features would let them bypass macguard.
type Fs struct {
	fs.Fs
	features *fs.Features
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
	f := &Fs{Fs: inner}
	stub := &fs.Features{
		CaseInsensitive:          true,
		DuplicateFiles:           true,
		ReadMimeType:             true,
		WriteMimeType:            true,
		CanHaveEmptyDirectories:  true,
		BucketBased:              true,
		BucketBasedRootOK:        true,
		SetTier:                  true,
		GetTier:                  true,
		ReadMetadata:             true,
		WriteMetadata:            true,
		UserMetadata:             true,
		ReadDirMetadata:          true,
		WriteDirMetadata:         true,
		WriteDirSetModTime:       true,
		UserDirMetadata:          true,
		DirModTimeUpdatesOnWrite: true,
		PartialUploads:           true,
		NoMultiThreading:         true,
		Overlay:                  true,
	}
	f.features = stub.Fill(ctx, f).Mask(ctx, inner).WrapsFs(f, inner)
	return f, err // err may be fs.ErrorIsFile
}

// Features returns macguard's own optional features (masked by the inner remote's).
func (f *Fs) Features() *fs.Features { return f.features }

// Name of the wrapped remote.
func (f *Fs) String() string { return "macguard:" + f.Fs.String() }

// UnWrap returns the wrapped remote.
func (f *Fs) UnWrap() fs.Fs { return f.Fs }

// About passes quota through.
func (f *Fs) About(ctx context.Context) (*fs.Usage, error) {
	do := f.Fs.Features().About
	if do == nil {
		return nil, errors.New("about not supported")
	}
	return do(ctx)
}

// PutStream discards metadata files and streams everything else.
func (f *Fs) PutStream(ctx context.Context, in io.Reader, src fs.ObjectInfo, options ...fs.OpenOption) (fs.Object, error) {
	if blockedPath(src.Remote()) {
		n, _ := io.Copy(io.Discard, in)
		return &discarded{f: f, remote: src.Remote(), size: n, modTime: src.ModTime(ctx)}, nil
	}
	do := f.Fs.Features().PutStream
	if do == nil {
		return nil, errors.New("can't PutStream")
	}
	return do(ctx, in, src, options...)
}

// Move renames on the server. Metadata files exist only in the mount's cache,
// so renaming one (the macOS NFS client renames "._a" along with "a") just
// moves the stand-in; without this the VFS would try to copy data that was
// never stored and leave an undeletable ghost entry behind.
func (f *Fs) Move(ctx context.Context, src fs.Object, remote string) (fs.Object, error) {
	if d, ok := src.(*discarded); ok {
		return &discarded{f: f, remote: remote, size: d.size, modTime: d.modTime}, nil
	}
	if blockedPath(remote) {
		// A real file renamed to a metadata name: drop it from the server.
		size, modTime := src.Size(), src.ModTime(ctx)
		if err := src.Remove(ctx); err != nil {
			return nil, err
		}
		return &discarded{f: f, remote: remote, size: size, modTime: modTime}, nil
	}
	do := f.Fs.Features().Move
	if do == nil {
		return nil, fs.ErrorCantMove
	}
	return do(ctx, src, remote)
}

// Copy copies on the server; copies of or to metadata names are discarded.
func (f *Fs) Copy(ctx context.Context, src fs.Object, remote string) (fs.Object, error) {
	if d, ok := src.(*discarded); ok {
		return &discarded{f: f, remote: remote, size: d.size, modTime: d.modTime}, nil
	}
	if blockedPath(remote) {
		return &discarded{f: f, remote: remote, size: src.Size(), modTime: src.ModTime(ctx)}, nil
	}
	do := f.Fs.Features().Copy
	if do == nil {
		return nil, fs.ErrorCantCopy
	}
	return do(ctx, src, remote)
}

// DirMove renames a folder on the server; metadata folders were never created.
func (f *Fs) DirMove(ctx context.Context, src fs.Fs, srcRemote, dstRemote string) error {
	if blockedPath(srcRemote) {
		return nil
	}
	do := f.Fs.Features().DirMove
	if do == nil {
		return fs.ErrorCantDirMove
	}
	if s, ok := src.(*Fs); ok {
		src = s.Fs
	}
	return do(ctx, src, srcRemote, dstRemote)
}

// Purge deletes a folder tree on the server.
func (f *Fs) Purge(ctx context.Context, dir string) error {
	if blockedPath(dir) {
		return nil
	}
	do := f.Fs.Features().Purge
	if do == nil {
		return fs.ErrorCantPurge
	}
	return do(ctx, dir)
}

// ChangeNotify passes server-side change notifications through, minus metadata paths.
func (f *Fs) ChangeNotify(ctx context.Context, notify func(string, fs.EntryType), pollInterval <-chan time.Duration) {
	do := f.Fs.Features().ChangeNotify
	if do == nil {
		return
	}
	do(ctx, func(p string, t fs.EntryType) {
		if !blockedPath(p) {
			notify(p, t)
		}
	}, pollInterval)
}

var (
	_ fs.Abouter        = (*Fs)(nil)
	_ fs.PutStreamer    = (*Fs)(nil)
	_ fs.Mover          = (*Fs)(nil)
	_ fs.Copier         = (*Fs)(nil)
	_ fs.DirMover       = (*Fs)(nil)
	_ fs.Purger         = (*Fs)(nil)
	_ fs.ChangeNotifier = (*Fs)(nil)
	_ fs.UnWrapper      = (*Fs)(nil)
)

// partialSuffix matches the temporary name rclone uploads to before renaming
// (e.g. ".DS_Store.1a2b3c4d.partial" on SFTP/FTP).
var partialSuffix = regexp.MustCompile(`\.[0-9a-f]{8}\.partial$`)

func blockedPath(remote string) bool {
	parts := strings.Split(remote, "/")
	if n := len(parts) - 1; n >= 0 {
		parts[n] = partialSuffix.ReplaceAllString(parts[n], "")
	}
	for _, part := range parts {
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
	return "", nil // unknown: nothing was stored to hash
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
