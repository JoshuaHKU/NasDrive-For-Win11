# NasDrive 0.4.0：SFTP、FTP/FTPS 与 Nextcloud 预设 设计方案

## 1. 范围

| 功能 | 说明 |
|---|---|
| **SFTP** | 新服务类型。密码或**私钥**登录；首次连接**确认服务器指纹**，之后固定（pin）该主机密钥，密钥变化时拒绝连接并警告 |
| **FTP / FTPS** | 新服务类型。加密方式三选一：显式 FTPS（推荐，默认）/ 隐式 FTPS（端口 990）/ 不加密 FTP（警告）；支持匿名登录 |
| **Nextcloud 预设** | WebDAV 类型下的开关：自动使用 Nextcloud/ownCloud 的 WebDAV 地址（`/remote.php/dav/files/<用户名>`）与 rclone 的 nextcloud 方言（保留修改时间、校验和、分块上传） |

服务类型共五种：WebDAV ｜ NFS ｜ 群晖 Synology ｜ SFTP ｜ FTP。

## 2. 实现方式

三者都直接使用 rclone v1.76.0 自带的成熟后端（`backend/sftp`、`backend/ftp`、`backend/webdav` 的 nextcloud vendor），NasDrive 负责：引擎链接后端、界面字段、连接字符串、凭据存储、主机密钥确认、错误分类与测试。

### 2.1 SFTP 连接字符串

```
:sftp,host='nas',port='22',user='bob',pass='<obscured>',host_keys='ssh-ed25519 AAAA…',
      disable_hashcheck='true',override.timeout='100s':/home/bob/data
:sftp,host='nas',user='bob',key_file='C:\Users\bob\.ssh\id_ed25519',key_file_pass='<obscured>',host_keys='…':/data
```

- **私钥**：保存私钥文件路径（profile 中），私钥口令存 Windows 凭据管理器。不把私钥内容存入凭据管理器（单条凭据上限 2.5 KB，RSA-4096 私钥会超限），也不复制私钥文件。支持 OpenSSH 与 PEM 格式（rclone 支持的格式）。
- **主机密钥固定**：profile 新增 `SshHostKey`（"算法 base64"，公钥，非机密）。连接字符串总是带 `host_keys`；若为空则先走确认流程，绝不在未确认时连接（rclone 在没有 known_hosts 与 host_keys 时会忽略主机校验，必须避免）。
- `disable_hashcheck=true`：不在服务器上执行 md5sum/sha1sum 命令（很多 NAS 的 SFTP 账户没有 shell）；VFS 以大小+修改时间判断变化。

### 2.2 主机指纹确认流程

新增引擎接口 `nasdrive/sshhostkey {host, port, timeout}`：只做 SSH 握手、取得服务器出示的主机公钥后立即断开（不认证），返回：

```json
{"ok": true, "keyType": "ssh-ed25519", "key": "ssh-ed25519 AAAAC3…", "fingerprint": "SHA256:…"}
```

界面在"测试连接""列出目录""连接"前调用：

| 情况 | 行为 |
|---|---|
| profile 中没有固定密钥 | 弹框显示主机、密钥类型、SHA256 指纹，用户确认后写入 profile |
| 与固定密钥一致 | 直接继续 |
| 不一致 | 红色警告"服务器密钥已改变，可能遭到中间人攻击"，默认取消；用户明确选择"信任新密钥"才替换 |

引擎侧 rclone 再次用 `host_keys` 校验，双重保证。

### 2.3 FTP 连接字符串

```
:ftp,host='nas',port='21',user='bob',pass='<obscured>',explicit_tls='true',override.no_check_certificate='true':/share
:ftp,host='nas',port='990',user='bob',pass='<obscured>',tls='true':/share
:ftp,host='nas',user='anonymous',pass='<obscured anonymous>':/pub
```

匿名：用户名 `anonymous`、密码任意。不加密 FTP 会以明文传输密码与文件，界面显示警告。

### 2.4 Nextcloud 预设

WebDAV 类型新增"Nextcloud / ownCloud"复选框（profile 的 `Vendor` = `nextcloud`）：

- 连接 URL 自动拼为 `https://host:port/remote.php/dav/files/<用户名>`，路径框只填该用户网盘内的文件夹；
- rclone `vendor=nextcloud`：使用 Nextcloud 的分块上传、保留修改时间、读取 quota。
- 建议使用 Nextcloud 的"应用密码"（设置 → 安全 → 设备与会话），尤其在启用两步验证时。

## 3. 错误分类（新增）

| 情况 | errorCode | 提示 |
|---|---|---|
| SSH 认证失败 | `unauthorized` | 用户名、密码或私钥不正确 |
| 私钥文件不存在 / 无法解析 / 口令错误 | `ssh_key` | 私钥文件无法读取或口令错误 |
| 主机密钥与固定值不符 | `ssh_hostkey_changed` | 服务器密钥已改变（可能是中间人攻击） |
| FTP 530 | `unauthorized` | 用户名或密码错误 |
| FTP 550 / 路径不存在 | `not_found` | |
| FTPS 证书不受信任 | `tls_cert` | 勾选"忽略证书错误"（自签名证书） |
| 服务器不支持 AUTH TLS | `ftp_no_tls` | 服务器不支持显式 FTPS，改用隐式或确认服务器设置 |

## 4. 测试

- **单元测试**：`nasdrive/sshhostkey`（进程内 SSH 服务器）、连接字符串生成、错误分类。
- **本机测试服务器**（不使用 macOS 自带 sshd，避免授予客户端对本机的 shell 权限）：`rclone serve sftp`（密码 + 公钥认证，限定测试目录）、`rclone serve ftp`（明文与 FTPS，自签名证书），使用一次性测试凭据。
- **端到端**：Win11 工作站作为客户端，`test-mount.ps1 -Protocol sftp|ftp`（含只读测试），覆盖密码登录、私钥登录、指纹确认与密钥变化拒绝、显式/隐式 FTPS。
- 回归：WebDAV、NFS、群晖。
