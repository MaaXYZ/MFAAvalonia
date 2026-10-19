# 作战策略 JSON 格式

格式版本为 1。客户端已实现编辑、校验、保存和重载；任务选择与 MAP Python 战斗端后续接入。

## 文件与标识

每份自定义策略保存到统一配置目录的 `strategies/`，当前对应安装目录下的 `config/strategies/<策略标识>.json`。使用 UTF-8、缩进与 snake_case。策略、阶段和转变标识均为 32 字符的 GUID N 格式。

名称仅用于显示，重命名和展示排序不改变引用。内置标识 `default` 由客户端生成，不占用户文件，不可覆盖删除。完整数据见[奥义依赖示例](../examples/combat-strategy-v1.json)，测试会实际解析该文件。

## 顶层与阶段

| 字段 | 含义 |
| --- | --- |
| `schema_version` | 固定为 `1`，其他版本拒绝加载 |
| `id` | 稳定标识，必须与文件名一致 |
| `name` | 非空用户名称 |
| `initial_stage_id` | 初始阶段标识 |
| `stages` | 至少一个阶段，数组顺序仅用于展示 |

阶段包含 `id`、`name`、`actions`、`frenzy`、`transitions`。初始阶段和转变目标必须存在，标识不重复。

## 行动与乱舞

`actions.priority` 是恰好包含 `ultimate` 和 `bomb` 的有序数组，连线隐含最后。普通操作与乱舞共用顺序。

- `bomb_levels`：允许的一二三级，可为空，不重复；执行端按可用等级从高到低选择。
- `ultimates`：包含三个位置的有序规则，每条为 `slot`、`enabled`、可选 `condition`。位置为 1、2、3，自身就绪内置检查，额外条件不满足则检查后面的位置。
- `normal_link.prioritize_colors`：颜色优先级开关。
- `normal_link.color_priority`：`red`、`blue`、`green`、`yellow` 的完整排列。
- `normal_link.long_link_threshold`：版本 1 固定为 `7`；有达标候选时仅在达标颜色中排序，否则在全部候选中排序。

`frenzy.allow_ultimates`、`allow_bombs` 为额外许可，开启后仍受本阶段勾选、顺序和条件限制。`reserved_color` 省略表示不留色，有值时仅一种颜色。

## 条件树

根节点为条件组。`type: group` 包含 `match: all/any` 与 `children`；`type: predicate` 为叶子。可递归嵌套，上限 16 层。奥义条件可省略，空根组表示无额外限制；转变组和嵌套组不可为空。

| `metric` | 数据含义 | 其他字段 |
| --- | --- | --- |
| `battle_normal_links` | 本场成功普通连线手数 | `comparison`、`value` |
| `stage_normal_links` | 本次进入阶段后的成功普通连线手数 | `comparison`、`value` |
| `enemy_hp`、`ally_hp` | 敌我血量数值 | `comparison`、`value` |
| `enemy_hp_percent`、`ally_hp_percent` | 敌我血量百分比，0 至 100 | `comparison`、`value` |
| `ultimate_ready` | 指定位置是否就绪 | `slots`、`slot_match`、`expected` |
| `ultimate_released` | 指定位置在当前阶段是否已释放 | `slots`、`slot_match`、`expected` |
| `frenzy_active` | 是否处于乱舞 | `expected` |

比较为 `greater_or_equal`、`less_or_equal`、`greater`、`less`、`equal`、`not_equal`。数值非负，手数为整数。

`slots` 至少一个位置且不重复，`slot_match` 为 `all/any`。先逐位置与 `expected` 比较，再合并；`any` 加 `expected: false` 表示至少一个位置未达到该状态。自身就绪不得写入本位置的奥义前置。

未知数据取反仍是未知，不能因此满足。血量百分比须有可靠的当前与最大血量。

## 转变与运行记录

`transitions` 每条为 `id`、`target_stage_id`、非空条件组 `condition`。按数组顺序检查，取第一条满足的转变，再检查目标阶段行动。任意现有阶段都可为目标。

执行端须防止没有新观察或行动的转变循环；每次执行一步并确认新状态后重判。只计成功普通连线，炸弹、奥义、乱舞连线不计。进入阶段时重置阶段手数和阶段已释放记录，本场手数保留。运行记录与编辑折叠状态不落盘。

## 保存与兼容

保存先校验，在同目录写临时文件再替换。失败保留原文件与草稿。坏 JSON、未知字段、缺少必备配置、无效条件和未来版本明确诊断，不悄悄覆盖。阶段的行动、乱舞、转变清单以及行动内的许可与排序必须显式提供，不能靠缺字段补成默认策略。

复制策略生成新标识并重映射内部阶段目标；复制单阶段生成新标识，其他目标保留，自身目标映射副本。

删除策略不查任务引用；后续任务找不到目标时用默认策略。损坏文件和不兼容版本另行报错。运行任务使用启动快照，之后保存下次启动生效。
