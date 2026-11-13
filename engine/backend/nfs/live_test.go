package nfs

import (
	"bytes"
	"context"
	"crypto/rand"
	"fmt"
	"io"
	"os"
	"testing"
	"time"

	"github.com/rclone/rclone/fs"
	"github.com/rclone/rclone/fs/object"
)

// TestLiveServer exercises a real NFS server in a temporary directory that is
// removed afterwards:
//
//	NFS_TEST_HOST=nas NFS_TEST_EXPORT=/volume1/share go test -run LiveServer -v
func TestLiveServer(t *testing.T) {
	host, export := os.Getenv("NFS_TEST_HOST"), os.Getenv("NFS_TEST_EXPORT")
	if host == "" {
		t.Skip("NFS_TEST_HOST not set")
	}
	ctx := context.Background()
	dir := fmt.Sprintf("nasdrive-nfs-test-%d", time.Now().Unix())
	ff, err := fs.NewFs(ctx, fmt.Sprintf(":nfs,host='%s',export='%s':%s", host, export, dir))
	if err != nil {
		t.Fatal(err)
	}
	f := ff.(*Fs)
	defer func() { _ = f.Shutdown(ctx) }()
	if err := f.Mkdir(ctx, ""); err != nil {
		t.Fatal(err)
	}
	defer func() {
		ents, _ := f.List(ctx, "")
		for _, e := range ents {
			if o, ok := e.(fs.Object); ok {
				_ = o.Remove(ctx)
			}
		}
		_ = f.Rmdir(ctx, "子目录")
		parent, _ := fs.NewFs(ctx, fmt.Sprintf(":nfs,host='%s',export='%s':", host, export))
		if err := parent.Rmdir(ctx, dir); err != nil {
			t.Errorf("cleanup: %v", err)
		}
		_ = parent.(*Fs).Shutdown(ctx)
	}()

	u, err := f.About(ctx)
	if err != nil {
		t.Fatal(err)
	}
	t.Logf("capacity total=%d GiB free=%d GiB", *u.Total>>30, *u.Free>>30)

	// Chinese names, subdirectory, rename
	if err := f.Mkdir(ctx, "子目录"); err != nil {
		t.Fatal(err)
	}
	small := []byte("你好，NasDrive NFS")
	mt := time.Date(2025, 1, 2, 3, 4, 5, 600000000, time.UTC)
	o, err := f.Put(ctx, bytes.NewReader(small), object.NewStaticObjectInfo("子目录/中文 文件.txt", mt, int64(len(small)), true, nil, nil))
	if err != nil {
		t.Fatal(err)
	}
	if !o.ModTime(ctx).Equal(mt) {
		t.Errorf("modtime %v != %v", o.ModTime(ctx), mt)
	}
	mo, err := f.Move(ctx, o, "改名后.txt")
	if err != nil {
		t.Fatal(err)
	}
	rc, _ := mo.Open(ctx)
	got, _ := io.ReadAll(rc)
	_ = rc.Close()
	if !bytes.Equal(got, small) {
		t.Fatalf("read back %q", got)
	}
	ents, err := f.List(ctx, "")
	if err != nil {
		t.Fatal(err)
	}
	t.Logf("listing: %v", ents)

	// 64 MiB throughput
	big := make([]byte, 64<<20)
	_, _ = rand.Read(big)
	start := time.Now()
	bo, err := f.Put(ctx, bytes.NewReader(big), object.NewStaticObjectInfo("big.bin", time.Now(), int64(len(big)), true, nil, nil))
	if err != nil {
		t.Fatal(err)
	}
	up := time.Since(start)
	start = time.Now()
	rc, err = bo.Open(ctx)
	if err != nil {
		t.Fatal(err)
	}
	got, err = io.ReadAll(rc)
	_ = rc.Close()
	if err != nil {
		t.Fatal(err)
	}
	down := time.Since(start)
	if !bytes.Equal(got, big) {
		t.Fatal("big file mismatch")
	}
	t.Logf("64 MiB upload %.1f MB/s, download %.1f MB/s", 64/up.Seconds(), 64/down.Seconds())

	// ranged read
	rc, _ = bo.Open(ctx, &fs.RangeOption{Start: 1000, End: 1999})
	got, _ = io.ReadAll(rc)
	_ = rc.Close()
	if !bytes.Equal(got, big[1000:2000]) {
		t.Fatal("range read mismatch")
	}
}
