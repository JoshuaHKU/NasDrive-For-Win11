//go:build unix

package nfs

import (
	"context"
	"fmt"
	"net"
	"os"
	"path/filepath"
	"testing"

	_ "github.com/rclone/rclone/backend/local"
	servenfs "github.com/rclone/rclone/cmd/serve/nfs"
	"github.com/rclone/rclone/fs"
	"github.com/rclone/rclone/fstest/fstests"
	"github.com/rclone/rclone/vfs"
	"github.com/rclone/rclone/vfs/vfscommon"
)

// TestIntegration runs rclone's backend conformance suite against an
// in-process `rclone serve nfs` of a temporary local directory. That server
// has no portmapper and serves MOUNT and NFS on one port.
func TestIntegration(t *testing.T) {
	ctx := context.Background()
	f, err := fs.NewFs(ctx, t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	vopt := vfscommon.Opt
	vopt.CacheMode = vfscommon.CacheModeWrites
	srv, err := servenfs.NewServer(ctx, vfs.New(ctx, f, &vopt), &servenfs.Options{ListenAddr: "127.0.0.1:0", HandleLimit: -1})
	if err != nil {
		t.Fatal(err)
	}
	go func() { _ = srv.Serve() }()
	defer func() { _ = srv.Shutdown() }()
	port := srv.Addr().(*net.TCPAddr).Port

	// fstests looks for rclone's fstest/testserver/init.d; give it an empty one.
	work := t.TempDir()
	if err := os.MkdirAll(filepath.Join(work, "fstest", "testserver", "init.d"), 0o755); err != nil {
		t.Fatal(err)
	}
	t.Chdir(work)

	fstests.Run(t, &fstests.Opt{
		RemoteName: fmt.Sprintf(":nfs,host=127.0.0.1,export=/,port=%d,mount_port=%d,privileged_port=false:", port, port),
		NilObject:  (*Object)(nil),
		// serve nfs maps through a VFS with its own quirks; these are not
		// properties of this backend.
		SkipInvalidUTF8: true,
	})
}
