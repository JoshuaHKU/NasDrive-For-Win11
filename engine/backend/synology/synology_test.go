package synology

import (
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/rclone/rclone/fs"
	"github.com/rclone/rclone/fs/config/configmap"
	"github.com/rclone/rclone/fs/config/obscure"
	"github.com/rclone/rclone/fstest/fstests"
)

// fakeDSM is an in-memory File Station implementing the subset of the DSM
// Web API the backend uses, including session expiry and Range downloads.
type fakeDSM struct {
	mu       sync.Mutex
	nodes    map[string]*fakeNode // "/share/dir/file"
	sid      string
	calls    int
	expireAt int // force a session timeout on this call number (0 = never)
	otp      bool
	did      string
	didName  string // device name the token was issued to
}

type fakeNode struct {
	dir   bool
	data  []byte
	mtime time.Time
}

func newFakeDSM() *fakeDSM {
	return &fakeDSM{nodes: map[string]*fakeNode{"/share1": {dir: true, mtime: time.Now()}, "/share2": {dir: true, mtime: time.Now()}}}
}

func (d *fakeDSM) reply(w http.ResponseWriter, data any) {
	w.Header().Set("Content-Type", "application/json; charset=\"UTF-8\"")
	_ = json.NewEncoder(w).Encode(map[string]any{"success": true, "data": data})
}

func (d *fakeDSM) fail(w http.ResponseWriter, code int, sub ...int) {
	w.Header().Set("Content-Type", "application/json; charset=\"UTF-8\"")
	e := map[string]any{"code": code}
	if len(sub) > 0 {
		e["errors"] = []map[string]any{{"code": sub[0], "path": "x"}}
	}
	_ = json.NewEncoder(w).Encode(map[string]any{"success": false, "error": e})
}

// str decodes a parameter sent either JSON quoted or raw.
func str(v string) string {
	var s string
	if json.Unmarshal([]byte(v), &s) == nil {
		return s
	}
	return v
}

func arr(v string) []string {
	var a []string
	if json.Unmarshal([]byte(v), &a) == nil {
		return a
	}
	return []string{str(v)}
}

func (d *fakeDSM) info(p string, n *fakeNode) map[string]any {
	return map[string]any{
		"name": path.Base(p), "path": p, "isdir": n.dir,
		"additional": map[string]any{
			"size": len(n.data), "time": map[string]any{"mtime": n.mtime.Unix()},
			"volume_status": map[string]any{"freespace": int64(3) << 40, "totalspace": int64(8) << 40},
		},
	}
}

func (d *fakeDSM) children(dir string) []string {
	var out []string
	for p := range d.nodes {
		if path.Dir(p) == dir && p != dir {
			out = append(out, p)
		}
	}
	sort.Strings(out)
	return out
}

func (d *fakeDSM) removeTree(p string) {
	for k := range d.nodes {
		if k == p || strings.HasPrefix(k, p+"/") {
			delete(d.nodes, k)
		}
	}
}

func (d *fakeDSM) moveTree(from, to string) {
	for k, n := range d.nodes {
		if k == from || strings.HasPrefix(k, from+"/") {
			delete(d.nodes, k)
			d.nodes[to+strings.TrimPrefix(k, from)] = n
		}
	}
}

func (d *fakeDSM) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	if strings.HasSuffix(r.URL.Path, "query.cgi") {
		apis := map[string]any{}
		for _, a := range []string{"SYNO.API.Auth", "SYNO.FileStation.List", "SYNO.FileStation.Download", "SYNO.FileStation.Upload",
			"SYNO.FileStation.CreateFolder", "SYNO.FileStation.Delete", "SYNO.FileStation.Rename", "SYNO.FileStation.CopyMove"} {
			apis[a] = map[string]any{"path": "entry.cgi", "minVersion": 1, "maxVersion": 7}
		}
		d.reply(w, apis)
		return
	}
	isUpload := strings.HasPrefix(r.Header.Get("Content-Type"), "multipart/")
	if isUpload {
		if err := r.ParseMultipartForm(1 << 30); err != nil {
			d.fail(w, 1800)
			return
		}
	} else {
		_ = r.ParseForm()
	}
	get := func(k string) string {
		if v := r.URL.Query().Get(k); v != "" {
			return v
		}
		return r.FormValue(k)
	}
	api, method := get("api"), get("method")

	d.mu.Lock()
	defer d.mu.Unlock()
	if api == "SYNO.API.Auth" {
		if method == "logout" {
			d.reply(w, nil)
			return
		}
		if get("account") != "u" || get("passwd") != "p" {
			d.fail(w, 400)
			return
		}
		if d.otp && (d.did == "" || get("device_id") != d.did || get("device_name") != d.didName) {
			if get("otp_code") != "123456" {
				d.fail(w, 403)
				return
			}
		}
		d.sid = "sid" + strconv.Itoa(d.calls)
		data := map[string]any{"sid": d.sid}
		if get("enable_device_token") == "yes" {
			d.did = "device-token-1"
			d.didName = get("device_name")
			data["did"] = d.did
		}
		d.reply(w, data)
		return
	}
	d.calls++
	if get("_sid") != d.sid || d.calls == d.expireAt {
		d.fail(w, 119)
		return
	}

	switch api + "." + method {
	case "SYNO.FileStation.List.list_share":
		var shares []any
		for _, p := range d.children("/") {
			shares = append(shares, d.info(p, d.nodes[p]))
		}
		d.reply(w, map[string]any{"shares": shares, "total": len(shares), "offset": 0})
	case "SYNO.FileStation.List.list":
		dir := str(get("folder_path"))
		n := d.nodes[dir]
		if n == nil || !n.dir {
			d.fail(w, 408)
			return
		}
		kids := d.children(dir)
		off, _ := strconv.Atoi(get("offset"))
		lim, _ := strconv.Atoi(get("limit"))
		if lim <= 0 {
			lim = len(kids)
		}
		var files []any
		for i := off; i < len(kids) && i < off+lim; i++ {
			files = append(files, d.info(kids[i], d.nodes[kids[i]]))
		}
		d.reply(w, map[string]any{"files": files, "total": len(kids), "offset": off})
	case "SYNO.FileStation.List.getinfo":
		var files []any
		for _, p := range arr(get("path")) {
			if n := d.nodes[p]; n != nil {
				files = append(files, d.info(p, n))
			} else {
				files = append(files, map[string]any{"code": 408, "path": p, "name": path.Base(p)})
			}
		}
		d.reply(w, map[string]any{"files": files})
	case "SYNO.FileStation.Download.download":
		n := d.nodes[str(get("path"))]
		if n == nil || n.dir {
			d.fail(w, 408)
			return
		}
		w.Header().Set("Content-Type", "application/octet-stream")
		http.ServeContent(w, r, "", n.mtime, strings.NewReader(string(n.data)))
	case "SYNO.FileStation.Upload.upload":
		dir := str(get("path"))
		if d.nodes[dir] == nil {
			if get("create_parents") != "true" {
				d.fail(w, 408)
				return
			}
			for p := dir; p != "/" && d.nodes[p] == nil; p = path.Dir(p) {
				d.nodes[p] = &fakeNode{dir: true, mtime: time.Now()}
			}
		}
		file, hdr, err := r.FormFile("file")
		if err != nil {
			d.fail(w, 1802)
			return
		}
		data, _ := io.ReadAll(file)
		ms, _ := strconv.ParseInt(get("mtime"), 10, 64)
		d.nodes[path.Join(dir, hdr.Filename)] = &fakeNode{data: data, mtime: time.UnixMilli(ms)}
		d.reply(w, map[string]any{})
	case "SYNO.FileStation.CreateFolder.create":
		parent, name := arr(get("folder_path"))[0], arr(get("name"))[0]
		p := path.Join(parent, name)
		if d.nodes[p] != nil {
			d.fail(w, 1100, 414)
			return
		}
		if d.nodes[parent] == nil {
			if get("force_parent") != "true" {
				d.fail(w, 1100, 408)
				return
			}
			for q := parent; q != "/" && d.nodes[q] == nil; q = path.Dir(q) {
				d.nodes[q] = &fakeNode{dir: true, mtime: time.Now()}
			}
		}
		d.nodes[p] = &fakeNode{dir: true, mtime: time.Now()}
		d.reply(w, map[string]any{"folders": []any{map[string]any{"path": p, "name": name, "isdir": true}}})
	case "SYNO.FileStation.Delete.start":
		for _, p := range arr(get("path")) {
			d.removeTree(p)
		}
		d.reply(w, map[string]any{"taskid": "del1"})
	case "SYNO.FileStation.Delete.status", "SYNO.FileStation.CopyMove.status":
		d.reply(w, map[string]any{"finished": true})
	case "SYNO.FileStation.Rename.rename":
		p, name := arr(get("path"))[0], arr(get("name"))[0]
		if d.nodes[p] == nil {
			d.fail(w, 1200, 408)
			return
		}
		to := path.Join(path.Dir(p), name)
		if d.nodes[to] != nil {
			d.fail(w, 1200, 414)
			return
		}
		d.moveTree(p, to)
		d.reply(w, map[string]any{})
	case "SYNO.FileStation.CopyMove.start":
		dest := str(get("dest_folder_path"))
		for _, p := range arr(get("path")) {
			if d.nodes[p] == nil {
				d.fail(w, 1001, 408)
				return
			}
			to := path.Join(dest, path.Base(p))
			d.removeTree(to)
			d.moveTree(p, to)
		}
		d.reply(w, map[string]any{"taskid": "mv1"})
	default:
		d.fail(w, 103)
	}
}

var fsNewFs = fs.NewFs

func startFake(t *testing.T) (*fakeDSM, string) {
	d := newFakeDSM()
	srv := httptest.NewServer(d)
	t.Cleanup(srv.Close)
	return d, srv.URL
}

func TestIntegration(t *testing.T) {
	d, u := startFake(t)
	d.expireAt = 25 // exercise transparent re-login
	work := t.TempDir()
	if err := os.MkdirAll(filepath.Join(work, "fstest", "testserver", "init.d"), 0o755); err != nil {
		t.Fatal(err)
	}
	t.Chdir(work)
	fstests.Run(t, &fstests.Opt{
		RemoteName:      fmt.Sprintf(":synology,url='%s',user=u,pass='%s':share1", u, obscure.MustObscure("p")),
		NilObject:       (*Object)(nil),
		SkipInvalidUTF8: true,
	})
}

func TestTwoFactorDeviceToken(t *testing.T) {
	d, u := startFake(t)
	d.otp = true
	ctx := t.Context()
	remote := func(extra string) string {
		return fmt.Sprintf(":synology,url='%s',user=u,pass='%s'%s:share1", u, obscure.MustObscure("p"), extra)
	}
	if _, err := fsNewFs(ctx, remote("")); !IsCode(err, 403) {
		t.Fatalf("want 2FA required (403), got %v", err)
	}
	m := configmap.Simple{"url": u, "user": "u", "pass": obscure.MustObscure("p")}
	if _, err := Login(ctx, m, "000000"); !IsCode(err, 403) {
		t.Fatalf("wrong code: want 403, got %v", err)
	}
	did, err := Login(ctx, m, "123456")
	if err != nil || did == "" {
		t.Fatalf("login with code: did=%q err=%v", did, err)
	}
	f, err := fsNewFs(ctx, remote(",device_id='"+obscure.MustObscure(did)+"'"))
	if err != nil {
		t.Fatalf("login with device token: %v", err)
	}
	if _, err := f.List(ctx, ""); err != nil {
		t.Fatal(err)
	}
}

func TestResponseErrorsShapes(t *testing.T) {
	var r response
	if err := json.Unmarshal([]byte(`{"error":{"code":403,"errors":{"token":"x","types":[{"type":"otp"}]}},"success":false}`), &r); err != nil {
		t.Fatalf("login 2FA shape: %v", err)
	}
	if r.Error.Code != 403 || r.subCode() != 0 {
		t.Fatalf("got %d/%d", r.Error.Code, r.subCode())
	}
	if err := json.Unmarshal([]byte(`{"error":{"code":1100,"errors":[{"code":414,"path":"/a"}]},"success":false}`), &r); err != nil || r.subCode() != 414 {
		t.Fatalf("file shape: %v %d", err, r.subCode())
	}
}
