package main

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"encoding/base64"
	"errors"
	"fmt"
	"net"
	"net/textproto"
	"strings"
	"testing"

	"github.com/rclone/rclone/fs/rc"
	"golang.org/x/crypto/ssh"
)

// startSSHServer runs a minimal SSH server that only performs key exchange.
func startSSHServer(t *testing.T) (addr string, pub ssh.PublicKey) {
	_, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	signer, err := ssh.NewSignerFromKey(priv)
	if err != nil {
		t.Fatal(err)
	}
	cfg := &ssh.ServerConfig{NoClientAuth: false, PasswordCallback: func(ssh.ConnMetadata, []byte) (*ssh.Permissions, error) {
		return nil, errors.New("no")
	}}
	cfg.AddHostKey(signer)
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = ln.Close() })
	go func() {
		for {
			c, err := ln.Accept()
			if err != nil {
				return
			}
			go func() { _, _, _, _ = ssh.NewServerConn(c, cfg); _ = c.Close() }()
		}
	}()
	return ln.Addr().String(), signer.PublicKey()
}

func TestSSHHostKey(t *testing.T) {
	addr, pub := startSSHServer(t)
	host, port, _ := net.SplitHostPort(addr)
	var p int64
	_, _ = fmt.Sscan(port, &p)
	out, err := rcSSHHostKey(context.Background(), rc.Params{"host": host, "port": p})
	if err != nil {
		t.Fatal(err)
	}
	if out["ok"] != true {
		t.Fatalf("probe failed: %v", out)
	}
	wantKey := pub.Type() + " " + base64.StdEncoding.EncodeToString(pub.Marshal())
	if out["key"] != wantKey || out["fingerprint"] != ssh.FingerprintSHA256(pub) || out["keyType"] != "ssh-ed25519" {
		t.Fatalf("got %v", out)
	}

	// Closed port: a clear connection error, not a hang.
	ln, _ := net.Listen("tcp", "127.0.0.1:0")
	closed := ln.Addr().(*net.TCPAddr).Port
	_ = ln.Close()
	out, _ = rcSSHHostKey(context.Background(), rc.Params{"host": "127.0.0.1", "port": int64(closed), "timeout": "3s"})
	if out["ok"] != false || out["errorCode"] != "refused" {
		t.Fatalf("closed port: %v", out)
	}
}

func TestClassifySSHFTPError(t *testing.T) {
	cases := map[string]error{
		"ssh_hostkey_changed": errors.New("sftp: host key mismatch for x: server offered ssh-ed25519 key"),
		"unauthorized":        errors.New("couldn't connect SSH: ssh: handshake failed: ssh: unable to authenticate, attempted methods [none password]"),
		"ssh_key":             errors.New("failed to parse private key file: ssh: this private key is passphrase protected"),
		"not_found":           fmt.Errorf("list: %w", &textproto.Error{Code: 550, Msg: "No such file"}),
		"ftp_no_tls":          &textproto.Error{Code: 534, Msg: "Policy requires SSL"},
		"ftp_no_tls ":         fmt.Errorf("NewFs: failed to make FTP connection to \"h:21\": %w", &textproto.Error{Code: 550, Msg: "Action not taken"}),
	}
	cases2 := map[string]error{"unauthorized": &textproto.Error{Code: 530, Msg: "Login incorrect"}}
	for want, err := range cases {
		if got, _ := classifySSHFTPError(err); got != strings.TrimSpace(want) {
			t.Errorf("%v: got %q want %q", err, got, want)
		}
	}
	for want, err := range cases2 {
		if got, _, _ := classifyError(err); got != want {
			t.Errorf("%v: got %q want %q", err, got, want)
		}
	}
}
