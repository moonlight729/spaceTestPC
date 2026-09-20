# SpaceTestPC 上位机架构与测试流程设计

> 本文档描述 SpaceTestPC 上位机（`SpaceTestPC.App`）的**现有实现**。上位机与下位机之间的报文格式、测试项参数与判定口径另见 [`communication-protocol.md`](communication-protocol.md)；配置项解释见 [`configuration.md`](configuration.md)。

## 1. 技术栈与工程边界

| 项 | 现状 |
| --- | --- |
| 目标框架 | `net10.0-windows`，WPF（`UseWPF=true`，`Nullable` 与 `ImplicitUsings` 均开启） |
| 主要 NuGet | `Microsoft.Data.Sqlite 10.0.0`、`SSH.NET 2025.1.0`（升级走 SSH/SCP）、`System.IO.Ports 10.0.0`、`MinVer 6.0.0`（版本号） |
| 解决方案 | `SpaceTestPC.slnx`，含 `SpaceTestPC.App` 与 `tests/SpaceTestPC.App.Tests` |
| 不参与编译 | `Services/PressureStationRunner.cs`、`ViewModels/PressureStationSlotViewModel.cs`（压力测试原型，保留待开发） |
| MVVM | 自建 `AsyncRelayCommand` / `RelayCommand` 与 `ObservableObject`，无第三方框架、无 DI 容器 |

单元测试覆盖两处易回归点：`jxTvm.channels` 配置绑定（`PcbaTestPointConfigurationTests`）与 SQLite 落库 + CSV 导出（`SqliteDatabaseRepositoryTests`）。

## 2. 分层结构

```text
App.xaml.cs                 启动检查、全局异常处理、日志落盘
MainWindow.xaml(.cs)        组合根（手工装配所有服务）+ 扫码输入 / 弹窗 / 滚动等纯 UI 行为
  Services/
    ConfigurationService    appsettings 加载与模式叠加
    PcbaCommandClientFactory按连接模式返回客户端实例
    MockPcbaCommandClient   离线模拟
    AdbPcbaCommandClient    ADB forward / 直连 TCP 的真实客户端（含升级）
    PcbaDiscoveryService    以太网网卡选择 + 网段扫描发现设备
    Jk5506Service           JK5506 仪器（串口）
    JxTvmService            32 通道电压检测仪（串口）
    BluetoothBroadcasterService  蓝牙广播器（串口）
    ScannerService          SN 归一化
    SqliteDatabaseRepository会话/结果落库 + CSV 导出
    LogService              文件日志
  ViewModels/
    MainViewModel           会话编排、人工判定、仪器取数、查询/导出
    其余 ViewModel          45 宫格 / 16 宫格 / USB 步骤 / 方向键 / 测试选择 等面板模型
  Models/                   配置模型 + 协议模型
  Views/                    EnvironmentSettingsWindow、TestRecordDialog
```

## 3. 启动装配（组合根）

`App.xaml.cs`：

1. 注册三类全局异常钩子（Dispatcher / AppDomain / TaskScheduler），异常写入 `logs/space-test-pc.log`。
2. 启动自检：运行目录存在 `appsettings.json`、无同名进程重复运行、SQLite 数据库可打开（WAL + `busy_timeout=2000`）。任一失败弹出警告并返回 `-2` 退出。
3. 创建并显示 `MainWindow`。

`MainWindow` 构造函数手工装配（**改动服务请只在这里进行**）：

```text
ConfigurationService.Load(BaseDirectory/appsettings.json)
 ├─ PcbaDiscoveryService(pcbaConnection)
 ├─ AdbPcbaCommandClient(adb 客户端)                  // AdbForward 模式
 ├─ AdbPcbaCommandClient(useAdbForward:false + 发现)  // Tcp 模式
 └─ MockPcbaCommandClient(testPlan.mock)
MainViewModel(Scanner, 工厂, 电压/电池 StatusMonitor, SqliteDatabaseRepository,
              LogService, 配置, ManualTestInteractionService,
              Jk5506Service, JxTvmService, BluetoothBroadcasterService)
```

数据库命名在 `MainWindow` 中确定：`data/<testModes.<mode>.databaseName>`，缺省值 `finished_product → space-test-finished-product.db`、`pcba → space-test-pcba.db`。

## 4. 配置加载

`ConfigurationService`：

1. **文件选择**：先找 `appsettings.jsonc`，存在则优先使用，否则用 `appsettings.json`；都不存在则返回空配置（全部走代码默认值）。
2. **容错解析**：`ReadCommentHandling=Skip` + `AllowTrailingCommas=true`，因此配置文件允许注释与尾逗号。
3. **损坏兜底**：整份反序列化失败时，用正则单独提取 `upgrade` 块，保证“板端程序升级”能力仍可用。
4. **模式叠加**：若存在 `appsettings.<testMode>.json`，把其中的 `TestModes[<mode>]` 作为模板补齐到主配置；**主配置 `appsettings.json` 中显式写入的值优先**（`testOrder` / `skippedTests` / `disabledTests` / `snLength` / `snRuleDescription` / `testParameters` 均按缺失才补齐）。

## 5. 连接模式

`pcbaConnection.mode` → `PcbaConnectionMode`：

| 模式 | 客户端 | 连接路径 | 升级传输 |
| --- | --- | --- | --- |
| `tcp`（默认） | `AdbPcbaCommandClient(useAdbForward:false)` | 直连 `<host>:<port>`（端口默认 19001）；`host=auto` 时由 `PcbaDiscoveryService` 在选定网卡的网段内并发 ping + TCP 探测自动发现 | SSH/SCP（`upgrade.transport=auto`） |
| `adbForward` | `AdbPcbaCommandClient` | 先 `adb forward tcp:<port> tcp:<port>` 再连本机端口 | ADB 脚本 |
| `mock` | `MockPcbaCommandClient` | 离线模拟，`testPlan.mock` 控制节奏与注入失败项 | 不升级 |

`pcbaConnection.discovery`：默认 `selectedAdapter + subnet=auto`，可在环境配置窗口选择“已连接且有 IPv4 的物理以太网卡”；`maxParallel=32`、`connectTimeoutMs=500`。

## 6. 一次完整会话流程

```text
扫码（SN 归一化 → 长度/字符校验，长度按模式：PCBA 17、整机 20）
  → 板端程序升级检查（差异则倒计时询问，详见 application-upgrade.md）
  → sys.get_board_state：读板 ID / 板 SN / 历史项 / 累计计数
  → SN 一致性处理：板端为空 → sys.write_sn；不一致 → 终止（developer 模式可按 allowSnMismatchForDebug 放行）
  → sys.enter_test_mode
  → session.start：下发本轮 tests[]（含 skip 标记与每项 parameters）
  → 事件循环：按 test.report 更新项状态 / 操作提示 / 明细面板，必要时调用仪器或等待人工判定
  → 面板需要人工判定时：通过 ManualTestInteractionService 弹出通过/失败按钮
  → session.completed → 汇总 PASS/FAIL/ABORT
  → 按需 device shutdown（testLifecycle.poweroffAfterTest，默认关）
  → SQLite 落库 → 追加导出 CSV
```

补充行为：

- **通信中断**：`session.completed` 未收到的场景按中断处理，界面保留“通信中断”提示，不做误判。
- **单项目重测**：失败板进入重测生命周期（`rootSessionId` / `attemptNo` / `recordType` / `retestTestId` 记录溯源），可单测单项或结束失败板流程。
- **连续测试**：`testPlan.continuous.enabledByDefault` 控制是否默认连续。
- **开发者模式**：`operationMode=developer` 时才允许测试项勾选、跳过 SN 不一致等调试能力；生产模式始终执行完整当前计划。

## 7. 测试计划的构建规则

`MainViewModel.BuildActiveTestPlan` 是当前唯一权威实现：

1. 候选集合为代码内置全量清单 `AllTestPlan`：

   ```text
   board_state, hdmi, keys, lcd, wifi, bluetooth, battery_management,
   typec_fast_charge, tf, emmc, ddr, typec_camera, usb2, usb3,
   pcba_test_points, ethernet_led, indicator_led, pcba_indicator_led, fan
   ```

2. 过滤来源：
   - `enabledTests` / `testPlan.enabledTests`：仅**开发者模式**生效；非空时结果为“白名单 ∩ AllTestPlan”。
   - `disabledTests`：模式块（当前模式）优先，其次开发者模式下的 `testPlan.disabledTests`；**生产模式同样生效**，这是 PDO/PCBA 差异的关键开关。
   - `skippedTests`：`{ id: 原因 }`，仅开发者模式生效，下发 `skip=true` 但保留在序列中显示。
3. 强制裁剪：`finished_product` 模式自动移除 `pcba_test_points` 与 `pcba_indicator_led`。
4. 排序：`testModes.<mode>.testOrder` 定义先后顺序（未列出的项排在最后）。
5. 兜底：`board_state` 必须存在且位于首位。
6. 每项携带 `Parameters = testParameters[id]`，并按 `<mode>` 覆盖 `testPlan.testParameters` 的同名项。

界面序列首项是伪测试项 `application_upgrade`（显示名“设备程序升级”），它不参与 `session.start` 下发，只体现升级检查/升级结果。

## 8. 测试项清单

| id | 显示名 | 主要参与者 | 备注 |
| --- | --- | --- | --- |
| `board_state` | 板状态 | 上位机 + 板端 | 含 U-Boot/Kernel/Rootfs 版本校验（`board_state.*` 期望值） |
| `hdmi` | HDMI | 板端 | 人工介入为主 |
| `keys` | 六键 / 七键测试 | 板端 | 整机六键，PCBA 七键（含 MASKROM） |
| `lcd` | LCD | 板端 | `session.start` 下发 `lcdDisplay` 彩条参数 |
| `wifi` | WiFi | 板端 | SSID、RSSI、重试参数在 `testParameters.wifi` |
| `bluetooth` | 蓝牙 | 板端 + 广播器 | `bt.scan_target_name` + `BluetoothBroadcasterService` |
| `battery_management` | 板放电测试 | 上位机 + 板端 | 未放电时提示拔充电器后重采；PCBA 模式默认 `disabledTests` 关闭 |
| `typec_fast_charge` | 板快充 | 板端 | PCBA 模式默认关闭 |
| `tf` | TF 卡 | 板端 | 要求挂载成功 |
| `emmc` | EMMC | 板端 | 容量下限 115 GiB，写 64 MiB 测试文件 |
| `ddr` | DDR | 板端 | 256 MiB 压力 ×2 轮 |
| `typec_camera` | 相机测试&同步信号测试 | 板端 | 1080p30 取流，检查中断数与 PWM 同步脉冲 |
| `usb2` / `usb3` | USB2.0 / USB3.0 测试 | 板端 + 上位机 | 插入/拔出分步，界面呈现 `Usb2TestSteps` / `Usb3TestSteps` |
| `pcba_test_points` | PCBA测试点 | 上位机（32 通道）+ 板端 | 仅 PCBA 模式；见第 9 节 |
| `ethernet_led` | 网口灯 | 板端 + 人工判定 | 100M/1000M 灯色与速度确认 |
| `indicator_led` | 指示灯板 | 板端 | PCBA 模式默认关闭 |
| `pcba_indicator_led` | PCBA红蓝指示灯 | 板端 + 人工判定 | 仅 PCBA 模式 |
| `fan` | 风扇 | 板端 | PCBA 下走 `reserved_voltage_test`（保留的电平/转速检查） |

## 9. 仪器与阈值

- **32 通道电压**（`pcba_test_points`）：`JxTvmService` 读串口数据，判定阈值来自 `jxTvm.channels`（`minMv`/`maxMv`/`name`），未在配置中列出的通道回落到代码内置的 `测试表.csv` 默认表。**更新阈值只需改 `appsettings.json`，无需重新编译**；启动时日志会打印“配置覆盖 N 个通道、其余走内置表”。
- **JK5506**（`jk5506`）：串口 `COM5 @115200`，站号 1，超时 1 s。
- **蓝牙广播器**（`bluetoothBroadcaster`）：串口 `COM6`，广播名 `yctc_bt_01`。
- 仪器不可用时应先由环境配置窗口（串口列表实时读取）调整端口，而不是改代码。

## 10. 数据持久化

SQLite（`Microsoft.Data.Sqlite`，连接串 `Default Timeout=2`，保存时对 SQLITE_BUSY 有重试）：

```sql
test_sessions(session_id PK, sn, start_time, end_time, final_verdict, board_id,
              root_session_id, attempt_no, record_type, retest_test_id)
test_results(id PK, session_id, test_id, status, result_code, message, data_json)
csv_exports(session_id PK, exported_at)
```

- 数据库文件：`data/<testModes.<mode>.databaseName>`。
- **CSV 导出**：每次保存后扫描未导出会话，追加写入 `data/records/<SN 安全化>_<finished_product|pcba>.csv`；UTF-8、首次写表头，测试 id 与 message 会被翻译为中文。
- 新建列采用 `ALTER TABLE ADD COLUMN` 的幂等补偿，升级数据库不需要迁移脚本。

## 11. 界面结构

- **测试页**（默认）：扫码输入框（自动提交：Enter / 回车换行 / 500 ms 空闲）、测试序列列表（自动滚动跟随当前项）、45 宫格 / 16 宫格总览、测试说明与详细数据面板、日志区、底部状态栏（SN / 运行时间 / 当前时间 / 进度）。
- **查询页**（`IsQueryPage`）：SN / RESULT / BOARD ID / START TIME / END TIME / ITEMS 六列的数据网格，支持刷新与查看会话（`TestRecordDialog`）。
- **环境配置窗口** `EnvironmentSettingsWindow`：修改测试模式、运行模式（切到开发者需密码）、网卡与连接参数等，结果写回运行目录的 `appsettings.json`。
- **交互弹窗**：升级确认（倒计时确认/跳过）、快充/指示灯插充电器提示、放电准备提示、`test-snapshot` 等均由 ViewModel 事件驱动 MainWindow 弹出。

## 12. 日志与排障

| 现象 | 排查方向 |
| --- | --- |
| 连接不上设备 | 看日志里的 discovery 过程（选定网卡、网段、并发探测结果）；确认模式为 `tcp` 且 `discovery.enabled` |
| 会话卡在某项 | 日志搜索该项 `testId` 的 `running` 与最后一条事件；人工判定项检查是否等待操作员确认 |
| 落库失败 | 检查是否有第二个上位机进程；数据库启用 WAL，勿用只读挂载目录 |
| 升级不生效 | 见 [`application-upgrade.md`](application-upgrade.md)，确认 MD5 与传输方式（ADB / SSH-SCP） |
| 阈值不对 | 检查 `jxTvm.channels` 是否覆盖该通道；启动日志会给出覆盖计数 |

## 13. 已知待办（实现层面）

- `App.xaml.cs` 的启动自检仍固定探测 `data/box-test-records.db`，与按模式命名的实际数据库不一致，仅用于“目录可写”探测，建议后续改为与 `MainWindow` 一致的库名。
- 压力测试相关原型（`PressureStationRunner`、`PressureStationSlotViewModel`）已排除编译，相关方案见 `docs/plans/`。
