//go:build darwin

package main

// On macOS NasDrive mounts with rclone's nfsmount: the engine serves the VFS
// over NFS on 127.0.0.1 and mounts it with the system NFS client, so no
// kernel extension (macFUSE) and no administrator rights are needed.
import (
	_ "github.com/JoshuaHKU/NasDrive-For-Win11/engine/backend/macguard" // keeps ._* / .DS_Store off the server
	_ "github.com/rclone/rclone/cmd/nfsmount"
)
