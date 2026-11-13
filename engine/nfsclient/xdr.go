// Package nfsclient is a small NFSv3 client (RFC 1813) with the MOUNT and
// portmapper protocols it needs, written for the NasDrive rclone nfs backend.
//
// The wire formats follow the RFCs; github.com/willscott/go-nfs-client
// (BSD-2-Clause, VMware) was used as a reference. Compared to that library this
// client bounds every call by a context deadline, never retries non-idempotent
// operations blindly, never panics on malformed replies, and can use a
// privileged source port on any OS (needed by most NAS defaults).
package nfsclient

import (
	"encoding/binary"
	"errors"
	"math"
)

var errShort = errors.New("nfs: short or malformed XDR reply")

// enc is an append-only XDR encoder.
type enc struct{ b []byte }

func (e *enc) u32(v uint32) { e.b = binary.BigEndian.AppendUint32(e.b, v) }
func (e *enc) u64(v uint64) { e.b = binary.BigEndian.AppendUint64(e.b, v) }
func (e *enc) bool(v bool) {
	if v {
		e.u32(1)
	} else {
		e.u32(0)
	}
}

// opaque writes variable length opaque data (length + data + padding).
func (e *enc) opaque(p []byte) {
	e.u32(uint32(len(p)))
	e.fixed(p)
}

// fixed writes fixed length opaque data with padding.
func (e *enc) fixed(p []byte) {
	e.b = append(e.b, p...)
	if pad := (4 - len(p)%4) % 4; pad > 0 {
		e.b = append(e.b, make([]byte, pad)...)
	}
}

func (e *enc) str(s string) { e.opaque([]byte(s)) }

// dec is an XDR decoder. The first error sticks and later reads return zero
// values, so callers check err once at the end.
type dec struct {
	b   []byte
	off int
	err error
}

func (d *dec) need(n int) bool {
	if d.err != nil {
		return false
	}
	if n < 0 || d.off+n > len(d.b) {
		d.err = errShort
		return false
	}
	return true
}

func (d *dec) u32() uint32 {
	if !d.need(4) {
		return 0
	}
	v := binary.BigEndian.Uint32(d.b[d.off:])
	d.off += 4
	return v
}

func (d *dec) u64() uint64 {
	if !d.need(8) {
		return 0
	}
	v := binary.BigEndian.Uint64(d.b[d.off:])
	d.off += 8
	return v
}

func (d *dec) bool() bool { return d.u32() != 0 }

func (d *dec) fixed(n int) []byte {
	padded := n + (4-n%4)%4
	if !d.need(padded) {
		return nil
	}
	p := d.b[d.off : d.off+n]
	d.off += padded
	return p
}

// opaque reads variable length opaque data, refusing absurd lengths.
func (d *dec) opaque(max int) []byte {
	n := d.u32()
	if d.err != nil {
		return nil
	}
	if n > math.MaxInt32 || int(n) > max {
		d.err = errShort
		return nil
	}
	p := d.fixed(int(n))
	if p == nil {
		return nil
	}
	out := make([]byte, len(p))
	copy(out, p)
	return out
}

func (d *dec) str(max int) string { return string(d.opaque(max)) }

func (d *dec) skip(n int) {
	if d.need(n) {
		d.off += n
	}
}
