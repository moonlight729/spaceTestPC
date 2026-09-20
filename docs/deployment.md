# SpaceTestPC 上位机部署说明

本文用于将 `SpaceTestPC.App` 部署到 Windows 测试工位。部署前请先完成代码编译、单元测试与 Mock 流程验证。
架构与数据文件说明见 [`architecture.md`](architecture.md)，现场参数说明见 [`configuration.md`](configuration.md)。

## 1. 部署前准备

- Windows 10/11 x64 测试电脑。
- 有权限安装 USB/ADB/串口设备驱动。
- 已确认 PCBA、JK5506、JX-TVM、蓝牙模块等设备的连接方式和 COM 端口。
- 预留一个普通用户可写的安装目录，例如 `C:\SpaceTestPC`。不要部署到 `C:\Program Files`，以避免数据库和 CSV 无法写入。
- 备份现有生产数据（如有）：`data\` 下的 SQLite 数据库与 `data\records\`。
- 确认与测试板的以太网连通性（生产默认走 TCP + 自动发现，详见 [`configuration.md`](configuration.md) 的 `pcbaConnection`）；仅调试时才需要 ADB。

## 2. 发布程序

在项目根目录执行下列命令之一：

```powershell
# 自带运行时（无需在目标机安装 .NET）
dotnet publish SpaceTestPC.App\SpaceTestPC.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o .\artifacts\publish\win-x64

# 依赖目标机已安装 .NET 10 运行时（体积小）
dotnet publish SpaceTestPC.App\SpaceTestPC.App.csproj -c Release -o .\artifacts\publish\framework
```

发布完成后，将发布目录的**全部内容**复制到目标机器，例如：

```text
C:\SpaceTestPC\
```

请勿只复制 `SpaceTestPC.App.exe`；SQLite 原生库、配置文件和其他依赖必须与程序一并复制。
`appsettings.json` / `appsettings.<mode>.json` 由 csproj 以 `PreserveNewest` 规则复制，重新部署时**不会**覆盖目标机上较新的现场配置。

## 3. 目标目录与数据文件

程序首次运行后会在程序同级目录创建如下文件：

```text
C:\SpaceTestPC\
├─ SpaceTestPC.App.exe
├─ appsettings.json
├─ appsettings.pcba.json            （可选，按模式叠加）
├─ appsettings.finished_product.json（可选，按模式叠加）
├─ data\
│  ├─ space-test-pcba.db               # PCBA 模式 SQLite 测试记录
│  ├─ space-test-finished-product.db   # 整机模式 SQLite 测试记录
│  └─ records\
│     ├─ <SN>_pcba.csv                 # 每会话追加导出的明细 CSV
│     ├─ <SN>_finished_product.csv
│     └─ ...
└─ logs\
   └─ space-test-pc.log                # 运行日志
```

`data\` 下的数据库与 `records` 是生产测试记录，不应随意删除。升级程序时应先备份 `data` 目录，随后将其保留或复制回新程序目录。

## 4. 配置现场参数

用文本编辑器打开应用目录下的 `appsettings.json`，按现场实际情况检查：

- `testMode`：`pcba` 或 `finished_product`（决定数据库、SN 长度与启用清单）。
- `operationMode`：现场保持 `production`；`developer` 仅供调试（会开放测试项勾选等能力）。
- `pcbaConnection.mode`：现场使用 `tcp`（推荐，`host=auto` 由上位机自动发现设备）或 `adbForward`；`mock` 仅用于演示。
- `jk5506.portName`：JK5506 串口号。
- `jxTvm.portName` / `jxTvm.channels`：32 通道电压检测仪串口号与每通道阈值。
- `bluetoothBroadcaster.portName` / `broadcastName`：蓝牙广播模块串口与广播名。
- `upgrade.localBinaryPath` / `remoteBinaryPath`：板端程序本地包路径与设备目标路径。
- `testModes.<mode>.enabledTests` / `disabledTests`：当前工位启用/禁用的测试项。
- `testParameters`：Wi-Fi、蓝牙、充电、电压阈值等测试参数。

生产环境不要使用 Mock 连接模式。启动前请确认 `pcbaConnection` 与物理连接一致（以太网直连或 ADB）。

## 5. 设备与连接检查

1. 以太网直连时确认工位网卡 IP 与板端同网段，且 19001 端口可连通；调试用 ADB 时执行 `adb devices` 确认状态为 `device`。
2. 在 Windows 设备管理器确认各仪器 COM 端口与配置一致。
3. 确认 USB 线、供电、网线、测试治具和外设均已接好。
4. 使用一块已知正常的样板完成一次完整测试。
5. 检查测试结论、`data\` 下对应模式的 SQLite 数据库与对应 SN 的 CSV 是否生成。

## 6. 上线验收

部署完成后至少确认：

- 程序可正常启动，且无缺少 DLL 或运行时错误。
- 扫描 SN 后，测试流程可完整执行。
- 成功和失败结果都能正确显示并写入记录。
- 断开或锁定 CSV 文件时，测试记录仍会保存至 SQLite；CSV 会在后续保存时自动补导出。
- 操作账户能够写入 `data` 目录。

## 7. 日常维护与备份

- 每日或每班次备份整个 `data` 目录到网络盘或受控存储。
- CSV 可供人工查看；以 SQLite 数据库为完整记录来源。
- 升级前备份 `data`，升级后保留原数据目录。
- 如需迁移工位，将程序目录与 `data` 目录一并复制，并在新电脑重新确认驱动、COM 口和 ADB。

## 8. 回滚

若新版本出现问题：

1. 关闭测试程序。
2. 备份当前 `data` 目录。
3. 恢复上一版完整程序目录。
4. 将最新备份的 `data` 目录复制回程序目录。
5. 按“设备与连接检查”完成一次样板验证。

## 9. 发布前检查清单

- [ ] `dotnet build SpaceTestPC.slnx -c Release` 通过，无警告升级项遗漏。
- [ ] `dotnet test SpaceTestPC.slnx` 通过。
- [ ] Mock 流程验证通过。
- [ ] Release 发布目录已在干净环境启动验证。
- [ ] `appsettings.json`（含 `pcbaConnection`、串口、阈值）已按工位校准。
- [ ] `data` 目录具备写权限并纳入备份。
- [ ] 板端程序自动升级已验证（`upgrade.localBinaryPath` → `/vendor/originflow/bin/spacetest3576`，见 [`application-upgrade.md`](application-upgrade.md)）。
