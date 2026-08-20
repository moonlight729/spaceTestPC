# 上位机测试配置说明

> **2026-08-20 更新**：环境配置入口已从顶部左侧（紧挨整机测试横幅）移至顶部右侧 **TEST** 按钮右侧的设置按钮。默认加载当前已保存的配置，修改后点击保存才落盘，原文件备份为 appsettings.json.bak，最终以保存版本为准。

## 1. 配置文件位置

开发和调试配置位于：

```text
SpaceTestPC.App/appsettings.json
SpaceTestPC.App/appsettings.pcba.json
SpaceTestPC.App/appsettings.finished_product.json
```

编译后需要确认以下目录中存在对应文件：

```text
SpaceTestPC.App/bin/Debug/net10.0-windows/
```

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

测试顺序配置在对应模式文件的 `testModes.<mode>.testOrder` 中。

PCBA 示例：

```json
{
  "testMode": "pcba",
  "testModes": {
    "pcba": {
      "displayName": "PCBA 测试",
      "databaseName": "space-test-pcba.db",
      "testOrder": [
        "board_state",
        "keys",
        "indicator_led",
        "fan",
        "ethernet",
        "wifi",
        "bluetooth"
      ]
    }
  }
}
```

整机示例：

```json
{
  "testMode": "finished_product",
  "testModes": {
    "finished_product": {
      "displayName": "整机测试",
      "databaseName": "space-test-finished-product.db",
      "testOrder": [
        "board_state",
        "hdmi",
        "indicator_led",
        "lcd",
        "keys",
        "fan",
        "ethernet",
        "wifi"
      ]
    }
  }
}
```

当前整机模式中，指示灯测试位于 HDMI 测试之后。

## 4. 测试项 ID

常用测试项 ID：

| ID | 说明 |
| --- | --- |
| `board_state` | 板状态读取 |
| `hdmi` | HDMI 人工判定 |
| `lcd` | LCD 人工判定 |
| `keys` | 上下左右确认和 Recovery 按键 |
| `indicator_led` | 指示灯测试 |
| `fan` | 风扇测试 |
| `ethernet` | 网口测试 |
| `wifi` | Wi-Fi 测试 |
| `bluetooth` | 蓝牙测试 |
| `fingerprint` | 指纹测试 |
| `typec_camera` | TYPE-C 相机测试 |
| `tf` | TF 卡测试 |
| `usb2` | USB2.0 结果文件测试 |
| `usb3` | USB3.0 结果文件测试 |
| `pcba_test_points` | PCBA 测试点 |
| `reset_button` | 复位按键人工判定 |

测试项必须使用代码中已有的 ID。新增 ID 需要同时增加上位机显示名称和底层协议支持。

## 5. 启用、禁用和跳过

### 5.1 启用指定测试项

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

当 `enabledTests` 为空时，可以使用 `disabledTests` 排除测试项：

```json
"enabledTests": [],
"disabledTests": [
  "fingerprint",
  "reset_button"
]
```

### 5.3 跳过测试项

`skippedTests` 会保留测试项显示，但运行时标记为 `SKIPPED`，不计入最终失败判定：

```json
"skippedTests": {
  "fingerprint": "当前未接指纹模组",
  "reset_button": "当前阶段暂不测试"
}
```

PCBA 和整机模式可以分别配置同一个测试项。例如：

```text
PCBA 模式跳过 indicator_led
整机模式正常测试 indicator_led
```

两种模式互不影响。

## 6. 配置测试参数

公共测试参数放在 `appsettings.json` 的 `testPlan.testParameters` 中。

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
    "pwmPath": "/sys/class/hwmon/hwmon12/pwm1",
    "tachPath": "/sys/class/hwmon/hwmon12/tach_rpm",
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

## 8. 推荐修改流程

1. 关闭正在运行的上位机。
2. 修改 `appsettings.json` 的 `testMode`。
3. 修改对应的 `appsettings.pcba.json` 或 `appsettings.finished_product.json`。
4. 确认 `testOrder` 中的测试项 ID 有效。
5. 确认 `enabledTests`、`disabledTests` 和 `skippedTests` 没有冲突。
6. 编译上位机。
7. 确认配置文件已经复制到 `bin/Debug/net10.0-windows`。
8. 启动程序后确认顶部显示的模式和数据库名称。

## 9. 常见问题

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
