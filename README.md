# NasDrive for Windows 11 & macOS

**把 NAS 上的文件夹映射成 Windows 盘符 / macOS 磁盘的开源工具** —— 支持 **WebDAV（含 Nextcloud）**、**NFS**、**群晖 Synology（DSM File Station）**、**SFTP** 与 **FTP/FTPS**，带图形界面和托盘图标，开箱即用。

> English: NasDrive maps folders on your NAS or server to Windows drive letters (and, since 0.5, to volumes in the macOS Finder) over WebDAV (incl. Nextcloud), NFSv3, the Synology DSM File Station API (with 2-step verification), SFTP (password or private key, host key pinning) or FTP/FTPS. It is a free, open-source (MIT) alternative to tools like RaiDrive, built on [rclone](https://rclone.org) and [WinFsp](https://winfsp.dev). See [docs/安装与使用说明.md](docs/安装与使用说明.md) for installation and [docs/系统介绍.md](docs/系统介绍.md) for the architecture.

![license](https://img.shields.io/badge/license-MIT-blue) ![platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20%7C%20macOS%2012%2B-lightgrey)

## 功能

| | WebDAV | NFS | 群晖 Synology | SFTP | FTP / FTPS |
|---|---|---|---|---|---|
| 适用 | 任何 WebDAV 服务；**Nextcloud/ownCloud 预设** | 开启 NFS 的 NAS / Linux | 群晖 DSM 6.2 / 7.x | 任何开启 SSH 的 NAS、Linux、VPS | 老 NAS、路由器存储、网站空间 |
| 认证 | 用户名 + 密码 | 按 IP 授权 | DSM 账户，**支持两步验证** | 密码或**私钥**；**服务器指纹确认与固定** | 用户名 + 密码 / 匿名 |
| 加密 | HTTPS | 无 | HTTPS | SSH | 显式 / 隐式 FTPS（可选不加密） |
| NAS 端需要 | 开启 WebDAV | 开启 NFS 并配置权限 | 无需额外设置 | 开启 SSH/SFTP | 开启 FTP |
| 列出共享 | — | ✓ | ✓ | — | — |
| 真实容量显示 | 取决于服务器 | ✓ | ✓ | ✓（服务器支持时） | — |

通用能力：

- 盘符以**网络驱动器**形式出现在"此电脑"，名称显示为 `名称 (X:)` 与可用/总空间；
- 本地读写缓存（VFS full cache）+ 延迟写回，大文件、Office 文档可直接打开编辑；
- 开机自动连接、断线检测与自动重连、上传队列提示，断开前确认未上传完的文件；
- **只读**驱动器（Windows 层面拒绝写入和删除）；
- 全局 HTTP 代理，可按驱动器启用；
- 密码与设备令牌只保存在 **Windows 凭据管理器**，不写入任何文件；
- 中文 / English 界面，深色主题，按屏幕缩放清晰显示的托盘图标（蓝 = 已连接，灰 = 未连接，琥珀 = 离线）。

## 下载与安装

**Windows**：到 [Releases](https://github.com/JoshuaHKU/NasDrive-For-Win11/releases) 下载最新的 `NasDrive-x.y.z-x64-setup.exe`，双击安装。安装程序会在需要时自动安装 [WinFsp](https://winfsp.dev)（文件系统驱动）。

**macOS**（0.5 起）：下载 `NasDrive-x.y.z-osx-arm64.dmg`（Apple 芯片）或 `osx-x64.dmg`（Intel），把 NasDrive 拖入"应用程序"。**无需安装 macFUSE，也不需要管理员权限**：引擎通过系统自带的 NFS 客户端挂载，驱动器默认出现在 `~/NasDrive/<名称>`（每个驱动器可单独指定挂载位置，统一的默认位置也可在设置中更改）和 Finder 边栏的"位置"中。NasDrive 是菜单栏应用（不显示在程序坞）。目前安装包只做了临时签名，首次打开请在 Finder 中右键 NasDrive →"打开"。

详细步骤、各协议的 NAS 端设置与常见问题见 **[安装与使用说明](docs/安装与使用说明.md)**。

## 快速开始

1. 安装后从开始菜单启动 NasDrive（托盘会出现图标）；
2. 点击 **添加**，选择服务类型：
   - **群晖 Synology**：填 NAS 地址、端口 5001（HTTPS）、DSM 账户密码 → **列出共享** → 选择共享文件夹；
   - **WebDAV**：填地址、端口（群晖 WebDAV 默认 5006）、账户密码、路径；
   - **NFS**：填 NAS 地址 → **列出共享** → 选择导出路径；
   - **SFTP**：填地址、端口 22、用户名，选择密码或私钥登录 → 首次连接时核对服务器指纹；
   - **FTP**：填地址，选择加密方式（显式 FTPS 端口 21 / 隐式 FTPS 端口 990），填账户或勾选匿名；
3. 点击 **连接**，盘符随即出现在资源管理器中。

## 从源码构建

需要 Go 1.26+ 与 .NET 8 SDK。引擎可在 macOS/Linux 上交叉编译；WPF 界面也可在 macOS/Linux 上编译，但只能在 Windows 上运行。

```bash
scripts/build.sh                # 输出 dist/win-x64/（nasdrive-engine.exe + NasDrive.exe）
ARCH=arm64 scripts/build.sh     # Windows on ARM
scripts/build-mac.sh            # macOS（需在 Mac 上）：dist/osx-arm64/NasDrive.app 与 .dmg
ARCH=amd64 scripts/build-mac.sh # Intel Mac
```

macOS 安装包默认临时签名（ad-hoc）；设置 `SIGN_ID="Developer ID Application: …"` 可用开发者证书签名，再用 `xcrun notarytool` 公证后分发。

安装包（Windows，需 Inno Setup 6；先把 WinFsp 安装包放到 `installer/deps/winfsp.msi`）：

```bat
iscc /DArch=x64 /DVersion=0.5.0 installer\NasDrive.iss
```

测试：

```bash
cd engine && go test ./...      # 含 rclone 官方后端一致性测试（NFS 对 rclone serve nfs，群晖对内置模拟 DSM）
```

Windows 端到端测试（挂载、读写、只读、容量等）见 `scripts/test-mount.ps1` 头部说明。macOS 端到端测试：

```bash
scripts/test-mac.sh             # 本机临时 WebDAV/SFTP/FTP 服务器；可加 NFS_HOST/NFS_EXPORT/NFS_CHECK 测真实 NAS
```

## 项目结构

```
engine/                      nasdrive-engine.exe（Go，内嵌 rclone 作为库）
  main.go, engine_cmd.go       入口与 engine 子命令（rc 控制接口 + 就绪握手）
  rc_nasdrive.go               NasDrive 专用 rc 接口：version/health/test/mounts/probe/
                               nfsexports/synologin/listdirs，错误分类
  backend/nfs/                 rclone "nfs" 后端（rclone 本身没有 NFS 客户端）
  backend/synology/            rclone "synology" 后端（DSM File Station API，含 2FA）
  nfsclient/                   纯 Go NFSv3 + MOUNT + portmapper 客户端
  backend/macguard/            macOS：拦截 ._* / .DS_Store 等元数据文件，不写入服务器
  mount_darwin.go              macOS：nfsmount（系统 NFS 客户端挂载，无需 macFUSE）
  platform_*.go                挂载后端检测（WinFsp / macOS NFS）、磁盘空间
gui/NasDrive.Core/           共享核心库（net8.0）：配置、引擎守护、rc 客户端、驱动器状态机、多语言、
                               平台抽象 IPlatform / ISecretStore
gui/NasDrive/                NasDrive.exe（WPF）：WindowsPlatform、凭据管理器、外壳集成、界面与托盘
gui/NasDrive.Mac/            NasDrive.app（Avalonia）：MacPlatform、钥匙串、LaunchAgent、界面与菜单栏图标
gui/NasDrive.Mac.Smoke/      macOS 无界面端到端测试（scripts/test-mac.sh 调用）
installer/                   Inno Setup 安装脚本（含 WinFsp 静默安装）
scripts/                     build.sh、build-mac.sh、test-mount.ps1、test-mac.sh、genicon（图标生成）
docs/                        系统介绍、安装说明、各期设计文档
```

## 文档

- [安装与使用说明](docs/安装与使用说明.md)
- [系统介绍（架构与原理）](docs/系统介绍.md)
- 设计文档：[一期（WebDAV）](docs/NasDrive详细设计文档.md)、[NFS 支持](docs/NFS支持设计.md)、[群晖与托盘图标](docs/群晖支持与托盘图标设计.md)、[SFTP/FTP/Nextcloud](docs/SFTP-FTP-Nextcloud设计.md)、[macOS 移植方案与实施结果](docs/macOS移植评估与方案.md)、[macOS P0 验证](docs/macOS-P0验证报告.md)
- [更新日志](CHANGELOG.md)

## 许可证

[MIT](LICENSE)。NasDrive 以库的形式使用 rclone（MIT），Windows 上通过 WinFsp（GPLv3 + FLOSS 例外）挂载，macOS 上通过系统 NFS 客户端挂载，macOS 界面使用 Avalonia（MIT）；第三方组件见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

NasDrive 与 Synology、RaiDrive 等公司或产品无关；"Synology"、"群晖"、"DSM" 是 Synology Inc. 的商标。
