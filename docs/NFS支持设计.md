# NasDrive NFS 支持详细设计

> 版本：v0.2 设计稿　日期：2026-11-12　适用：NasDrive 0.2.0

## 1. 结论摘要

| 问题 | 结论 |
|---|---|
| rclone 是否支持 NFS？ | **只支持做 NFS 服务端，不支持做 NFS 客户端。** `backend/` 下 70 余个存储后端里没有 `nfs`。 |
| rclone 里与 NFS 有关的代码 | `cmd/serve/nfs`：把任意 rclone 远端以 NFSv3 服务出去（基于 `willscott/go-nfs`）。`cmd/nfsmount`：仅限 Unix，先在本机起一个 `serve nfs`，再调用系统 `mount -t nfs` 挂上，方向与我们的需求相反；Windows 下是 `nfsmount_unsupported.go`。 |
| NasDrive 能否支持 NFS？ | 能。方案：**在 NasDrive 引擎里自己实现一个 rclone `nfs` 存储后端**，挂载、缓存、状态监控、界面全部复用现有 WebDAV 那一套。 |
| 依赖 | rclone 已间接依赖纯 Go 的 NFSv3 客户端库 `willscott/go-nfs-client`（BSD-2）。评估后发现它的 RPC 层不够健壮、且只有约 700 行，故**以它和 RFC 1813 为参考，在 `engine/nfsclient` 自行实现客户端**（手写 XDR，约 900 行），不引入该库。 |

## 2. rclone 源码分析

### 2.1 NFS 相关代码清单

| 路径 | 作用 | 可用于 NasDrive？ |
|---|---|---|
| `cmd/serve/nfs/*` | NFSv3 服务端：把 VFS 暴露为 NFS 共享，单端口同时提供 MOUNT 和 NFS 协议，无 portmapper | 否（方向相反）；**可用作测试用的 NFS 服务器** |
| `cmd/nfsmount/nfsmount.go` | `//go:build unix`，起本地 NFS 服务后调用系统 mount | 否 |
| `vfs/vfscommon/vfsflags_non_unix.go` 等 | 仅涉及 NFS 句柄缓存等参数 | 否 |
| `go.mod`：`willscott/go-nfs v0.0.4` | 服务端库 | 测试用 |
| `go.mod`：`willscott/go-nfs-client`（indirect） | **NFSv3 客户端库**（MOUNT/NFS3 协议、XDR 编解码、portmapper） | **是，作为基础** |

### 2.2 go-nfs-client 评估

已实现：portmapper 查询、MOUNT3（MNT/UMNT）、NFS3 的 LOOKUP、GETATTR、SETATTR、ACCESS、READDIRPLUS、CREATE、READ、WRITE、COMMIT、MKDIR、REMOVE、RMDIR、RENAME、SYMLINK、READLINK、FSINFO；AUTH_UNIX（AUTH_SYS）认证。

问题（直接使用会影响稳定性）：

| 问题 | 影响 |
|---|---|
| `rpc.Client.Call` 超时后最多重试 100 次，每次 5 秒 | NAS 断网时一个操作卡住 8 分钟以上，资源管理器假死 |
| 断线时关闭全部应答通道，但接收协程仍可能向已关闭通道发送 | 可能 **panic**，整个引擎崩溃、所有盘符掉线 |
| 解析异常处直接 `panic` | 同上 |
| 仅当 `uid == "0"` 时才用特权源端口（<1024）；Windows 上 uid 是 SID，永远不满足 | 群晖默认只接受特权端口来的连接，**直接被拒** |
| 超时重试不区分幂等性，CREATE/REMOVE/RENAME 也会盲目重发 | 偶发"文件已存在/不存在"错误 |
| 没有 FSSTAT | 无法显示容量 |
| `Target` 自带目录项缓存和永不退出的清理协程 | 与 rclone VFS 缓存重复；每次连接测试泄漏一个协程 |

### 2.3 NAS 现状（实测）

```
showmount -e 192.168.1.10
/volume1/share      192.168.1.0/24
/volume1/home   192.168.1.0/24
rpcinfo：portmapper 111，mountd 892（v1/2/3），nfs 2049（v2/3/4，TCP/UDP）
```

## 3. 方案比较

| | A. 调用 Windows 自带"NFS 客户端" | **B. 引擎内实现 rclone nfs 后端（采用）** | C. 等 rclone 上游 |
|---|---|---|---|
| 系统要求 | 仅专业版/企业版，需管理员启用可选功能 | 任何 Win10/11，无需管理员 | — |
| 中文文件名 | Windows NFS 客户端**不支持 UTF-8**，群晖上的中文名会乱码 | 原样 UTF-8，正常 | — |
| 缓存/断线处理/状态 | 无，与 NasDrive 其他驱动器体验不一致 | 复用 VFS 全量缓存、写回、在线探测、上传队列 | — |
| 用户身份 | 注册表全局 AnonymousUid，所有共享共用 | 每个驱动器单独设置 uid/gid | — |
| 性能 | 内核客户端，最快 | 用户态，受 VFS 缓存加持，局域网足够 | — |
| 工作量 | 小 | 中（约 2000 行 Go + 界面） | 不确定 |

**选 B。**中文文件名乱码和家庭版不可用这两点对目标用户是硬伤。

## 4. 总体架构

```
NasDrive.exe (WPF)                    nasdrive-engine.exe (Go)
 ├ 驱动器编辑：协议 = WebDAV | NFS      ├ backend/webdav   （rclone 自带）
 └ ConnectionString.Build              ├ backend/nfs      （新增，本仓库 engine/nfs）
      │                                │    └ engine/nfsclient（自研 NFSv3/MOUNT/portmapper 客户端）
      │  mount/mount fs=":nfs,..."      ├ cmount + VFS 全量缓存（不变）
      └──────────── rc ───────────────▶ └ nasdrive/test|probe|mounts（不变）+ nasdrive/nfsexports（新增）
```

挂载、缓存、托盘状态、自动重连、上传队列等逻辑与协议无关，**全部不变**。

## 5. 引擎：rclone `nfs` 后端

### 5.1 连接字符串与选项

```
:nfs,host='192.168.1.10',export='/volume1/share',uid=1026,gid=100:/子目录
```

| 选项 | 默认 | 说明 |
|---|---|---|
| `host` | 必填 | NAS 地址 |
| `export` | 必填 | 导出路径，如 `/volume1/share`（`showmount -e` 所列） |
| `port` | 0 | NFS 端口；0 = 通过 portmapper(111) 查询 |
| `mount_port` | 0 | MOUNT 端口；0 = portmapper 查询。两者都填且相同 = 单端口服务器（如 rclone serve nfs） |
| `uid` / `gid` | 0 / 0 | AUTH_SYS 身份。群晖建议在 NFS 规则中设"映射所有用户为 admin"，此时可保持默认 |
| `privileged_port` | true | 使用 <1024 的源端口（Windows 无需管理员即可绑定）。群晖默认要求，否则需勾选"允许非特权端口连接" |
| `rpc_timeout` | 15s | 单次 RPC 超时；也受 rclone `--timeout` 覆盖项约束 |

### 5.2 rclone 接口映射

| rclone 接口 | NFS 操作 | 说明 |
|---|---|---|
| `NewFs` | portmapper → MNT → FSINFO | 若根路径是文件，按 rclone 惯例返回 `fs.ErrorIsFile` |
| `List(dir)` | LOOKUP（逐级，带句柄缓存）→ READDIRPLUS（分页 cookie） | 跳过 `.`、`..` 和临时文件；符号链接、设备文件等非常规文件不显示 |
| `NewObject` | LOOKUP | 是目录则 `fs.ErrorIsDir`，不存在则 `fs.ErrorObjectNotFound` |
| `Put` / `Update` | CREATE 临时文件 `.名.nasdrive-<rand>` → WRITE(UNSTABLE，按 wtpref 分块) → COMMIT → SETATTR(mtime) → RENAME 覆盖目标 | 失败时删除临时文件；RENAME 原子替换，读者不会看到半截文件 |
| `Object.Open` | READ（按 rtpref 分块，支持 Range/Seek 选项） | |
| `Mkdir` | 逐级 MKDIR（EXIST 视为成功） | |
| `Rmdir` | RMDIR | NOTEMPTY → `fs.ErrorDirectoryNotEmpty` |
| `Remove` | REMOVE | |
| `Move` / `DirMove` | RENAME | 服务端重命名，无需复制 |
| `About` | FSSTAT | 总量/可用 → 资源管理器容量、主界面容量条 |
| `SetModTime` | SETATTR mtime | 精度 1 µs |
| `Hashes` | 无 | VFS 以 大小+修改时间 判断变化 |

### 5.3 RPC 层要点（`engine/nfsclient/rpc.go`）

- 每次调用带截止时间（`context`），超时即返回 `context.DeadlineExceeded`，不再无限重试。
- 只有**幂等**操作（GETATTR、LOOKUP、READ、READDIRPLUS、FSSTAT、FSINFO、ACCESS、COMMIT、WRITE）在连接断开时自动重连并重试 1 次；CREATE/REMOVE/RENAME/MKDIR/RMDIR 不自动重发，交给 VFS 的重试机制。
- 应答通道带缓冲，断线时以错误唤醒所有等待者；不再有 panic 路径。
- 特权源端口：从 1023 向下尝试可用端口（`EADDRINUSE`/`WSAEADDRINUSE` 时换下一个）。
- 连接断开后下次调用自动重连；开启 TCP keepalive。
- **不使用 rclone 默认的 pacer 节流**：默认 pacer 每次调用至少间隔 10 ms，会把局域网 NFS 限制在约 100 次 RPC/秒（128 KB 块 ≈ 12 MB/s）。改为成功不等待、仅在重试时指数退避（50 ms～2 s）。

### 5.4 句柄缓存

后端维护 `路径 → 文件句柄` 的 LRU 缓存（READDIRPLUS 返回的句柄直接入缓存），避免每次操作从根逐级 LOOKUP。收到 `NFS3ERR_STALE` 时清除该路径及其子路径缓存并重试一次。

### 5.5 错误分类（`nasdrive/test` 新增 NFS 分支）

| 情况 | errorCode | 界面提示 |
|---|---|---|
| 111 端口拒绝/不通 | `refused` / `timeout` | NAS 未开启 NFS 服务，或防火墙拦截 |
| `MNT3ERR_ACCES` / `MNT3ERR_PERM` | `nfs_denied` | NAS 未允许本机 IP 访问该共享：检查 NFS 权限规则（以及"允许非特权端口"） |
| `MNT3ERR_NOENT` | `not_found` | 导出路径不存在 |
| `NFS3ERR_ACCES` / `NFS3ERR_PERM` | `forbidden` | 无权限：检查 uid/gid 或 Squash 设置 |
| `NFS3ERR_NOENT` | `not_found` | 子目录不存在 |
| `NFS3ERR_NOTDIR` | `is_file` | 路径是文件 |

### 5.6 新增 rc 接口 `nasdrive/nfsexports`

输入 `{host, timeout}`，经 portmapper + MOUNT EXPORT 返回 `{exports:[{path, groups:[...]}]}`，供界面"浏览共享"下拉框使用。

## 6. 界面（WPF）

- 编辑页顶部新增 **协议** 单选：WebDAV / NFS。
- 选 NFS 时：显示 主机、**共享（导出路径，带"刷新"按钮自动列出）**、子路径；隐藏 HTTPS、端口、账号密码、代理、忽略证书。高级里增加 uid、gid、"使用特权端口"。
- 粘贴 `nfs://host/volume1/share/sub` 自动解析。
- 列表行显示 `nfs://host/export/子路径`，账号列显示 `uid 1026` 或"映射为管理员"。
- 卷名：`\\host\共享名`（网络驱动器模式）。
- 设置页的代理对 NFS 不生效（NFS 不支持 HTTP 代理），编辑页给出说明。
- 安全提示：NFSv3 不加密、按 IP 授权，只建议在局域网使用。

`DriveProfile` 新增字段：`Protocol`（"webdav" | "nfs"，缺省 webdav，兼容旧配置）、`NfsExport`、`NfsUid`、`NfsGid`、`NfsPrivilegedPort`。

## 7. 群晖设置说明（写入 README）

控制面板 → 文件服务 → NFS：启用 NFS 服务（NFSv3）。
共享文件夹 → 编辑 → NFS 权限 → 新增：
- 主机名或 IP：本机 IP 或网段，如 `192.168.1.0/24`
- 权限：可读写
- Squash：**映射所有用户为 admin**（最省事；否则需在 NasDrive 里填与群晖用户一致的 uid/gid）
- 安全性：sys
- "允许来自非特权端口的连接"：NasDrive 默认用特权端口，可不勾

## 8. 测试计划

| 层级 | 内容 | 环境 |
|---|---|---|
| 单元 | RPC 超时/断线/重连、无 panic；XDR 编解码 | macOS |
| 集成 | **rclone 官方 `fstest/fstests` 后端一致性测试套件**，对象是进程内启动的 `rclone serve nfs`（单端口，无 portmapper） | macOS |
| 真机 | 对群晖 `/volume1/share`：列目录、中文文件名、大文件读写、改名、删除、容量 | macOS → NAS |
| 端到端 | `test-mount.ps1 -Protocol nfs` 挂载为盘符，读写改删、上传队列、在线探测 | Win11（192.168.1.20） |
| 界面 | 新建 NFS 驱动器、列出共享、连接、断开 | Win11 桌面 |

### 8.1 测试结果（2026-11-12）

| 测试 | 结果 |
|---|---|
| rclone `fstests` 一致性测试（对 `rclone serve nfs`） | 91 项通过，0 失败（跳过项为哈希、元数据等可选能力） |
| Mac → 群晖 `/volume1/share` | 中文目录/文件名、改名、修改时间、64 MiB 读写校验、范围读取均通过；容量来自 FSSTAT（6857 GiB）。该链路 RTT 42 ms、带宽约 1 MB/s，WebDAV 同链路同样 1.0 MB/s |
| 错误分类 | 本机 IP 未授权 → `nfs_denied`；子目录不存在 → `not_found`；NFS 服务未开 → `refused` |
| Win11（192.168.1.20）`test-mount.ps1 -Protocol nfs` | 全部通过：挂载 T:、列目录、Windows 显示真实容量 6857 GB、中文文件读写改删、上传队列、在线探测、卸载 |
| 64 MB 写入盘符直到上传完成（含 5 s 写回延迟） | NFS 5.6 s，WebDAV 6.1 s |
| 图形界面 | 0.2.0 安装包升级后，NFS 驱动器自动连接为 N:，桌面会话可见 |

## 9. 部署

版本号升为 0.2.0；重新生成安装包；先在 192.168.1.20 验证，再由用户决定是否升级其他电脑。

## 10. 风险与限制

- 仅 NFSv3、仅 AUTH_SYS；不支持 NFSv4/Kerberos。
- NFS 没有密码，安全性取决于 NAS 的 IP 规则；跨网段时 NAS 看到的是路由后的地址。
- 用户态客户端吞吐低于内核客户端；大文件依赖 VFS 缓存与写回。
- 硬链接、ACL、扩展属性不支持；符号链接只读呈现。
