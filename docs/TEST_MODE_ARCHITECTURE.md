# PCBA 与整机测试模式架构

## 1. 目标

系统同时支持 PCBA 测试和整机测试。两种模式的大部分测试项相同，少数项目的执行方式不同，例如指示灯：PCBA 阶段通过 GPIO 和电压检测仪自动判定，整机阶段由操作员观察后手动判定。

目标是复用公共测试逻辑，通过配置或界面切换模式，不维护两套 Git 分支，并隔离保存两种模式的数据库记录。

## 2. 测试模式

定义两个模式：

```text
pcba
finished_product
```

配置示例：

```json
{
  "testMode": "pcba",
  "testModes": {
    "pcba": { "databaseName": "space-test-pcba.db" },
    "finished_product": { "databaseName": "space-test-finished-product.db" }
  }
}
```

测试开始后禁止切换模式，避免测试计划、数据库和当前记录不一致。

## 3. 模式切换

初期可通过配置文件切换：

```json
{ "testMode": "pcba" }
```

后续可增加界面按钮或启动参数。切换时应检查当前没有测试运行，然后重新加载测试计划、数据库和界面状态。

## 4. 测试项复用

测试项使用稳定的统一 ID，例如：

```text
wifi
bluetooth
ethernet
lcd
indicator_led
```

完全相同的测试项只实现一套函数，两个模式都调用同一个服务：

```csharp
await _wifiTestService.RunAsync(context);
```

不要为 PCBA 和整机复制两套 WiFi、蓝牙或网口测试代码。

## 5. 差异化策略

执行方式不同的项目，使用“模式 + 测试项 ID”选择不同处理器：

```csharp
handlers[(TestMode.Pcba, "indicator_led")] = new PcbaIndicatorLedTest();
handlers[(TestMode.FinishedProduct, "indicator_led")] = new ManualIndicatorLedTest();
handlers[(TestMode.Pcba, "wifi")] = new WifiTest();
handlers[(TestMode.FinishedProduct, "wifi")] = new WifiTest();
```

统一调用入口：

```csharp
var handler = handlers[(currentMode, testId)];
await handler.RunAsync(context);
```

推荐使用策略注册表或工厂，避免在主流程中堆积大量模式判断。

## 6. 测试计划

每种模式独立配置启用项和顺序：

```json
{
  "testModes": {
    "pcba": { "enabledTests": ["board_state", "indicator_led", "wifi", "ethernet"] },
    "finished_product": { "enabledTests": ["indicator_led", "wifi", "ethernet", "lcd"] }
  }
}
```

测试项 ID 应保持稳定，便于历史查询和统计。

### 6.1 按模式跳过测试项

`skippedTests` 不能继续只使用一份全局配置，否则某个测试项一旦被设置为跳过，所有模式都会跳过该项目。

必须按模式分别配置。例如：

```json
{
  "testModes": {
    "pcba": {
      "skippedTests": {}
    },
    "finished_product": {
      "skippedTests": {
        "indicator_led": "整机模式使用人工判定"
      }
    }
  }
}
```

行为要求：

- PCBA 模式中，`indicator_led` 正常执行并参与最终结果判定。
- 整机模式中，`indicator_led` 标记为 `SKIPPED`，不执行、不计入最终结果判定。
- 一个模式的跳过配置不能影响另一个模式。
- 切换模式时必须重新加载该模式的 `enabledTests` 和 `skippedTests`。

当前全局 `testPlan.skippedTests` 可以作为兼容配置，但模式配置优先级应高于全局配置。后续完成模式化改造后，应移除全局配置，避免产生歧义。

## 7. 数据库隔离

数据库文件名按模式区分：

```text
data/space-test-pcba.db
data/space-test-finished-product.db
```

启动时根据当前模式选择数据库路径。查询页面只读取当前模式数据库，不需要改变现有表结构。

## 8. PCBA 端边界

PCBA 端不需要为完全相同的测试项增加两套实现：

- 相同测试项继续调用现有 PCBA 协议。
- PCBA 专用项目继续由 PCBA 自动执行。
- 整机人工判定项目由上位机负责交互，可以不下发到底层。
- 只有整机测试需要新的底层硬件能力时，才增加 PCBA 协议或底层接口。

## 9. 推荐实施顺序

1. 增加 `TestMode` 枚举和配置读取。
2. 按模式加载测试计划。
3. 按模式生成数据库文件名。
4. 抽取相同测试项的公共服务。
5. 为指示灯等差异项目增加策略接口。
6. 增加模式切换界面或启动参数。
7. 分别验证两种模式的测试流程和数据库。

## 10. 验收标准

- 默认 PCBA 模式行为保持不变。
- 相同测试项没有重复实现。
- 差异项目能按模式选择正确的判定方式。
- 两种模式的记录写入不同数据库。
- 测试过程中不能切换模式。
- 整机模式不破坏现有 PCBA 协议和测试计划。
