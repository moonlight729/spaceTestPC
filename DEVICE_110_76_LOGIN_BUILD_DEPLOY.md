# 110.76 设备登录、代码检查、编译与服务部署指南

## 1. 环境信息

| 项目 | 当前值 |
|---|---|
| Windows 工作目录 | `C:\Users\31239\work\spaceTestPC` |
| 设备完整 IP | `192.168.110.76` |
| SSH 用户 | `originflow` |
| SSH 端口 | `22` |
| 设备源码目录 | `/userdata/work/spaceTest3576` |
| 工作目录构建产物 | `/userdata/work/spaceTest3576/spacetest3576` |
| 正式运行程序 | `/vendor/originflow/bin/spacetest3576` |
| systemd 服务 | `pcba-test.service` |
| 业务 TCP 端口 | `19001` |
| 服务日志 | `/tmp/spacetest3576.log` |
| 部署备份 | `/vendor/originflow/bin/spacetest3576.bak` |

本文中的 `110.76` 均指完整地址 `192.168.110.76`。

## 2. 登录前准备

推荐从 PowerShell 调用 Git for Windows 自带的 Bash 和 OpenSSH。这样可以通过 `SSH_ASKPASS` 非交互输入密码，避免 PowerShell、SSH 和远端 shell 的多层引号互相干扰。

确认 Git Bash 路径存在：

```powershell
Test-Path 'C:\Program Files\Git\bin\bash.exe'
```

在工作目录创建临时密码辅助文件 `.tmp_ssh_askpass.sh`：

```sh
#!/bin/sh
printf '%s\n' '设备密码'
```

注意：

- 文件必须使用 LF 换行，首行必须是 `#!/bin/sh`。
- 不要把真实密码写入本文、Git 提交或应用日志。
- `.tmp_ssh_askpass.sh` 当前是未跟踪文件，必须保持不提交。
- 使用完毕后应删除该临时文件，或改用受控的凭据管理方式。

## 3. 稳定验证 SSH 登录

在仓库根目录执行：

```powershell
& 'C:\Program Files\Git\bin\bash.exe' -lc "export DISPLAY=codex:0 SSH_ASKPASS=/c/Users/31239/work/spaceTestPC/.tmp_ssh_askpass.sh SSH_ASKPASS_REQUIRE=force; ssh -T -o RequestTTY=no -o ConnectTimeout=10 -o StrictHostKeyChecking=accept-new -o PreferredAuthentications=password -o PubkeyAuthentication=no originflow@192.168.110.76 /bin/echo REMOTE_OK"
```

成功输出：

```text
REMOTE_OK
```

关键参数说明：

- `DISPLAY=codex:0`：使 OpenSSH 调用 `SSH_ASKPASS`。
- `SSH_ASKPASS_REQUIRE=force`：强制使用辅助程序读取密码。
- `-T -o RequestTTY=no`：避免分配交互终端，适合自动命令。
- `PreferredAuthentications=password`：明确使用密码认证。
- `PubkeyAuthentication=no`：避免本机密钥认证顺序影响排查。
- `StrictHostKeyChecking=accept-new`：首次连接接受新主机密钥，但不会静默接受已变化的密钥。

如果需要诊断连接过程，可临时给 SSH 增加 `-vv`，确认日志中出现：

```text
Authenticated to 192.168.110.76 ... using "password".
```

## 4. 登录后阅读代码

### 4.1 查看目录和 Git 状态

```powershell
& 'C:\Program Files\Git\bin\bash.exe' -lc "export DISPLAY=codex:0 SSH_ASKPASS=/c/Users/31239/work/spaceTestPC/.tmp_ssh_askpass.sh SSH_ASKPASS_REQUIRE=force; ssh -T -o RequestTTY=no -o StrictHostKeyChecking=accept-new -o PreferredAuthentications=password -o PubkeyAuthentication=no originflow@192.168.110.76 'cd /userdata/work/spaceTest3576 && pwd && git status --short && find . -maxdepth 3 -type f | sort | head -300'"
```

### 4.2 查看入口和核心调用链

```powershell
& 'C:\Program Files\Git\bin\bash.exe' -lc "export DISPLAY=codex:0 SSH_ASKPASS=/c/Users/31239/work/spaceTestPC/.tmp_ssh_askpass.sh SSH_ASKPASS_REQUIRE=force; ssh -T -o RequestTTY=no -o StrictHostKeyChecking=accept-new -o PreferredAuthentications=password -o PubkeyAuthentication=no originflow@192.168.110.76 'cd /userdata/work/spaceTest3576 && sed -n 1,220p main.c && sed -n 400,500p manage/session_manager.c && sed -n 1850,2060p tests/test_runner.c'"
```

当前主要调用关系：

```text
main.c
  accept TCP client
    -> session_manager_handle_client()
      -> 解析 JSON Lines 请求
      -> session.start
        -> test_runner_run_plan()
          -> 按 tests[] 顺序调用 run_one_test()
          -> 发送 test.report
          -> 最后发送 session.completed
```

### 4.3 查看具体逻辑

优先使用 `grep -n -C` 定位，再用 `sed -n` 阅读上下文：

```sh
cd /userdata/work/spaceTest3576
grep -n -C 20 ethernet_led tests/test_runner.c
grep -n -C 20 operator.decision tests/test_runner.c
sed -n '1860,2060p' tests/test_runner.c
```

避免一次输出整个大型 C 文件，否则终端容易截断，关键上下文也不易确认。

## 5. 修改设备代码的推荐流程

仓库中维护了设备源码镜像：

```text
C:\Users\31239\work\spaceTestPC\remote_stage
```

推荐流程：

1. 在本地 `remote_stage` 中修改并审查 diff。
2. 本地提交上位机和 `remote_stage` 镜像。
3. 使用 `scp` 将涉及的文件同步到设备源码目录。
4. 在设备上执行 `make`，由设备本机编译器完成严格检查。
5. 编译成功后再备份和替换正式二进制。
6. 重启服务并核对状态、PID 和 MD5。

不要直接覆盖设备正式二进制后再尝试编译。编译失败时必须保留当前运行版本。

### 5.1 同步单个源码文件

以下示例同步 `tests/test_runner.c`：

```powershell
& 'C:\Program Files\Git\bin\bash.exe' -lc "export DISPLAY=codex:0 SSH_ASKPASS=/c/Users/31239/work/spaceTestPC/.tmp_ssh_askpass.sh SSH_ASKPASS_REQUIRE=force; scp -o StrictHostKeyChecking=accept-new -o PreferredAuthentications=password -o PubkeyAuthentication=no /c/Users/31239/work/spaceTestPC/remote_stage/tests/test_runner.c originflow@192.168.110.76:/userdata/work/spaceTest3576/tests/test_runner.c"
```

同步多个文件时应保持它们在设备仓库中的目录位置。例如新增硬件模块时，先确认目标目录存在，再分别同步 `.c`、`.h` 和 `Makefile` 相关修改。

## 6. 在 110.76 上重新编译

### 6.1 增量编译

```powershell
& 'C:\Program Files\Git\bin\bash.exe' -lc "export DISPLAY=codex:0 SSH_ASKPASS=/c/Users/31239/work/spaceTestPC/.tmp_ssh_askpass.sh SSH_ASKPASS_REQUIRE=force; ssh -T -o RequestTTY=no -o StrictHostKeyChecking=accept-new -o PreferredAuthentications=password -o PubkeyAuthentication=no originflow@192.168.110.76 'cd /userdata/work/spaceTest3576 && make'"
```

当前 `Makefile` 使用：

```text
-std=c11 -Wall -Wextra -Werror -O2
```

因此警告也会导致编译失败。成功后生成：

```text
/userdata/work/spaceTest3576/spacetest3576
```

### 6.2 完整清理后编译

当头文件依赖或 Makefile 发生变化时使用：

```powershell
& 'C:\Program Files\Git\bin\bash.exe' -lc "export DISPLAY=codex:0 SSH_ASKPASS=/c/Users/31239/work/spaceTestPC/.tmp_ssh_askpass.sh SSH_ASKPASS_REQUIRE=force; ssh -T -o RequestTTY=no -o StrictHostKeyChecking=accept-new -o PreferredAuthentications=password -o PubkeyAuthentication=no originflow@192.168.110.76 'cd /userdata/work/spaceTest3576 && make clean && make'"
```

### 6.3 编译后检查

```powershell
& 'C:\Program Files\Git\bin\bash.exe' -lc "export DISPLAY=codex:0 SSH_ASKPASS=/c/Users/31239/work/spaceTestPC/.tmp_ssh_askpass.sh SSH_ASKPASS_REQUIRE=force; ssh -T -o RequestTTY=no -o StrictHostKeyChecking=accept-new -o PreferredAuthentications=password -o PubkeyAuthentication=no originflow@192.168.110.76 'cd /userdata/work/spaceTest3576 && ls -l --full-time spacetest3576 && md5sum spacetest3576'"
```

不要在 `pcba-test.service` 正在监听 `19001` 时直接启动工作目录中的第二个 `spacetest3576`，否则会因端口占用而失败，也可能干扰测试判断。

## 7. 部署并重启服务

以下操作需要 `sudo`。命令执行顺序是：计算新文件 MD5、备份当前正式版本、安装新版本、重启服务、核验状态。

```powershell
& 'C:\Program Files\Git\bin\bash.exe' -lc "export DISPLAY=codex:0 SSH_ASKPASS=/c/Users/31239/work/spaceTestPC/.tmp_ssh_askpass.sh SSH_ASKPASS_REQUIRE=force; ssh -T -o RequestTTY=no -o StrictHostKeyChecking=accept-new -o PreferredAuthentications=password -o PubkeyAuthentication=no originflow@192.168.110.76 'cd /userdata/work/spaceTest3576 && md5sum spacetest3576 && echo 设备密码 | sudo -S cp /vendor/originflow/bin/spacetest3576 /vendor/originflow/bin/spacetest3576.bak && echo 设备密码 | sudo -S install -m 755 spacetest3576 /vendor/originflow/bin/spacetest3576 && echo 设备密码 | sudo -S systemctl restart pcba-test.service && sleep 2 && systemctl is-active pcba-test.service && md5sum /vendor/originflow/bin/spacetest3576 && systemctl show pcba-test.service -p MainPID -p NRestarts -p Result'"
```

预期结果：

```text
active
MainPID=<非零 PID>
Result=success
NRestarts=0
```

工作目录二进制和正式二进制的 MD5 必须一致：

```sh
md5sum /userdata/work/spaceTest3576/spacetest3576 \
       /vendor/originflow/bin/spacetest3576
```

## 8. 服务状态和日志检查

### 8.1 查看服务状态

```powershell
& 'C:\Program Files\Git\bin\bash.exe' -lc "export DISPLAY=codex:0 SSH_ASKPASS=/c/Users/31239/work/spaceTestPC/.tmp_ssh_askpass.sh SSH_ASKPASS_REQUIRE=force; ssh -T -o RequestTTY=no -o StrictHostKeyChecking=accept-new -o PreferredAuthentications=password -o PubkeyAuthentication=no originflow@192.168.110.76 'systemctl status pcba-test.service --no-pager -l && systemctl show pcba-test.service -p MainPID -p NRestarts -p Result'"
```

### 8.2 查看进程和端口

```sh
ps -ef | grep '[s]pacetest3576'
ss -lntp | grep ':19001'
```

### 8.3 查看服务日志

服务单元当前配置为：

```ini
StandardOutput=append:/tmp/spacetest3576.log
StandardError=append:/tmp/spacetest3576.log
```

查看日志：

```sh
wc -l /tmp/spacetest3576.log
tail -n 300 /tmp/spacetest3576.log
```

`journalctl -u pcba-test.service` 可能没有业务输出，因为 stdout/stderr 已重定向到文件。

## 9. 回滚

如果新服务不能启动或协议验证失败，使用部署前备份回滚：

```powershell
& 'C:\Program Files\Git\bin\bash.exe' -lc "export DISPLAY=codex:0 SSH_ASKPASS=/c/Users/31239/work/spaceTestPC/.tmp_ssh_askpass.sh SSH_ASKPASS_REQUIRE=force; ssh -T -o RequestTTY=no -o StrictHostKeyChecking=accept-new -o PreferredAuthentications=password -o PubkeyAuthentication=no originflow@192.168.110.76 'echo 设备密码 | sudo -S install -m 755 /vendor/originflow/bin/spacetest3576.bak /vendor/originflow/bin/spacetest3576 && echo 设备密码 | sudo -S systemctl restart pcba-test.service && sleep 2 && systemctl is-active pcba-test.service'"
```

回滚后再次检查：

```sh
systemctl show pcba-test.service -p MainPID -p NRestarts -p Result
md5sum /vendor/originflow/bin/spacetest3576
```

## 10. Git 提交流程

### 10.1 本地仓库

```powershell
git status --short
git diff --check
git add SpaceTestPC.App remote_stage <相关文档>
git commit -m "描述本次上位机和设备镜像修改"
```

禁止提交：

- `.tmp_ssh_askpass.sh`
- 包含密码、Token 或私钥的文件
- 临时构建目录和日志
- 无关备份文件，例如 `NuGet.Config.bak`

### 10.2 设备仓库

```sh
cd /userdata/work/spaceTest3576
git status --short
git diff --check
git add <本次修改文件>
git commit -m "描述本次设备端修改"
```

提交前应确认设备仓库源码与实际部署二进制来自同一次编译，避免“Git 已提交但设备运行旧版本”或“设备运行新版本但源码未提交”。

## 11. 常见问题

### PowerShell 报引号或括号错误

原因通常是 PowerShell、Git Bash、SSH 和远端 shell 四层引号嵌套。推荐规则：

- PowerShell 的 `-lc` 参数外层使用双引号。
- SSH 远端命令整体使用单引号。
- 尽量避免在远端命令内部再次使用带空格的双引号。
- 复杂操作拆成多条命令，不要把诊断、编译、后台启动和 MD5 检查混在同一行。

### SSH 成功但没有输出

先执行最小命令 `/bin/echo REMOTE_OK`。如果最小命令正常，问题通常在远端命令引号，而不是认证或网络。

### 编译成功但服务仍运行旧程序

`make` 只更新工作目录的 `spacetest3576`，不会自动替换 `/vendor/originflow/bin/spacetest3576`。必须执行备份、`install` 和 `systemctl restart`，然后比较两个文件的 MD5。

### 服务反复重启

检查：

```sh
systemctl show pcba-test.service -p MainPID -p NRestarts -p ExecMainCode -p ExecMainStatus -p Result
tail -n 300 /tmp/spacetest3576.log
dmesg | tail -n 100
```

### 上位机提示连接提前关闭

结合上位机日志和设备逻辑按时间顺序检查：

1. 上位机最后收到的 `test.report`。
2. 是否进入允许断联的 `ethernet_led/prepare_disconnect` 阶段。
3. 上位机是否按 `reconnectDelayMs` 等待。
4. 重连计划是否给 `ethernet_led` 增加 `resumeAfterReconnect=true`。
5. 设备是否进入 `waiting_decision_after_reconnect`，并继续执行后续测试。
## 11. 110.76 当前实际部署方式（直接运行工作目录版本）

设备当前以 `/userdata/work/spaceTest3576` 作为直接开发和运行目录，systemd 服务为
`spacetest3576.service`，`ExecStart` 指向 `/userdata/work/spaceTest3576/spacetest3576`。

每次设备端 `make` 成功后，执行：

```sh
cd /userdata/work/spaceTest3576
echo '<BOARD_PASSWORD>' | sudo -S sh deploy/install_systemd_service.sh
```

如果旧的 `/vendor/originflow/bin/spacetest3576` 进程仍占用 19001 端口，先停止旧进程，再重启新服务：

```sh
echo '<BOARD_PASSWORD>' | sudo -S kill <OLD_VENDOR_PID>
echo '<BOARD_PASSWORD>' | sudo -S systemctl restart spacetest3576.service
systemctl --no-pager --full status spacetest3576.service
```

生效检查：

```sh
systemctl is-enabled spacetest3576.service
systemctl is-active spacetest3576.service
pgrep -a spacetest3576
ss -lntp | grep ':19001'
```

预期为 `active (running)`，且进程路径为 `/userdata/work/spaceTest3576/spacetest3576`。不要同时运行厂商版本和工作目录版本，否则会因端口占用导致服务反复重启。
