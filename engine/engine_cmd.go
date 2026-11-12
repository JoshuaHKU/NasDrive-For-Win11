package main

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"

	"github.com/rclone/rclone/cmd"
	"github.com/rclone/rclone/fs"
	"github.com/rclone/rclone/fs/rc"
	"github.com/rclone/rclone/fs/rc/rcserver"
	"github.com/spf13/cobra"
)

var readyFile string

func init() {
	cmd.Root.AddCommand(engineCommand)
	engineCommand.Flags().StringVar(&readyFile, "ready-file", "", "Write a JSON file with the rc URL and PID once the server is ready")
}

// readyInfo is written to stdout and to --ready-file once the rc server listens.
type readyInfo struct {
	URL     string `json:"url"`
	PID     int    `json:"pid"`
	Version string `json:"version"`
}

var engineCommand = &cobra.Command{
	Use:   "engine",
	Short: "Run the NasDrive engine (rc server with mount support).",
	Long: `Runs the NasDrive engine. It listens for rc commands only, like
"rclone rcd", and is normally started by the NasDrive GUI.

Use --rc-addr to choose the listening address (keep it on 127.0.0.1) and
set RCLONE_RC_USER / RCLONE_RC_PASS to protect it.`,
	Run: func(command *cobra.Command, args []string) {
		cmd.CheckArgs(0, 0, command, args)
		if rc.Opt.Enabled {
			fs.Fatalf(nil, "Don't supply --rc flag when using engine")
		}
		if rc.Opt.Auth.BasicUser == "" && rc.Opt.Auth.HtPasswd == "" && !rc.Opt.NoAuth {
			fs.Logf(nil, "WARNING: rc server started without authentication")
		}
		rc.Opt.Enabled = true
		// Never serve remote files or a web GUI from the engine.
		rc.Opt.Serve = false
		rc.Opt.Files = ""
		rc.Opt.WebUI = false

		s, err := rcserver.Start(context.Background(), &rc.Opt)
		if err != nil {
			fs.Fatalf(nil, "Failed to start remote control: %v", err)
		}
		if s == nil {
			fs.Fatal(nil, "rc server not configured")
		}

		urls := s.URLs()
		info := readyInfo{PID: os.Getpid(), Version: Version}
		if len(urls) > 0 {
			info.URL = urls[0]
		}
		data, _ := json.Marshal(info)
		// The GUI waits for this line on stdout.
		fmt.Printf("NASDRIVE_READY %s\n", data)
		if readyFile != "" {
			if err := writeFileAtomic(readyFile, data); err != nil {
				fs.Errorf(nil, "Failed to write ready file: %v", err)
			}
			defer func() { _ = os.Remove(readyFile) }()
		}
		s.Wait()
	},
}

// writeFileAtomic writes data to a temporary file and renames it into place.
func writeFileAtomic(name string, data []byte) error {
	if err := os.MkdirAll(filepath.Dir(name), 0o700); err != nil {
		return err
	}
	tmp := name + ".tmp"
	if err := os.WriteFile(tmp, data, 0o600); err != nil {
		return err
	}
	return os.Rename(tmp, name)
}
