package synology

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"mime/multipart"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"
)

// DSM Web API client for the File Station APIs (SYNO.API.Auth,
// SYNO.FileStation.*). See Synology's "File Station API Guide".

// Error is a DSM API error code.
type Error struct {
	Code   int
	API    string
	Method string
	Login  bool // error returned by SYNO.API.Auth login (codes mean different things)
}

var commonErrors = map[int]string{
	100: "unknown error", 101: "missing parameter", 102: "API does not exist", 103: "method does not exist",
	104: "API version not supported", 105: "permission denied", 106: "session timeout",
	107: "session interrupted by duplicate login", 119: "session not found",
}

var loginErrors = map[int]string{
	400: "incorrect user name or password", 401: "account disabled", 402: "permission denied",
	403: "2-step verification code required", 404: "2-step verification code incorrect or expired",
	406: "2-step verification must be enabled for this account", 407: "this IP address is blocked",
	408: "password expired, change it in DSM", 409: "password expired", 410: "password must be changed in DSM",
}

var fileErrors = map[int]string{
	400: "invalid parameter", 401: "unknown file operation error", 402: "system too busy",
	403: "user not allowed to do this file operation", 404: "group not allowed to do this file operation",
	405: "user/group not allowed", 406: "cannot get user/group information", 407: "operation not permitted",
	408: "no such file or directory", 409: "file system not supported", 410: "cannot connect remote file system",
	411: "read-only file system", 412: "file name too long", 413: "file name too long (encrypted share)",
	414: "file already exists", 415: "disk quota exceeded", 416: "no space left on device",
	417: "I/O error", 418: "illegal name or path", 419: "illegal file name", 420: "illegal file name on FAT",
	421: "device or resource busy", 599: "no such task",
	900: "failed to delete", 1000: "failed to copy", 1001: "failed to move", 1002: "destination error",
	1003: "file exists in destination", 1100: "failed to create folder", 1101: "too many folders",
	1200: "failed to rename", 1800: "incomplete upload data", 1801: "upload timed out",
	1802: "no file name", 1803: "upload cancelled", 1804: "file too large for FAT", 1805: "file exists",
}

// Error describes the DSM error code in words.
func (e *Error) Error() string {
	msg := commonErrors[e.Code]
	if msg == "" {
		if e.Login {
			msg = loginErrors[e.Code]
		} else {
			msg = fileErrors[e.Code]
		}
	}
	if msg == "" {
		msg = "error"
	}
	return fmt.Sprintf("synology %s %s: %s (code %d)", e.API, e.Method, msg, e.Code)
}

// IsCode reports whether err is a DSM error with one of the codes.
func IsCode(err error, codes ...int) bool {
	var e *Error
	if !errors.As(err, &e) {
		return false
	}
	for _, c := range codes {
		if e.Code == c {
			return true
		}
	}
	return false
}

type response struct {
	Success bool            `json:"success"`
	Data    json.RawMessage `json:"data"`
	Error   *struct {
		Code int `json:"code"`
		// An array of per-path errors for file operations, but an object
		// (2FA details) for login: decode lazily.
		Errors json.RawMessage `json:"errors"`
	} `json:"error"`
}

// subCode returns the first per-path error code of a file operation, or 0.
func (r *response) subCode() int {
	if r.Error == nil || len(r.Error.Errors) == 0 || r.Error.Errors[0] != '[' {
		return 0
	}
	var list []struct {
		Code int `json:"code"`
	}
	if json.Unmarshal(r.Error.Errors, &list) != nil || len(list) == 0 {
		return 0
	}
	return list[0].Code
}

// client is a logged in DSM session that re-logs in when the session expires.
type client struct {
	base       string // https://host:5001/webapi/
	http       *http.Client
	user, pass string
	deviceID   string
	deviceName string

	mu      sync.Mutex
	paths   map[string]string // API name -> cgi path
	authVer int
	sid     string
}

// LoginDiagnostics describes the last 2FA login that returned no device token.
var LoginDiagnostics string

func newClient(base string, hc *http.Client, user, pass, deviceID, deviceName string) *client {
	return &client{base: strings.TrimRight(base, "/") + "/webapi/", http: hc, user: user, pass: pass,
		deviceID: deviceID, deviceName: deviceName}
}

// qs returns a JSON encoded string parameter, as the DSM web UI sends them
// for APIs with requestFormat JSON.
func qs(s string) string { b, _ := json.Marshal(s); return string(b) }

// qa returns a JSON array parameter.
func qa(ss ...string) string { b, _ := json.Marshal(ss); return string(b) }

func (c *client) post(ctx context.Context, cgi string, form url.Values) (*response, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, c.base+cgi, strings.NewReader(form.Encode()))
	if err != nil {
		return nil, err
	}
	req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	resp, err := c.http.Do(req)
	if err != nil {
		return nil, err
	}
	defer func() { _ = resp.Body.Close() }()
	return decodeResponse(resp)
}

func decodeResponse(resp *http.Response) (*response, error) {
	body, err := io.ReadAll(io.LimitReader(resp.Body, 64<<20))
	if err != nil {
		return nil, err
	}
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("synology: HTTP %s", resp.Status)
	}
	var r response
	if err := json.Unmarshal(body, &r); err != nil {
		s := string(body)
		if len(s) > 200 {
			s = s[:200]
		}
		return nil, fmt.Errorf("synology: unexpected response (is this a DSM address?): %q", s)
	}
	return &r, nil
}

// discover asks SYNO.API.Info for the cgi path and versions.
func (c *client) discover(ctx context.Context) error {
	if c.paths != nil {
		return nil
	}
	form := url.Values{"api": {"SYNO.API.Info"}, "version": {"1"}, "method": {"query"},
		"query": {"SYNO.API.Auth,SYNO.FileStation.List,SYNO.FileStation.Download,SYNO.FileStation.Upload," +
			"SYNO.FileStation.CreateFolder,SYNO.FileStation.Delete,SYNO.FileStation.Rename,SYNO.FileStation.CopyMove"}}
	r, err := c.post(ctx, "query.cgi", form)
	if err != nil {
		return err
	}
	if !r.Success {
		return fmt.Errorf("synology: API discovery failed")
	}
	var info map[string]struct {
		Path       string `json:"path"`
		MinVersion int    `json:"minVersion"`
		MaxVersion int    `json:"maxVersion"`
	}
	if err := json.Unmarshal(r.Data, &info); err != nil {
		return err
	}
	auth, ok := info["SYNO.API.Auth"]
	if !ok {
		return errors.New("synology: SYNO.API.Auth not available (not a DSM server?)")
	}
	if _, ok := info["SYNO.FileStation.List"]; !ok {
		return errors.New("synology: File Station API not available on this server")
	}
	c.paths = map[string]string{}
	for k, v := range info {
		c.paths[k] = v.Path
	}
	// Version 6 is what DSM 6.2 and 7.x both support for device tokens
	// (Home Assistant's synology_dsm uses it too); v7 may not return "did".
	c.authVer = min(auth.MaxVersion, 6)
	if c.authVer < 3 {
		c.authVer = 3
	}
	return nil
}

// Login logs in (with otp if non-empty) and returns the device token if one
// was requested and issued.
func (c *client) Login(ctx context.Context, otp string) (string, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.loginLocked(ctx, otp)
}

func (c *client) loginLocked(ctx context.Context, otp string) (string, error) {
	if err := c.discover(ctx); err != nil {
		return "", err
	}
	form := url.Values{
		"api": {"SYNO.API.Auth"}, "version": {strconv.Itoa(c.authVer)}, "method": {"login"},
		"account": {c.user}, "passwd": {c.pass}, "session": {"FileStation"}, "format": {"sid"},
	}
	if c.deviceName != "" {
		form.Set("device_name", c.deviceName)
	}
	if otp != "" {
		form.Set("otp_code", otp)
		form.Set("enable_device_token", "yes")
	} else if c.deviceID != "" {
		form.Set("device_id", c.deviceID)
	}
	r, err := c.post(ctx, c.paths["SYNO.API.Auth"], form)
	if err != nil {
		return "", err
	}
	if !r.Success {
		code := 100
		if r.Error != nil {
			code = r.Error.Code
		}
		return "", &Error{Code: code, API: "SYNO.API.Auth", Method: "login", Login: true}
	}
	var d struct {
		SID      string `json:"sid"`
		DID      string `json:"did"`
		DeviceID string `json:"device_id"`
	}
	if err := json.Unmarshal(r.Data, &d); err != nil {
		return "", err
	}
	if d.DID == "" {
		d.DID = d.DeviceID
	}
	if otp != "" && d.DID == "" {
		// Record which fields DSM sent (names only) to diagnose missing tokens.
		var m map[string]json.RawMessage
		_ = json.Unmarshal(r.Data, &m)
		keys := make([]string, 0, len(m))
		for k := range m {
			keys = append(keys, k)
		}
		LoginDiagnostics = fmt.Sprintf("auth v%d login with code returned fields %v", c.authVer, keys)
	}
	if d.SID == "" {
		return "", errors.New("synology: login returned no session")
	}
	c.sid = d.SID
	if d.DID != "" {
		c.deviceID = d.DID
	}
	return d.DID, nil
}

func (c *client) session(ctx context.Context) (string, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.sid == "" {
		if _, err := c.loginLocked(ctx, ""); err != nil {
			return "", err
		}
	}
	return c.sid, nil
}

// invalidate drops sid if it is still the current one.
func (c *client) invalidate(sid string) {
	c.mu.Lock()
	if c.sid == sid {
		c.sid = ""
	}
	c.mu.Unlock()
}

func sessionExpired(err error) bool { return IsCode(err, 106, 107, 119) }

// Call invokes api/method with params, re-logging in once if the session expired.
func (c *client) Call(ctx context.Context, api string, version int, method string, params map[string]string, out any) error {
	for attempt := 0; ; attempt++ {
		sid, err := c.session(ctx)
		if err != nil {
			return err
		}
		form := url.Values{"api": {api}, "version": {strconv.Itoa(version)}, "method": {method}, "_sid": {sid}}
		for k, v := range params {
			form.Set(k, v)
		}
		r, err := c.post(ctx, c.cgi(api), form)
		if err != nil {
			return err
		}
		if !r.Success {
			e := &Error{Code: 100, API: api, Method: method}
			if r.Error != nil {
				e.Code = r.Error.Code
				// File operations report the specific reason per path.
				if sc := r.subCode(); sc != 0 {
					e.Code = sc
				}
			}
			if sessionExpired(e) && attempt == 0 {
				c.invalidate(sid)
				continue
			}
			return e
		}
		if out != nil && len(r.Data) > 0 {
			return json.Unmarshal(r.Data, out)
		}
		return nil
	}
}

func (c *client) cgi(api string) string {
	c.mu.Lock()
	defer c.mu.Unlock()
	if p := c.paths[api]; p != "" {
		return p
	}
	return "entry.cgi"
}

// waitTask polls a background task (Delete, CopyMove) until it finishes.
func (c *client) waitTask(ctx context.Context, api string, version int, taskID string) error {
	delay := 100 * time.Millisecond
	for {
		var st struct {
			Finished bool `json:"finished"`
		}
		if err := c.Call(ctx, api, version, "status", map[string]string{"taskid": qs(taskID)}, &st); err != nil {
			return err
		}
		if st.Finished {
			return nil
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(delay):
		}
		delay = min(delay*2, 2*time.Second)
	}
}

// Download opens path for reading from offset (and up to limit bytes if >= 0).
func (c *client) Download(ctx context.Context, path string, offset, limit int64) (io.ReadCloser, error) {
	for attempt := 0; ; attempt++ {
		sid, err := c.session(ctx)
		if err != nil {
			return nil, err
		}
		q := url.Values{"api": {"SYNO.FileStation.Download"}, "version": {"2"}, "method": {"download"},
			"mode": {"download"}, "path": {qs(path)}, "_sid": {sid}}
		req, err := http.NewRequestWithContext(ctx, http.MethodGet, c.base+c.cgi("SYNO.FileStation.Download")+"?"+q.Encode(), nil)
		if err != nil {
			return nil, err
		}
		if offset > 0 || limit >= 0 {
			if limit >= 0 {
				req.Header.Set("Range", fmt.Sprintf("bytes=%d-%d", offset, offset+limit-1))
			} else {
				req.Header.Set("Range", fmt.Sprintf("bytes=%d-", offset))
			}
		}
		resp, err := c.http.Do(req)
		if err != nil {
			return nil, err
		}
		// Errors come back as a small JSON document instead of the file.
		if strings.HasPrefix(resp.Header.Get("Content-Type"), "application/json") || resp.StatusCode != http.StatusOK && resp.StatusCode != http.StatusPartialContent {
			r, derr := decodeResponse(resp)
			_ = resp.Body.Close()
			if derr != nil {
				return nil, derr
			}
			e := &Error{Code: 100, API: "SYNO.FileStation.Download", Method: "download"}
			if r.Error != nil {
				e.Code = r.Error.Code
			}
			if sessionExpired(e) && attempt == 0 {
				c.invalidate(sid)
				continue
			}
			return nil, e
		}
		var body io.Reader = resp.Body
		if resp.StatusCode == http.StatusOK && offset > 0 {
			// Range ignored by the server: skip to the offset.
			if _, err := io.CopyN(io.Discard, resp.Body, offset); err != nil {
				_ = resp.Body.Close()
				return nil, err
			}
		}
		if limit >= 0 {
			body = io.LimitReader(body, limit)
		}
		return struct {
			io.Reader
			io.Closer
		}{body, resp.Body}, nil
	}
}

// Upload streams size bytes from in to dir/name. size must be known because
// DSM requires a Content-Length.
func (c *client) Upload(ctx context.Context, dir, name string, in io.Reader, size int64, modTime time.Time) error {
	sid, err := c.session(ctx)
	if err != nil {
		return err
	}
	// Build the multipart envelope around the streamed file part.
	var head bytes.Buffer
	mw := multipart.NewWriter(&head)
	fields := [][2]string{
		{"path", dir}, {"create_parents", "true"}, {"overwrite", "true"},
		{"mtime", strconv.FormatInt(modTime.UnixMilli(), 10)},
	}
	for _, f := range fields {
		if err := mw.WriteField(f[0], f[1]); err != nil {
			return err
		}
	}
	if _, err := mw.CreateFormFile("file", name); err != nil {
		return err
	}
	headBytes := append([]byte(nil), head.Bytes()...)
	head.Reset()
	if err := mw.Close(); err != nil {
		return err
	}
	tail := append([]byte(nil), head.Bytes()...) // Close writes "\r\n--boundary--\r\n"

	q := url.Values{"api": {"SYNO.FileStation.Upload"}, "version": {"2"}, "method": {"upload"}, "_sid": {sid}}
	body := io.MultiReader(bytes.NewReader(headBytes), io.LimitReader(in, size), bytes.NewReader(tail))
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, c.base+c.cgi("SYNO.FileStation.Upload")+"?"+q.Encode(), body)
	if err != nil {
		return err
	}
	req.ContentLength = int64(len(headBytes)) + size + int64(len(tail))
	req.Header.Set("Content-Type", mw.FormDataContentType())
	resp, err := c.http.Do(req)
	if err != nil {
		return err
	}
	defer func() { _ = resp.Body.Close() }()
	r, err := decodeResponse(resp)
	if err != nil {
		return err
	}
	if !r.Success {
		e := &Error{Code: 100, API: "SYNO.FileStation.Upload", Method: "upload"}
		if r.Error != nil {
			e.Code = r.Error.Code
		}
		if sessionExpired(e) {
			// The body is consumed, so let the caller (VFS) retry the upload.
			c.invalidate(sid)
		}
		return e
	}
	return nil
}

// Logout ends the session (best effort).
func (c *client) Logout(ctx context.Context) {
	c.mu.Lock()
	sid := c.sid
	c.sid = ""
	c.mu.Unlock()
	if sid == "" || c.paths == nil {
		return
	}
	form := url.Values{"api": {"SYNO.API.Auth"}, "version": {strconv.Itoa(c.authVer)}, "method": {"logout"},
		"session": {"FileStation"}, "_sid": {sid}}
	_, _ = c.post(ctx, c.paths["SYNO.API.Auth"], form)
}
