package main

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"testing"

	"github.com/rclone/rclone/fs/config/obscure"
	"github.com/rclone/rclone/fs/rc"
)

// The 2FA login must honour per-drive overrides such as ignoring the
// self-signed certificate DSM uses by default (regression test).
func TestSynoLoginHonoursNoCheckCertificate(t *testing.T) {
	srv := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_ = r.ParseForm()
		w.Header().Set("Content-Type", "application/json")
		if r.URL.Path == "/webapi/query.cgi" {
			_ = json.NewEncoder(w).Encode(map[string]any{"success": true, "data": map[string]any{
				"SYNO.API.Auth":         map[string]any{"path": "entry.cgi", "minVersion": 1, "maxVersion": 7},
				"SYNO.FileStation.List": map[string]any{"path": "entry.cgi", "minVersion": 1, "maxVersion": 2},
			}})
			return
		}
		switch r.FormValue("method") {
		case "login":
			if r.FormValue("otp_code") != "123456" && r.FormValue("device_id") != "dev-1" {
				_ = json.NewEncoder(w).Encode(map[string]any{"success": false, "error": map[string]any{"code": 403, "errors": map[string]any{"types": []any{}}}})
				return
			}
			_ = json.NewEncoder(w).Encode(map[string]any{"success": true, "data": map[string]any{"sid": "s", "did": "dev-1"}})
		default:
			_ = json.NewEncoder(w).Encode(map[string]any{"success": true})
		}
	}))
	defer srv.Close()

	base := fmt.Sprintf(":synology,url='%s',user=u,pass='%s'", srv.URL, obscure.MustObscure("p"))
	ctx := context.Background()

	// Without the override the self-signed certificate is rejected.
	out, err := rcSynoLogin(ctx, rc.Params{"fs": base + ":", "otp": "123456"})
	if err != nil {
		t.Fatal(err)
	}
	if out["ok"] != false || out["errorCode"] != "tls_cert" {
		t.Fatalf("want tls_cert without override, got %v", out)
	}

	out, err = rcSynoLogin(ctx, rc.Params{"fs": base + ",override.no_check_certificate='true':", "otp": "123456"})
	if err != nil {
		t.Fatal(err)
	}
	if out["ok"] != true {
		t.Fatalf("login with override failed: %v", out)
	}
	if did, _ := obscure.Reveal(out["deviceId"].(string)); did != "dev-1" {
		t.Fatalf("device id %q", did)
	}
}
