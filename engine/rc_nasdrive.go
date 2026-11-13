package main

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"errors"
	"fmt"
	"net"
	"net/url"
	"os"
	"path"
	"runtime"
	"strings"
	"syscall"
	"time"

	"github.com/nasdrive/nasdrive/engine/backend/synology"
	"github.com/nasdrive/nasdrive/engine/nfsclient"
	"github.com/rclone/rclone/backend/webdav/api"
	"github.com/rclone/rclone/fs"
	"github.com/rclone/rclone/fs/cache"
	"github.com/rclone/rclone/fs/config"
	"github.com/rclone/rclone/fs/config/configmap"
	"github.com/rclone/rclone/fs/config/configstruct"
	"github.com/rclone/rclone/fs/config/obscure"
	"github.com/rclone/rclone/fs/rc"
)

func init() {
	rc.Add(rc.Call{
		Path:  "nasdrive/version",
		Fn:    rcVersion,
		Title: "Return NasDrive engine, rclone and WinFsp versions.",
	})
	rc.Add(rc.Call{
		Path:  "nasdrive/health",
		Fn:    rcHealth,
		Title: "Self check: WinFsp presence and cache directory.",
	})
	rc.Add(rc.Call{
		Path:  "nasdrive/test",
		Fn:    rcTest,
		Title: "Test a connection string: list the root and read the quota.",
		Help: `Parameters:

- fs - connection string to test (required)
- timeout - overall timeout, e.g. "20s" (optional, default 20s)

Returns ok, latencyMs, errorCode, errorMsg, httpStatus and quota fields.`,
	})
	rc.Add(rc.Call{
		Path:  "nasdrive/nfsexports",
		Fn:    rcNFSExports,
		Title: "List the exports of an NFS server.",
		Help: `Input: host (required), mountPort (default 0 = ask the portmapper), timeout (default 10s).
Returns {ok, exports:[{path, groups}]} or {ok:false, errorCode, errorMsg}.`,
	})
	rc.Add(rc.Call{
		Path:  "nasdrive/synologin",
		Fn:    rcSynoLogin,
		Title: "Log in to Synology DSM with a 2-step verification code and return a device token.",
		Help: `Input: fs (a :synology connection string), otp (the 6 digit code), timeout (default 20s).
Returns {ok, deviceId} (deviceId is obscured, ready for the device_id option) or {ok:false, errorCode, errorMsg}.`,
	})
	rc.Add(rc.Call{
		Path:  "nasdrive/listdirs",
		Fn:    rcListDirs,
		Title: "List the sub folders of a directory of a connection string (e.g. Synology shared folders).",
		Help:  "Input: fs, dir (default root), timeout (default 20s). Returns {ok, dirs:[names]} or {ok:false, errorCode, errorMsg}.",
	})
	rc.Add(rc.Call{
		Path:  "nasdrive/mounts",
		Fn:    rcMounts,
		Title: "List mounts with VFS cache and upload queue statistics.",
	})
	rc.Add(rc.Call{
		Path:  "nasdrive/probe",
		Fn:    rcProbe,
		Title: "Check whether the server behind a mounted fs is reachable and read its quota.",
		Help: `Parameters:

- fs - the fs string of a mount as returned by nasdrive/mounts (required)
- timeout - e.g. "10s" (optional, default 10s)

Returns online, errorCode, errorMsg and quota fields.`,
	})
	// The engine never needs to run arbitrary rclone commands, so replace
	// core/command with a stub that refuses.
	rc.Add(rc.Call{
		Path: "core/command",
		Fn: func(context.Context, rc.Params) (rc.Params, error) {
			return nil, errors.New("core/command is disabled in nasdrive-engine")
		},
		Title: "Disabled in nasdrive-engine.",
	})
}

func rcVersion(ctx context.Context, in rc.Params) (rc.Params, error) {
	w := winfspInfo()
	return rc.Params{
		"engine":   Version,
		"rclone":   fs.Version,
		"go":       runtime.Version(),
		"os":       runtime.GOOS,
		"arch":     runtime.GOARCH,
		"winfsp":   w.Version,
		"winfspOk": w.Installed,
	}, nil
}

func rcHealth(ctx context.Context, in rc.Params) (rc.Params, error) {
	w := winfspInfo()
	out := rc.Params{
		"winfspOk":   w.Installed,
		"winfspDir":  w.InstallDir,
		"winfsp":     w.Version,
		"cacheDir":   config.GetCacheDir(),
		"cacheDirOk": false,
		"pid":        os.Getpid(),
	}
	dir := config.GetCacheDir()
	if err := os.MkdirAll(dir, 0o700); err == nil {
		f, err := os.CreateTemp(dir, ".nasdrive-health-*")
		if err == nil {
			name := f.Name()
			_ = f.Close()
			_ = os.Remove(name)
			out["cacheDirOk"] = true
		}
	}
	if free, ok := diskFree(dir); ok {
		out["cacheDirFree"] = free
	}
	return out, nil
}

// addQuota adds total/used/free to out if the remote supports About.
func addQuota(ctx context.Context, f fs.Fs, out rc.Params) error {
	doAbout := f.Features().About
	if doAbout == nil {
		return nil
	}
	u, err := doAbout(ctx)
	if err != nil {
		return err
	}
	if u.Total != nil {
		out["total"] = *u.Total
	}
	if u.Used != nil {
		out["used"] = *u.Used
	}
	if u.Free != nil {
		out["free"] = *u.Free
	}
	return nil
}

func getTimeout(in rc.Params, def time.Duration) time.Duration {
	if s, err := in.GetString("timeout"); err == nil && s != "" {
		if d, err := fs.ParseDuration(s); err == nil && d > 0 {
			return d
		}
	}
	return def
}

func rcTest(ctx context.Context, in rc.Params) (rc.Params, error) {
	fsString, err := in.GetString("fs")
	if err != nil {
		return nil, err
	}
	timeout := getTimeout(in, 20*time.Second)
	ctx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()

	out := rc.Params{"ok": false}
	start := time.Now()
	// Use a fresh Fs, not the cache, so that changed settings are honoured.
	f, err := fs.NewFs(ctx, fsString)
	if err != nil && !errors.Is(err, fs.ErrorIsFile) {
		fillError(out, err)
		return out, nil
	}
	// The Fs is not cached, so release its connections (NFS) when done.
	if s, ok := f.(fs.Shutdowner); ok {
		defer func() { _ = s.Shutdown(context.Background()) }()
	}
	if errors.Is(err, fs.ErrorIsFile) {
		fillError(out, errPathIsFile)
		return out, nil
	}
	if _, err = f.List(ctx, ""); err != nil {
		fillError(out, err)
		return out, nil
	}
	out["latencyMs"] = time.Since(start).Milliseconds()
	out["ok"] = true
	out["errorCode"] = "ok"
	if err := addQuota(ctx, f, out); err != nil {
		fs.Debugf(nil, "test: quota not available: %v", err)
	}
	return out, nil
}

func rcMounts(ctx context.Context, in rc.Params) (rc.Params, error) {
	listCall := rc.Calls.Get("mount/listmounts")
	if listCall == nil {
		return nil, errors.New("mount support not compiled in")
	}
	res, err := listCall.Fn(ctx, rc.Params{})
	if err != nil {
		return nil, err
	}
	statsCall := rc.Calls.Get("vfs/stats")
	var mounts []rc.Params
	if list, ok := res["mountPoints"]; ok {
		// Round trip through rc.Params to avoid depending on the
		// unexported shape of mountlib.MountInfo.
		var infos []struct {
			Fs         string    `json:"Fs"`
			MountPoint string    `json:"MountPoint"`
			MountedOn  time.Time `json:"MountedOn"`
		}
		if err := rc.Reshape(&infos, list); err != nil {
			return nil, err
		}
		for _, mi := range infos {
			m := rc.Params{
				"fs":         mi.Fs,
				"mountPoint": mi.MountPoint,
				"mountedOn":  mi.MountedOn,
			}
			if statsCall != nil {
				if st, err := statsCall.Fn(ctx, rc.Params{"fs": mi.Fs}); err == nil {
					if dc, ok := st["diskCache"]; ok {
						m["diskCache"] = dc
					}
					if md, ok := st["metadataCache"]; ok {
						m["metadataCache"] = md
					}
				} else {
					fs.Debugf(nil, "mounts: vfs/stats for %q failed: %v", mi.MountPoint, err)
				}
			}
			mounts = append(mounts, m)
		}
	}
	if mounts == nil {
		mounts = []rc.Params{}
	}
	return rc.Params{"mounts": mounts}, nil
}

func rcProbe(ctx context.Context, in rc.Params) (rc.Params, error) {
	fsString, err := in.GetString("fs")
	if err != nil {
		return nil, err
	}
	timeout := getTimeout(in, 10*time.Second)
	// Use the cached Fs that backs the mount.
	f, err := cache.Get(ctx, fsString)
	if err != nil {
		return nil, err
	}
	ctx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	out := rc.Params{"online": true, "errorCode": "ok"}
	var probeErr error
	if f.Features().About != nil {
		probeErr = addQuota(ctx, f, out)
	} else {
		_, probeErr = f.List(ctx, "")
	}
	if probeErr != nil {
		code, msg, status := classifyError(probeErr)
		out["errorCode"] = code
		out["errorMsg"] = msg
		if status != 0 {
			out["httpStatus"] = status
		}
		// The server answered with an HTTP error, so it is reachable.
		out["online"] = status != 0
	}
	return out, nil
}

var errPathIsFile = errors.New("the path points to a file, not a directory")

func fillError(out rc.Params, err error) {
	code, msg, status := classifyError(err)
	out["ok"] = false
	out["errorCode"] = code
	out["errorMsg"] = msg
	if status != 0 {
		out["httpStatus"] = status
	}
}

// classifyError maps an error to a stable code the GUI can translate.
//
// Codes: dns, refused, timeout, tls_cert, tls, unauthorized, forbidden,
// not_found, method_not_allowed, http, is_file, cancelled, network, unknown.
func classifyError(err error) (code, msg string, httpStatus int) {
	msg = err.Error()
	// Proxies and some servers return whole HTML pages as error bodies.
	if r := []rune(msg); len(r) > 300 {
		msg = string(r[:300]) + "…"
	}
	var apiErr *api.Error
	if errors.As(err, &apiErr) {
		httpStatus = apiErr.StatusCode
		switch apiErr.StatusCode {
		case 401:
			return "unauthorized", msg, httpStatus
		case 403:
			return "forbidden", msg, httpStatus
		case 404:
			return "not_found", msg, httpStatus
		case 405:
			return "method_not_allowed", msg, httpStatus
		}
		return "http", msg, httpStatus
	}
	if code, ok := classifySynoError(err); ok {
		return code, msg, 0
	}
	if code, ok := classifyNFSError(err); ok {
		return code, msg, 0
	}
	if errors.Is(err, fs.ErrorDirNotFound) {
		return "not_found", msg, 404
	}
	if errors.Is(err, errPathIsFile) {
		return "is_file", msg, 0
	}
	var dnsErr *net.DNSError
	if errors.As(err, &dnsErr) {
		return "dns", msg, 0
	}
	var unknownAuth x509.UnknownAuthorityError
	var hostErr x509.HostnameError
	var invalidErr x509.CertificateInvalidError
	var verifyErr *tls.CertificateVerificationError
	if errors.As(err, &unknownAuth) || errors.As(err, &hostErr) || errors.As(err, &invalidErr) || errors.As(err, &verifyErr) {
		return "tls_cert", msg, 0
	}
	var recordErr tls.RecordHeaderError
	if errors.As(err, &recordErr) || strings.Contains(msg, "tls: ") || strings.Contains(msg, "HTTP response to HTTPS client") {
		return "tls", msg, 0
	}
	if errors.Is(err, syscall.ECONNREFUSED) || isWSAConnRefused(err) {
		return "refused", msg, 0
	}
	if errors.Is(err, context.DeadlineExceeded) {
		return "timeout", msg, 0
	}
	if errors.Is(err, context.Canceled) {
		return "cancelled", msg, 0
	}
	var netErr net.Error
	if errors.As(err, &netErr) && netErr.Timeout() {
		return "timeout", msg, 0
	}
	var urlErr *url.Error
	if errors.As(err, &urlErr) {
		return "network", msg, 0
	}
	// rclone wraps some HTTP failures as plain strings
	for _, s := range []string{"401 Unauthorized", "403 Forbidden", "404 Not Found"} {
		if strings.Contains(msg, s) {
			var st int
			_, _ = fmt.Sscanf(s, "%d", &st)
			return map[int]string{401: "unauthorized", 403: "forbidden", 404: "not_found"}[st], msg, st
		}
	}
	return "unknown", msg, 0
}

// classifyNFSError maps MOUNT/NFS/RPC failures to error codes.
func classifyNFSError(err error) (string, bool) {
	var me *nfsclient.MountError
	if errors.As(err, &me) {
		switch me.Status {
		case 1, 13: // PERM, ACCES: client IP not allowed / privileged port required
			return "nfs_denied", true
		case 2:
			return "not_found", true
		case 20:
			return "is_file", true
		}
		return "nfs", true
	}
	var ne *nfsclient.Error
	if errors.As(err, &ne) {
		switch ne.Status {
		case nfsclient.ErrPerm, nfsclient.ErrAcces:
			return "forbidden", true
		case nfsclient.ErrNoEnt:
			return "not_found", true
		case nfsclient.ErrNotDir:
			return "is_file", true
		}
		return "nfs", true
	}
	var re *nfsclient.RPCError
	if errors.As(err, &re) {
		// e.g. NFS/MOUNT not registered with the portmapper: service disabled
		return "nfs_unavailable", true
	}
	return "", false
}

func rcNFSExports(ctx context.Context, in rc.Params) (rc.Params, error) {
	host, err := in.GetString("host")
	if err != nil {
		return nil, err
	}
	mountPort, _ := in.GetInt64("mountPort")
	timeout := getTimeout(in, 10*time.Second)
	ctx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	exports, err := nfsclient.Exports(ctx, host, int(mountPort), nfsclient.DialOptions{Timeout: timeout})
	if err != nil {
		out := rc.Params{"ok": false}
		fillError(out, err)
		return out, nil
	}
	if exports == nil {
		exports = []nfsclient.Export{}
	}
	return rc.Params{"ok": true, "exports": exports}, nil
}

// classifySynoError maps DSM API error codes to error codes.
func classifySynoError(err error) (string, bool) {
	var se *synology.Error
	if !errors.As(err, &se) {
		return "", false
	}
	if se.Login {
		switch se.Code {
		case 400:
			return "unauthorized", true
		case 401, 402:
			return "forbidden", true
		case 403:
			return "syno_2fa", true
		case 404:
			return "syno_2fa_bad", true
		case 406:
			return "syno_2fa_enforce", true
		case 407:
			return "syno_blocked", true
		}
		return "syno", true
	}
	switch se.Code {
	case 105, 403, 405, 407:
		return "forbidden", true
	case 408:
		return "not_found", true
	case 415, 416:
		return "syno_nospace", true
	}
	return "syno", true
}

func rcSynoLogin(ctx context.Context, in rc.Params) (rc.Params, error) {
	fsString, err := in.GetString("fs")
	if err != nil {
		return nil, err
	}
	otp, err := in.GetString("otp")
	if err != nil {
		return nil, err
	}
	ctx, cancel := context.WithTimeout(ctx, getTimeout(in, 20*time.Second))
	defer cancel()
	info, _, _, m, err := fs.ConfigFs(fsString)
	if err != nil {
		return nil, err
	}
	if info.Name != "synology" {
		return nil, errors.New("fs is not a synology remote")
	}
	// fs.NewFs applies the per-remote "override.*" options (timeouts, ignore
	// certificate errors, proxy) to the context; do the same here since we
	// log in without creating an Fs.
	if ctx, err = withOverrides(ctx, m); err != nil {
		return nil, err
	}
	did, err := synology.Login(ctx, m, strings.TrimSpace(otp))
	if err != nil {
		fs.Logf(nil, "synology 2FA login failed: %v", err)
		out := rc.Params{"ok": false}
		fillError(out, err)
		return out, nil
	}
	return rc.Params{"ok": true, "deviceId": obscure.MustObscure(did)}, nil
}

func rcListDirs(ctx context.Context, in rc.Params) (rc.Params, error) {
	fsString, err := in.GetString("fs")
	if err != nil {
		return nil, err
	}
	dir, _ := in.GetString("dir")
	ctx, cancel := context.WithTimeout(ctx, getTimeout(in, 20*time.Second))
	defer cancel()
	out := rc.Params{"ok": false}
	f, err := fs.NewFs(ctx, fsString)
	if err != nil && !errors.Is(err, fs.ErrorIsFile) {
		fillError(out, err)
		return out, nil
	}
	if s, ok := f.(fs.Shutdowner); ok {
		defer func() { _ = s.Shutdown(context.Background()) }()
	}
	entries, err := f.List(ctx, dir)
	if err != nil {
		fillError(out, err)
		return out, nil
	}
	dirs := []string{}
	for _, e := range entries {
		if d, ok := e.(fs.Directory); ok {
			dirs = append(dirs, path.Base(d.Remote()))
		}
	}
	return rc.Params{"ok": true, "dirs": dirs}, nil
}

// withOverrides returns ctx with the remote's "override.<option>" values
// applied to a copy of the global config, like fs.NewFs does.
func withOverrides(ctx context.Context, m configmap.Getter) (context.Context, error) {
	ov := configmap.Simple{}
	for _, opt := range fs.ConfigOptionsInfo {
		if v, ok := m.Get("override." + opt.Name); ok {
			ov[opt.Name] = v
		}
	}
	if len(ov) == 0 {
		return ctx, nil
	}
	newCtx, ci := fs.AddConfig(ctx)
	if err := configstruct.Set(ov, ci); err != nil {
		return ctx, fmt.Errorf("override options: %w", err)
	}
	return newCtx, nil
}
