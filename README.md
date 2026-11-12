# NasDrive

把 NAS 上的 WebDAV 目录映射为 Windows 本地盘符的开源工具（对标 RaiDrive）。
基于 [rclone](https://github.com/rclone/rclone)（作为 Go 库引用，未修改源码）和 [WinFsp](https://winfsp.dev)。

> 状态：一期 MVP 开发中（v0.1.0）。设计见 `docs/NasDrive详细设计文档.md`。

## 组成

```
engine/        nasdrive-engine.exe（Go）：rclone WebDAV + cmount(WinFsp) + rc 控制接口
  main.go          入口，默认运行 engine 子命令
  engine_cmd.go    engine 子命令：启动 rc 服务，输出 NASDRIVE_READY 握手行
  rc_nasdrive.go   自定义接口 nasdrive/version|health|test|mounts|probe；禁用 core/command
  platform_*.go    WinFsp 检测、磁盘空间
gui/NasDrive/  NasDrive.exe（C# .NET 8 WPF）：托盘、主窗口、添加/编辑、设置
  Services/        引擎守护、rc 客户端、驱动器状态机、凭据管理器、注册表/外壳集成、多语言
  ViewModels/      MVVM
  Views/           XAML 界面与托盘
installer/     Inno Setup 安装脚本（含 WinFsp 静默安装）
scripts/       build.sh 构建脚本、test-mount.ps1 Windows 端到端测试、genicon 图标生成
```

## 构建

需要 Go 1.26+ 与 .NET 8 SDK（macOS/Linux 可交叉编译，WPF 程序只能在 Windows 运行）。

```bash
scripts/build.sh                # 输出 dist/win-x64/
ARCH=arm64 scripts/build.sh     # Windows on ARM
```

安装包（在 Windows 上，需 Inno Setup 6；先把 WinFsp 安装包放到 `installer/deps/winfsp.msi`）：

```bat
iscc /DArch=x64 /DVersion=0.1.0 installer\NasDrive.iss
```

## 测试

引擎端到端测试（Windows，已安装 WinFsp）。NAS 密码只保存在 Windows 凭据管理器中，脚本从中读取：

```bat
cmdkey /generic:NasDrive:test-nas /user:NAS用户名 /pass
powershell -ExecutionPolicy Bypass -File test-mount.ps1 -Url https://nas:5006 -RemotePath /folder -Drive T: -SkipTlsVerify
```

## 关键设计

- **无 rclone.conf**：挂载时用即时连接字符串 `:webdav,url=...,pass=<obscured>:/path`；密码存于 Windows 凭据管理器（`NasDrive:drive:<id>`）。
- **每驱动器覆盖项**：超时、忽略证书、代理通过 rclone `override.*` 传入，互不影响。
- **双进程**：GUI 关闭或崩溃不影响已挂载盘符；GUI 重启后通过 `engine.ready.json` + 凭据重新接管引擎；引擎崩溃时自动重启并重连。
- **不提权运行**（`asInvoker`），否则资源管理器看不到盘符。
- 引擎启动时清除 `HTTP(S)_PROXY` 环境变量，避免局域网 NAS 流量被系统代理劫持。

## 数据位置

| 内容 | 路径 |
|---|---|
| 驱动器配置 / 设置 | `%APPDATA%\NasDrive\profiles.json`、`settings.json` |
| 缓存 | `%LOCALAPPDATA%\NasDrive\Cache`（可在设置中修改） |
| 日志 | `%LOCALAPPDATA%\NasDrive\Logs\gui-*.log`、`engine.log` |
| 密码 | Windows 凭据管理器，目标名 `NasDrive:*` |

## 许可证

MIT。第三方组件见 `THIRD-PARTY-NOTICES.md`。
