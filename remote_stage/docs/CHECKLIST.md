# 3576 端与上位机完整对接 Checklist

目标：3576 端通过 ADB forward 与上位机完整联调，测试顺序、事件流、失败继续策略、结果保存和服务常驻全部闭环。

## 1. 当前硬件模块状态

| 模块 | 状态 | 当前验证 | 剩余工作 |
|---|---|---|---|
| 五键 `keys` | 已接入 runner | timeout smoke 已验证 `failed/4001` 与 `session.completed/failed` | 人工依次按 `up/down/left/right/confirm`，验证逐键 running 和最终 passed |
| Wi-Fi `wifi` | 已完成并接入 runner | 参数 smoke PASS，已读取 `ssid/routerIp/pingCount/timeoutMs` | 后续用正式 JSON parser 替代轻量字符串解析 |
| 蓝牙 `bluetooth` | 已接入 runner | 当前无默认控制器，返回 `failed/4200`；已读取 `targetName/minRssi/scanWindowMs` | 恢复蓝牙硬件/驱动后实测扫描目标名称 |
| 指纹 `fingerprint` | 临时 PASS | C 编译和 runner smoke 通过 | 有真实 SPI 指纹模组后替换实现，保持 API 不变 |
| 板快充 `typec_fast_charge` | 已接入 runner | session smoke PASS，约 8332mV、0mA；已读取阈值和采样参数 | 改为最终板端文件接口 |
| 相机 `typec_camera` | 已完成并接入 runner | `/dev/video0` 拉流 PASS；已读取 devicePath/timeout/中断阈值 | 接入曝光中断计数文件，拉流后要求曝光计数增量 >= 30 |
| TF 卡 `tf` | 已完成并接入 runner | sudo session smoke PASS；已支持 devicePath/mountPoint/allowFormatExt4/minCapacityMb | 确认正式服务以 root 运行，或确保挂载点对服务账户可写 |
| 指示灯板 `indicator_led` | 框架完成，未接入 runner | brightness 节点 C 编译通过 | 接入 runner；通过 `test.control` 控制输出，等待上位机电压检测仪 `test.decision` |
| 风扇 `fan` | 未开始 | 未验证 | 确认控制节点/转速反馈；接入 runner；支持电压检测仪判定或设备端反馈 |
| USB2.0/USB3.0 插拔 | 框架完成，未接入上位机计划 | C 编译通过，未配置文件接口返回 `4500` | 等板端提供 USB2.0/USB3.0 四次插拔记录文件；确认是否加入上位机测试计划 |
| HDMI `hdmi` | 未实现，当前按 unsupported 失败 | runner 按 `failed/3900` 上报并继续后续项 | 实现人工判定等待 `operator.decision` |
| LCD `lcd` | 未实现，当前按 unsupported 失败 | runner 按 `failed/3900` 上报并继续后续项 | 实现人工判定等待 `operator.decision` |
| 板放电 `battery_management` | 未实现，当前按 unsupported 失败 | runner 可返回 `failed/3900` 并继续 | 实现电压/电流读取或上位机自动判定闭环 |
| OTG `otg` | 未实现，当前不在上位机计划 | 未验证 | 确认是否保留测试项；若保留则定义设备端判定方式 |

## 2. 协议与会话

- [x] TCP 服务监听 `127.0.0.1:19001`，适配 ADB forward。
- [x] 实现 UTF-8 JSON Lines 读取和发送。
- [x] `sys.get_board_state` 返回上位机需要的 `ResponseEnvelope<BoardState>`。
- [x] `session.start` 返回流式 `test.report` 与最终 `session.completed`。
- [x] runner 严格按上位机 `tests[]` 数组顺序执行。
- [x] 单项失败后继续执行后续项，最终用 `session.completed/failed` 汇总。
- [x] 未实现项返回 `test.report/failed`，`resultCode=3900`，并继续后续项。
- [x] 已轻量解析 `tests[].parameters`，覆盖 Wi-Fi、蓝牙、快充、相机、TF、五键 timeout。
- [ ] 使用真正 JSON parser 解析 `tests[]` 和 `parameters`，替代当前字符串扫描。
- [ ] 完整校验 `sessionId`、`sn`、协议版本和必填字段。
- [ ] 一次只允许一个 active session；并发 `session.start` 返回 busy。
- [ ] 处理连接断开、读取超时、重复请求和服务重启后的状态恢复。
- [ ] 实现 `operator.decision`，用于 HDMI/LCD 人工判定。
- [ ] 实现 `test.control`，用于 indicator_led/fan 输出控制。
- [ ] 实现 `test.decision`，用于上位机仪器自动判定回传。

## 3. 上位机参数对接

- [x] 上位机已能下发 `session.start.parameters.tests[]`。
- [x] 3576 已按 `tests[]` 顺序执行。
- [x] 3576 已轻量解析每个测试项的 `parameters`。
- [x] Wi-Fi 使用上位机下发的 `ssid/routerIp/pingCount/timeoutMs`；当前仍复用已有连接，不使用 password 主动切网。
- [x] 蓝牙使用上位机下发的 `targetName/minRssi/scanWindowMs`。
- [x] 快充使用上位机下发的电压/电流范围、稳定采样次数和超时。
- [x] 相机使用上位机下发的拉流超时、帧数/中断计数阈值。
- [x] TF 使用上位机下发的设备路径、挂载点、是否允许格式化和最小容量。
- [ ] indicator_led/fan 使用上位机下发的通道、目标电压和容差。
- [ ] battery_management 使用上位机下发的放电电压/电流阈值。

## 4. 板状态与本地摘要

- [x] `sys.get_board_state` 协议格式已和上位机对齐。
- [ ] `sys.get_board_state` 读取真实本地摘要，而不是默认值。
- [ ] 实现 `sys.write_sn`：仅允许空 SN 写入，写后回读验证。
- [ ] 本地摘要原子写入：SN、sessionId、开始/结束时间、最终结果、失败项、累计 PASS/FAIL。
- [ ] 断电/异常退出后不产生半文件。
- [ ] 本地记录保存失败时不阻断上位机保存，但下次 `board_state.data.warnings` 上报。

## 5. ADB 与上位机 UI 联调

- [x] PC 能识别 ADB 设备。
- [x] `adb forward tcp:19001 tcp:19001` 已验证。
- [x] PC 经 ADB forward 调用 `sys.get_board_state` 成功。
- [x] PC 经 ADB forward 读取到 `session.start` 首条 `test.report`。
- [ ] 上位机切换 `PcbaConnectionMode.AdbForward`。
- [ ] 用上位机 UI 扫码跑完整一轮真机测试。
- [ ] 验证中间失败项显示 failed，但后续项继续刷新。
- [ ] 验证最终 `session.completed/failed` 后数据库记录完整保存。
- [ ] 验证 SQLite/CSV 中 SN、sessionId、每项结果、最终结果与 3576 上报一致。
- [ ] 验证上位机查询页面能查到真机测试记录。

## 6. 服务部署

- [x] 临时方案：`nohup ./spacetest3576 >/tmp/spacetest3576.log 2>&1 < /dev/null &` 可稳定运行。
- [ ] 创建 systemd service。
- [ ] 开机自启。
- [ ] 异常退出自动拉起。
- [ ] 日志落盘到固定目录。
- [ ] 日志滚动和磁盘空间保护。
- [ ] 服务启动后自动确认 `ss -lnt` 存在 `127.0.0.1:19001`。

## 7. 验收标准

- [ ] 上位机按配置顺序下发 `tests[]`，3576 按相同顺序上报所有测试项。
- [ ] 全部测试项 PASS 时，上位机收到完整事件和 `session.completed/passed`。
- [ ] 任一测试项 FAIL 时，3576 继续执行后续项，最终发送 `session.completed/failed`。
- [ ] 未实现项显示为 failed/unsupported，不阻断后续项。
- [ ] 上位机 UI、数据库、CSV 与 3576 事件中的 SN、sessionId、测试项结果、最终结果一致。
- [ ] 设备端重启、ADB 断开、重复扫码、测试超时均可定位日志并给出明确错误。

## 8. 近期优先级

1. 上位机切到 `AdbForward`，跑完整 UI 真机流程。
2. 3576 解析 `tests[].parameters`，取消 Wi-Fi/蓝牙/快充等硬编码默认值。
3. 实现 systemd 服务，替代手动 nohup。
4. 接入 `indicator_led` 和 `fan` 的 `test.control` / `test.decision` 闭环。
5. 实现 `sys.write_sn` 与本地摘要存储。
6. HDMI/LCD 人工判定接入 `operator.decision`。
## 2026-07-16 Checklist Update

- [x] Host/3576 skip protocol: `status=skipped`, `resultCode=2900`, excluded from final verdict.
- [x] Ethernet protocol: host item `ethernet`, 3576 disables Wi-Fi, checks carrier/IP/ping, prompts cable unplug.
- [x] USB2.0&3.0 protocol: host item `usb2_3`, 3576 reads `/tmp/spacetest_usb_ports.json`.
- [x] USB2.0&3.0 default policy: default skip until board file producer is ready.
- [x] PCBA test points protocol: host item `pcba_test_points`, 32 channel voltage result contract defined.
- [x] PCBA test points 3576 framework: reads `/tmp/spacetest_pcba_points.json`, returns per-channel voltage and pass/fail.
- [x] PCBA test points default policy: default skip until acquisition hardware/file producer is ready.
- [ ] PCBA test points real acquisition backend: replace summary-file reader with ADC / fixture interface.
- [ ] USB2.0&3.0 real summary producer: board side generates USB2.0/USB3.0 plug history file.
- [ ] Ethernet real production validation: run through UI/ADB with wired network connected; avoid running over SSH Wi-Fi path.
