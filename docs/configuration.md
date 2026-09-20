# 上位机配置说明

> **2026-09 更新**：对齐 `SpaceTestPC.App` 当前实现。
> 相关文档：[`architecture.md`](architecture.md)（架构与流程）、[`communication-protocol.md`](communication-protocol.md)（协议字段含义）。
> 环境配置入口位于顶部右侧 **TEST** 按钮右侧的设置按钮；默认加载当前已保存的配置，修改后点击保存才落盘，原文件备份为 `appsettings.json.bak`。

## 1. 配置文件与加载顺序

源码 / 运行目录中的配置文件：

```text
SpaceTestPC.App/appsettings.json                    主配置（必选）
SpaceTestPC.App/appsettings.pcba.json               可选，pcba 模式叠加模板
SpaceTestPC.App/appsettings.finished_product.json   可选，finished_product 模式叠加模板
SpaceTestPC.App/appsettings.example.jsonc           参考模板
```

编译后必须出现在运行目录（默认 `SpaceTestPC.App/bin/Debug/net10.0-windows/`），csproj 通过 `CopyToOutputDirectory=PreserveNewest` 自动复制。

加载规则（`ConfigurationService`）：

1. **同名 `.jsonc` 优先**：若 `appsettings.jsonc` 存在则忽略 `appsettings.json`。
2. 允许注释与尾逗号（配置文件里可以直接写 `// ...` 说明）。
3. 模式叠加：存在 `appsettings.<testMode>.json` 时，把其中的 `TestModes[<mode>]` 作为**模板**补齐到主配置——只有主配置缺失的项才会被模板填充（`testOrder`、`skippedTests`、`disabledTests`、`snLength`、`snRuleDescription`、`testParameters` 均遵循此规则）。**通过环境配置窗口保存的值永远优先。**
4. 主配置反序列化失败时只有 `upgrade` 段会被兜底解析，其余回落默认值（保证仍能连接与升级）。

## 2. 切换测试模式

修改 `appsettings.json` 中的 `testMode`：

```json
{
  "testMode": "pcba"
}
```

PCBA 模式：

```json
"testMode": "pcba"
```

整机模式：

```json
"testMode": "finished_product"
```

程序启动时会根据 `testMode` 自动加载：

```text
pcba              -> appsettings.pcba.json
finished_product  -> appsettings.finished_product.json
```

公共配置仍然从 `appsettings.json` 读取，模式配置只覆盖当前模式相关内容。

## 3. 配置测试项顺序

测试顺序配置在对应模式块的 `testModes.<mode>.testOrder` 中；未列出的项按 `AllTestPlan` 原有相对顺序排在后面，`board_state` 缺失时会自动补到首位。

```json
{
  "testMode": "pcba",
  "testModes": {
    "pcba": {
      "displayName": "PCBA 测试",
      "databaseName": "space-test-pcba.db",
      "snLength": 17,
      "testOrder": [
        "board_state",
        "hdmi",
        "ethernet_led",
        "lcd",
        "typec_camera",
        "usb2",
        "usb3",
        "keys",
        "fan",
        "wifi",
        "bluetooth",
        "tf",
        "emmc",
        "ddr",
        "pcba_test_points",
        "pcba_indicator_led"
      ]
    }
  }
}
```

顺序约束：

- `board_state` 必须是第一项。
- `ethernet_led` 必须在 `wifi` 之前（网口测试会关闭 Wi-Fi，Wi-Fi 测试会重新打开）。
- `finished_product` 模式下 `pcba_test_points` / `pcba_indicator_led` 会被强制移除，写在 `testOrder` 里也不生效。

每个模式块还可配置：

| 字段 | 说明 |
| --- | --- |
| `displayName` | 界面显示的模式名 |
| `databaseName` | 该模式使用的 SQLite 文件名（位于 `data/`） |
| `snLength` | 扫码 SN 要求长度（默认 20；PCBA 17） |
| `snRuleDescription` | 扫码失败时的提示文案，留空则按 `snLength` 自动生成 |

## 4. 测试项 ID

上位机内置全集（`AllTestPlan`，即 `enabledTests`/`disabledTests` 为空时的结果）：

| ID | 显示名 | 说明 |
| --- | --- | --- |
| `board_state` | 板状态 | 板 ID / 板 SN / 历史项 / 版本校验 |
| `hdmi` | HDMI | HDMI 输出检查 |
| `keys` | 六键/七键测试 | 上下左右 + 确认 + Recovery（PCBA 另有 MASKROM） |
| `lcd` | LCD | 屏幕显示（下发彩条参数） |
| `wifi` | WiFi | 连接指定 SSID 并 ping 网关 |
| `bluetooth` | 蓝牙 | 扫描指定广播名并取 RSSI |
| `battery_management` | 板放电测试 | 放电电压/电流采样判定 |
| `typec_fast_charge` | 板快充 | 快充电流判定 |
| `tf` | TF 卡 | 挂载检查 |
| `emmc` | EMMC | 容量与读写自检 |
| `ddr` | DDR | 容量与压力测试 |
| `typec_camera` | 相机测试&同步信号测试 | 取流帧数/中断数/同步脉冲 |
| `usb2` | USB2.0 测试 | 插入/拔出分步检查 |
| `usb3` | USB3.0 测试 | 插入/拔出分步检查 |
| `pcba_test_points` | PCBA测试点 | 32 通道电压（仅 PCBA 模式） |
| `ethernet_led` | 网口灯 | 速率与灯色人工确认 |
| `indicator_led` | 指示灯板 | 指示灯切换检查 |
| `pcba_indicator_led` | PCBA红蓝指示灯 | 仅 PCBA 模式，人工判定 |
| `fan` | 风扇 | 转速/电平检查 |

说明：

- 界面序列里还会出现一项 `application_upgrade`（显示名“设备程序升级”），它是上位机的升级占位项，**不会**下发到板端。
- `fingerprint`、`reset_button`、`otg`、`ethernet` 属于历史/预留 ID，当前已不在内置全集中；写入 `enabledTests`/`disabledTests` 不会产生任何效果。
- 新增 ID 需要同时修改上位机 `AllTestPlan` 与板端协议实现。

## 5. 启用、禁用和跳过

三者由 `MainViewModel.BuildActiveTestPlan` 统一处理，**是否生效取决于运行模式**：

| 配置 | 读取位置优先级 | 生产模式（`operationMode=production`） | 开发者模式（`operationMode=developer`） |
| --- | --- | --- | --- |
| `enabledTests` | `testModes.<mode>.enabledTests` → `testPlan.enabledTests` | **不生效**，始终按完整计划执行 | 生效，取白名单 ∩ 全集 |
| `disabledTests` | `testModes.<mode>.disabledTests` → `testPlan.disabledTests` | **仅模式块生效**（用于 PDO/PCBA 差异） | 两者均可生效 |
| `skippedTests` | `testModes.<mode>.skippedTests` → `testPlan.skippedTests` | **不生效** | 生效 |

### 5.1 启用指定测试项（开发者模式）

`enabledTests` 非空时，只运行其中列出的测试项：

```json
"enabledTests": [
  "board_state",
  "hdmi",
  "indicator_led",
  "keys"
]
```

### 5.2 禁用测试项

`enabledTests` 为空时，用 `disabledTests` 排除测试项：

```json
"enabledTests": [],
"disabledTests": [
  "battery_management",
  "typec_fast_charge",
  "indicator_led"
]
```

当前 PCBA 模式默认禁用 `battery_management` / `typec_fast_charge` / `indicator_led`：放电电流由
**外部仪表**（万用表/电子负载）人工判定，上位机不自动采样判定。需要恢复时，把对应 ID 从模式块下的
`disabledTests` 删除即可。

> 注意：不要用 `skippedTests` 代替禁用。跳过项仍会发给 3576 并上报 `skipped`，板端会把会话判定为
> `Session incomplete with N skipped test(s)`（整轮结果 Fail）。只有 `disabledTests` 才是真正不测试。

### 5.3 跳过测试项（开发者模式）

`skippedTests` 会保留测试项显示，下发时带 `skip=true` / `skipReason`，不计入最终失败判定：

```json
"skippedTests": {
  "fingerprint": "当前未接指纹模组",
  "reset_button": "当前阶段暂不测试"
}
```

PCBA 和整机模式可以分别配置同一个测试项，两种模式互不影响。

## 6. 配置测试参数

公共测试参数放在 `appsettings.json` 的 `testPlan.testParameters` 中，按测试项 ID 分组。

**覆盖顺序**（`GetTestParameters`）：先取 `testPlan.testParameters[id]`，再用
`testModes.<mode>.testParameters[id]` 逐键覆盖；此外上位机会自动注入 `mode` 键，蓝牙项会用
`bluetoothBroadcaster.broadcastName` 覆盖 `targetName`（现场只改广播名即可），
`indicator_led` 缺键时会补默认值。

Wi-Fi 示例：

```json
"wifi": {
  "ssid": "test_router_001",
  "interfaceName": "wlan0",
  "minRssi": -55,
  "maxRetryCount": 5,
  "retryIntervalMs": 2000,
  "decisionTimeoutMs": 5000,
  "scanTimeoutMs": 10000
}
```

Recovery 按键示例：

```json
"keys": {
  "timeoutMs": 45000,
  "recoveryPressThreshold": 100,
  "recoveryMaxRaw": 5000,
  "recoveryStableSampleCount": 3,
  "recoverySampleIntervalMs": 100,
  "recoveryTimeoutMs": 10000
}
```

整机模式需要覆盖测试方法时，可以在模式配置中增加 `testParameters`。例如：

```json
"testParameters": {
  "fan": {
    "method": "tach_auto",
    "hwmonRoot": "/sys/bus/platform/drivers/pwm-fan/fan1/hwmon",
    "startValue": 100,
    "stopValue": 0,
    "tachSettleMs": 1000
  }
}
```

## 7. 数据库区分

PCBA 和整机模式必须使用不同数据库名称：

```json
PCBA: "databaseName": "space-test-pcba.db"
整机: "databaseName": "space-test-finished-product.db"
```

不要让两种模式使用相同数据库文件，否则查询记录和测试统计会混在一起。

## 8. 其它常用配置块

| 配置块 | 关键字段 | 说明 |
| --- | --- | --- |
| `testMode` | `finished_product`（默认）/ `pcba` | 决定数据库、SN 长度、启用清单 |
| `operationMode` | `production`（默认）/ `developer` | 开发者模式才允许勾选测试项、放行 SN 不一致等调试能力（切换需密码） |
| `pcbaConnection` | `mode`（`tcp`/`adbForward`/`mock`）、`host`、`port`、`ethernetOnly`、`adbPath`、`discovery` | `host=auto` 时由发现服务在选定网卡网段内扫描，详见 [`architecture.md`](architecture.md) 第 5 节 |
| `upgrade` | `enabled`、`transport`（`auto`/`adb`/`sshScp`）、`localBinaryPath`、`remoteBinaryPath`、`serviceName`、`autoUpgradeDelaySeconds`、`sshUser/sshPassword/sshPort` | 板端程序 MD5 校验与升级，详见 [`application-upgrade.md`](application-upgrade.md) |
| `logging` | `fileEnabled`、`filePath` | 运行日志，默认 `logs/space-test-pc.log` |
| `testLifecycle` | `enabled`、`serviceName`、`poweroffAfterTest` | 测试结束后是否切断设备电源，默认关 |
| `jk5506` | `enabled`、`portName`、`baudRate`、`slaveAddress`、`timeoutMs` | JK5506 仪器串口 |
| `jxTvm` | `enabled`、`portName`、`channels[]` | 32 通道电压阈值；`channels` 中的 `channel/minMv/maxMv/name` 会覆盖内置 `测试表.csv` 默认值，改阈值**无需重新编译** |
| `bluetoothBroadcaster` | `enabled`、`portName`、`broadcastName` | 蓝牙广播器；`broadcastName` 会覆盖蓝牙项的 `targetName` |
| `testPlan.mock` | `failingTestId`、`runningDelayMs`、`resultHoldMs` 等 | Mock 会话节奏与注入失败项 |
| `testPlan.continuous` | `enabledByDefault` | 是否默认连续测试 |

## 9. 推荐修改流程

1. 关闭正在运行的上位机。
2. 修改 `appsettings.json` 的 `testMode`。
3. 修改对应的 `appsettings.pcba.json` 或 `appsettings.finished_product.json`。
4. 确认 `testOrder` 中的测试项 ID 有效。
5. 确认 `enabledTests`、`disabledTests` 和 `skippedTests` 没有冲突。
6. 编译上位机。
7. 确认配置文件已经复制到 `bin/Debug/net10.0-windows`。
8. 启动程序后确认顶部显示的模式和数据库名称。

## 10. 常见问题

### 修改配置后没有生效

优先检查实际运行目录中的配置，而不是只检查源码目录：

```text
SpaceTestPC.App/bin/Debug/net10.0-windows/appsettings.json
SpaceTestPC.App/bin/Debug/net10.0-windows/appsettings.pcba.json
SpaceTestPC.App/bin/Debug/net10.0-windows/appsettings.finished_product.json
```

### 测试项顺序没有变化

确认修改的是当前模式文件中的 `testOrder`，并且 `testMode` 与文件匹配。

### 测试项没有显示

检查该 ID 是否被放入 `enabledTests`，或是否被 `disabledTests` 排除。

### 测试项显示但不计入结果

检查该 ID 是否配置在 `skippedTests` 中。跳过项只用于显示和记录，不计入最终通过判定。
