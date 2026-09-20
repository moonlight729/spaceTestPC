# PCBA 板端部署加固方案（防止 0 字节二进制导致设备永久失联）

> **文档位置说明**：原 `PCBA_UPGRADE_DESIGN.md` 已更名为 [`application-upgrade.md`](application-upgrade.md)。
> 上位机侧修复：`SpaceTestPC.App/Services/AdbPcbaCommandClient.cs`（已完成 P0-1～P0-3 + P1-4/5，见下文"与上位机升级流程的配合"）。
> 板端脚本部分（自愈 wrapper、known-good 副本、OTA 互斥）为本文的落地内容，部署时按第 3 章清单核对。

## 1. 背景与问题

现场板 `192.168.137.224` 出现的事故形态：

- `/vendor/originflow/bin/spacetest3576` 为 **0 字节**（MD5 `d41d8cd98f00b204e9800998ecf8427e`）
- 回滚副本 `spacetest3576.bak` 同样为 **0 字节** —— 唯一的"备份"与主文件同归于尽
- `pcba-test.service` 因 `Restart=always` + 空文件必失败，陷入 **203/EXEC 无限重启**
- 板上不存在任何一份完好二进制副本（`/userdata` 无备份，OTA staging 为空）
- 19001 端口无监听，上位机无法通过协议做任何恢复操作

事故根因（上位机侧已修复）：跨文件系统 `mv`（`/tmp` → `/vendor`）退化为"先截断后拷贝"，中断即产生 0 字节目标文件；备份 `cp` 同样先截断；回滚不校验备份有效性。

**上位机修复只能保证"从今往后的升级"不再把板子搞坏，但无法防止：**

1. 已部署在现场的旧版本上位机继续用旧流程升级
2. 升级中板子物理断电
3. 板内 OTA 框架（`/vendor/originflow/ota/`）自身的缺陷
4. 磁盘满、文件系统异常等导致的写入失败

因此**板端部署文件必须具备自愈能力**：即使二进制再次损坏，服务也要能自动恢复到 known-good 副本，而不是无限 203/EXEC 循环等死。

## 2. 目标

1. `spacetest3576` 损坏（0 字节 / 非 ELF）时，`pcba-test.service` **不进入无限重启**，自动从 known-good 副本恢复
2. known-good 副本存放在**独立文件系统**（`/userdata`），与主文件、`.bak` 物理隔离，三者不可能同时损坏
3. 升级成功后 known-good 副本自动更新为新版本
4. 板内 OTA 脚本与上位机升级互斥，防止双写
5. 兼容现有上位机升级流程（服务名、二进制路径、协议端口 19001 均不变）

## 3. 部署文件修改清单

| # | 文件 | 操作 | 作用 |
|---|---|---|---|
| 1 | `/etc/systemd/system/pcba-test.service` | **修改** | 启动前校验二进制，限制重启风暴 |
| 2 | `/usr/local/bin/spacetest3576-verify` | **新增** | 二进制有效性检查脚本（ExecStartPre 调用） |
| 3 | `/etc/systemd/system/pcba-restore.service` | **新增** | 自愈服务：从 known-good 恢复 |
| 4 | `/usr/local/bin/spacetest3576-restore` | **新增** | 恢复脚本（带副本校验） |
| 5 | `/usr/local/bin/spacetest3576-save-lastgood` | **新增** | 升级成功后更新 known-good 副本 |
| 6 | `/userdata/originflow/backup/` | **新增目录** | known-good 副本存放地（独立于 /vendor） |
| 7 | `/vendor/originflow/ota/scripts/*` | **审计修改** | 板内 OTA 脚本加原子替换 + 校验 + flock |

## 4. 各文件内容

### 4.1 `/etc/systemd/system/pcba-test.service`（修改）

现状（`.224` 上实测）：

```ini
[Service]
ExecStart=/vendor/originflow/bin/spacetest3576
Restart=always
RestartSec=2
```

修改后：

```ini
[Unit]
Description=PCBA test application
# 自愈链条：本单元 failed 时触发恢复服务
OnFailure=pcba-restore.service

[Service]
Type=simple
# 启动前校验：文件非空 + ELF 魔数。失败则本单元 failed → 触发 pcba-restore
ExecStartPre=/usr/local/bin/spacetest3576-verify /vendor/originflow/bin/spacetest3576
ExecStart=/vendor/originflow/bin/spacetest3576
Restart=always
RestartSec=2

# 重启风暴限制：30 秒内最多 5 次，超限进入 failed → 触发 pcba-restore
# （默认值通常是 10s/5 次，显式写出便于确认）
StartLimitIntervalSec=30
StartLimitBurst=5

# 崩溃后退出码/信号写日志，便于事后排查
RestartForceExitStatus=203

[Install]
WantedBy=multi-user.target
```

要点：

- **`ExecStartPre` 校验失败 = 服务 never start**，比启动一个必然 203 的空文件干净得多
- **`StartLimitBurst` 到达后单元进入 failed**，`Restart=always` 不再无限重试 —— 这是打破 203/EXEC 死循环的关键，也是触发 `OnFailure` 的条件
- 真正的程序崩溃（如启动 3 秒后段错误）同样会走"5 次重试 → failed → 恢复旧版"，这是期望行为：新版本不稳定时自动退回 known-good

### 4.2 `/usr/local/bin/spacetest3576-verify`（新增）

```sh
#!/bin/sh
# 校验 spacetest3576 二进制有效性。用法: spacetest3576-verify <path>
# 退出码: 0=有效, 1=无效（systemd 将拒绝启动）
set -u

target="$1"

if [ ! -f "$target" ]; then
    logger -t pcba-verify -p user.err "binary missing: $target"
    exit 1
fi

if [ ! -s "$target" ]; then
    logger -t pcba-verify -p user.err "binary is empty (0 bytes): $target"
    exit 1
fi

magic=$(head -c 4 "$target" | od -An -tx1 | tr -d ' \n')
if [ "$magic" != "7f454c46" ]; then
    logger -t pcba-verify -p user.err "binary is not ELF (magic=$magic): $target"
    exit 1
fi

# 最小体积门槛（当前 v1.3.0 约 215KB，取 64KB 下限防"半截文件"）
size=$(stat -c %s "$target")
if [ "$size" -lt 65536 ]; then
    logger -t pcba-verify -p user.err "binary suspiciously small ($size bytes): $target"
    exit 1
fi

exit 0
```

### 4.3 `/etc/systemd/system/pcba-restore.service`（新增）

```ini
[Unit]
Description=Restore spacetest3576 from known-good backup
# 只由 pcba-test.service 的 OnFailure 触发；不在开机常规路径中
After=local-fs.target

[Service]
Type=oneshot
ExecStart=/usr/local/bin/spacetest3576-restore
# 恢复脚本自身失败也要留日志，但不再级联触发任何东西
Restart=no

[Install]
WantedBy=multi-user.target
```

### 4.4 `/usr/local/bin/spacetest3576-restore`（新增）

```sh
#!/bin/sh
# 从 /userdata 的 known-good 副本恢复 spacetest3576 并重启服务。
# 前提假设：副本在升级成功后由 save-lastgood 维护（见 4.5）。
set -u

target=/vendor/originflow/bin/spacetest3576
lastgood=/userdata/originflow/backup/spacetest3576.lastgood
service=pcba-test.service

log() { logger -t pcba-restore -p user.err "$1"; echo "pcba-restore: $1"; }

if [ ! -s "$lastgood" ]; then
    log "FATAL: no known-good backup at $lastgood; manual recovery required"
    exit 1
fi

lastgood_md5=$(md5sum "$lastgood" | awk '{print $1}')

# 副本自身必须通过有效性检查，否则可能把坏副本装回去（.224 事故的回滚教训）
magic=$(head -c 4 "$lastgood" | od -An -tx1 | tr -d ' \n')
if [ "$magic" != "7f454c46" ]; then
    log "FATAL: known-good backup is not ELF (magic=$magic); refusing to restore"
    exit 1
fi

target_md5=$(md5sum "$target" 2>/dev/null | awk '{print $1}' || echo none)
if [ "$target_md5" = "$lastgood_md5" ]; then
    # 目标本身没问题（是程序运行期崩溃触发的 OnFailure），不需要文件级恢复
    log "target binary is intact (md5=$target_md5); service failure is runtime, not file corruption"
    exit 0
fi

log "restoring: $lastgood (md5=$lastgood_md5) -> $target (was md5=$target_md5)"

# 先落同目录临时文件再 rename，避免 cp 直接截断目标（与上位机 P0-1 同一原则）
cp -f "$lastgood" "$target.restore-tmp" || { log "FATAL: copy to staging failed"; exit 1; }
staging_md5=$(md5sum "$target.restore-tmp" | awk '{print $1}')
if [ "$staging_md5" != "$lastgood_md5" ]; then
    rm -f "$target.restore-tmp"
    log "FATAL: staging copy md5 mismatch ($staging_md5 != $lastgood_md5); aborting"
    exit 1
fi
chmod 755 "$target.restore-tmp"
mv -f "$target.restore-tmp" "$target" || { log "FATAL: atomic swap failed"; exit 1; }

# 清掉 systemd 的失败计数，否则 StartLimit 仍处于触发状态
systemctl reset-failed "$service"
systemctl restart "$service"

sleep 3
if systemctl is-active --quiet "$service"; then
    log "restore OK, service active again (md5=$lastgood_md5)"
    exit 0
else
    log "restore done but service still not active; check journalctl -u $service"
    exit 1
fi
```

### 4.5 `/usr/local/bin/spacetest3576-save-lastgood`（新增）

```sh
#!/bin/sh
# 升级成功后调用：把当前已验证的版本固化为 known-good 副本。
# 调用时机（二选一，建议都做）：
#   a) 上位机升级流程 MD5+功能校验全部通过后（见第 6 节）
#   b) 板内 OTA 脚本升级成功后
set -eu

target=/vendor/originflow/bin/spacetest3576
backup_dir=/userdata/originflow/backup
lastgood=$backup_dir/spacetest3576.lastgood

/usr/local/bin/spacetest3576-verify "$target"   # 只固化验证过的版本

mkdir -p "$backup_dir"
cp -f "$target" "$lastgood.tmp"
cp_md5=$(md5sum "$lastgood.tmp" | awk '{print $1}')
target_md5=$(md5sum "$target" | awk '{print $1}')
[ "$cp_md5" = "$target_md5" ]
chmod 755 "$lastgood.tmp"
mv -f "$lastgood.tmp" "$lastgood"
echo "lastgood updated: $lastgood (md5=$target_md5)"
```

> `/userdata` 与 `/vendor` 是**不同挂载点**（`.16`/`.224` 均已确认）。单一点故障（断电、写坏、误删）不可能同时毁掉 `/vendor/.../spacetest3576`、`/vendor/.../spacetest3576.bak`、`/userdata/.../spacetest3576.lastgood` 三份副本 —— 这是与 `.224` 现状（主文件+备份同目录同归于尽）的本质区别。

### 4.6 升级互斥锁（P2-8）

`.224` 上存在板内 OTA 框架（`/vendor/originflow/ota/scripts/`），与上位机升级流程可能同时操作同一目标文件。所有修改目标二进制的脚本（板内 OTA 的 install/upgrade 脚本）入口统一加：

```sh
exec 9>/var/lock/spacetest-upgrade.lock
flock -n 9 || { echo "another upgrade is in progress"; exit 50; }
# ... 原有升级逻辑 ...
```

> 上位机 `AdbPcbaCommandClient.cs` 生成的远端脚本当前未加锁；后续把 `BuildRemoteUpgradeScript` 输出包一层同样的 `flock` 即可（属上位机代码改动，不在本部署文档范围，但保持锁文件路径一致：`/var/lock/spacetest-upgrade.lock`）。

### 4.7 板内 OTA 脚本审计要求（`/vendor/originflow/ota/scripts/`）

`.224` 上的 OTA 脚本尚未完成审计（板子失联）。恢复网络后必须检查并按以下规则整改，规则与上位机侧 P0/P1 一致：

1. **同文件系统原子替换**：上传到 `/tmp` 的包必须先 `cp` 到目标同目录再 `mv`，禁止 `/tmp` 直接 `mv` 到 `/vendor`
2. **备份后校验**：`cp` 到 `.bak` 后必须复验 MD5 与 pre-MD5 一致，不一致中止
3. **回滚前校验**：恢复 `.bak` 前必须验证其非空、ELF、MD5 与 pre-MD5 一致
4. **新包校验**：非空、ELF 魔数、MD5 三重检查通过才允许替换
5. **成功后**：调用 `/usr/local/bin/spacetest3576-save-lastgood`
6. **全程持锁**：见 4.6

## 5. 部署步骤

板端部署文件的归属仓库是 `spaceTest3576`（当前源码在开发板 `.16` 的 `/userdata/work/spaceTest3576`）。以下给出**手动部署**步骤（用于救 `.224` 和改造 `.16`），并给出**镜像固化**要求。

### 5.1 手动部署（对单块板）

```sh
# 在板上以 root 执行（或 ssh 上去 sudo -s）

mkdir -p /userdata/originflow/backup

# 1) 安装脚本（scp 推上去后）
install -m 755 spacetest3576-verify   /usr/local/bin/
install -m 755 spacetest3576-restore  /usr/local/bin/
install -m 755 spacetest3576-save-lastgood /usr/local/bin/

# 2) 安装 service 单元
install -m 644 pcba-test.service    /etc/systemd/system/
install -m 644 pcba-restore.service /etc/systemd/system/
systemctl daemon-reload

# 3) 初始化 known-good：当前二进制必须先验证通过
/usr/local/bin/spacetest3576-verify /vendor/originflow/bin/spacetest3576 \
  && /usr/local/bin/spacetest3576-save-lastgood \
  || echo "当前二进制本身损坏，先修复二进制再初始化 lastgood"

# 4) 重启服务使新单元生效
systemctl restart pcba-test.service
```

> 对 `.224`：其 `/vendor` 下二进制已是 0 字节，步骤 3 会失败。需先用 `.16` 的 v1.3.0 二进制（MD5 `08c828e1...`，已在本地备好）恢复 `/vendor/originflow/bin/spacetest3576`，再走步骤 3。**先装部署文件、再修二进制**的顺序也可以——装好后直接把完好二进制放到 `/userdata/originflow/backup/spacetest3576.lastgood`，`pcba-restore` 也能自动完成恢复。

### 5.2 镜像/构建系统固化

部署文件应进入板端根文件系统构建（Yocto recipe / Debian rootfs overlay，具体取决于 `.224` 固件的构建方式）：

```
etc/systemd/system/pcba-test.service        (覆盖现有)
etc/systemd/system/pcba-restore.service     (新增)
usr/local/bin/spacetest3576-verify          (新增, 0755)
usr/local/bin/spacetest3576-restore         (新增, 0755)
usr/local/bin/spacetest3576-save-lastgood   (新增, 0755)
```

`pcba-test.service`、`pcba-restore.service` 需在镜像中执行 `systemctl enable`（分别链接到 `multi-user.target.wants`）。

首次开机初始化 lastgood：可在镜像首次启动脚本（或 `pcba-test.service` 的 `ExecStartPost` 一次性单元）中加入：

```sh
[ -s /userdata/originflow/backup/spacetest3576.lastgood ] \
  || /usr/local/bin/spacetest3576-save-lastgood
```

## 6. 与上位机升级流程的配合

上位机 `AdbPcbaCommandClient.cs` 已加固（P0-1～P0-3 + P1-4/5），两者配合关系：

| 升级阶段 | 上位机行为 | 板端部署文件的配合 |
|---|---|---|
| 升级前 | 读 pre-MD5，本地包 ELF/大小/MD5 校验 | 服务正常运行，`ExecStartPre` 一直在把关 |
| 替换 | 同目录 staging → 原子 `mv`；备份 `.bak` 后复验 MD5 | （无感） |
| 验证 | `is-active` 10s + 19001 发 `get_version` 功能检查 | 新二进制正常应答 |
| 成功后 | **待补充**：执行 `sudo /usr/local/bin/spacetest3576-save-lastgood` | lastgood 更新为新版本 |
| 失败 | 回滚 `.bak`（先验 MD5） | 若板端同时已因崩溃触发 restore，两边都退回旧版，最终状态一致 |

注意两点：

1. **lastgood 更新必须发生在功能校验通过之后**，否则坏版本会被固化为"known-good"。建议在 `AdbPcbaCommandClient` 成功路径加一步 SSH/ADB 调用（后续上位机改动项）。
2. **服务重启风暴窗口**：上位机替换二进制并 `systemctl start` 后，若新版本启动即崩，板端会在 30 秒内自行恢复旧版并占用 StartLimit 状态；此时上位机的功能校验也会失败并尝试回滚。可能出现"板端已恢复、上位机又 mv 一次 `.bak`"的竞争，但两边恢复的都是同一个 pre-MD5 版本，最终一致；`save-lastgood` 持锁可进一步消除该窗口。

## 7. 验证清单（在 `.16` 开发板上先做）

部署后逐项验证：

1. **正常启动**：`systemctl restart pcba-test` → active，19001 监听
2. **0 字节自愈**：
   ```sh
   sudo sh -c ': > /vendor/originflow/bin/spacetest3576'
   sudo systemctl reset-failed pcba-test.service
   sudo systemctl restart pcba-test.service   # ExecStartPre 失败 → failed → OnFailure
   # 预期：约 30 秒内 pcba-restore 自动恢复，服务回到 active，lastgood 不变
   journalctl -u pcba-restore --no-pager | tail
   ```
3. **非 ELF 自愈**：`sudo cp /bin/sh /vendor/originflow/bin/spacetest3576` 后重复上述步骤
4. **运行期崩溃自愈**：`sudo kill -9 $(pidof spacetest3576)` 连续多次触发 StartLimit，确认恢复路径不误动作（目标 MD5 未变时 restore 直接退出）
5. **lastgood 更新**：跑一次上位机升级，确认 `/userdata/originflow/backup/spacetest3576.lastgood` 的 MD5 变为新版本
6. **断电演练**：升级替换阶段拔电，重启后确认 `/vendor` 目标文件完好（原子替换兜底）或被 restore 恢复（部署文件兜底）
7. **锁**：手工持锁 `flock /var/lock/spacetest-upgrade.lock sleep 60 &`，再发起升级，确认被拒绝而不是双写

## 8. 后续待办

- [ ] `.224` 恢复网络后：审计 `/vendor/originflow/ota/scripts/`（按 4.7 规则）
- [ ] `.224` 修复：部署本方案文件 + 推入 v1.3.0 二进制（MD5 `08c828e1...`）
- [ ] 上位机成功路径补充 `save-lastgood` 调用（第 6 节注意点 1）
- [ ] 上位机远端脚本加 `flock`（4.6）
- [ ] `PCBA_UPGRADE_DESIGN.md` 同步更新（新退出码 41/42、`.upgrade-staging`、功能校验）
- [ ] 部署文件合入 `spaceTest3576` 板端仓库与镜像构建
