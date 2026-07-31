# USB 预检单服务方案

## 1. 目标

当前已有一批设备没有 USB2.0/USB3.0 检测能力，需要通过升级现有 PCBA 应用增加 USB 预检功能。

方案要求：

- 不新增 systemd 服务，继续使用 `pcba-test.service`。
- USB 预检在接入 ADB 前通过 HDMI 和鼠标完成。
- PCBA 和整机模式使用独立结果文件。
- 接入 ADB 后，现有上位机流程只读取预检结果，不重复插拔检测。
- 未启用 USB 预检的旧设备保持原有测试流程不变。

## 2. 服务结构

继续使用：

```text
pcba-test.service
/vendor/originflow/bin/spacetest3576
```

应用内部划分为以下线程或模块：

```text
主通信线程
  负责 ADB/TCP 协议、测试计划和结果上报

USB 检测线程
  负责设备插拔、USB 速度、正反插确认和联通性状态机

HDMI UI 线程
  负责 HDMI 窗口、鼠标操作和测试引导

结果持久化模块
  负责结果文件原子写入和读取校验
```

USB 检测线程和 HDMI UI 线程不能阻塞主通信线程。UI 或 USB 检测异常时，只允许 USB 功能失败，不能导致 ADB 测试协议退出。

## 3. USB 测试状态机

服务启动后进入待机，不自动开始测试：

```text
SERVICE_READY
    -> USB_TEST_IDLE
    -> USB2_PORT1_NORMAL
    -> USB2_PORT1_REVERSE
    -> USB2_PORT2_NORMAL
    -> USB2_PORT2_REVERSE
    -> USB3_PORT1_NORMAL
    -> USB3_PORT1_REVERSE
    -> USB3_PORT2_NORMAL
    -> USB3_PORT2_REVERSE
    -> USB_TEST_COMPLETED
    -> WAIT_ADB_TEST
```

物理上只有两个 USB 口，两个口均同时支持 USB2.0 和 USB3.0。每个协议和每个物理口都必须测试正插、反插两个方向。

因此完整测试共 8 次：

- USB2.0 U 盘：端口 1 正插、端口 1 反插、端口 2 正插、端口 2 反插，共 4 次；
- USB3.0 U 盘：端口 1 正插、端口 1 反插、端口 2 正插、端口 2 反插，共 4 次。

操作员通过 HDMI 界面点击“开始 USB 测试”后，依次完成：

1. USB2.0 U 盘插入物理端口 1，正插；
2. USB2.0 U 盘插入物理端口 1，反插；
3. USB2.0 U 盘插入物理端口 2，正插；
4. USB2.0 U 盘插入物理端口 2，反插；
5. USB3.0 U 盘插入物理端口 1，正插；
6. USB3.0 U 盘插入物理端口 1，反插；
7. USB3.0 U 盘插入物理端口 2，正插；
8. USB3.0 U 盘插入物理端口 2，反插。

每个阶段都必须完成插入方向确认、设备枚举、速度识别和拔出确认。当前只验证 USB2.0/USB3.0 联通性，不执行挂载、格式化、写入或读回校验。插拔状态需要去抖，不能只根据一次枚举事件判定通过。

需要注意：USB 协议本身通常无法直接报告 Type-A 插头的物理正反方向。方向判定应由 USB 设备实际枚举结果、端口硬件信号或操作员在 HDMI 界面确认完成。若硬件无法自动区分方向，则界面必须明确显示当前要求的方向，并由操作员确认后进入下一步；不能把“同一个枚举成功”同时记为正插和反插。

HDMI 界面至少提供：

- 当前测试阶段；
- 当前端口；
- 请插入/请拔出提示；
- USB 协商速度；
- USB 枚举结果；
- 重新测试；
- 放弃测试；
- 最终通过/失败状态。

## 4. ADB 口处理

USB 预检阶段不连接 ADB。预检全部完成后：

1. 写入当前模式结果文件；
2. 锁定 USB 预检状态；
3. HDMI 提示拔出 U 盘；
4. 操作员接入 ADB；
5. 上位机启动现有测试流程。

USB 预检完成后，USB 线程进入等待状态，不再自动开始下一轮测试。下一块板开始前必须清理旧结果，防止复用上一块板的结果。

## 5. 结果文件

PCBA 和整机必须使用不同文件：

```text
/userdata/factory_test/usb/pcba_usb_test.json
/userdata/factory_test/usb/finished_product_usb_test.json
```

建议结果格式：

```json
{
  "schemaVersion": 1,
  "testMode": "pcba",
  "boardSn": "6957303888863",
  "sessionId": "usb-20260724-001",
  "startedAt": "2026-07-24T10:00:00+08:00",
  "completedAt": "2026-07-24T10:08:30+08:00",
  "overallResult": "passed",
  "usb2": {
    "port1": {
      "normal": {
        "detected": true,
        "speedMbps": 480,
        "passed": true
      },
      "reverse": {
        "detected": true,
        "speedMbps": 480,
        "passed": true
      }
    },
    "port2": {
      "normal": {
        "detected": true,
        "speedMbps": 480,
        "passed": true
      },
      "reverse": {
        "detected": true,
        "speedMbps": 480,
        "passed": true
      }
    }
  },
  "usb3": {
    "port1": {
      "normal": {
        "detected": true,
        "speedMbps": 5000,
        "passed": true
      },
      "reverse": {
        "detected": true,
        "speedMbps": 5000,
        "passed": true
      }
    },
    "port2": {
      "normal": {
        "detected": true,
        "speedMbps": 5000,
        "passed": true
      },
      "reverse": {
        "detected": true,
        "speedMbps": 5000,
        "passed": true
      }
    }
  }
}
```

每个端口的正反插记录必须独立保存，不能只保存端口计数。例如：

```json
{
  "port1": {
    "normal": {
      "detected": true,
      "speedMbps": 480,
      "passed": true
    },
    "reverse": {
      "detected": true,
      "speedMbps": 480,
      "passed": true
    }
  }
}
```

文件写入必须使用临时文件和原子替换：

```text
写入 *.json.tmp
fsync
rename 为正式 JSON 文件
```

上位机或主测试流程不能接受以下结果：

- 文件不存在；
- JSON 不完整；
- `testMode` 不匹配；
- SN 不匹配；
- `overallResult` 不是 `passed`；
- 测试结果过期；
- 任意端口的正插或反插未完成。

## 6. 上位机读取流程

上位机保持现有 `usb2_3` 测试项。运行到该项时，底层读取当前模式对应结果文件并直接判定：

```text
PCBA模式       -> pcba_usb_test.json
整机模式       -> finished_product_usb_test.json
```

建议增加协议命令：

```json
{
  "commandGroup": "sys",
  "command": "get_usb_test_result"
}
```

返回结果文件摘要和校验结果。文件仍是持久化依据，协议用于避免上位机直接依赖板端路径。

上位机详情应显示：

- 结果文件路径；
- 测试模式；
- SN；
- USB2.0 两个端口的正插/反插结果，共 4 次；
- USB3.0 两个端口的正插/反插结果，共 4 次；
- 实际协商速度；
- USB 枚举结果；
- 最终判定原因。

## 7. 旧设备兼容和升级启用

新增配置默认关闭：

```json
{
  "usbPretest": {
    "enabled": false
  }
}
```

升级完成并验证后，再按批次启用：

```json
{
  "usbPretest": {
    "enabled": true,
    "requiredUsb2Cycles": 4,
    "requiredUsb3Cycles": 4,
    "requiredPorts": 2,
    "requireNormalAndReverse": true,
    "recordDirectory": "/userdata/factory_test/usb",
    "resultExpireHours": 24
  }
}
```

兼容规则：

- 缺少 `usbPretest` 配置时默认为关闭；
- USB 预检关闭时，原有 ADB、WiFi、蓝牙等流程不变；
- USB 预检开启但结果文件不存在时，`usb2_3` 明确失败；
- 升级失败不能影响主服务继续提供原有测试协议；
- PCBA 和整机结果文件不能互相读取。

## 8. 稳定性要求

- USB 检测线程异常时自动退出并由主进程重新创建；
- HDMI UI 异常时只结束 UI，不退出主通信线程；
- 每个端口和每个阶段设置超时，例如 60 秒；
- 插入和拔出均进行状态去抖；
- 检测到 ADB 连接后禁止启动新的 USB 预检；
- 测试开始前清理当前模式旧结果文件；
- 结果文件写入完成后执行 `fsync` 和原子 `rename`；
- 记录 USB 节点、端口、速度、错误码和阶段日志；
- `pcba-test.service` 保持 `Restart=always`，必要时增加 systemd watchdog；
- 主线程与 USB 线程之间使用消息队列或互斥状态，不直接共享可变测试结构。

## 9. 推荐实施顺序

1. 增加 USB 预检配置结构，默认关闭；
2. 实现 USB 检测状态机和端口速度/联通性判定；
3. 实现 HDMI UI 线程；
4. 实现 PCBA/整机独立结果文件和原子写入；
5. 增加 `get_usb_test_result` 协议；
6. 修改 `usb2_3` 读取并校验结果文件；
7. 增加旧配置和缺少结果文件的兼容处理；
8. 在测试板上升级现有应用并验证 `pcba-test.service` 重启恢复；
9. 先关闭功能验证主流程，再按批次打开 `usbPretest.enabled`。

## 10. 方案 2：本地 Web UI

### 10.1 适用场景

如果不希望在生产板上安装 Qt、GTK 或 SDL2 运行库，可以使用本地 Web UI 完成 HDMI 和鼠标交互。

需要注意：HTTP 服务本身不能显示页面，板端仍必须存在 Chromium、Firefox 或其他浏览器/WebView 运行时。当前板端已经有 Xorg + XFCE，因此优先复用 XFCE 图形会话启动浏览器。

### 10.2 结构

方案 2 仍然只使用一个 `pcba-test.service`：

```text
pcba-test.service
└── spacetest3576
    ├── 主通信线程
    ├── USB 检测线程
    └── 内置 HTTP 服务
            ↓
        XFCE 启动本地浏览器 kiosk 模式
            ↓
        HDMI + 鼠标
```

USB 检测线程负责真实硬件检测，Web 页面只负责显示状态和发送操作指令，不能直接操作 USB 设备。

### 10.3 页面地址和接口

HTTP 服务只监听本机回环地址：

```text
http://127.0.0.1:18080/usb-test
```

建议接口：

```text
GET  /api/usb/state
POST /api/usb/start
POST /api/usb/retry
POST /api/usb/confirm-direction
POST /api/usb/abort
GET  /api/usb/result
```

初版可以使用 500 ms 轮询获取状态，避免引入 WebSocket 依赖。后续需要实时刷新时再增加 WebSocket 或 Server-Sent Events。

典型交互流程：

```text
网页点击“开始”
    -> HTTP 请求
    -> USB 检测线程执行
    -> 更新共享状态
    -> 网页轮询显示状态
```

线程之间使用互斥锁、条件变量或消息队列通信，禁止 HTTP 处理线程直接执行阻塞式 USB 检测。

### 10.4 页面测试顺序

页面依次显示以下 8 个步骤：

```text
USB2.0 - USB口1 - 正插
USB2.0 - USB口1 - 反插
USB2.0 - USB口2 - 正插
USB2.0 - USB口2 - 反插
USB3.0 - USB口1 - 正插
USB3.0 - USB口1 - 反插
USB3.0 - USB口2 - 正插
USB3.0 - USB口2 - 反插
```

每一步显示：

- 当前 USB 类型；
- 当前物理端口；
- 当前正插/反插要求；
- 请插入或拔出提示；
- 设备枚举状态；
- 协商速度；
- USB 枚举结果；
- 重试和终止操作；
- 最终通过或失败状态。

### 10.5 浏览器启动

如果板端有 Chromium，建议由 XFCE 用户会话以 kiosk 模式启动，而不是让 root 服务直接创建 X11 窗口：

```bash
chromium \
  --kiosk \
  --app=http://127.0.0.1:18080/usb-test \
  --noerrdialogs \
  --disable-session-crashed-bubble
```

这样可以避免后台 root 服务访问 X11 时的 `DISPLAY`、`XAUTHORITY` 和桌面会话权限问题。`pcba-test.service` 只负责提供 HTTP 服务和 USB 检测，不负责管理桌面登录。

如果没有浏览器，必须将浏览器运行时随升级包部署；仅增加 HTTP 服务不能在 HDMI 上显示网页。

### 10.6 与 ADB 主流程衔接

USB 预检完成后，网页显示：

```text
USB 预检已完成，请拔出 U 盘并接入 ADB 线。
```

网页进入只读状态，USB 检测线程进入 `WAIT_ADB_TEST`。接入 ADB 后，上位机继续现有测试流程，在 `usb2_3` 项中通过协议读取当前模式结果文件，不再重新执行 USB 插拔测试。

### 10.7 配置

旧设备默认关闭 Web UI：

```json
{
  "usbPretest": {
    "enabled": false,
    "webUiEnabled": false
  }
}
```

确认浏览器和页面可用后再启用：

```json
{
  "usbPretest": {
    "enabled": true,
    "webUiEnabled": true,
    "webPort": 18080,
    "requiredUsb2Cycles": 4,
    "requiredUsb3Cycles": 4
  }
}
```

### 10.8 方案 2 的优缺点

优点：

- 不需要把现有 C 服务改造成 Qt 应用；
- 页面使用 HTML/CSS/JavaScript，后续修改界面简单；
- USB 检测逻辑和显示逻辑解耦；
- 可以继续使用同一个 `pcba-test.service`。

限制：

- 必须有浏览器或 WebView 运行时；
- 浏览器启动依赖 Xorg/ XFCE 用户会话；
- 需要处理浏览器崩溃、页面刷新和 kiosk 恢复；
- 需要限制 HTTP 仅监听 `127.0.0.1`，避免测试接口暴露到生产网络。

### 10.9 推荐选择

当前板端已经运行 Xorg + XFCE 时，优先验证方案 2：

```text
纯 HTML/JavaScript 页面
+ pcba-test.service 内置 HTTP 服务
+ XFCE 自动启动 Chromium kiosk
+ USB 检测线程负责硬件
+ 上位机通过 ADB 读取结果文件
```

如果板端无法提供浏览器运行时，再采用方案 1 的 Qt Widgets + X11，并将 Qt 运行库随应用升级包部署。

### 10.10 手动打开网页和 USB 事件交互

网页可以由操作员手动打开，不要求 `pcba-test.service` 自动创建浏览器窗口。操作员在 HDMI 上打开：

```text
http://127.0.0.1:18080/usb-test
```

网页本身不直接访问 Linux USB 设备，也不依赖浏览器 USB API。真实 USB 事件由板端后台服务处理：

```text
U 盘插拔
    -> udev/USB 检测线程
    -> 更新 USB 测试状态
    -> HTTP 轮询或 WebSocket 推送
    -> 网页显示状态
```

网页只负责：

- 显示当前测试步骤；
- 显示“请插入/请拔出”提示；
- 接收鼠标点击；
- 发送开始、重试、确认和终止指令；
- 显示 USB 速度、枚举结果和最终判定。

后台服务负责：

- 监听 U 盘插拔事件；
- 判断设备对应的物理 USB 口；
- 判断 USB2.0/USB3.0 协商速度；
- 判断 USB 设备是否成功枚举；
- 判断设备是否已经拔出；
- 写入 PCBA 或整机结果文件。

建议接口：

```text
GET  /api/usb/state
POST /api/usb/start
POST /api/usb/retry
POST /api/usb/confirm-direction
POST /api/usb/abort
GET  /api/usb/result
```

网页初版可以每 500 ms 请求一次 `/api/usb/state`，不强制依赖 WebSocket。网页刷新或关闭时，USB 检测线程继续运行；网页重新打开后，通过 `/api/usb/state` 恢复当前步骤和结果。

不能把浏览器 USB API 作为生产判定依据，因为浏览器权限、浏览器兼容性和物理端口识别都不稳定，也无法可靠替代底层 udev 事件监听。

只要满足以下条件，手动打开网页即可完成交互：

1. `pcba-test.service` 正常运行；
2. USB 检测线程正常监听 udev；
3. HTTP 服务监听 `127.0.0.1:18080`；
4. 板端网页程序能够访问该地址；
5. 鼠标事件能够被当前 XFCE 图形会话接收。

此时网页只是显示和操作层，USB 插拔检测和测试判定不依赖网页是否持续打开。

### 10.11 第一版桌面快捷方式

第一版不修改 `pcba-test.service`，也不要求应用直接启动浏览器。由 `spacetest3576` 在启动时创建桌面快捷方式：

```text
/home/originflow/Desktop/USB-Test.desktop
```

快捷方式内容：

```ini
[Desktop Entry]
Type=Application
Name=USB测试
Comment=打开USB预检页面
Exec=xdg-open http://127.0.0.1:18080/usb-test
Icon=applications-internet
Terminal=false
Categories=Utility;
```

## Web UI 交互升级方案

USB 页面应使用内置的原生 HTML/CSS/JavaScript，继续由 `spacetest3576` 提供 HTTP 服务，不增加 Qt、GTK、SDL、Node.js、npm 或其他系统依赖。页面主标题固定为：

```text
Space USB口测试
```

页面必须突出显示当前模式、总体进度、当前 USB 版本、目标端口、正反插方向和下一步动作。8 个测试步骤为：

```text
USB2.0 端口1 正插
USB2.0 端口1 反插
USB2.0 端口2 正插
USB2.0 端口2 反插
USB3.0 端口1 正插
USB3.0 端口1 反插
USB3.0 端口2 正插
USB3.0 端口2 反插
```

每一步使用以下状态机：

```text
waiting_insert -> detected -> testing -> passed/failed
passed -> waiting_remove -> next_step
```

操作员只按页面提示插入或拔出 U 盘，不需要点击开始。检测到设备后自动读取设备节点、USB 拓扑和 `speed`，并显示：

```text
已检测到 U盘
设备节点：/dev/sda
实际速率：480 Mbps
检测耗时：1.2 秒
检测结果：PASS
```

测试失败时显示目标速率、实际速率和失败原因，并提供重试按钮。通过后必须提示拔出 U 盘，确认设备消失并完成去抖后，才进入下一项。

`/api/usb/state` 至少返回以下字段：

```json
{
  "mode": "pcba",
  "currentStep": 0,
  "totalSteps": 8,
  "usbVersion": "usb2.0",
  "port": "port1",
  "direction": "normal",
  "phase": "detected",
  "detected": true,
  "device": "/dev/sda",
  "speedMbps": 480,
  "result": "passed",
  "elapsedMs": 1230,
  "message": "USB2.0 port1 normal test passed",
  "nextAction": "拔出U盘"
}
```

页面以约 500ms 间隔轮询 `/api/usb/state`。颜色只能作为辅助，必须同时显示文字：蓝色表示当前项、绿色表示 PASS、红色表示 FAIL、灰色表示待测、橙色表示等待拔出。

USB 协议通常不能直接识别 Type-A 插头物理正反方向。端口和方向必须在页面上明确提示，并作为操作员确认信息保存。物理端口优先通过 sysfs USB 拓扑路径识别；如果硬件拓扑无法稳定映射端口1和端口2，不得使用 `/dev/sda`、`/dev/sdb` 作为物理端口判断依据。

每个步骤独立保存 USB 版本、端口、方向、设备节点、拓扑路径、实际速率、检测耗时、结果和错误信息。页面异常不能退出主通信服务，USB 服务异常也不能影响 ADB 测试协议。

应用启动顺序：

1. 启动 USB 检测线程；
2. 启动本地 HTTP 服务；
3. 创建或更新 `USB-Test.desktop`；
4. 设置快捷方式权限为可执行；
5. 设置文件属主为 `originflow:originflow`；
6. 操作员通过 HDMI 鼠标双击快捷方式；
7. `xdg-open` 调用板端默认浏览器打开 USB 预检页面。

快捷方式只负责打开网页，不负责 USB 检测。USB 插拔、速度判断、正反插步骤、结果文件写入仍由 `spacetest3576` 内部 USB 检测线程负责。

应用不得删除桌面上的其他文件。如果快捷方式已经存在，只更新自己的 URL 和显示内容。即使浏览器关闭，USB 检测线程仍可以继续运行；重新双击快捷方式后，网页通过 `/api/usb/state` 恢复当前状态。
