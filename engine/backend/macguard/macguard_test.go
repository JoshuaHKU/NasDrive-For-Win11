package macguard

import (
	"bytes"
	"context"
	"io"
	"testing"
	"time"

	_ "github.com/rclone/rclone/backend/local"
	_ "github.com/rclone/rclone/backend/memory"
	"github.com/rclone/rclone/fs"
	"github.com/rclone/rclone/fs/object"
	"github.com/rclone/rclone/fs/operations"
)

func put(t *testing.T, f fs.Fs, name, data string) fs.Object {
	t.Helper()
	o, err := f.Put(context.Background(), bytes.NewBufferString(data),
		object.NewStaticObjectInfo(name, time.Now(), int64(len(data)), true, nil, nil))
	if err != nil {
		t.Fatalf("put %s: %v", name, err)
	}
	return o
}

func names(t *testing.T, f fs.Fs, dir string) []string {
	t.Helper()
	entries, err := f.List(context.Background(), dir)
	if err != nil {
		t.Fatalf("list %q: %v", dir, err)
	}
	var out []string
	for _, e := range entries {
		out = append(out, e.Remote())
	}
	return out
}

func TestBlocked(t *testing.T) {
	for _, n := range []string{"._a.txt", "._", ".DS_Store", ".Trashes", ".Spotlight-V100", ".fseventsd", ".apdisk", "Icon\r"} {
		if !Blocked(n) {
			t.Errorf("%q should be blocked", n)
		}
	}
	for _, n := range []string{"a.txt", ".hidden", "_a", ".git", "Icon", "DS_Store"} {
		if Blocked(n) {
			t.Errorf("%q should not be blocked", n)
		}
	}
}

func TestMacguard(t *testing.T) {
	ctx := context.Background()
	inner, err := fs.NewFs(ctx, ":memory:bucket")
	if err != nil {
		t.Fatal(err)
	}
	// metadata already on the server must be hidden
	put(t, inner, "._old.txt", "appledouble")
	put(t, inner, "keep.txt", "keep")

	f, err := fs.NewFs(ctx, ":macguard,remote=':memory:':bucket")
	if err != nil {
		t.Fatal(err)
	}
	if got := names(t, f, ""); len(got) != 1 || got[0] != "keep.txt" {
		t.Fatalf("listing should hide ._old.txt: %v", got)
	}
	if _, err := f.NewObject(ctx, "._old.txt"); err != fs.ErrorObjectNotFound {
		t.Fatalf("NewObject(._old.txt) = %v", err)
	}

	// metadata uploads are accepted but not stored
	o := put(t, f, "sub/._new.txt", "xattrs")
	if o.Size() != int64(len("xattrs")) {
		t.Errorf("discarded object size %d", o.Size())
	}
	put(t, f, ".DS_Store", "finder")
	if _, err := inner.NewObject(ctx, "sub/._new.txt"); err != fs.ErrorObjectNotFound {
		t.Fatalf("._new.txt reached the server: %v", err)
	}
	if _, err := inner.NewObject(ctx, ".DS_Store"); err != fs.ErrorObjectNotFound {
		t.Fatalf(".DS_Store reached the server: %v", err)
	}
	if err := o.Remove(ctx); err != nil {
		t.Errorf("removing a discarded object: %v", err)
	}

	// metadata folders are skipped, normal ones created
	if err := f.Mkdir(ctx, ".Trashes"); err != nil {
		t.Errorf("mkdir .Trashes: %v", err)
	}
	if err := f.Mkdir(ctx, "docs"); err != nil {
		t.Fatal(err)
	}

	// normal files pass through unchanged
	n := put(t, f, "docs/report.txt", "hello")
	rc, err := n.Open(ctx)
	if err != nil {
		t.Fatal(err)
	}
	b, _ := io.ReadAll(rc)
	_ = rc.Close()
	if string(b) != "hello" {
		t.Fatalf("read back %q", b)
	}
	if _, err := inner.NewObject(ctx, "docs/report.txt"); err != nil {
		t.Fatalf("report.txt not on the server: %v", err)
	}

	testRename(t, f, inner, o, n)
}

// TestMacguardLocal runs the rename checks on a backend with server-side Move.
func TestMacguardLocal(t *testing.T) {
	ctx := context.Background()
	dir := t.TempDir()
	inner, err := fs.NewFs(ctx, dir)
	if err != nil {
		t.Fatal(err)
	}
	f, err := fs.NewFs(ctx, ":macguard,remote='"+dir+"':")
	if err != nil {
		t.Fatal(err)
	}
	if f.Features().Move == nil || f.Features().About == nil {
		t.Fatal("expected Move and About from the local backend")
	}
	o := put(t, f, "sub/._new.txt", "xattrs")
	n := put(t, f, "docs/report.txt", "hello")
	testRename(t, f, inner, o, n)
}

// testRename: the macOS NFS client renames "._a" along with "a"; renaming the
// discarded stand-in must work without touching the server, real renames must
// reach it, and copies to metadata names are discarded.
func testRename(t *testing.T, f, inner fs.Fs, discardedObj, real fs.Object) {
	ctx := context.Background()
	moved, err := operations.Move(ctx, f, nil, "sub/._renamed.txt", discardedObj)
	if err != nil {
		t.Fatalf("moving a discarded object: %v", err)
	}
	if moved.Remote() != "sub/._renamed.txt" {
		t.Errorf("moved remote %q", moved.Remote())
	}
	if _, err := inner.NewObject(ctx, "sub/._renamed.txt"); err != fs.ErrorObjectNotFound {
		t.Fatalf("renamed ._ file reached the server: %v", err)
	}
	if _, err := operations.Move(ctx, f, nil, "docs/renamed.txt", real); err != nil {
		t.Fatal(err)
	}
	if _, err := inner.NewObject(ctx, "docs/renamed.txt"); err != nil {
		t.Fatalf("renamed file not on the server: %v", err)
	}
	if _, err := inner.NewObject(ctx, "docs/report.txt"); err != fs.ErrorObjectNotFound {
		t.Fatalf("old name still on the server: %v", err)
	}
	r, err := f.NewObject(ctx, "docs/renamed.txt")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := operations.Copy(ctx, f, nil, "docs/._renamed.txt", r); err != nil {
		t.Fatal(err)
	}
	if _, err := inner.NewObject(ctx, "docs/._renamed.txt"); err != fs.ErrorObjectNotFound {
		t.Fatalf("copy to ._ name reached the server: %v", err)
	}

	// SFTP/FTP upload to "<name>.<hex>.partial" and rename: both steps are filtered
	p := put(t, f, "docs/.DS_Store.1a2b3c4d.partial", "finder")
	if _, ok := p.(*discarded); !ok {
		t.Fatalf("partial upload of .DS_Store was stored: %T", p)
	}
	if _, err := operations.Move(ctx, f, nil, "docs/.DS_Store", p); err != nil {
		t.Fatal(err)
	}
	if _, err := inner.NewObject(ctx, "docs/.DS_Store"); err != fs.ErrorObjectNotFound {
		t.Fatalf(".DS_Store reached the server via partial upload: %v", err)
	}
	if blockedPath("docs/report.1a2b3c4d.partial") {
		t.Error("normal partial upload must not be blocked")
	}

	// a real file renamed to a metadata name leaves the server
	if mg, ok := f.(*Fs); ok {
		x2 := put(t, f, "docs/y.txt", "y")
		if _, err := mg.Move(ctx, x2, "docs/.DS_Store"); err != nil {
			t.Fatal(err)
		}
		if _, err := inner.NewObject(ctx, "docs/y.txt"); err != fs.ErrorObjectNotFound {
			t.Fatalf("source of rename to .DS_Store still on the server: %v", err)
		}
		if _, err := inner.NewObject(ctx, "docs/.DS_Store"); err != fs.ErrorObjectNotFound {
			t.Fatalf("rename to .DS_Store reached the server: %v", err)
		}
	}
}
