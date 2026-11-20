package main

import (
	"context"
	"encoding/base64"
	"errors"
	"fmt"
	"net"
	"net/textproto"
	"strconv"
	"strings"
	"time"

	"github.com/rclone/rclone/fs/rc"
	"golang.org/x/crypto/ssh"
)

func init() {
	rc.Add(rc.Call{
		Path:  "nasdrive/sshhostkey",
		Fn:    rcSSHHostKey,
		Title: "Fetch the host key an SSH/SFTP server presents, without logging in.",
		Help: `Input: host, port (default 22), timeout (default 10s).
Returns {ok, keyType, key ("algo base64", the format of rclone's sftp host_keys option),
fingerprint ("SHA256:...")} or {ok:false, errorCode, errorMsg}.

The GUI shows the fingerprint for the user to confirm on first connection and
pins the key with host_keys; a different key later makes rclone refuse to connect.`,
	})
}

// errGotHostKey aborts the handshake once the host key has been captured.
var errGotHostKey = errors.New("host key captured")

// fetchHostKey performs the SSH key exchange with addr and returns the host
// key the server presents. No authentication is attempted. The default host
// key algorithm preference matches rclone's sftp backend (both use
// golang.org/x/crypto/ssh defaults), so the same key type is negotiated.
func fetchHostKey(ctx context.Context, addr string, timeout time.Duration) (ssh.PublicKey, error) {
	d := net.Dialer{Timeout: timeout}
	conn, err := d.DialContext(ctx, "tcp", addr)
	if err != nil {
		return nil, err
	}
	defer func() { _ = conn.Close() }()
	if dl, ok := ctx.Deadline(); ok {
		_ = conn.SetDeadline(dl)
	} else {
		_ = conn.SetDeadline(time.Now().Add(timeout))
	}
	var key ssh.PublicKey
	cfg := &ssh.ClientConfig{
		User: "nasdrive-hostkey-probe",
		HostKeyCallback: func(_ string, _ net.Addr, k ssh.PublicKey) error {
			key = k
			return errGotHostKey
		},
		Timeout: timeout,
	}
	_, _, _, err = ssh.NewClientConn(conn, addr, cfg)
	if key != nil {
		return key, nil
	}
	if err == nil {
		err = errors.New("server did not present a host key")
	}
	return nil, err
}

func rcSSHHostKey(ctx context.Context, in rc.Params) (rc.Params, error) {
	host, err := in.GetString("host")
	if err != nil {
		return nil, err
	}
	port, err := in.GetInt64("port")
	if err != nil || port <= 0 {
		port = 22
	}
	timeout := getTimeout(in, 10*time.Second)
	ctx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	addr := net.JoinHostPort(strings.Trim(host, "[]"), strconv.FormatInt(port, 10))
	key, err := fetchHostKey(ctx, addr, timeout)
	if err != nil {
		out := rc.Params{"ok": false}
		fillError(out, err)
		return out, nil
	}
	if _, isCert := key.(*ssh.Certificate); isCert {
		out := rc.Params{"ok": false}
		fillError(out, fmt.Errorf("server presented an SSH certificate (%s), which cannot be pinned", key.Type()))
		return out, nil
	}
	return rc.Params{
		"ok":          true,
		"keyType":     key.Type(),
		"key":         key.Type() + " " + base64.StdEncoding.EncodeToString(key.Marshal()),
		"fingerprint": ssh.FingerprintSHA256(key),
	}, nil
}

// classifySSHFTPError maps SFTP and FTP failures to error codes.
func classifySSHFTPError(err error) (string, bool) {
	msg := err.Error()
	switch {
	case strings.Contains(msg, "host key mismatch") || strings.Contains(msg, "no key is pinned for this algorithm"):
		return "ssh_hostkey_changed", true
	case strings.Contains(msg, "ssh: unable to authenticate"):
		return "unauthorized", true
	case strings.Contains(msg, "failed to read private key file") || strings.Contains(msg, "failed to parse private key file") ||
		strings.Contains(msg, "passphrase protected") || strings.Contains(msg, "decryption password incorrect"):
		return "ssh_key", true
	case strings.Contains(msg, "ssh: handshake failed"):
		return "ssh_handshake", true
	}
	var tp *textproto.Error
	if errors.As(err, &tp) {
		switch {
		case tp.Code == 530:
			return "unauthorized", true
		case strings.Contains(msg, "failed to make FTP connection"):
			// Refused while setting up the session (AUTH TLS, PBSZ/PROT, ...):
			// the server does not support the chosen encryption mode.
			return "ftp_no_tls", true
		case tp.Code == 550:
			return "not_found", true
		case tp.Code == 534 || tp.Code == 504 && strings.Contains(strings.ToUpper(tp.Msg), "AUTH"):
			return "ftp_no_tls", true
		}
		return "ftp", true
	}
	if strings.Contains(msg, "AUTH TLS") || strings.Contains(msg, "AUTH command") {
		return "ftp_no_tls", true
	}
	return "", false
}
