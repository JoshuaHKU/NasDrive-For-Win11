# NasDrive macOS 移植 P0 技术验证报告

> 日期：2026-11-20　环境：macOS 26.5，Apple 芯片，未安装 macFUSE / FUSE-T　引擎：NasDrive 0.4.0 + rclone v1.76.0 `nfsmount`
> 结论：**P0 通过，macOS 移植技术上可行。** 发现 3 个必须处理的问题，均已找到并验证了解决方案。

## 1. 验证范围与结果

| # | 验证项 | 结果 |
|---|---|---|
| 1 | 引擎在 macOS 上通过 rc `mount/mount mountType=nfsmount` 挂载，**无管理员权限、无内核扩展** | ✅ 通过 |
| 2 | WebDAV：列目录、新建、8 MB 拷贝与回读校验、改名、建目录、上传到服务器、删除并同步到服务器 | ✅ 9/9 |
| 3 | SFTP（含固定主机密钥）：同上 | ✅ 9/9 |
| 4 | FTP：同上 | ✅ 9/9 |
| 5 | NFS（**真实 NAS**，NasDrive 自研 NFS 客户端再经 nfsmount 二次 NFS）：读写改删；1 MB 文件 4 秒内上传到 NAS 并校验大小；经挂载删除后 NAS 上同步消失 | ✅ 通过 |
| 6 | 群晖 Synology | ⏸ 未测（需两步验证码）；挂载层与协议无关，WebDAV/SFTP/FTP/NFS 已覆盖 |
| 7 | 中文卷名（挂载目录 `资料盘`）、中文文件内容与文件名 | ✅ 通过 |
| 8 | Finder 可见性 | ✅ 系统将其识别为可浏览的网络卷（`volumeIsBrowsable=true`），卷名即挂载目录名 |
| 9 | 真实容量 | ✅ NAS 经挂载显示 6.7 TiB / 已用 5.1 TiB / 可用 1.6 TiB |
| 10 | 吞吐（本机 WebDAV 服务器） | ✅ 写入挂载 70 MB/s；256 MB 7.7 s 上传完成且校验一致；回读 190 MB/s |
| 11 | **macOS 元数据污染（`._*`、`.DS_Store`）** | ⚠️ 发现问题 → 已用 macguard 解决（见 2.1） |
| 12 | **Unicode 规范化（NFD/NFC）** | ⚠️ 发现问题 → 已用挂载选项 `nfc` 解决（见 2.2） |
| 13 | **只读** | ⚠️ 追加写入被"静默吞掉" → 已用挂载选项 `ro` 解决（见 2.3） |
| 14 | **引擎崩溃后的挂载** | ✅ 使用软挂载选项后不会卡死；普通用户可强制卸载（见 2.4） |
| 15 | 睡眠/唤醒、切换网络 | ⏸ 需人工在真机上操作，列入 P5 测试 |

## 2. 发现的问题与解决方案

### 2.1 macOS 元数据文件污染 NAS（已解决）

**现象**：经挂载拷贝带扩展属性的文件时，NAS 上出现 `._文件名`（4 KB）；Finder 还会写 `.DS_Store`、`.Trashes` 等。原因是 NFSv3 不支持命名属性，macOS NFS 客户端改用 AppleDouble 旁路文件保存扩展属性。

**尝试 1（无效）**：rclone 过滤规则（rc `_filter` 排除 `._*`）——过滤只影响列表显示，写入照样上传。

**方案（已实现并验证）**：新增引擎后端 **`macguard`**（`engine/backend/macguard`，仅 macOS 编译）。它包装真正的后端：

- `._*`、`.DS_Store`、`.Spotlight-V100`、`.Trashes`、`.fseventsd`、`.TemporaryItems`、`.apdisk` 等：上传被接受但不写入服务器，列表中隐藏，建目录静默跳过；
- 其余文件原样透传。

效果与 macFUSE 的 `noappledouble` 一致：拷贝成功（`cp` 退出码 0、数据一致），**服务器上只出现真正的文件**；常规读写 9/9 通过。代价：Finder 标签等扩展属性不会保存到 NAS（与 macFUSE 相同）。

连接字符串：`:macguard,remote=':webdav,url=''…'',user=''…'',pass=''…'':':/路径`

### 2.2 Unicode 规范化（已解决）

**现象**：不加处理时，Mac 上创建的 NFD 文件名会以 NFD 形式存到 NAS，Windows 与 NAS 端（NFC）会显示成"看起来一样、实际不同"的文件名。

**方案**：挂载选项 **`nfc`**（macOS mount_nfs 原生支持）。实测：Mac 创建的 NFD 名称在服务器上存为 NFC；服务器上的 NFC 名称在 Mac 上用 NFC 或 NFD 名称都能打开。

### 2.3 只读：追加写入被静默吞掉（已解决）

**现象**：仅设置 VFS 只读时，新建、覆盖、改名、删除、建目录都会被拒绝，但 `>>` 追加写入**返回成功**（文件实际未变）——macOS NFS 客户端缓冲了写入，服务器的拒绝来得太晚（与 Windows 版"删除假成功"同类问题）。

**方案**：只读驱动器额外加挂载选项 **`ro`**，由内核在入口处拒绝。实测新建、覆盖、追加、改名、删除、建目录、修改时间全部立即返回 "Read-only file system"，文件可正常读取且不变。

### 2.4 引擎崩溃后的挂载（已验证）

rclone 默认以"硬挂载"方式挂载 NFS，服务端消失后访问会无限期阻塞（NFS 的设计行为），因此 NasDrive 必须改用软挂载。实测（`kill -9` 杀掉引擎）：

| 操作 | 结果 |
|---|---|
| 访问已缓存的目录 | 立即返回（来自缓存） |
| 读取从未读过的文件 | 7 秒后报错返回（超时） |
| 之后列未缓存目录、创建文件 | 立即报错（客户端已判定服务器不可用） |
| 普通用户 `diskutil umount force` | 成功，1–9 秒 |
| macOS 自动卸载（`deadtimeout`） | **不会**自动卸载（缓存命中时没有失败的请求）→ 必须由 GUI 守护进程主动卸载 |

**结论**：使用软挂载选项后不会让 Finder/应用卡死；GUI 守护进程在检测到引擎退出时，以及每次启动时，对本程序的残留挂载执行 `diskutil umount force`。

## 3. macOS 版确定的挂载参数

```
mountType   = nfsmount
fs          = :macguard,remote='<原连接字符串，单引号加倍>':<路径>
ExtraOptions = soft, intr, timeo=50, retrans=3, deadtimeout=45, nfc, locallocks [, ro 只读时]
vfsOpt       = 与 Windows 相同（CacheMode=full、WriteBack 等）[, ReadOnly=true 只读时]
挂载点       = ~/NasDrive/<驱动器名称>（中文名可用）
```

## 4. 已产生的代码（未提交，P1 继续完善）

| 文件 | 内容 |
|---|---|
| `engine/mount_darwin.go` | 仅 macOS：链接 rclone `nfsmount` 与 `macguard` |
| `engine/backend/macguard/macguard.go` | macOS 元数据过滤后端 |

Windows 版构建不受影响（已确认 Windows 引擎不包含 nfsmount）。

## 5. 对 P1 的输入

1. 引擎：`nasdrive/health` 在 macOS 上检查 `/sbin/mount_nfs`；新增启动时清理残留挂载的接口；macguard 补单元测试。
2. GUI（macOS）：按第 3 节参数构造挂载请求；守护进程负责崩溃后的强制卸载；挂载点目录的创建与清理。
3. P5 真机测试补充：睡眠/唤醒、切换 Wi-Fi、Finder 拖拽与"显示简介"、Office 文档直接编辑、Time Machine 不应把网络盘当备份盘。
