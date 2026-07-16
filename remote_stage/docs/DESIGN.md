# 3576 端测试服务设计方案

## 1. 目标和边界

`spaceTest3576` 运行在盒子端，负责执行板级测试、采集硬件结果、维护本地测试摘要，并把实时事件回复给上位机。

- 上位机：扫码、生成 `sessionId`、下发计划、展示、仪器判定、SQLite/CSV 汇总。
- 3576 端：执行测试项、维护单会话状态、保存本地简要结论、上报完整测试事件。
- 3576 端不直接管理上位机的 SQLite，也不重复计算产线直通率。

## 2. 与上位机通信

### 2.1 传输

```text
PC 上位机 -- adb forward tcp:19001 tcp:19001 --> 3576:127.0.0.1:19001
```

- TCP 长连接；每行一个 UTF-8 JSON 对象（JSON Lines）。
- 一次连接只对应一个 `sessionId`；会话结束后关闭连接。
- 收到非法 JSON、未知事件或不匹配的 `sessionId`，回复结构化失败并终止会话。

### 2.2 上位机 -> 3576

启动命令：

```json
{"protocolVersion":"1.0","sessionId":"uuid","sn":"SN123","commandGroup":"session","command":"start","parameters":{"tests":[{"id":"board_state","parameters":{}}]}}
```

会话期间还可能收到：

- `operator.decision`：人工 HDMI/LCD 判定。
- `test.decision`：上位机对板快充、板放电等测量的自动判定。
- `test.control`：例如 LED/风扇 `set_output_level`。

### 2.3 3576 -> 上位机

测试开始/过程/结束均使用：

```json
{"event":"test.report","testId":"keys","status":"running","resultCode":0,"message":"key detected","data":{"key":"confirm"},"timestamp":"2026-07-15T10:00:00+08:00"}
```

- `status`：`running`、`passed`、`failed`。
- 测试执行顺序强依赖上位机 `session.start.parameters.tests[]` 的数组顺序；3576 端必须按该顺序逐项执行和上报。
- 单项失败后继续执行后续测试项；未实现项也必须上报 `test.report/failed`，并继续后续项。
- 所有测试项执行结束后发送一次 `session.completed`；任一项失败时最终 `status=failed`，全部通过时 `status=passed`。
- 五键固定 ID：`up`、`down`、`left`、`right`、`confirm`；可兼容输入 `ok` 并归一化为 `confirm`。

## 3. 端侧结构

```text
spaceTest3576/
├─ hardware/       # 仅硬件通讯、设备发现、读写与资源释放
├─ manage/         # 会话、计划编排、事件回复、人工/上位机判定、状态机
├─ protocol/       # JSON Lines DTO、编解码、验证、错误码
├─ tests/          # 测试项实现及其参数/结果模型
├─ storage/        # 板端简要状态与会话摘要，原子写入
├─ config/         # 端口、设备路径、测试阈值等运行配置
├─ docs/
└─ main.*          # 服务入口与 TCP listener
```

### 3.1 `hardware/`

按具体测试资源建立目录，不保留通用 `gpio/i2c/spi/serial/network/media` 目录。硬件层只负责设备发现、读写、资源释放和错误码，不包含会话逻辑，也不直接拼装上位机 JSON。

当前目录：

- `keys/`：五键 evdev 输入。
- `wifi/`：基于 `nmcli` 的 Wi-Fi 状态和 ping 检测。
- `bluetooth/`：基于 `bluetoothctl` 的扫描检测。
- `fingerprint/`：指纹模组临时 PASS 框架。
- `fast_charge/`：板快充输入电压/电流读取框架。
- `tf_card/`：TF 卡 ext4、自动挂载、容量和读写校验。
- `indicator_led/`：LED 输出控制，未来接电压检测仪判定。
- `usb3.0/`：USB2.0/USB3.0 插拔记录文件接口框架。
- `camera/`、`fan/`：待实现。

每个模块优先提供 `open`、`close`、测试/读取函数和明确的错误码；硬件异常由 `tests/` 或 `manage/` 转换为 `test.report`。

### 3.2 `manage/`

- `session_manager`：只允许一个活动会话；校验 `sessionId`、SN、超时与取消。
- `plan_runner`：严格按上位机 `tests[]` 顺序执行；未实现项明确失败；单项失败不阻断后续项，最终统一汇总会话结果。
- `result_reporter`：将内部结果转换成 `test.report` 与 `session.completed`。
- `decision_manager`：等待/校验 `operator.decision`、`test.decision`、`test.control`。
- `board_state_manager`：读取板号、本地 SN、版本、累计数量；仅空 SN 时允许写入。

状态机：`idle -> preparing -> running -> waiting_decision -> completed|failed -> idle`。

### 3.3 `storage/`

板端只保存可追溯摘要：SN、sessionId、开始/结束时间、最终结果、失败 testId、版本和累计通过/失败次数。采用临时文件 + rename 原子落盘；完整明细由上位机保存。

## 4. 固定测试项

`board_state`、`hdmi`、`keys`、`lcd`、`wifi`、`bluetooth`、`fingerprint`、`typec_fast_charge`（板快充）、`typec_camera`、`tf`、`indicator_led`、`fan`、`otg`、`battery_management`（板放电）。

测试 ID 是协议常量，不因 UI 显示名称变化而改变。具体参数和 `data` 字段以本仓库的《开发对接说明.md》为基准。

## 5. 实施顺序

1. TCP JSON Lines server、协议 DTO、单会话状态机和 `board_state`。
2. `hardware/` 基础抽象与 mock hardware；可完成端到端虚拟会话。
3. 五键、LCD、指纹、网络、存储等无人工项目。
4. HDMI/LCD 人工判定和 LED/风扇控制回路。
5. 板快充/板放电与上位机自动判定回路。
6. 硬件实机联调、异常恢复、板端摘要持久化。
## 2026-07-16 Protocol Update

The current 3576 service supports a host-driven test plan with optional `skip`.

- `skip=true` returns `test.report/status=skipped`, `resultCode=2900`.
- Skipped items are recorded but are not counted in the final verdict.

New / updated modules:

- `hardware/ethernet`: uses `nmcli`, disables Wi-Fi first, waits Ethernet link/IP, pings with `-I <interfaceName>`, optionally waits for cable unplug.
- `hardware/usb3.0`: `usb2_3` reads `/tmp/spacetest_usb_ports.json` and validates `usb2Count` / `usb3Count`; default host config skips it until the board file producer is ready.
- `hardware/pcba_points`: `pcba_test_points` reads `/tmp/spacetest_pcba_points.json`, validates up to 32 `voltageMv` values against host-provided default limits, and returns per-channel results; default host config skips it until the acquisition interface is ready.

Recommended order:

`board_state -> hdmi -> keys -> lcd -> ethernet -> wifi -> bluetooth -> fingerprint -> typec_fast_charge -> typec_camera -> tf -> usb2_3 -> pcba_test_points -> indicator_led -> fan -> battery_management`

`ethernet` must stay before `wifi` because Ethernet test disables Wi-Fi and Wi-Fi test re-enables it.
