# NasDrive macOS 移植：可行性评估与方案

> 日期：2026-11-20　基于 NasDrive 0.4.0　结论：**可行，推荐实施；引擎基本复用，GUI 需按平台重做，约 10–12 人周**

## 1. 结论摘要

| 问题 | 结论 |
|---|---|
| 是否可行 | **可行**。NasDrive 的"大脑"（Go 引擎：rclone + 5 种协议 + rc 控制接口）本身就是跨平台的，本仓库的引擎测试一直在 macOS 上编译运行 |
| 最大技术风险（挂载） | **已实测消除**：rclone 内置的 `nfsmount` 在 macOS 26（Apple Silicon）上**无需管理员权限、无需安装任何驱动**即可挂载、读写、卸载（本机实测：中文文件读取正常、32 MB 写入约 106 MB/s） |
| 架构是否要改 | **总体架构不变**（GUI 进程 + 引擎进程 + rc HTTP 接口）。需要：引擎按平台选择挂载方式；把 GUI 中与平台无关的逻辑抽成共享库；macOS 端重做界面与系统集成层 |
| 工作量 | 约 **10–12 人周**（1 名熟练开发者 2.5–3 个月），其中真机测试与打包签名约占 1/4 |

## 2. 现有架构中哪些可复用

```
NasDrive.exe (WPF, Windows 专用)  ──rc HTTP──▶  nasdrive-engine (Go, 跨平台)
```

| 组件 | 代码量 | macOS 复用度 | 说明 |
|---|---|---|---|
| 引擎：协议后端（WebDAV/NFS/群晖/SFTP/FTP）、VFS 缓存、rc 接口、错误分类 | ~5,000 行 Go | **≈95%** | 只需替换挂载方式与少量平台检测 |
| GUI：Models、连接字符串、引擎守护与 rc 客户端、驱动器状态机、多语言、ViewModels 逻辑 | ~3,500 行 C# | **≈60%**（抽成共享库后） | 依赖 WPF 的部分（Dispatcher、CommandManager、MessageBox）需抽象 |
| GUI：XAML 界面、托盘（WinForms NotifyIcon） | ~1,500 行 | 0% | 需重做 |
| Windows 系统集成：凭据管理器、注册表、盘符/资源管理器标签、提权检测、WinFsp | ~800 行 | 0% | 换成 macOS 对应机制 |
| 安装程序（Inno Setup + WinFsp） | — | 0% | 换成签名 + 公证的 .dmg |

## 3. macOS 与 Windows 的关键差异及对策

| 主题 | Windows 现状 | macOS 对策 |
|---|---|---|
| **挂载驱动** | WinFsp（cmount） | **首选 rclone `nfsmount`**：引擎内起本地 NFS 服务，用系统自带 NFS 客户端挂载；零驱动、零管理员权限（已实测）。备选 FUSE-T（无内核扩展，但闭源，需确认再分发许可）。**不用 macFUSE**：在 Apple 芯片上需进入恢复模式降低安全级别，普通用户无法接受 |
| **挂载位置** | 盘符 X: | 无盘符。挂载到 `~/NasDrive/<名称>`（或用户选择的目录）；连接后在 Finder 中打开；可选加入 Finder 边栏 |
| **密码存储** | 凭据管理器 | **钥匙串**（Security.framework，Generic Password，服务名 `NasDrive`） |
| **托盘** | 系统托盘图标 | **菜单栏图标**（不显示 Dock 图标：`LSUIElement`），状态颜色沿用 |
| **开机启动** | HKCU\Run | `SMAppService.mainApp`（macOS 13+ 登录项） |
| **只读** | WinFsp 安全描述符（解决"关闭时删除"假象） | 不需要该技巧：NFS 的删除直接返回只读错误；用 VFS ReadOnly + 挂载 `-o ro` 即可 |
| **以管理员运行** | 自动降权重启 | 不存在此问题，删除相关逻辑 |
| **Finder 元数据污染** | — | macOS 会在网络卷写 `._*`、`.DS_Store`（本项目在 NAS 上已遇到）：挂载时用 rclone 过滤规则排除，避免污染 NAS |
| **Unicode 规范化** | — | macOS 文件名为 NFD，NAS 多为 NFC：启用 rclone VFS 的规范化处理，验证带重音字母、日文假名 |
| **引擎崩溃后的挂载** | WinFsp 自动卸载 | NFS 挂载在服务端消失后会让 Finder 卡死：守护进程在引擎异常退出时立即 `umount -f`，启动时清理残留挂载 |
| **睡眠/唤醒、网络切换** | 自动重连 | 同样需要：监听系统唤醒与网络变化，探测并重连 |
| **分发** | 未签名 exe + SmartScreen 提示 | **必须签名 + 公证**（Apple Developer 账号 99 美元/年），否则 Gatekeeper 直接拦截；引擎二进制同样签名；arm64 + x86_64 通用包 |

## 4. GUI 技术路线比较

| 方案 | 说明 | 优点 | 缺点 | 评估 |
|---|---|---|---|---|
| **A. 共享 .NET 核心 + Avalonia 界面（推荐）** | 抽出 `NasDrive.Core`（net8.0），Windows 继续用现有 WPF，macOS 用 Avalonia（与 WPF 相近的 XAML） | 复用约 60% C#；协议字段等新功能只写一次逻辑；将来可让 Windows 也迁到 Avalonia 实现单一界面 | Avalonia 外观需细调才像原生 mac 应用 | ★★★★ |
| B. SwiftUI 原生应用 | macOS 端用 Swift 重写，直接调引擎 rc 接口 | 最原生的体验；将来可加 File Provider 扩展、上架 App Store | 逻辑全部重写，两套代码长期双倍维护 | ★★★ |
| C. 全部改用 Go GUI（Wails/Fyne）合并为单进程 | | 单语言 | 放弃现有成熟的 WPF 界面；双进程的稳定性优势也会丢失 | ★★ |

**推荐 A**：风险最低、复用最多；在 macOS 用户基数明确后，再评估是否用 SwiftUI 做更原生的版本。

## 5. 详细改造方案

### 5.1 引擎（约 1.5 人周）

1. **按平台选择挂载方式**：Windows `mountType=cmount`（WinFsp），macOS `mountType=nfsmount`；rclone v1.76 的 nfsmount 已注册 rc 挂载类型，`mount/mount` 调用方式不变。
2. 构建标签：Windows 保留 `-tags cmount`；macOS 纯 Go（`CGO_ENABLED=0`），链接 `cmd/nfsmount`。
3. `platform_darwin.go`：`nasdrive/health` 改为检查 `/sbin/mount_nfs` 是否可用、缓存目录剩余空间；`winfsp` 字段在 macOS 上返回不适用。
4. 挂载默认过滤：排除 `._*`、`.DS_Store`、`.Spotlight-V100`、`.Trashes`、`.fseventsd`。
5. 新增 `nasdrive/unmountstale`：启动时找出属于本程序但服务端已不存在的 NFS 挂载并强制卸载。
6. 通用二进制：`GOARCH=arm64` 与 `amd64` 分别编译后用 `lipo` 合并。

### 5.2 共享核心库 `NasDrive.Core`（约 1 人周）

- 迁入：Models、ConnectionString、EngineClient、EngineSupervisor（进程启动参数按平台）、DriveManager、Loc、Format、ViewModels（去掉 WPF 依赖）。
- 定义平台接口，各平台实现：
  - `ISecretStore`（Win：凭据管理器；mac：钥匙串）
  - `IShellIntegration`（打开挂载位置、标签/图标、通知）
  - `IMountTarget`（Win：盘符；mac：挂载目录）
  - `IAutostart`、`IUiDispatcher`、`IDialogs`
- Windows WPF 应用改为引用 Core（行为不变，需完整回归测试）。

### 5.3 macOS 应用（约 4–5 人周）

- Avalonia 11：主窗口（驱动器列表、容量、状态）、添加/编辑（五种协议、Nextcloud 预设、群晖列出共享、NFS 列出导出、SFTP 私钥选择）、设置、对话框（两步验证码、SFTP 指纹确认、凭据输入）。
- 菜单栏图标与菜单（复用托盘图标设计，提供 macOS 模板图标以适配深/浅色菜单栏）。
- 钥匙串、登录项、挂载目录管理（自动创建/清理 `~/NasDrive/<名称>`）、睡眠唤醒与网络变化监听、通知中心。
- 应用包：`NasDrive.app/Contents/MacOS/` 放 GUI 与 `nasdrive-engine`。

### 5.4 打包与分发（约 1 人周）

- `dotnet publish -r osx-arm64 / osx-x64` + `lipo`，生成通用 `.app`。
- 代码签名（Developer ID Application）、Hardened Runtime、`notarytool` 公证、`stapler` 装订；生成 `.dmg`。
- GitHub Releases 同时发布 Windows 安装包与 macOS dmg；"检查更新"按平台选择资产。

### 5.5 测试（约 2 人周）

- 矩阵：macOS 13 / 14 / 15 / 26 × Apple 芯片 / Intel × 五种协议（WebDAV、NFS、群晖含 2FA、SFTP 含私钥、FTP/FTPS）。
- 专项：Finder 浏览与拷贝、Office/预览直接打开编辑、只读、中文与带重音文件名、`._*` 不落盘到 NAS、睡眠唤醒与切换网络、引擎被强制结束后 Finder 不卡死、断开时的上传队列提示。
- 复用现有的引擎测试（已在 macOS 运行）；新增 macOS 端到端脚本（对应 Windows 的 `test-mount.ps1`）。

## 6. 计划与里程碑

| 阶段 | 内容 | 人周 | 产出 |
|---|---|---|---|
| P0 技术验证 | 引擎走 nfsmount 挂载五种协议；Finder 行为、Unicode、`._*`、睡眠唤醒、引擎崩溃后的清理 | 1 | 验证报告；不可行项与替代方案 |
| P1 引擎跨平台 | 5.1 全部 | 1.5 | macOS 引擎通用二进制 |
| P2 共享核心 | 5.2；Windows 版回归测试 | 1 | Core 库；Windows 0.5 行为不变 |
| P3 macOS 界面 | 5.3 | 4–5 | 可用的 macOS 应用 |
| P4 打包 | 5.4 | 1 | 签名公证的 dmg |
| P5 测试与 Beta | 5.5；文档 | 2 | macOS 0.5 Beta |
| **合计** | | **10.5–11.5** | |

## 7. 风险

| 风险 | 影响 | 缓解 |
|---|---|---|
| nfsmount 在 rclone 中标记为"实验性" | 边缘场景（文件锁、扩展属性、大目录）可能有问题 | P0 重点验证；保留 FUSE-T 作为备选挂载方式 |
| NFS 挂载在引擎退出后使 Finder 卡死 | 用户体验差 | 守护进程强制卸载 + 启动清理（5.1-5） |
| Apple 签名与公证 | 无开发者账号无法正常分发 | 提前申请 Developer ID；CI 中自动公证 |
| macOS 版本差异（13–26） | 登录项、通知等 API 行为不同 | 最低支持 macOS 13；测试矩阵覆盖 |
| 两套界面的长期维护 | 新功能需改两处界面 | 共享核心承载全部逻辑；中长期评估 Windows 迁移到 Avalonia |

## 8. 附：本机实测记录（2026-11-20）

环境：macOS 26.5，Apple 芯片，未安装 macFUSE/FUSE-T（`/Library/Filesystems` 仅有系统自带的 NetFSPlugins）。

```
rclone nfsmount <源目录> <挂载点> --vfs-cache-mode writes   # 普通用户，无 sudo
mount → localhost:/ on <挂载点> (nfs, nodev, nosuid, mounted by <当前用户>)
读取中文内容文件：正常
写入：首次写入有一次性的预热延迟；随后小文件即时完成，32 MB 写入 0.32 s（约 106 MB/s）
umount <挂载点>：普通用户即可卸载
```

rclone v1.76 的 `cmd/nfsmount` 已通过 `mountlib.AddRc` 注册为 rc 挂载类型，NasDrive 引擎可直接以 `mount/mount mountType=nfsmount` 调用。

## 9. 实施结果（2026-11-20，版本 0.5.0）

P0–P5 按本方案完成，架构与 5.x 一致，未引入 macFUSE/FUSE-T。

| 阶段 | 结果 |
|---|---|
| P1 引擎 | `mount_darwin.go`（nfsmount）、`backend/macguard`、`nasdrive/health` 增加 `mountBackend/mountOk/mountVersion`；Windows 与 macOS 均可编译，`go test ./...` 通过 |
| P2 共享核心 | `gui/NasDrive.Core`（模型、服务、`DriveManager` 等以 `git mv` 迁入）+ `IPlatform`/`ISecretStore`；WPF 版改为 `WindowsPlatform` + `WindowsCredentialStore` |
| P3 macOS 界面 | `gui/NasDrive.Mac`（Avalonia 11.3）：主窗口/编辑/设置三页、菜单栏图标与菜单、对话框（两步验证码、主机指纹、凭据、文件选择）、钥匙串、LaunchAgent、单实例 |
| P4 打包 | `scripts/build-mac.sh`：引擎 + 自包含 .NET 单文件 → `NasDrive.app`（`LSUIElement`、icns）→ 临时签名 → dmg（约 50 MB）。正式分发需 Developer ID 签名与公证（`SIGN_ID=…`，再 `xcrun notarytool submit`） |
| P5 测试 | 见下 |

### 9.1 测试

**macOS（本机，`scripts/test-mac.sh`，53/53 通过）**：Avalonia 无界面模式渲染全部页面（列表、五种协议的编辑页、设置）并人工检查截图；经真实的 `MainViewModel` → `DriveManager` → `MacPlatform` → 引擎挂载：

| 协议 | 读写 3 MB 回读 | 上传到服务器 | `._*`/`.DS_Store` 不落盘 | NFD→NFC | 改名 | 递归删除同步 | 断开后挂载点清除 |
|---|---|---|---|---|---|---|---|
| WebDAV（中文卷名"资料盘"） | ✓ | ✓ 5.5 s | ✓ | ✓ | ✓ | ✓ | ✓ |
| WebDAV 只读 | 写入、删除均被系统拒绝（EROFS） | | | | | | ✓ |
| SFTP（首次连接确认并固定主机指纹） | ✓ | ✓ 5.0 s | ✓ | ✓ | ✓ | ✓ | ✓ |
| FTP | ✓ | ✓ 5.0 s | ✓ | ✓ | ✓ | ✓ | ✓ |
| NFS（真实群晖 NAS） | ✓ | ✓ 9.8 s | ✓ | ✓ | ✓ | ✓ | ✓ |

另：钥匙串读写删除；引擎被强制结束后，新引擎清理失效挂载并自动重连、目录可读；LaunchAgent 创建与删除。

**Windows 回归（Win11 工作站，安装 0.5.0 安装包）**：`test-mount.ps1` 对 WebDAV（含只读全套 9 项）、SFTP（私钥 + 口令）、FTP、NFS（真实 NAS，含只读）全部通过；GUI 自动连接 4 个测试驱动器（WebDAV、SFTP 固定指纹、FTP、NFS 只读），资源管理器显示"名称 (X:)"（含中文名），读写与只读拒绝正常，盘符被占用时正确提示。用户原有的 3 个驱动器在升级后自动重连。

### 9.2 实施中发现并解决的问题

| 问题 | 原因 | 解决 |
|---|---|---|
| 改名后文件夹无法删除（I/O 错误） | macOS NFS 客户端改名时连同 `._旧名` 一起改名；`macguard` 内嵌 `fs.Fs`，`Features()` 返回的是内层后端的 Move/Copy，绕过了过滤，VFS 试图复制并不存在的 `._` 文件，留下删不掉的幽灵条目 | `macguard` 改为标准包装器写法：自有 `Features()`（Fill/Mask/WrapsFs），自行实现并过滤 Move/Copy/DirMove/PutStream/Purge/About/ChangeNotify；被丢弃对象可改名、可复制，哈希返回"未知" |
| SFTP/FTP 上 `.DS_Store` 仍写入服务器 | 这两个后端先上传到 `名称.<8位十六进制>.partial` 再改名，临时名不在拦截列表中 | 拦截时去掉 partial 后缀再判断；把真实文件改名为元数据名时从服务器删除 |
| .NET/部分程序递归删除失败（EIO） | .NET 先对非空目录直接 `rmdir` 并期望 ENOTEMPTY；go-nfs v0.0.4 把该错误映射成 `NFS3ERR_IO` | 升级 go-nfs 到上游已修复的版本（rmdir 前检查目录内容，返回 `NFS3ERR_NOTEMPTY`） |
| 屏幕锁定时启动即崩溃 | Avalonia 的渲染计时器基于 CVDisplayLink，锁屏时没有有效显示器（-6661） | 启动时若会话处于锁屏状态则等待解锁后再初始化界面 |

### 9.3 尚未覆盖 / 后续

- 本次测试期间本机处于锁屏状态，**真实窗口下的交互（菜单栏图标、弹窗、钥匙串授权提示）未能实机操作**，界面以无界面渲染 + 视图模型驱动的方式验证；建议在解锁的 Mac 上安装 dmg 走一遍添加/连接/断开流程。
- 睡眠/唤醒、切换网络后的行为（P0 已列为人工测试项）。
- Apple Developer ID 签名与公证；Intel 版 dmg 已可构建（`ARCH=amd64`），未在 Intel 机器上实测。
- Finder 标签等扩展属性只在挂载期间有效（设计取舍，同 macFUSE `noappledouble`）。
