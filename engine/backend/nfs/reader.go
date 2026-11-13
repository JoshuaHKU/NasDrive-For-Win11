package nfs

import (
	"context"
	"errors"
	"io"
	"sync"

	"github.com/nasdrive/nasdrive/engine/nfsclient"
)

// reader reads a file sequentially with up to inFlight READs prefetched.
type reader struct {
	ctx    context.Context
	cancel context.CancelFunc
	f      *Fs
	fh     []byte
	cs     int64
	next   int64 // offset of the next chunk to request
	end    int64 // -1 = until EOF
	queue  []*chunk
	cur    []byte
	eof    bool
	err    error
	once   sync.Once
}

type chunk struct {
	done chan struct{}
	data []byte
	eof  bool
	err  error
}

func newReader(ctx context.Context, f *Fs, fh []byte, offset, end int64) *reader {
	ctx, cancel := context.WithCancel(ctx)
	r := &reader{ctx: ctx, cancel: cancel, f: f, fh: fh, next: offset, end: end,
		cs: int64(f.chunkSize(f.conn.Info.RTPref, f.conn.Info.RTMax))}
	r.fill()
	return r
}

// fill keeps the prefetch queue topped up.
func (r *reader) fill() {
	for len(r.queue) < inFlight && !r.eof {
		if r.end >= 0 && r.next >= r.end {
			return
		}
		n := r.cs
		if r.end >= 0 && r.next+n > r.end {
			n = r.end - r.next
		}
		c := &chunk{done: make(chan struct{})}
		go r.fetch(c, r.next, n)
		r.queue = append(r.queue, c)
		r.next += n
	}
}

func (r *reader) fetch(c *chunk, off, n int64) {
	defer close(c.done)
	var buf []byte
	for int64(len(buf)) < n {
		var data []byte
		var eof bool
		err := r.f.pacer.Call(func() (bool, error) {
			var err error
			data, eof, err = r.f.conn.Read(r.ctx, r.fh, uint64(off+int64(len(buf))), uint32(n-int64(len(buf))))
			return shouldRetry(r.ctx, err)
		})
		if err != nil {
			c.err = err
			return
		}
		buf = append(buf, data...)
		if eof || len(data) == 0 {
			c.eof = true
			break
		}
	}
	c.data = buf
}

func (r *reader) Read(p []byte) (int, error) {
	for len(r.cur) == 0 {
		if r.err != nil {
			return 0, r.err
		}
		if len(r.queue) == 0 {
			return 0, io.EOF
		}
		c := r.queue[0]
		select {
		case <-c.done:
		case <-r.ctx.Done():
			return 0, r.ctx.Err()
		}
		r.queue = r.queue[1:]
		if c.err != nil {
			r.err = c.err
			if nfsclient.IsStatus(c.err, nfsclient.ErrStale) {
				r.err = errors.New("nfs: file changed or was deleted on the server while reading")
			}
			return 0, r.err
		}
		r.cur = c.data
		if c.eof {
			r.eof = true
			// drop chunks requested beyond EOF
			r.queue = nil
		}
		r.fill()
	}
	n := copy(p, r.cur)
	r.cur = r.cur[n:]
	return n, nil
}

func (r *reader) Close() error {
	r.once.Do(r.cancel)
	return nil
}
