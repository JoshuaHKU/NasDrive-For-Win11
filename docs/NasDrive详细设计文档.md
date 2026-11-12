# NasDrive 详细设计文档（评审稿 v0.1）

> 基于 rclone 的 Windows WebDAV 网盘映射工具（对标 RaiDrive）
> 日期：2026-11-11　|　rclone 基线版本：v1.77.0（commit `f42d829`，已拉取至 `rclone_github/`）
> 产品名 "NasDrive" 为暂定代号，可在评审时更改。

---

## 0. 结论摘要

| 项目 | 结论 |
|---|---|
| 可行性 | **可行，且难度可控**。rclone 已经具备本需求约 70% 的核心能力：WebDAV 客户端、Windows 盘符挂载（WinFsp）、本地读写缓存（VFS）、远程控制 API（rc）。缺的主要是 **Windows 原生图形界面、托盘、配置管理、凭据安全存储、开机自启、状态监控与安装包**。 |
| 推荐路线 | **不 fork 修改 rclone 主干**，而是把 rclone 当作 Go 库引用（go.mod 锁定版本），自建两个程序：①`nasdrive-engine.exe`（Go，内嵌 rclone 的 WebDAV + 挂载 + rc 服务）；②`NasDrive.exe`（Windows 图形界面 + 托盘）。两者通过本机回环地址上的 rc HTTP API 通信。 |
| 推荐 GUI 技术 | **C# / .NET 8 + WPF**（首选）；备选 Go + Wails（单语言）。见 §4.3 对比，**需您拍板**。 |
| 关键外部依赖 | **WinFsp**（Windows 用户态文件系统驱动，必须随安装包分发）。注意其 GPLv3 + FLOSS 例外许可，**闭源商用需购买商业授权**（§12）。 |
| 首版工期估算 | 约 8–10 人周（1 名熟悉 Go + 1 名熟悉 C#/WPF 并行约 5–6 周），见 §13。 |

---

## 1. 需求理解

### 1.1 目标
在 Windows 10/11 上提供一个类似 RaiDrive 的工具：

1. 图形界面管理多个"虚拟驱动器"配置；
2. 每个配置通过 **WebDAV（HTTP/HTTPS）** 连接 NAS 上的某个远程目录；
3. 把该远程目录映射为 Windows 本地盘符（如 `Z:`），在资源管理器和任意程序中像本地盘一样读写；
4. 常驻系统托盘，可开机自启、启动后自动连接。

### 1.2 从《RaiDrive参考界面.pdf》提取的界面需求

| 参考界面 | 功能点 | 本设计是否实现 |
|---|---|---|
| 托盘左键菜单 | 版本号标题、启动主界面、检查更新、添加、设置、驱动器列表（灰色=未连接，带子菜单）、退出 | ✅ 全部实现（驱动器子菜单：连接/断开、打开、编辑） |
| 主窗口 | 标题栏「添加 / 设置 / 登录 / 关闭」；每个驱动器一行：图标、盘符+名称、`用户@主机`、远程路径、容量条（"未知空间容量"）、状态小图标（只读/代理/网络/TLS）；操作按钮：连接▶/断开■（连接中显示转圈）、设置⚙、打开📂、删除🗑（连接时禁用） | ✅ 实现；**不做**广告横幅、「登录」账号体系 |
| 设置页 | 常规：添加到启动项、连接后打开资源管理器、允许自定义图标；浏览器/无痕浏览；语言；临时目录 + 读/写持续时间；检查更新；代理（类型/主机/端口/用户名/密码） | ✅ 实现；「浏览器」项仅 OAuth 云盘需要，**本期不做** |
| 新虚拟驱动器 | 服务类型（Personal/Business/Enterprise/NAS），NAS 下：WebDAV、SFTP、FTP、Synology、QNAP、ipTIME、Nextcloud、ASUSTOR | ✅ 首版：**WebDAV**；二期：Synology/QNAP/Nextcloud/ASUSTOR（本质是 WebDAV 预设模板）；SFTP/FTP rclone 原生支持，可作为三期扩展 |
| | 虚拟驱动器：启用勾选、盘符下拉、名称 | ✅ |
| | 启动后自动连接、代理、连接时输入账号密码、只读 | ✅ |
| | 模拟本地磁盘（公开/私有） | ✅ 映射为「固定磁盘模式 / 网络驱动器模式」；"公开"=对所有用户可见（需以服务方式运行，见 §9.4，二期） |
| | 文件锁 | ⚠️ rclone VFS **不支持 WebDAV LOCK**，首版不提供（§11 风险） |
| | 地址：HTTPS 勾选、主机、端口、路径 | ✅ |
| | 账户：用户名、密码、匿名 | ✅ |
| | TLS/SSL 版本下拉、超时（秒） | ⚠️ rclone 无"最低 TLS 版本"选项，改为「忽略证书错误」+「自定义 CA 证书」；超时 ✅ |
| | 连接 / 取消 / 帮助 | ✅ 「连接」= 保存并立即挂载 |

---

## 2. rclone 项目分析

### 2.1 项目概况
- 语言：Go 1.26；许可证：**MIT**（`COPYING`），可自由修改、闭源再分发。
- 定位："云存储界的 rsync"，支持 72 个存储后端（`backend/`），提供 sync/copy/mount/serve 等命令（`cmd/`）。
- 架构：`rclone.go` 入口 → `backend/all`、`cmd/all` 通过 `init()` 注册所有后端和命令；核心抽象 `fs.Fs` / `fs.Object`（`fs/types.go`）。

### 2.2 与本需求直接相关的模块

| 模块 | 路径 | 作用 | 对本项目的价值 |
|---|---|---|---|
| WebDAV 后端 | `backend/webdav/webdav.go` | WebDAV 客户端：PROPFIND/PUT/MOVE/COPY/DELETE；vendor：nextcloud/owncloud/sharepoint/fastmail/rclone/other；Basic/Bearer 认证；Nextcloud 分块上传、TUS 上传 | **核心**：直接用于连接 NAS。`About()`（行 1397）通过 RFC 4331 `quota-available-bytes/quota-used-bytes` 获取容量 → 资源管理器显示总容量/可用空间 |
| cmount（WinFsp 挂载） | `cmd/cmount/` | 基于 cgofuse + WinFsp 的挂载实现，Windows 上唯一可用的挂载方式；`mountpoint_windows.go` 处理盘符、`*` 自动分配盘符、UNC 网络共享路径 | **核心**：盘符映射 |
| mountlib | `cmd/mountlib/` | 挂载通用逻辑和挂载选项（`volname`、`network_mode` 等）；`rc.go` 提供 `mount/mount`、`mount/unmount`、`mount/listmounts`、`mount/unmountall` 接口 | **核心**：GUI 通过这些接口控制挂载 |
| VFS 虚拟文件系统 | `vfs/`、`vfs/vfscache/`、`vfs/vfscommon/options.go` | 把"对象存储语义"转换为 POSIX 文件语义；四级缓存模式 off/minimal/writes/full；目录缓存、预读、写回延迟、缓存容量上限 | **核心**：决定 Office、视频播放、随机读写的兼容性；对应 RaiDrive 的"临时目录/读写持续时间" |
| rc 远程控制 | `fs/rc/`、`fs/rc/rcserver/` | 内嵌 HTTP JSON-RPC 服务，几乎所有功能都可通过 API 调用：`config/*`、`core/stats`、`core/obscure`、`operations/about`、`vfs/stats`、`vfs/queue`、`options/set` 等 | **核心**：GUI 与引擎之间的控制通道 |
| librclone | `librclone/` | 把 rc 导出为 C 函数 `RPC(method, json)`，可编译为 DLL | 备选通信方式（§4.2） |
| 全局 HTTP 选项 | `fs/config.go` | `timeout`、`contimeout`（行 131/136）、`no_check_certificate`（146）、`ca_cert`（448）、`http_proxy`（586） | 对应界面的超时、TLS、代理 |
| 配置 | `fs/config/` | rclone.conf 读写、加密、`obscure` 密码混淆 | 本设计**不使用** rclone.conf，改用"连接字符串"即时传参（§6.3） |
| Web GUI | `cmd/gui/` | v1.74 起内置的浏览器版 GUI（rclone-web 1.2.1），通用型管理界面 | 可参考其调用 rc 的方式；但它是浏览器页面、通用而复杂、无托盘，**不满足 RaiDrive 式体验** |

### 2.3 Windows 挂载的关键事实（来自 `cmd/mountlib/mount.md`）
1. **必须安装 WinFsp**；rclone 通过 cgofuse 调用。
2. 两种模式：
   - **固定磁盘模式**（默认）：可挂载到盘符或不存在的目录；
   - **网络驱动器模式** `--network-mode`：只能挂载到盘符，资源管理器显示为网络驱动器，**不会生成缩略图、对高延迟更友好**——NAS 场景推荐默认使用。
3. `--volname \\server\share` 设置网络共享路径/卷标，**多个驱动器卷名必须唯一**。
4. 以管理员身份创建的盘符对普通资源管理器**不可见** → GUI 和引擎必须以**普通权限**运行。
5. 以 SYSTEM 账户（Windows 服务）运行可让所有用户可见 → 对应 RaiDrive 的"公开"选项。

### 2.4 rclone 现有能力与需求差距

| 需求 | rclone 现状 | 差距/工作量 |
|---|---|---|
| WebDAV 连接 NAS | ✅ 完整 | 无 |
| 映射盘符 | ✅ cmount + WinFsp | 无（需随包分发 WinFsp） |
| 读写缓存 | ✅ VFS | 需要给出 NAS 场景的默认参数 |
| 容量显示 | ✅ 若 NAS 支持 RFC 4331 | 群晖/威联通一般支持；不支持时可用 `vfs_disk_space_total_size` 兜底 |
| 多挂载并行管理 | ✅ `mount/*` rc 接口 | 无 |
| 图形界面 / 托盘 | ❌（仅浏览器版通用 GUI） | **全新开发** |
| 驱动器配置持久化 | 部分（rclone.conf） | 自建配置模型 |
| 凭据安全 | 仅 obscure（可逆混淆）或整体配置加密 | 改用 **Windows 凭据管理器 / DPAPI** |
| 开机自启、自动连接 | ❌ | 新开发 |
| 连接状态/离线检测 | 部分（vfs/stats、operations/about） | GUI 层轮询 + 状态机 |
| 自定义驱动器图标/卷标 | ❌ | 写注册表实现 |
| WebDAV 文件锁 | ❌ | 本期不做 |
| 安装包、升级 | ❌ | 新开发 |

---

## 3. 总体方案选择

### 3.1 三种改造路线

| 路线 | 描述 | 优点 | 缺点 | 结论 |
|---|---|---|---|---|
| A. 深度 fork rclone | 在 rclone 源码中直接加 GUI 代码、删减功能 | 单仓库 | 与上游合并困难；rclone 核心以 CLI 为中心，改动侵入大；跟进安全更新成本高 | ❌ 不推荐 |
| B. 外挂官方 rclone.exe | GUI 启动原版 `rclone.exe rcd`，通过 rc 控制 | 零 Go 代码 | 体积大（含 72 个后端，约 70MB）；无法定制；进程命令行可见 | 可作为原型 |
| **C. rclone 作库 + 自建引擎 + 独立 GUI** | 自写 Go `main`，只引入 webdav/local 后端、cmount、rcserver；GUI 独立进程 | 上游零改动、升级只需改 go.mod；体积小（约 25MB）；可加少量自定义 rc 接口；引擎崩溃不拖垮 GUI，反之亦然 | 两个进程，需要 IPC | ✅ **推荐** |

### 3.2 为什么拆成两个进程
- **故障隔离**：挂载在引擎进程中，GUI 关闭/崩溃/升级不影响已挂载盘符；引擎崩溃时 GUI 可检测并自动重启、重新挂载。
- **技术自由**：GUI 可用最适合 Windows 桌面的技术，引擎保持 Go。
- **便于二期做"服务模式"**：引擎可改为 Windows 服务（SYSTEM），GUI 不变。

---

## 4. 系统架构

### 4.1 架构图

```
┌──────────────────────────── 用户会话（普通权限） ────────────────────────────┐
│                                                                              │
│  ┌─────────────────────────────┐        HTTP JSON (127.0.0.1:随机端口)       │
│  │ NasDrive.exe  (GUI, WPF)    │  ───────────────────────────────────────┐   │
│  │ ├ 托盘 TrayIcon             │   Basic Auth：每次启动随机生成的令牌      │   │
│  │ ├ 主窗口 / 添加 / 设置       │                                          ▼   │
│  │ ├ ProfileStore (JSON)       │        ┌──────────────────────────────────┐ │
│  │ ├ CredentialStore (凭据管理器)│        │ nasdrive-engine.exe (Go)         │ │
│  │ ├ EngineSupervisor          │ 启动/守护 │ ├ rc server (rclone fs/rc)       │ │
│  │ ├ DriveManager + 状态机      │ ───────▶ │ ├ mount/* (cmd/mountlib)         │ │
│  │ ├ StatusPoller              │        │ ├ cmount ──▶ cgofuse ──▶ WinFsp  │ │
│  │ ├ Autostart / Registry      │        │ ├ VFS + 本地缓存(%LOCALAPPDATA%) │ │
│  │ └ Updater / i18n / Logger   │        │ ├ backend/webdav ── HTTPS ───────┼─┼──▶ NAS
│  └─────────────────────────────┘        │ └ 自定义 rc: nasdrive/health 等    │ │  (WebDAV)
│                                          └──────────────────────────────────┘ │
│  资源管理器 / Office / 任意程序 ──▶ Z:\ ──▶ WinFsp 内核驱动 ──▶ 引擎 VFS       │
└──────────────────────────────────────────────────────────────────────────────┘
```

### 4.2 IPC 方式对比

| 方式 | 说明 | 结论 |
|---|---|---|
| **rc over HTTP（回环）** | 引擎监听 `127.0.0.1:<随机端口>`，GUI 用 HttpClient 调用；rclone 原生支持 `--rc-user/--rc-pass` | ✅ 推荐：调试方便（可用 curl），完全复用 rclone 现有协议 |
| librclone.dll 进程内 | GUI 通过 P/Invoke 调 `RPC()` | ❌ 挂载跟 GUI 同生共死，失去故障隔离 |
| 命名管道 | 更安全，不占端口 | 二期可选：rclone rcserver 需改造，暂不做 |

安全措施：只绑定 `127.0.0.1`；用户名/密码每次启动随机生成（32 字节），通过**环境变量**（而非命令行，避免被其他进程读取）传给引擎；引擎不开启 `--rc-serve`、不开启 Web GUI。

### 4.3 GUI 技术选型（**待您决定**）

| 方案 | 优点 | 缺点 |
|---|---|---|
| **C# .NET 8 + WPF**（推荐） | Windows 原生、成熟；托盘（H.NotifyIcon）、凭据管理器、注册表、DPAPI、开机启动均有现成 API；深色主题自定义样式容易做出 RaiDrive 风格；可自包含发布 | 双语言（Go + C#）；需要 .NET 运行时（可自包含，体积 +60MB，或用 ReadyToRun/裁剪约 30MB） |
| Go + Wails（WebView2 + Vue/React） | 单语言，可把引擎和 GUI 合并为一个代码仓库 | Wails v2 托盘支持弱；WebView2 运行时依赖；Windows API 调用需自行封装 |
| WinUI 3 | 最新微软 UI 风格 | 打包和部署复杂度高，生态不如 WPF 稳定 |
| Qt (C++) | 跨平台 | 许可与开发成本高，对本需求过重 |

> 下文以 **WPF** 为准描述 GUI 设计；若改用 Wails，模块划分不变，仅实现语言不同。

---

## 5. 引擎设计（nasdrive-engine.exe，Go）

### 5.1 代码组织

```
nasdrive/
├─ engine/
│  ├─ go.mod                  # require github.com/rclone/rclone v1.77.0
│  ├─ main.go                 # 入口：解析参数、初始化、启动 rc server
│  ├─ imports.go              # 只引入需要的包（见 5.2）
│  ├─ rc_nasdrive.go          # 自定义 rc 接口（见 5.4）
│  ├─ health.go               # NAS 连通性检测
│  └─ logging.go              # 日志文件与轮转
├─ gui/                       # WPF 解决方案
├─ installer/                 # Inno Setup / WiX 脚本 + WinFsp MSI
└─ docs/
```

`rclone_github/` 仅作为源码参考和本地 `replace` 调试使用（`go.mod` 中 `replace github.com/rclone/rclone => ../rclone_github`），正式构建使用锁定版本。

### 5.2 最小化引入（体积和攻击面最小）

```go
import (
    _ "github.com/rclone/rclone/backend/webdav"   // WebDAV 后端
    _ "github.com/rclone/rclone/backend/local"    // VFS 缓存需要
    _ "github.com/rclone/rclone/cmd/cmount"       // WinFsp 挂载（需 -tags cmount）
    "github.com/rclone/rclone/fs/rc/rcserver"     // rc 服务
    _ "github.com/rclone/rclone/fs/operations"    // operations/about 等 rc 接口
    _ "github.com/rclone/rclone/vfs"              // vfs/* rc 接口
    // 二期：backend/sftp、backend/ftp
)
```

构建命令：

```bash
GOOS=windows GOARCH=amd64 go build -tags cmount -trimpath -ldflags "-s -w -H windowsgui" -o nasdrive-engine.exe ./engine
```

`-H windowsgui` 使引擎不弹出控制台窗口。另构建 arm64 版本支持 Windows on ARM。

### 5.3 启动流程
1. 读取环境变量 `NASDRIVE_RC_USER / NASDRIVE_RC_PASS`、参数 `--port`、`--cache-dir`、`--log-file`、`--parent-pid`。
2. 设置全局选项：`cache_dir`、日志级别、`http_proxy`（若设置）、`timeout`/`contimeout`。
3. 启动 rcserver，监听 `127.0.0.1:<port>`；就绪后向 stdout 输出 `READY <port>`（GUI 据此判断启动成功）。
4. 监视父进程（GUI）：父进程退出**不**卸载（挂载继续工作）；仅收到 `core/quit` 或 `mount/unmountall` 才退出。
5. 检测 WinFsp 是否安装（加载 `winfsp-x64.dll` 失败时）→ 通过 `nasdrive/health` 返回 `winfsp_missing`，GUI 提示修复安装。

### 5.4 自定义 rc 接口（新增，不改 rclone 源码）

通过 `rc.Add(rc.Call{...})` 在引擎中注册：

| 接口 | 入参 | 出参 | 说明 |
|---|---|---|---|
| `nasdrive/version` | — | 引擎版本、rclone 版本、WinFsp 版本 | 关于页 / 托盘标题 |
| `nasdrive/health` | — | `winfsp_ok`、缓存目录可写、可用空间 | 自检 |
| `nasdrive/test` | `fs`（连接字符串） | `ok`、`latencyMs`、`errorCode`、`errorMsg`、`quota` | "测试连接"按钮：对根目录做一次 PROPFIND + About；错误归类为 DNS/超时/证书/401/404 等 |
| `nasdrive/mounts` | — | 每个挂载：盘符、fs、uptime、vfs 统计、上传队列长度、最近错误 | 聚合 `mount/listmounts` + `vfs/stats` + `vfs/queue`，减少 GUI 轮询次数 |
| `nasdrive/probe` | `mountPoint` | `online`/`offline` | 后台每 30s 对每个挂载做轻量 PROPFIND(Depth 0)，用于"离线"状态显示 |

### 5.5 复用的 rclone 原生 rc 接口

| 接口 | 用途 |
|---|---|
| `core/obscure` | 把明文密码转为连接字符串所需的 obscure 形式 |
| `mount/mount` | 挂载（参数见 §6.3） |
| `mount/unmount` / `mount/unmountall` | 断开 |
| `mount/listmounts` | 校对实际挂载状态 |
| `operations/about` | 获取容量（总/已用/可用） |
| `vfs/stats`、`vfs/queue` | 缓存占用、待上传文件数（断开前提醒） |
| `vfs/refresh`、`vfs/forget` | 主窗口"刷新"操作，立即刷新目录缓存 |
| `core/stats` | 实时上传/下载速度（主窗口可选显示） |
| `options/set` | 运行时修改代理、带宽限制等全局选项 |
| `core/quit` | 退出引擎 |

---

## 6. 挂载参数映射

### 6.1 界面字段 → rclone 参数

| 界面字段 | rclone 参数 | 默认值 | 备注 |
|---|---|---|---|
| 盘符 | `mountPoint` = `Z:` | 第一个可用盘符（从 Z 倒序） | GUI 用 `GetLogicalDrives()` 列出可用盘符 |
| 名称 | `volname` | `\\<host>\<名称>` | 网络模式下作为卷标显示；保证全局唯一（重名自动加后缀） |
| HTTPS 勾选 + 主机 + 端口 + 路径 | webdav `url` = `https://host:port/路径前缀` | HTTPS、443 | 见 6.2 关于"地址路径"与"挂载子目录" |
| 用户名 / 密码 | webdav `user` / `pass`（obscure） | — | 匿名时不传 |
| 厂商（二期预设） | webdav `vendor` | `other` | Nextcloud 用 `nextcloud`，启用分块上传 |
| 只读 | `vfs_opt.ReadOnly=true` | 否 | 状态图标🔒 |
| 模拟本地磁盘 = 否 | `network_mode=true` | **网络驱动器（推荐）** | 无缩略图、对延迟友好 |
| 模拟本地磁盘 = 是 | `network_mode=false` | — | 部分只认本地盘的软件需要 |
| 超时（秒） | 全局 `timeout`、`contimeout` | 100s / 15s | rc `options/set` 设置 `main.Timeout` 等 |
| 忽略证书错误 | `no_check_certificate` | 否 | 自签名证书 NAS 常用；界面红字警告 |
| 自定义 CA | `ca_cert` | — | 二期 |
| 代理 | `http_proxy` | 系统/无 | 设置页全局；按驱动器启用（见 §11 限制） |
| 临时目录 | `cache_dir` | `%LOCALAPPDATA%\NasDrive\Cache` | 引擎启动参数 |
| 读取持续时间 | `vfs_cache_max_age` | 1h | 缓存文件保留时长 |
| 写入持续时间 | `vfs_write_back` | 5s | 文件关闭后多久开始上传 |
| 缓存上限（新增） | `vfs_cache_max_size` | 10G | RaiDrive 界面无，建议加在高级设置 |

### 6.2 NAS 场景默认 VFS 参数（经验值，需实测调优）

| 参数 | 值 | 理由 |
|---|---|---|
| `vfs_cache_mode` | `full` | 支持随机读写、Office 保存、视频拖动；`writes` 为保守备选 |
| `dir_cache_time` | `30s` | WebDAV 无变更通知（无 ChangeNotify），缩短以较快看到他人修改；配合"刷新"按钮 |
| `vfs_read_chunk_size` | `16M` | 局域网大文件顺序读性能 |
| `vfs_read_ahead` | `64M` | 视频播放流畅 |
| `vfs_cache_min_free_space` | `5G` | 防止把系统盘写满 |
| `vfs_case_insensitive` | `true` | Windows 语义（WinFsp 默认即为不区分大小写） |
| `vfs_fast_fingerprint` | `true` | 减少 WebDAV 上的哈希请求 |
| `vfs_disk_space_total_size` | 不设 | 仅当 NAS 不返回配额时由用户在高级设置填入 |
| WinFsp `FileSecurity` | `--option FileSecurity=D:P(A;;FA;;;OW)`（rc 中为 `mountOpt.ExtraOptions`） | 避免 Windows 权限导致"无法写入"（mount.md "Windows filesystem permissions" 一节） |

### 6.3 不使用 rclone.conf：连接字符串

挂载时 GUI 组装 rclone **即时远程（on-the-fly remote）** 字符串，密码先经 `core/obscure` 处理：

```
:webdav,url='https://192.168.1.10:5006',vendor=other,user='nasuser',pass='<obscured>':/share
```

调用示例：

```json
POST /mount/mount
{
  "fs": ":webdav,url='https://192.168.1.10:5006',vendor=other,user='nasuser',pass='xxxx':/share",
  "mountPoint": "J:",
  "mountType": "cmount",
  "mountOpt": { "VolumeName": "\\\\192.168.1.10\\Share", "NetworkMode": true, "ExtraOptions": ["FileSecurity=D:P(A;;FA;;;OW)"] },
  "vfsOpt":   { "CacheMode": "full", "DirCacheTime": "30s", "CacheMaxAge": "1h", "WriteBack": "5s", "ReadOnly": false }
}
```

好处：磁盘上**没有任何含密码的 rclone 配置文件**；密码只存在于 Windows 凭据管理器和引擎内存中。
（mountOpt 字段名已与 `cmd/mountlib/mount.go:175` 的 `Options` 结构体核对一致：`VolumeName`、`NetworkMode`、`ExtraOptions`；也可使用 rc 支持的扁平参数写法 `volname=...`、`network_mode=true`。）

---

## 7. GUI 设计（NasDrive.exe）

### 7.1 模块划分

| 模块 | 职责 |
|---|---|
| `App` | 单实例（Mutex `Global\NasDrive.Gui.<SID>`）；第二次启动时激活已有窗口；处理 `--minimized` 参数 |
| `TrayIconService` | 托盘图标（已连接数徽标）、左/右键菜单、气泡通知 |
| `MainWindow` + `DriveListViewModel` | 驱动器列表与操作 |
| `DriveEditorWindow` | 新建/编辑虚拟驱动器 |
| `SettingsView` | 设置页 |
| `ProfileStore` | 驱动器配置的读写（JSON），带版本号和迁移 |
| `CredentialStore` | 通过 Windows 凭据管理器（`CredWrite/CredRead`，目标名 `NasDrive:<profileId>`）存取密码 |
| `EngineSupervisor` | 启动引擎、健康检查、崩溃重启（指数退避：1s/2s/5s/10s，最多 5 次） |
| `EngineClient` | rc HTTP 客户端，强类型封装 §5.4/§5.5 的接口 |
| `DriveManager` | 每个驱动器一个状态机（§7.6），串行化连接/断开操作 |
| `StatusPoller` | 每 5s 调用 `nasdrive/mounts`，每 60s 调 `operations/about` 更新容量 |
| `ShellIntegration` | 打开资源管理器、驱动器图标/卷标注册表、`SHChangeNotify` 刷新 |
| `AutostartService` | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\NasDrive = "...\NasDrive.exe --minimized"` |
| `UpdateService` | 读取更新清单 JSON（GitHub Releases 或自建服务器），比较版本，下载安装包并校验签名 |
| `Localization` | 资源文件 zh-CN / en-US / （可扩展），运行时切换 |
| `Logger` | Serilog 写 `%LOCALAPPDATA%\NasDrive\Logs\gui-*.log`，保留 7 天 |

### 7.2 托盘菜单（对应 PDF 图 1）

```
┌────────────────────────────────┐
│ NasDrive  1.0.0                │  ← 标题（不可点）
├────────────────────────────────┤
│ ▣ 打开 NasDrive                │  → 显示主窗口
│ ✔ 检查更新                      │
│ ⊕ 添加                          │  → 新建驱动器窗口
│ ⚙ 设置                          │
├────────────────────────────────┤
│ 🌐 Setup (H:)          ▸        │  灰色 = 未连接
│ 🌐 DOC (I:)            ▸        │  白色 = 已连接
│      ├ 断开 / 连接
│      ├ 在资源管理器中打开
│      ├ 刷新目录缓存
│      └ 编辑…
├────────────────────────────────┤
│ ⇥ 退出                          │  → 若有待上传文件先确认；询问是否同时断开所有驱动器
└────────────────────────────────┘
```

- 左键单击：弹出上述菜单（与 RaiDrive 一致）；双击：打开主窗口。
- 托盘图标三态：灰（无连接）、蓝（有连接）、黄（有错误/离线）。

### 7.3 主窗口（对应 PDF 图 2，去除广告与登录）

```
┌─ NasDrive 1.0.0 ─────────────────────────────── ⊕添加 │ ⚙设置 │ ✕ ┐
│ ┌────┬──────────────────────────────────────────┬──────────────────┐│
│ │ 🌐 │ [J:] Share            nasuser@192.168.1.10 │ ■  ⚙  📂  🗑 ││
│ │    │ ▓▓▓▓▓▓▓▓░░░░░░░░░░░  /share               │                  ││
│ │    │ 1.2 TB 可用 / 共 3.6 TB    🔒 ⇄ 🌐 🛡       │                  ││
│ └────┴──────────────────────────────────────────┴──────────────────┘│
│   …其他驱动器行…                                                       │
│   [空列表时：大号"添加你的第一个 NAS 驱动器"按钮]                         │
└──────────────────────────────────────────────────────────────────────┘
```

- 行内状态小图标：🔒只读、⇄代理、🌐网络驱动器模式、🛡HTTPS、⚠离线/错误（悬停显示原因）、⬆N（N 个文件待上传）。
- 按钮规则：
  - ▶/■：未连接显示 ▶；连接中显示转圈且不可点；已连接显示 ■。
  - ⚙ 编辑：已连接时可查看，保存需先断开（或提示"保存后将重新连接"）。
  - 📂 打开：仅已连接可用。
  - 🗑 删除：仅未连接可用；二次确认；同时删除凭据和该驱动器缓存。
- 容量条：取自 `operations/about`；NAS 不支持配额时显示"未知空间容量"（与 RaiDrive 一致）。
- 关闭按钮 = 最小化到托盘（首次给出提示）。
- 支持拖动排序；右键菜单与托盘子菜单一致。

### 7.4 新建/编辑虚拟驱动器（对应 PDF 图 4）

```
新虚拟驱动器
服务类型   [NAS]                                      （首版仅 NAS 页签）
           ┌──────────────────────────────────────┐
           │ ◉ WebDAV   ○ Synology  ○ QNAP         │  二期：预设
           │ ○ Nextcloud ○ ASUSTOR   (○ SFTP ○ FTP)│  三期
           └──────────────────────────────────────┘
虚拟驱动器 [Z: ▾] [名称________________]
           ☑ 启动 NasDrive 后自动连接      ☐ 使用代理
           ☐ 连接时输入账号密码            ☐ 只读
           ☐ 模拟本地磁盘（默认网络驱动器）
地址       ☑ HTTPS  https://[host.example.com____] : [443]
                   路径 [/________________________]
账户       [用户名______] [密码______]  ☐ 匿名
安全       ☐ 忽略证书错误（不安全）
超时       [100 ▾] 秒
▸ 高级     缓存模式 / 缓存上限 / 目录缓存时间 / 容量兜底 / 厂商 / 自定义 HTTP 头

[测试连接]                         [连接]  [取消]  [? 帮助]
```

交互细节：
- 主机输入框支持直接粘贴完整 URL（`https://nas:5006/dav/share`），自动拆分为协议/主机/端口/路径。
- 勾选/取消 HTTPS 时，若端口是 443/80 则自动切换。
- 必填项（主机、用户名、密码）为空时红框提示（与 RaiDrive 一致），「连接」按钮禁用。
- 「测试连接」调用 `nasdrive/test`，按错误类型给出中文提示：
  - 无法解析主机 / 连接超时 → "请检查地址与网络，或确认 NAS 已开启 WebDAV 服务"
  - 证书错误 → "证书不受信任，可勾选'忽略证书错误'或导入 CA"
  - 401 → "用户名或密码错误"；403 → "该账户无权访问此路径"；404/405 → "路径不存在或该端口不是 WebDAV 服务"
- 「连接」= 校验 → 保存配置 → 写入凭据 → 挂载；失败则保留配置并显示错误。
- "连接时输入账号密码"：不保存密码，每次连接弹出输入框。

### 7.5 设置页（对应 PDF 图 3）

| 分组 | 项目 | 实现 |
|---|---|---|
| 常规 | 添加到启动项 | `AutostartService` |
| | 连接虚拟驱动器后打开资源管理器 | 挂载成功后 `explorer.exe Z:\` |
| | 允许虚拟驱动器使用自定义图标 | `ShellIntegration` 写 DriveIcons 注册表 |
| 语言 | 简体中文 / English | 运行时切换 |
| 缓存 | 临时目录 + 浏览 + 默认 | 修改后需重启引擎（提示并自动执行：断开 → 重启 → 重新连接） |
| | 读取持续时间 / 写入持续时间 | 见 §6.1 |
| | 缓存上限、清空缓存按钮 | 清空前确认无待上传文件 |
| 检查更新 | 检查按钮 + 当前状态；☐ 自动检查 | `UpdateService` |
| 代理 | 类型（无/系统/HTTP）、主机、端口、用户名、密码、应用、默认 | `options/set` 设置 `http_proxy`（代理认证写成 `http://user:pass@host:port`，密码存凭据管理器） |
| 高级 | 日志级别、打开日志目录、导出诊断包（脱敏）、带宽限制（`core/bwlimit`） | |
| 关于 | 版本、rclone 版本、WinFsp 版本、开源许可声明 | 必须展示 rclone MIT、WinFsp 许可 |

### 7.6 驱动器状态机

```
             连接(手动/自动)                  挂载成功
 ┌────────┐ ─────────────▶ ┌────────────┐ ───────────▶ ┌───────────┐
 │未连接   │                │ 连接中      │              │ 已连接     │◀──┐
 │Disconn.│ ◀───────────── │ Connecting │              │ Connected │   │ 探测恢复
 └────────┘   失败(记录错误) └────────────┘              └───────────┘   │
     ▲  ▲                                          探测失败 │   │断开     │
     │  │                                                  ▼   │        │
     │  │      卸载完成   ┌──────────────┐   断开      ┌──────────┐       │
     │  └──────────────── │ 断开中        │ ◀───────── │ 离线      │ ──────┘
     │                    │ Disconnecting│            │ Offline  │
     │                    └──────────────┘            └──────────┘
     │  引擎崩溃：所有状态 → 未连接；EngineSupervisor 重启后对"应连接"的驱动器自动重连
```

- **离线**：盘符仍在（WinFsp 挂载未卸载），访问会超时；UI 显示黄色警告，恢复后自动回到已连接。
- **断开前检查**：`vfs/queue` 有待上传文件时弹窗："还有 N 个文件未上传，立即断开可能丢失修改。[等待上传完成] [强制断开] [取消]"。
- 对同一驱动器的操作串行化，防止重复点击。

### 7.7 数据模型

`%APPDATA%\NasDrive\profiles.json`（不含密码）：

```json
{
  "schemaVersion": 1,
  "drives": [
    {
      "id": "6f1c…uuid",
      "enabled": true,
      "name": "Share",
      "driveLetter": "J",
      "serviceType": "webdav",
      "vendor": "other",
      "https": true,
      "host": "192.168.1.10",
      "port": 5006,
      "basePath": "/share",
      "username": "nasuser",
      "anonymous": false,
      "promptCredentials": false,
      "autoConnect": true,
      "readOnly": false,
      "networkMode": true,
      "useProxy": false,
      "skipTlsVerify": false,
      "timeoutSec": 100,
      "customIcon": null,
      "advanced": { "cacheMode": "full", "dirCacheTime": "30s", "cacheMaxSize": "10G", "diskSpaceTotal": null, "headers": [] },
      "order": 2
    }
  ]
}
```

`%APPDATA%\NasDrive\settings.json`：启动项、语言、缓存目录、读写持续时间、代理（不含密码）、更新选项、日志级别、窗口位置等。

### 7.8 关键时序：开机自启与自动连接

```
Windows 登录
  └─▶ NasDrive.exe --minimized（HKCU Run）
        ├─ 单实例检查
        ├─ 加载 settings / profiles
        ├─ EngineSupervisor：生成随机 rc 凭据 → 启动 engine（端口 0 自动分配）→ 等待 "READY <port>"
        ├─ nasdrive/health：WinFsp 缺失 → 托盘气泡 + 引导修复
        ├─ mount/listmounts：与配置比对（引擎复用场景）
        ├─ 对 autoConnect=true 的驱动器依次（并发度 2）连接：
        │     读取凭据 → core/obscure → mount/mount → 成功则写注册表图标/卷标
        │     失败（如网络未就绪）→ 按 5s/15s/30s/60s 重试，期间显示"等待网络"
        └─ 启动 StatusPoller
```

---

## 8. Windows 外壳集成

| 功能 | 实现方式 | 备注 |
|---|---|---|
| 网络驱动器显示名 | 网络模式下 volname `\\host\name`；另写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\MountPoints2\##host#name\_LabelFromReg` | 需实测 |
| 自定义驱动器图标 | `HKCU\Software\Classes\Applications\Explorer.exe\Drives\<盘符>\DefaultIcon`（每用户），回退 `HKLM\…\Explorer\DriveIcons\<盘符>\DefaultIcon`（需管理员，安装时可选） | 断开时清理；需在 Win10/11 实测确认 |
| 通知资源管理器刷新 | `SHChangeNotify(SHCNE_DRIVEADD / SHCNE_DRIVEREMOVED)` | 避免盘符不出现/不消失 |
| 打开资源管理器 | `Process.Start("explorer.exe", "J:\\")` | |
| 资源管理器右键菜单（可选，三期） | "复制 NAS 路径"、"刷新此文件夹" | Shell 扩展，复杂度高 |

---

## 9. 安全设计

1. **凭据**：密码存 Windows 凭据管理器（底层 DPAPI，绑定当前用户）；配置文件无密码；日志与诊断包对 `pass=`、`Authorization` 脱敏。
2. **引擎控制通道**：仅 127.0.0.1；随机凭据经环境变量传入；不启用 `rc-serve`/web GUI/`core/command`（引擎中不导入执行外部命令的模块；对 `core/command` 等敏感接口在引擎内注销或拒绝）。
3. **TLS**：默认校验证书；"忽略证书错误"需用户显式勾选，界面持续显示⚠图标。HTTP 明文连接时在编辑窗口提示风险。
4. **权限**：GUI 与引擎以普通用户运行（`asInvoker` 清单），避免管理员挂载导致资源管理器不可见。
5. **"公开"模式（二期）**：引擎以 Windows 服务（LocalSystem）运行，挂载对所有用户可见；GUI 通过命名管道与服务通信；需要处理多用户凭据隔离，风险较高，单列评审。
6. **升级包**：代码签名（Authenticode）+ 清单中的 SHA-256 校验。

---

## 10. 安装、升级与发布

- **安装器**：Inno Setup（简单）或 WiX（MSI，便于企业部署）。内容：
  - `NasDrive.exe`（自包含 .NET 8）、`nasdrive-engine.exe`、语言资源、许可证文件；
  - **WinFsp MSI**：检测未安装或版本过低时静默安装 `msiexec /i winfsp-x.y.msi /qn INSTALLLEVEL=1000`（仅需 Core 组件，需管理员权限，安装器统一提权一次）；
  - 可选：开机启动、桌面快捷方式。
- **安装位置**：`%ProgramFiles%\NasDrive`；用户数据 `%APPDATA%\NasDrive`；缓存 `%LOCALAPPDATA%\NasDrive\Cache`。
- **卸载**：先断开所有驱动器、结束引擎；可选保留配置；**不卸载 WinFsp**（可能被其他软件使用）。
- **升级**：下载新安装包 → 校验 → 提示用户 → 断开（确认无待上传）→ 静默安装 → 重启应用并自动重连。
- **CI**：GitHub Actions，windows-latest 构建引擎（Go）和 GUI（dotnet publish），签名，打包，生成更新清单。

---

## 11. 风险与限制

| 风险 | 影响 | 对策 |
|---|---|---|
| WebDAV 无文件锁（LOCK）支持 | 多人同时编辑同一文件可能互相覆盖；Office 不能显示"他人正在编辑" | 首版明确声明不支持；文档建议协作场景使用 NAS 自带协同；后续可研究在 VFS 层实现 LOCK（需改 rclone，工作量大） |
| WebDAV 无变更通知 | 他人修改需等目录缓存过期（30s）才可见 | 可配置 `dir_cache_time`；提供"刷新"按钮（`vfs/refresh`） |
| 缓存模式 full 下写回延迟 | 断网/强制关机可能导致未上传修改滞留缓存 | rclone 会在下次挂载时恢复上传（vfscache 持久化）；断开/退出前检查上传队列；托盘显示⬆N |
| 大文件（>4GB）上传 | 部分 NAS WebDAV 有单文件大小或超时限制 | 超时可调；Nextcloud 使用分块上传；测试矩阵覆盖 |
| 不同 NAS WebDAV 实现差异 | 修改时间、特殊字符、配额支持不一 | 测试矩阵（群晖 DSM 7、威联通 QTS 5、Nextcloud、Apache mod_dav、IIS WebDAV）；厂商预设 |
| 最低 TLS 版本无法指定 | 与 RaiDrive 界面不一致 | 改为证书相关选项；如确需，在引擎中设置 `http.Transport.TLSClientConfig.MinVersion`（需小幅改造 fshttp，评审决定） |
| 按驱动器单独代理 | rclone `http_proxy` 为全局选项 | 首版：代理全局生效，驱动器上"使用代理"勾选仅在多引擎实例模式下生效；或简化为"全局代理 + 驱动器级排除"，**需评审** |
| WinFsp 依赖与许可 | 安装需管理员；闭源分发受限 | 见 §12 |
| 杀毒软件误报 | Go 程序 + 文件系统驱动可能被误报 | 代码签名；提交主流厂商白名单 |
| 引擎崩溃 | 所有盘符消失，正在写入的程序报错 | 故障隔离 + 自动重启重连；充分测试 |
| 开发机环境 | 当前工作目录位于非 APFS 卷，克隆后产生 `._*` AppleDouble 文件（`git status` 可见） | 在 `.gitignore` 中忽略 `._*`，或改用本地 APFS 磁盘开发；Windows 构建建议在 Windows/CI 上进行 |

---

## 12. 许可证合规

| 组件 | 许可证 | 要求 |
|---|---|---|
| rclone | MIT | 再分发时保留版权声明（关于页 + LICENSE 文件） |
| cgofuse | MIT | 同上 |
| **WinFsp** | **GPLv3 + FLOSS 例外** | 若 NasDrive **开源**（使用 FLOSS 许可），可免费随包分发；若 **闭源/商业销售**，需向 WinFsp 作者购买商业许可，或让用户自行安装 WinFsp（体验下降）。**请在立项时决定产品开源与否** |
| .NET 8 运行时 | MIT | 无特殊要求 |
| H.NotifyIcon、Serilog 等 | MIT/Apache-2.0 | 保留声明 |

---

## 13. 开发计划（建议分期）

### 一期 MVP（约 5–6 周，2 人）
| 周 | 引擎（Go） | GUI（WPF） |
|---|---|---|
| 1 | 工程搭建、最小化引入、rc 启动、READY 握手、Windows 构建 | 工程搭建、单实例、托盘、主窗口骨架、深色主题 |
| 2 | `nasdrive/test`、`nasdrive/health`、mount 参数验证 | ProfileStore、CredentialStore、新建/编辑窗口 |
| 3 | `nasdrive/mounts`、`nasdrive/probe`、日志轮转 | EngineSupervisor、EngineClient、DriveManager 状态机 |
| 4 | NAS 兼容性测试与默认参数调优 | 设置页、开机自启、自动连接、容量显示、错误提示 |
| 5 | 稳定性（断网、休眠唤醒、引擎重启） | 托盘子菜单、断开前检查、i18n、外壳集成 |
| 6 | 安装包（含 WinFsp）、签名、联调、Bug 修复 | 同左 |

**一期交付**：WebDAV 驱动器增删改、连接/断开、开机自动连接、托盘、设置、容量显示、离线检测、安装包。

### 二期（约 3 周）
Synology/QNAP/Nextcloud/ASUSTOR 预设、自动更新、自定义图标、诊断包、带宽限制、自定义 CA、"公开"模式（服务化）可行性验证。

### 三期（按需）
SFTP/FTP、Shell 右键扩展、文件锁研究、ARM64、企业部署（MSI + 组策略预配置）。

---

## 14. 测试方案

| 类别 | 内容 |
|---|---|
| 单元测试 | Go：自定义 rc 接口（用 rclone `backend/memory` 或本地 `rclone serve webdav` 做服务端）；C#：ViewModel、URL 解析、状态机 |
| 集成测试 | 用 `rclone serve webdav` 在本机起 WebDAV 服务，自动化执行挂载→读写→断开 |
| 兼容性矩阵 | NAS：群晖 DSM 7.2、威联通 QTS 5、Nextcloud 28+、Apache、IIS；系统：Win10 22H2、Win11 23H2/24H2，x64/ARM64 |
| 应用兼容 | Office（Word/Excel 打开-编辑-保存）、WPS、Photoshop、VLC/PotPlayer 拖动播放、7-Zip 解压、Git 仓库（只做提示性测试）、资源管理器复制/重命名/删除大量小文件 |
| 异常场景 | 拔网线、NAS 重启、电脑睡眠/唤醒、密码被修改、证书过期、盘符被占用、缓存盘满、引擎被强杀、WinFsp 未安装 |
| 性能 | 千兆局域网下大文件读写吞吐（目标 ≥ 80MB/s）、1 万个小文件目录列出时间、内存占用（引擎空闲 < 80MB） |

---

## 15. 需要您确认的问题

1. **GUI 技术栈**：C# WPF（推荐） 还是 Go + Wails（单语言）？
2. **产品是否开源**？决定 WinFsp 许可方式（§12）。
3. **代理**：接受"全局代理"简化方案吗，还是必须支持每个驱动器独立代理？
4. **TLS 版本选项**：是否接受用"忽略证书错误 / 自定义 CA"替代"TLS 版本下拉"？
5. **"公开"（所有 Windows 用户可见）模式**是否为一期必需？
6. **NAS 型号**：您主要使用的 NAS 品牌/型号与 WebDAV 端口（截图中为 192.168.1.10 / 253），用于优先建立测试环境和厂商预设。
7. **产品名称与图标**："NasDrive" 是否可用？
8. 是否需要多语言（除简体中文外的英文等）？

---

## 附录 A：快速原型验证（无需写代码，可在 Windows 上先行体验核心能力）

1. 安装 WinFsp：https://winfsp.dev
2. 下载官方 rclone Windows 版，执行：

```bash
rclone obscure "你的NAS密码"
```

```bash
rclone mount ":webdav,url='https://192.168.1.10:5006',vendor=other,user='nasuser',pass='上一步输出':/share" J: --network-mode --volname "\\NAS\Share" --vfs-cache-mode full --dir-cache-time 30s
```

3. 打开资源管理器即可看到 `J:` 网络驱动器。这验证了引擎层方案；GUI 只是把这些参数可视化并加上生命周期管理。

## 附录 B：参考源码位置索引

| 内容 | 文件 |
|---|---|
| rc 挂载接口 | `rclone_github/cmd/mountlib/rc.go`（`mount/mount` 行 49 起） |
| 挂载选项定义 | `rclone_github/cmd/mountlib/mount.go`（`network_mode` 行 147、`volname` 行 132） |
| Windows 盘符处理 | `rclone_github/cmd/cmount/mountpoint_windows.go` |
| Windows 挂载文档 | `rclone_github/cmd/mountlib/mount.md`（行 67–268） |
| VFS 选项 | `rclone_github/vfs/vfscommon/options.go` |
| VFS rc 接口 | `rclone_github/vfs/rc.go` |
| WebDAV 后端选项 | `rclone_github/backend/webdav/webdav.go`（行 71–217；About 行 1397） |
| 全局 HTTP 选项 | `rclone_github/fs/config.go`（timeout/contimeout/no_check_certificate/ca_cert/http_proxy） |
| C 库接口 | `rclone_github/librclone/librclone/librclone.go` |
| 官方 Web GUI | `rclone_github/cmd/gui/gui.go`、`rclone_github/docs/content/gui.md` |
