# 更新日志 / Changelog

## 0.5.0

- **新增 macOS 版**（Apple 芯片 / Intel，macOS 12+）：菜单栏应用（Avalonia UI），与 Windows 版共用同一套引擎和核心逻辑，支持全部五种连接方式。
  - **无需安装 macFUSE、无需管理员权限、无内核扩展**：引擎内置 NFS 服务器，由系统自带的 NFS 客户端挂载（rclone `nfsmount`）；驱动器默认挂载在 `~/NasDrive/<名称>`，出现在 Finder 边栏"位置"中；每个驱动器可在添加/编辑界面单独指定挂载位置（任意空文件夹或新文件夹）；未单独指定的驱动器使用"设置 → 挂载位置"中的统一位置（更改时自动断开并在新位置重新连接已连接的驱动器）。
  - 自动拦截 macOS 元数据文件（`._*`、`.DS_Store` 等），不会污染 NAS；文件名统一为 NFC，与 Windows/NAS 端一致；只读驱动器由系统层拒绝写入。
  - 密码保存在**钥匙串**；"登录时启动"使用 LaunchAgent；引擎意外退出后自动清理失效挂载并重连。
  - 安装包：`NasDrive-0.5.0-osx-arm64.dmg` / `osx-x64.dmg`（`scripts/build-mac.sh` 构建）。
- 代码结构：界面无关的逻辑（配置、引擎守护、驱动器状态机、多语言等）抽取为共享库 **NasDrive.Core**，Windows 版（WPF）与 macOS 版（Avalonia）各自只实现平台层（`IPlatform`）。
- 检查更新：只认带有本平台安装包的正式版本（macOS 不会被提示只有 Windows 安装包的版本，反之亦然），"已是最新"提示中显示当前版本号；版本号取自应用本身而不是共享库。
- 引擎：`nasdrive/health` 增加 `mountBackend` / `mountOk` / `mountVersion`；Windows 版行为不变。
- 测试：新增 macOS 无界面端到端测试 `scripts/test-mac.sh`（渲染全部页面，并经真实的视图模型、引擎与挂载完成 WebDAV/只读/SFTP/FTP/NFS 读写改删、引擎崩溃恢复、挂载位置切换、单个驱动器挂载位置、检查更新等 68 项检查）。

## 0.4.0

- 新增服务类型 **SFTP**：密码或私钥登录（OpenSSH/PEM，支持口令）；首次连接确认**服务器指纹**并固定主机密钥，密钥改变时拒绝连接并警告。
- 新增服务类型 **FTP / FTPS**：显式 FTPS（推荐）、隐式 FTPS（990）、不加密 FTP；支持匿名登录与自签名证书。
- WebDAV 新增 **Nextcloud / ownCloud 预设**：自动使用 `/remote.php/dav/files/<用户名>` 与 Nextcloud 方言。
- 引擎：连接测试与列目录增加硬超时，避免个别服务器无响应时界面一直等待；新增 SFTP/FTP 错误分类。

## 0.3.1

- 修复：勾选"只读"的驱动器仍可在资源管理器中"删除"文件（实际未删除）。现在 Windows 会直接拒绝写入与删除。WebDAV、NFS、群晖均已验证。
- 代码整理：统一格式（`.editorconfig`、gofmt、dotnet format），补充注释，移除未使用的代码；引擎不再包含调试用的 `rcd` 命令。
- 新增系统介绍与安装使用说明文档；"检查更新"指向本仓库的 Releases。

## 0.3.0

- 新增服务类型 **群晖 Synology**（DSM File Station API）：无需在 NAS 上开启 WebDAV/NFS；列出共享；真实容量；支持**两步验证**（受信任设备令牌）。
- 服务类型精简为 WebDAV / NFS / 群晖 三种。
- 全新托盘与程序图标：按 DPI 选用像素对齐的图标，底色表示连接状态。

## 0.2.0

- 新增 **NFS v3** 支持（自研 NFS 客户端与 rclone 后端）：列出导出、特权端口、真实容量。
- 网络驱动器在资源管理器中显示为"名称 (X:)"并显示可用/总空间。

## 0.1.0

- 首个版本：WebDAV 驱动器映射、托盘、自动连接与重连、上传队列、代理、凭据管理器、Inno Setup 安装包（含 WinFsp）。
- 自动避免以管理员身份运行（否则资源管理器看不到盘符）。
