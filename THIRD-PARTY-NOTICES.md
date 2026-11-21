# Third party notices

NasDrive includes or depends on the following software.

| Component | License | Source |
|---|---|---|
| rclone (embedded in nasdrive-engine.exe) | MIT, Copyright (C) 2012 Nick Craig-Wood | https://github.com/rclone/rclone |
| cgofuse (embedded in nasdrive-engine.exe) | MIT, Copyright (c) 2017 Bill Zissimopoulos | https://github.com/winfsp/cgofuse |
| Go standard library and golang.org/x modules | BSD-3-Clause | https://go.dev |
| Other Go modules used by rclone | see `go version -m nasdrive-engine.exe` | |
| go-nfs (NFS server used by rclone `nfsmount`, in the macOS nasdrive-engine) | Apache-2.0 | https://github.com/willscott/go-nfs |
| go-nfs-client (test-time only; protocol reference for `engine/nfsclient`, no code copied) | BSD-2-Clause | https://github.com/willscott/go-nfs-client |
| pkg/sftp, golang.org/x/crypto/ssh (SFTP, in nasdrive-engine.exe) | BSD-2-Clause / BSD-3-Clause | https://github.com/pkg/sftp |
| jlaffaye/ftp (FTP, in nasdrive-engine.exe) | ISC | https://github.com/jlaffaye/ftp |
| WinFsp (installed separately by the installer) | GPLv3 with FLOSS exception | https://github.com/winfsp/winfsp |
| .NET runtime (self-contained in NasDrive.exe / NasDrive.app) | MIT | https://github.com/dotnet/runtime |
| Avalonia UI (macOS app) | MIT | https://github.com/AvaloniaUI/Avalonia |
| SkiaSharp, HarfBuzzSharp (rendering, bundled with Avalonia in NasDrive.app) | MIT | https://github.com/mono/SkiaSharp |
| Inter font (bundled with Avalonia in NasDrive.app) | SIL Open Font License 1.1 | https://github.com/rsms/inter |

WinFsp is distributed with NasDrive under the WinFsp FLOSS license exception,
which permits its use with free/open source software such as NasDrive (MIT).
