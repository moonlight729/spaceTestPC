# SpaceTestPC

PCBA / 整机（成品）自动化测试上位机。上位机是一套 Windows WPF 应用（`net10.0-windows`），通过以太网 TCP 与 RK3576 下位机测试程序 `spacetest3576` 通信，完成扫码、板卡状态校验、板端程序升级、整轮测试执行、结果判定、数据落库与查询导出。

- 上位机工程：`SpaceTestPC.App`
- 单元测试：`tests/SpaceTestPC.App.Tests`
- 配套验证工具：`tools/`
- 板端源码镜像（与设备同步的工作副本）：`remote_stage/`

## 快速开始

```powershell
# 还原 / 构建
dotnet restore SpaceTestPC.slnx
dotnet build SpaceTestPC.slnx -c Debug

# 运行上位机（appsettings.json 位于运行目录）
dotnet run --project SpaceTestPC.App\SpaceTestPC.App.csproj

# 单元测试
dotnet test tests\SpaceTestPC.App.Tests\SpaceTestPC.App.Tests.csproj
```

运行期产物集中在运行目录下：

| 路径 | 说明 |
| --- | --- |
| `data/space-test-pcba.db` | PCBA 模式 SQLite 数据库（`testMode = pcba`） |
| `data/space-test-finished-product.db` | 整机模式 SQLite 数据库（`testMode = finished_product`） |
| `data/records/<SN>_<mode>.csv` | 每会话追加导出的测试明细 CSV |
| `logs/space-test-pc.log` | 上位机运行日志 |

## 目录结构

```text
SpaceTestPC.App/
  Services/      通信客户端、串口仪器、发现服务、SQLite 仓储、日志、配置加载
  ViewModels/    MainViewModel + 各面板 ViewModel（无第三方 MVVM 框架）
  Models/        配置模型（AppConfiguration 等）与协议模型
  Views/         EnvironmentSettingsWindow、TestRecordDialog 等窗口
tests/           xunit 单元测试（配置绑定、SQLite 仓储与 CSV 导出）
tools/           独立的板端/上位机联调验证小工具（不影响主工程编译）
remote_stage/    从设备同步下来的板端 C 源码工作副本（便于本地对照）
docs/            全部设计、对接、部署文档（见下表）
```

## 文档索引

| 文档 | 内容 | 适用对象 |
| --- | --- | --- |
| [`docs/architecture.md`](docs/architecture.md) | 上位机架构、启动装配、会话流程、测试计划规则、仪器集成、数据存储、UI 与排障 | 上位机开发 / 维护 |
| [`docs/communication-protocol.md`](docs/communication-protocol.md) | 上位机 ↔ 3576 下位机**唯一**通信协议口径（命令、事件、测试项参数、判定规则） | 上位机与板端联调 |
| [`docs/configuration.md`](docs/configuration.md) | `appsettings.json` 全量配置说明（连接、模式、测试项、阈值、仪器串口） | 现场部署 / 调试 |
| [`docs/deployment.md`](docs/deployment.md) | 上位机打包、发布与现场安装步骤 | 现场部署 |
| [`docs/application-upgrade.md`](docs/application-upgrade.md) | 板端程序 MD5 校验、自动升级流程与加固回滚 | 上位机 / 板端维护 |
| [`docs/board-deployment-hardening.md`](docs/board-deployment-hardening.md) | 板端服务自愈、看门狗与固化 | 板端维护 |
| [`docs/board-build-deploy.md`](docs/board-build-deploy.md) | 板端源码同步、SSH 登录、编译与上传发布 | 板端开发 |
| `docs/plans/aging-test-design.md` | 老化测试总体设计（**规划中，未实施**） | 方案评审 |
| `docs/plans/thermal-stress-test-design.md` | 高低温压力测试设计（**规划中，未实施**） | 方案评审 |
| `docs/plans/usb-pretest-design.md` | USB 预检单服务方案（上位机侧已落地，板端待联调） | 方案评审 / 联调 |

## 约定

- 上位机不使用 DI 容器，所有服务在 `MainWindow` 构造函数中手工装配；新增服务请在此处注入，并把日志回调挂到 `MainViewModel.AppendExternalLog`。
- 测试模式由 `appsettings.json` 的 `testMode` 决定，默认 `finished_product`；每个模式独立使用自己的数据库、SN 长度与测试项清单。
- 协议变更必须同步维护 `docs/communication-protocol.md`，不得再新增平行的协议说明文档。
