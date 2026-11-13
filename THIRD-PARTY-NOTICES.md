# Third party notices

NasDrive includes or depends on the following software.

| Component | License | Source |
|---|---|---|
| rclone (embedded in nasdrive-engine.exe) | MIT, Copyright (C) 2012 Nick Craig-Wood | https://github.com/rclone/rclone |
| cgofuse (embedded in nasdrive-engine.exe) | MIT, Copyright (c) 2017 Bill Zissimopoulos | https://github.com/winfsp/cgofuse |
| Go standard library and golang.org/x modules | BSD-3-Clause | https://go.dev |
| Other Go modules used by rclone | see `go version -m nasdrive-engine.exe` | |
| go-nfs / go-nfs-client (test-time only; protocol reference for `engine/nfsclient`, no code copied) | Apache-2.0 / BSD-2-Clause | https://github.com/willscott/go-nfs, https://github.com/willscott/go-nfs-client |
| WinFsp (installed separately by the installer) | GPLv3 with FLOSS exception | https://github.com/winfsp/winfsp |
| .NET runtime (self-contained in NasDrive.exe) | MIT | https://github.com/dotnet/runtime |

WinFsp is distributed with NasDrive under the WinFsp FLOSS license exception,
which permits its use with free/open source software such as NasDrive (MIT).
