package nfsclient

import (
	"context"
	"os"
	"testing"
	"time"
)

// Live test against a real server: NFS_TEST_HOST=nas NFS_TEST_EXPORT=/volume1/x go test -run Live -v
func TestLive(t *testing.T) {
	host, export := os.Getenv("NFS_TEST_HOST"), os.Getenv("NFS_TEST_EXPORT")
	if host == "" {
		t.Skip("NFS_TEST_HOST not set")
	}
	ctx := context.Background()
	ex, err := Exports(ctx, host, 0, DialOptions{Timeout: 5 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	t.Logf("exports: %+v", ex)
	c, err := Dial(ctx, Options{Host: host, Export: export, Privileged: os.Getenv("NFS_TEST_UNPRIV") == "", Timeout: 10 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	defer c.Close()
	t.Logf("fsinfo %+v", c.Info)
	st, err := c.FSStat(ctx, c.Root)
	t.Logf("fsstat %+v err=%v", st, err)
	ents, err := c.ReadDirPlus(ctx, c.Root)
	if err != nil {
		t.Fatal(err)
	}
	for i, e := range ents {
		if i < 15 {
			t.Logf("%q dir=%v size=%d fh=%d", e.Name, e.Attr != nil && e.Attr.IsDir(), func() uint64 {
				if e.Attr != nil {
					return e.Attr.Size
				}
				return 0
			}(), len(e.FH))
		}
	}
	t.Logf("%d entries", len(ents))
}
