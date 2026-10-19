# 作战策略编辑器实施计划

目标：实现独立策略编辑页，可靠生成和重新加载 JSON。
架构：独立 DTO、校验器、文件服务、编辑会话，阶段和递归条件控件。
技术：.NET 10、Avalonia 11.3、SukiUI、System.Text.Json、现有 xUnit。
设计：docs/superpowers/specs/2026-10-19-combat-strategy-editor-design.md。

## 约束与检查重点

中文文案注释文档；不覆盖已有 RootView.axaml 修改，不提交推送；不接战斗执行。重点覆盖嵌套条件重载、稳定标识与复制重映射、默认只读、保存失败、损坏文件、未来版本、取消离开和无效阶段引用。

## 任务 1：数据契约与存储

- [x] 先编写 CombatStrategyStoreTests，覆盖嵌套条件重载、引用校验、非法百分比、未来版本、默认保护和保存失败。
- [x] 运行缺失实现测试，随后实现 Features/CombatStrategies 下 StrategyDocument.cs、StrategyJson.cs、StrategyValidator.cs、StrategyStore.cs。
- [x] 接口为 StrategyDefaults.Create、StrategyJson.Serialize/Deserialize/Clone、StrategyValidator.Validate、StrategyStore.List/Load/Save/Delete，存储接受显式目录，测试不写真实配置。
- [x] 运行测试并检查样例 JSON。

## 任务 2：编辑会话

- [x] 先写 CombatStrategyEditorTests：保存放弃取消、保存失败保留修改、阶段引用删除、复制独立与重映射。
- [x] 实现 StrategyEditorSession.cs，管理清单、当前草稿和未保存状态，所有编辑登记 MarkDirty，TryLeaveAsync 接受提示回调。
- [x] 运行测试。

## 任务 3：界面

- [x] 新建 Views/Pages/CombatStrategyView.axaml 及代码、Features/CombatStrategies/Controls 下阶段、条件和拖动控件。
- [x] 左列表、右滚动阶段卡片、末尾加号、底部固定工具栏；默认禁用编辑。
- [x] 阶段菜单、初始阶段、各类行动勾选排序、乱舞、跳转与递归条件全部接入真实草稿。
- [x] 输入校验就地显示，添加控件编辑测试并构建。

## 任务 4：接入与交付

- [x] 注册页面，接入 RootViewContent 侧边栏与选择切换、RootView 关闭时的修改保护。
- [x] 更新定制台账、JSON 格式示例与 MAP 总设计对应段落。
- [x] 全量测试、桌面构建、git diff --check，独立审查并修复关键问题。

## 验证

dotnet test MFAAvalonia.Tests/MFAAvalonia.Tests.csproj -c Debug -r win-x64 --no-restore --nologo

dotnet build MFAAvalonia.Desktop/MFAAvalonia.Desktop.csproj -c Debug -r win-x64 --no-restore --nologo