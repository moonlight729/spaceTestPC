# PCBA 测试板执行设计

## 1. 文档定位

本文档描述 PCBA 测试板侧的执行架构、命令处理、测试模块划分和对上位机的返回格式。

对接基准文档为 [上位机总体设计方案.md](C:/Users/31239/work/spaceTestPC/上位机总体设计方案.md)。

两份文档共用以下接口对象：

- `HostCommand`
- `CommandResponse`
- `TestSession`
- `TestItemResult`

## 2. 目标

PCBA 测试板侧负责：

- 接收上位机命令
- 调用底层测试能力
- 返回结构化结果
- 屏蔽板级实现细节

不负责：

- 上位机界面
- 数据库存储
- 电压检测仪采样
- 电池模拟器仪器控制

## 3. 本地状态

PCBA 测试板本地需要维护一份测试状态文件，建议使用 `txt` 或 `json`，核心目标是让上位机在测试开始前读取当前板状态，并在测试完成后写回摘要信息。

### 3.1 推荐文件

建议文件名：

```text
board_test_state.txt
```

### 3.2 建议字段

至少包含：

- `board_id`
- `board_sn`
- `test_mode`
- `current_state`
- `last_session_id`
- `last_start_time`
- `last_end_time`
- `last_verdict`
- `pass_count`
- `fail_count`
- `total_count`
- `version`

### 3.3 状态示例

```text
board_id=PCB001
board_sn=SN123456
test_mode=ready
current_state=idle
last_session_id=
last_start_time=
last_end_time=
last_verdict=
pass_count=0
fail_count=0
total_count=0
version=1
```

### 3.4 更新原则

- 测试开始前，上位机先读取该状态
- 测试进行中，PCBA 可更新 `current_state`
- 测试完成后，PCBA 更新最后一次摘要字段
- 上位机和 PCBA 使用相同 `sessionId` 和 `sn`

## 3. 分层架构

### 3.1 Manager 层

负责：

- 命令入口
- 会话上下文
- 测试项调度
- 结果封装
- 状态回传

建议模块：

- `CommandManager`
- `SessionManager`
- `TestDispatcher`
- `ResponseBuilder`

### 3.2 Hardware 层

负责：

- 蓝牙
- WiFi
- TF 卡
- LCD
- 指纹
- 按键
- HDMI
- Type-C
- 电池管理
- 风扇
- OTG
- 相机

建议模块：

- `BluetoothHardwareService`
- `WifiHardwareService`
- `TfCardHardwareService`
- `SpiLcdHardwareService`
- `FingerprintHardwareService`
- `KeyHardwareService`
- `HdmiHardwareService`
- `TypeCHardwareService`
- `BatteryHardwareService`
- `FanHardwareService`
- `OtgHardwareService`
- `CameraHardwareService`

## 4. 调用链

```text
上位机 HostCommand
  -> CommandManager
    -> TestDispatcher
      -> 指定 HardwareService
        -> 底层驱动/系统接口
      -> ResponseBuilder
  -> CommandResponse
```

## 5. 对接命令模型

PCBA 板接收的命令结构必须与上位机文档一致：

```json
{
  "requestId": "uuid",
  "sessionId": "uuid",
  "commandGroup": "wifi",
  "command": "connect_and_ping",
  "parameters": {},
  "timestamp": "2026-07-13T16:00:00+08:00"
}
```

返回：

```json
{
  "requestId": "uuid",
  "sessionId": "uuid",
  "resultCode": 0,
  "message": "ok",
  "data": {},
  "timestamp": "2026-07-13T16:00:01+08:00"
}
```

## 6. 命令分组

### 6.1 系统类

- `sys.ping`
- `sys.get_info`
- `sys.get_board_state`
- `sys.enter_test_mode`
- `sys.exit_test_mode`

### 6.2 蓝牙类

- `bt.scan_target_name`
- `bt.get_scan_result`

参数示例：

```json
{
  "targetName": "NODE_A_01",
  "timeoutMs": 5000,
  "minRssi": -80
}
```

### 6.3 WiFi 类

- `wifi.connect_and_ping`

参数示例：

```json
{
  "ssid": "FactoryAP",
  "password": "12345678",
  "targetIp": "192.168.1.1",
  "pingCount": 4,
  "timeoutMs": 10000
}
```

### 6.4 网线类

- `eth.connect_and_ping`

参数示例：

```json
{
  "routerIp": "192.168.1.1",
  "targetIp": "192.168.1.1",
  "pingCount": 4,
  "timeoutMs": 10000
}
```

说明：

- 网线测试使用与 WiFi 相同的路由器
- 重点检查有线链路是否建立、是否能获取网络并 ping 通目标

### 6.5 其他测试类

- `tf.*`
- `lcd.*`
- `fp.*`
- `key.*`
- `hdmi.*`
- `typec.*`
- `battery.*`
- `fan.*`
- `otg.*`
- `camera.*`

## 7. 各测试项执行要求

### 7.1 蓝牙测试

流程：

1. 接收上位机下发的 `targetName`
2. 启动扫描
3. 匹配指定广播名称
4. 输出扫描结果

返回数据建议：

```json
{
  "found": true,
  "targetName": "NODE_A_01",
  "rssi": -62
}
```

### 7.2 WiFi 测试

流程：

1. 使用参数连接指定 AP
2. 获取连接状态
3. 执行 ping
4. 返回结果

返回数据建议：

```json
{
  "connected": true,
  "ip": "192.168.1.20",
  "pingOk": true,
  "avgDelayMs": 12
}
```

### 7.3 网线测试

流程：

1. 使用与 WiFi 相同的路由器资源
2. 通过网线建立链路
3. 获取网络状态
4. 执行 ping
5. 返回结果

返回数据建议：

```json
{
  "linked": true,
  "ip": "192.168.1.30",
  "pingOk": true,
  "avgDelayMs": 8
}
```

### 7.4 其他测试项

统一要求：

- 有明确输入参数
- 有结构化返回字段
- 有清晰的成功失败码
- 能附带必要原始值

## 8. 状态同步流程

### 8.1 测试开始前

1. 上位机发起 `sys.get_board_state`
2. PCBA 返回当前板状态和统计摘要
3. 上位机创建测试会话并绑定 `sn`
4. 上位机开始正式测试

### 8.2 测试进行中

- PCBA 可更新本地状态文件
- 上位机可周期性查询状态
- 状态变化必须与当前 `sessionId` 对齐

### 8.3 测试完成后

1. PCBA 更新本地状态文件中的摘要字段
2. 上位机写入总记录和测试明细
3. 上位机计算直通率

### 8.4 一致性要求

- `board_id` 必须固定且唯一
- `sn` 必须与上位机扫码结果一致
- `sessionId` 必须与上位机测试会话一致
- `current_state` 变化要能追溯

## 8. 返回结果规范

### 8.1 成功结果

- `resultCode = 0`
- `message = ok`
- `data` 中带结构化结果

### 8.2 失败结果

- `resultCode != 0`
- `message` 提供失败说明
- `data` 可附加原始错误信息

### 8.3 建议错误码

- `0`：成功
- `1000-1999`：参数错误
- `2000-2999`：通信错误
- `3000-3999`：执行异常
- `4000-4999`：测试失败

## 9. 会话与状态

建议每次执行都带 `sessionId`，便于和上位机测试会话一致。

可选能力：

- 记录当前执行测试项
- 返回阶段性状态
- 提供当前测试模式状态查询

## 10. 与上位机的对接约束

### 10.1 字段名必须一致

以下字段名不要在两侧各自改名：

- `requestId`
- `sessionId`
- `commandGroup`
- `command`
- `parameters`
- `resultCode`
- `message`
- `data`
- `timestamp`

### 10.2 时间与超时

- PCBA 板应接受上位机传入的超时参数
- 返回时间戳格式统一为 ISO 8601

### 10.3 结果结构稳定

同一命令返回的 `data` 结构应固定，避免上位机频繁适配。

## 11. 下一步建议

建议下一步补一份单独的 [通信协议说明.md](C:/Users/31239/work/spaceTestPC/通信协议说明.md)，把：

- 命令字典
- 参数字段
- 返回字段
- 错误码
- 示例报文

统一固化，这样上位机和 PCBA 板就能并行开发。
