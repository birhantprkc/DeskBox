# DeskBox 架构升级与组件解耦路线图

本文档记录 DeskBox 后续重构的主线。核心原则是：**物理形态保留多窗口，逻辑控制统一收口**。
这条路线的目标不是重做一套 Windows 桌面，也不是尽快做一个全屏透明覆盖桌面，而是在不破坏原生桌面体验的前提下，把普通收纳格子、随记格子、未来 Todo/监控等组件接入同一套外壳、状态、动画和层级管理。

## 结论

当前方向总体正确，但需要明确一条关键边界：

**DeskBoxLayerWindow / 全屏 Widget Host 不应作为必经终点。**

对 DeskBox 这类贴近 Windows 桌面的产品来说，默认长期方案应优先是：
1. 保留每个格子的独立物理窗口，继续利用 Windows 原生窗口边界、拖拽、DPI 和桌面穿透能力。
2. 抽出统一的 `WidgetShell` 和 `WidgetContent`，让所有格子拥有一致外壳和业务插槽。
3. 抽出统一的 `WidgetSessionManager`，把 F7、托盘、菜单、拖拽、设置页激活、临时置顶、恢复到底层统一成一套会话状态。
4. 动画先做"多窗口同步 + 内部内容动画"的优化，不先走全屏透明覆盖桌面的重方案。

全屏 Host 可以作为远期实验方案，但只有在拖拽穿透、多层 DPI、桌面右键、从格子拖回桌面都验证稳定后，才考虑进入主线。

## 不做什么？

重构期间需要明确这些非目标，避免方向发散：

- 不替代 Windows Explorer 桌面。
- 不接管桌面空白区域右键菜单。
- 不让透明窗口覆盖桌面后抢占空白区域点击或拖放。
- 不把所有格子强行塞进一个跨显示器的大窗口。
- 不让单个窗口继续私自决定 `PushToBottom()`、临时置顶恢复、F7 显隐状态。

## 核心概念

### 1. WidgetSessionManager

`WidgetSessionManager` 是所有格子层级和显隐行为的单一真相源。窗口可以上报事件，但不应各自维护一套互相竞争的状态标志。

建议管理的状态：

- `DesktopResting`：桌面常驻。格子贴底展示，允许被其他应用自然覆盖。
- `RaisedSession`：F7 或托盘唤起。所有可见格子作为一个整体临时置顶，并使用同一时间线进入。
- `InteractionActive`：用户正在操作格子、菜单、输入框、拖拽或设置页。DeskBox 相关窗口保持同一层级。
- `ExternalInteraction`：用户切到外部应用或点击非 DeskBox 区域。管理器统一恢复到底层或退出临时置顶。
- `Hidden`：整体隐藏。由托盘或快捷键统一触发。

必须收口的行为：

- F7 和托盘左键走同一个入口。
- 普通格子和随记格子走同一套临时置顶和恢复逻辑。
- 菜单关闭后不允许单个窗口自己强制 `PushToBottom()`。
- 设置页、右键菜单、弹窗要纳入 DeskBox 窗口识别，避免激活设置页时格子压住设置。
- 如果格子已经显示但被外部窗口覆盖，再按 F7 应进入 `RaisedSession`，而不是被误判为"已经显示所以隐藏"。

### 2. WidgetShell

`WidgetShell` 是所有格子的统一外壳，建议做成 `UserControl`，由现有 `WidgetWindow` / `QuickCaptureWidgetWindow` 作为物理宿主加载。

Shell 负责：
- 标题栏、图标、更多、删除、通用按钮区域。
- 边框、圆角、阴影、背景、透明度、主题色、字体和图标尺寸。
- 外壳拖动、缩放、最小尺寸、命中区域。
- 右键菜单入口和菜单生命周期通知。
- 通用 ToolTip / Toast / 空态占位。

Shell 不负责：
- 具体业务逻辑（收纳、随记、Todo）。
- 具体列表/卡片布局。
- 业务数据持久化。
- 具体动画细节（只提供宿主钩子）。

### 3. WidgetContent

`WidgetContent` 是每种格子类型的业务内容区，建议做成 `UserControl`，通过 `IWidgetContent` 接口与 Shell / 物理解耦。

`IWidgetContent` 至少包括：
- `WidgetConfig Config { get; }`
- `void OnThemeChanged()`
- `Task RefreshAsync()`
- `void ActivateWidget()`

这样做的好处：
- 未来新增格子类型时，只需新建 `XxxWidgetContent : UserControl, IWidgetContent`。
- Shell 和 SessionManager 不再直接依赖具体 ViewModel 内部实现。
- 业务测试可以更集中在 Content 层。

### 4. 窗口层级和状态

物理窗口仍然由 `WidgetWindow` / `QuickCaptureWidgetWindow` 承担，它们只是宿主。

宿主职责：
- 窗口句柄、AppWindow、Win32 样式。
- DPI / 多显示器 / 位置恢复。
- 把 Shell 事件（拖动、缩放、关闭）转成真正窗口行为。
- 接收 SessionManager 指令：Show / Hide / Raise / Lower / Restore。

宿主不负责：
- 直接管理自己的 `IsAlwaysOnTop`、`PushToBottom()` 时序。
- 自己维护互相竞争的层标志。
- 自己决定 F7 行为。

## 重构分阶段计划

### 阶段 0：基线稳定化（不改行为）

目标：确保回滚后的当前版本可构建、可启动、可回归测试。

任务：
- 确认 `dotnet build -p:Platform=x64` 通过。
- 确认启动后普通格子、随记格子、F7、托盘菜单基本可用。
- 记录当前已知问题列表，作为后续验收对照。

产出：
- 一个可回归的稳定基线 commit。

### 阶段 1：抽取 WidgetShell（结构迁移，不改行为）

目标：把通用外壳从 `QuickCaptureWidgetWindow` 中抽出，但行为尽量保持一致。

任务：
- 创建 `src/DeskBox/Controls/WidgetShell.xaml(.cs)`，实现标题栏、图标、按钮区域、拖动/缩放命中区域。
- 创建 `src/DeskBox/Contracts/IWidgetContent.cs`。
- 创建 `src/DeskBox/Controls/QuickCaptureWidgetContent.xaml(.cs)`，把随记格子业务内容从窗口中剥离。
- 修改 `QuickCaptureWidgetWindow.xaml`，改为加载 `WidgetShell` + `QuickCaptureWidgetContent`。
- 修改 `QuickCaptureWidgetWindow.xaml.cs`，保留窗口句柄管理，把 UI 事件桥接到 Shell。
- 普通格子暂不迁移，保持原样。

验收：
- 随记格子外观、拖动、缩放、关闭、显示/隐藏与之前一致。
- 普通格子不受影响。
- F7 / 托盘行为不变。

### 阶段 2：迁移普通格子到 WidgetShell

目标：`WidgetWindow` 也使用 `WidgetShell` + `WidgetContent`。

任务：
- 创建 `src/DeskBox/Controls/OrganizerWidgetContent.xaml(.cs)`，把普通格子业务内容从 `WidgetWindow` 中剥离。
- 修改 `WidgetWindow.xaml`，改为加载 `WidgetShell` + `OrganizerWidgetContent`。
- 修改 `WidgetWindow.xaml.cs`，保留窗口句柄管理，把 UI 事件桥接到 Shell。

验收：
- 普通格子外观、拖动、缩放、关闭、显示/隐藏与之前一致。
- 随记格子不受影响。
- 两种格子共享同一套 Shell 外观。

### 阶段 3：抽取 WidgetSessionManager

目标：统一管理所有格子的层级和显隐状态。

任务：
- 创建 `src/DeskBox/Services/WidgetSessionManager.cs`。
- 定义 5 种会话状态：`DesktopResting` / `RaisedSession` / `InteractionActive` / `ExternalInteraction` / `Hidden`。
- 把 F7、托盘左键、设置页激活、外部应用切换、菜单关闭等事件统一接入。
- 窗口不再自己维护 `_isAtDesktopLayer`、`_keepRaisedUntilDeactivate`、`_restoreDesktopLayerWhenIdle` 等竞争标志，改为向 SessionManager 查询/响应。
- 保留旧逻辑的 fallback 开关，必要时可切回窗口自管模式。

验收：
- F7 唤起 / 再次 F7 / 托盘左键行为一致。
- 外部应用全屏时，F7 能整体临时置顶，点击外部应用后能整体恢复。
- 设置页激活时不会被格子压住。
- 随记和普通格子的菜单关闭后不会单独置底。

### 阶段 4：动画策略统一

目标：统一格子的显示/隐藏/置顶动画。

任务：
- 在 SessionManager 层协调动画时间线。
- Shell 提供动画钩子（Show / Hide / Raise / Lower）。
- 动画做成可切换策略，方便用户测试"旧动画/新动画"差异。
- 记录动画状态切换日志，便于排查。

验收：
- 格子从托盘唤起时有平滑动画。
- 格子隐藏到托盘时有平滑动画。
- 多个格子动画同步，不出现个别格子滞后。
- 动画开关关闭时能跳过动画直接显示/隐藏。

### 阶段 5：拖拽和多显示器优化

目标：确保拖拽和多显示器场景稳定。

任务：
- 拖拽逻辑集中在 Shell / SessionManager 层协调。
- 多显示器和不同缩放比例下，独立窗口保持清晰和稳定。
- 覆盖测试矩阵：
  - 单屏 100% 缩放
  - 单屏 150% 缩放
  - 双屏不同缩放比例
  - 格子跨屏边缘移动
  - 主屏切换后重启恢复位置

## 可观测性与回退

这类重构最容易出问题的地方，不是代码能不能跑，而是"跑起来后某个状态悄悄丢了"。因此要提前加上观测和回退手段。

建议补充：
- 记录每次会话状态切换的来源、目标状态、触发窗口、时间戳。
- 记录 `Show / Hide / Raise / Lower / Restore` 的调用链，方便排查谁在抢层级。
- 记录窗口创建、销毁、重建、刷新、拖拽接收、右键菜单打开关闭。
- 记录出现重复订阅、重复定时器、重复恢复逻辑时的警告。
- 对 Shell 迁移、Session 统管、动画策略分别保留开关，必要时可以快速回退。
- 对新旧动画、新旧层级策略做灰度切换，避免一次性替换所有用户。

## 回滚策略

这次重构会碰到窗口、UI、拖拽、输入、动画等核心链路，建议每个阶段都能单独提交和回滚。

建议节奏：
1. 每完成一个阶段，先打 Git checkpoint。
2. 组件化阶段尽量不改变行为，只改结构。
3. Session 阶段先保留旧逻辑的 fallback，再逐步切断窗口自管逻辑。
4. 动画阶段做成可切换策略，方便用户测试"旧动画/新动画"差异。

## 最小验收清单

重构后至少验证这些用例：

- 启动后所有格子恢复完整，没有漏格子。
- F7 唤起、再次 F7、托盘左键行为一致。
- 外部应用全屏时，F7 能整体临时置顶，点击外部应用后能整体恢复。
- 设置页激活时不会被格子压住。
- 随记和普通格子的菜单关闭后不会单独置底。
- 文件图标启动后正常加载，不需要右键刷新才能恢复。
- 文件/文件夹中文重命名正常。
- 随记单击复制、Toast 自动消失、双击预览正常。
- 所有格子主题、透明度、字号、图标大小同步设置。
- 拖拽在 Win11 23H2/24H2/25H2 至少覆盖一次真实用户路径。
