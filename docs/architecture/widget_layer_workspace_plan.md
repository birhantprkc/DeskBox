# DeskBox 层级模式与工作区架构计划

本文档记录 DeskBox 后续“窗口层级模式”和“工作区”功能的产品规则、技术边界和实施顺序。后续如果行为、数据结构或打包策略有调整，需要同步更新本文档，避免开发和发布时出现口径不一致。

## 目标

DeskBox 需要支持两类用户场景：

1. 用户希望保持当前动态层级体验：通过托盘或全局快捷键唤起格子时临时置顶，再隐藏或恢复到桌面层。
2. 用户希望格子真正固定在桌面上：即使点击 Windows 右下角“显示桌面”，DeskBox 也继续显示在桌面图标上方。

同时，DeskBox 需要支持“工作区”：

1. 用户可以把一组格子的布局保存为“办公区”“娱乐区”等工作区。
2. 用户可以通过托盘右键菜单或工作区独立快捷键切换工作区。
3. 工作区只保存格子布局和显示状态，不隔离 Todo 数据、随记内容、音乐播放状态。

## 已确认产品规则

### 层级模式

DeskBox 只做全局层级模式，不做每个格子独立层级。不同场景的格子组合由工作区解决。

第一版规划两个层级模式：

1. 动态层级
   - 默认模式。
   - 保持当前行为。
   - 托盘点击或全局快捷键：临时置顶展示，再次触发隐藏。
   - 交互结束后恢复到普通桌面层。

2. 桌面固定层
   - 实验模式。
   - 目标是真正挂到 Windows 桌面容器，并显示在桌面图标上方。
   - 点击“显示桌面”后，DeskBox 仍然显示。
   - 托盘点击或全局快捷键：只负责显示/隐藏，不做临时置顶。
   - 如果桌面容器挂载失败，需要自动回退到动态层级，不能让格子消失。

### 工作区

工作区保存：

1. 工作区名称。
2. 工作区独立快捷键。
3. 格子列表。
4. 每个格子的位置、大小、显示状态、标题、标题样式、锁定状态和内容绑定关系。

工作区不保存：

1. Todo 任务数据。
2. 随记记录内容。
3. 音乐播放状态。
4. 全局外观设置，例如主题、透明度、字号、图标模式。
5. 真实文件内容。

切换工作区时：

1. 自动保存当前工作区的位置和尺寸。
2. 如果当前 DeskBox 处于隐藏状态，工作区快捷键触发后应切换并显示目标工作区。
3. 删除工作区时，只删除布局配置，绝不删除文件、Todo、随记或音乐相关数据。
4. 同一个文件格子允许出现在多个工作区中，但引用同一份文件夹或文件列表，不复制文件内容。

## 建议数据结构

当前 `AppSettings.Widgets` 继续表示当前激活工作区的运行态格子列表。

后续新增：

```csharp
public sealed class WidgetWorkspace
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public bool HotkeyEnabled { get; set; }
    public int HotkeyModifiers { get; set; }
    public int HotkeyKey { get; set; }
    public List<WidgetConfig> Widgets { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

`AppSettings` 后续新增：

```csharp
public string WidgetLayerMode { get; set; } = "Dynamic";
public string ActiveWorkspaceId { get; set; } = string.Empty;
public List<WidgetWorkspace> WidgetWorkspaces { get; set; } = [];
```

## 技术实施路线

### 阶段一：层级服务收口

新增 `WidgetLayerService`，把现有分散在文件格子、随记格子、内容格子中的 Z-order 操作统一收口。

本阶段不改变行为，只把现有逻辑集中到服务层：

1. 清除 TopMost。
2. 移动到底部。
3. 临时置顶。
4. 恢复前台窗口。
5. 普通 BringToFront。

### 阶段二：桌面固定层原型

新增实验性的 `DesktopPinned` 实现。

技术方向：

1. 查找 `Progman` / `WorkerW` 下的 `SHELLDLL_DefView` 桌面图标视图。
2. 通过 `SetWindowLongPtr(GWLP_HWNDPARENT)` 将 DeskBox 顶层窗口的 owner 指向桌面图标视图。
3. 确保显示在桌面图标上方。
4. 不使用 `SetParent` 将 WinUI 窗口改成子窗口，避免破坏 WinUI 3 / Windows App SDK 的组合与输入模型。
5. Explorer 重启、多屏变化、DPI 变化后重新绑定 owner。
6. 绑定失败时回退动态层级。

重点验证：

1. 点击“显示桌面”后是否仍显示。
2. 文件拖放是否正常。
3. 右键菜单、日期弹窗、随记全屏编辑、Todo 编辑弹层是否正常。
4. 音乐音量滑杆等内部弹层是否正常。
5. 多屏、主屏切换、分辨率变化是否稳定。

### 阶段三：工作区核心服务

新增 `WorkspaceService`：

1. 创建工作区。
2. 保存当前布局到工作区。
3. 切换工作区。
4. 删除工作区。
5. 重命名工作区。
6. 切换前自动保存当前工作区。

切换工作区时，`WidgetManager` 负责关闭当前窗口、替换 `Settings.Widgets`，再恢复目标工作区窗口。

### 阶段四：设置页、托盘和快捷键

设置页新增完整工作区管理：

1. 工作区列表。
2. 新建、重命名、删除。
3. 保存当前布局。
4. 设置工作区快捷键。
5. 快捷键冲突提示。

托盘右键新增工作区菜单：

1. 当前工作区状态。
2. 切换到指定工作区。
3. 保存当前布局。
4. 打开工作区设置。

快捷键服务升级为多快捷键注册：

1. 全局快捷键：显示/隐藏当前工作区。
2. 工作区快捷键：切换并显示目标工作区。

## Store 版本注意事项

桌面固定层需要使用 Win32 窗口 owner 和 Z-order 能力。`SetWindowLongPtr(GWLP_HWNDPARENT)`、`SetWindowPos` 是公开 Win32 API，但 `Progman` / `WorkerW` / `SHELLDLL_DefView` 桌面容器行为不是微软承诺给普通业务应用的稳定 API。

因此建议：

1. Direct 版先开放桌面固定层实验功能。
2. Store 版初期默认隐藏或关闭该实验功能。
3. Store 包在开放前需要单独验证 WACK、安装、运行和审核风险。
4. 设置页需要给用户明确说明该模式会改变 DeskBox 与 Windows 桌面的贴合方式，并可随时关闭。

## 当前阶段状态

当前已完成阶段一：层级服务收口。

已新增 `WidgetLayerService`，并把文件格子、随记格子、内容格子的主要 Z-order 操作统一收口。

当前进入阶段二：桌面固定层原型。

已新增隐藏配置项：

```json
"widgetLayerMode": "Dynamic"
```

可手动改为：

```json
"widgetLayerMode": "DesktopPinned"
```

默认仍为 `Dynamic`，设置页暂不展示该选项。`DesktopPinned` 会尝试把格子顶层窗口的 owner 绑定到桌面图标视图，使格子停留在桌面图标上方，并把托盘/全局快捷键唤起行为改为“显示/隐藏”，不再临时置顶。

后续测试重点：

1. 手动启用 `DesktopPinned` 后，点击 Windows 右下角“显示桌面”，格子是否仍留在桌面图标上方。
2. 文件格子拖放、右键菜单、重命名是否正常。
3. 随记全屏编辑、最近图片预览、编辑弹窗是否正常。
4. Todo 日期弹窗、编辑弹窗是否正常。
5. 音乐音量滑杆、播放控制是否正常。
6. Explorer 重启、多显示器切换、DPI 变化后是否需要补重新挂载逻辑。
