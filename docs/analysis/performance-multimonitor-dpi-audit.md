# DeskBox 性能 / 多屏 / DPI 缩放审计报告

> 审计日期：2026-07-10
> 最后更新：2026-07-20
> 范围：`src/DeskBox` 全部窗口类型（WidgetWindow、QuickCaptureWidgetWindow、ContentWidgetWindow、SettingsWindow、OnboardingWindow）及相关服务

## 状态总览（2026-07-20 核对）

| 编号 | 标题 | 状态 | 说明 |
|------|------|------|------|
| P-1 | 天气动画在不可见时未暂停 | ✅ 已完成 | `IsWidgetActive` 驱动动画生命周期，通过 `OnWindowVisibilityChanged` 传递 |
| P-2 | Backdrop 刷新在拖拽时累积 | ✅ 已完成 | `QueueBackdropRefresh` 使用 generation token + `DispatcherQueueTimer` 分阶段刷新 |
| P-3 | FolderWatcher 频繁创建 Task | ✅ 已完成 | 已改为 `DispatcherQueueTimer` 防抖 |
| P-4 | 图标缓存淘汰非 LRU | ✅ 已完成 | 缩略图缓存已实现 LRU 链表；图标缓存上限降至 200 条半量淘汰 |
| P-5 | retiredWindows 引用延迟清理 | ✅ 已完成 | `_retiredWindows` 已移除 |
| P-6 | WeatherService HttpClient 未共享 | ✅ 已完成 | 已改为共享静态 `s_httpClient` |
| M-1 | 隐藏窗口在显示变更时未更新坐标 | ✅ 已完成 | `RestoreBoundsAfterDisplayChange` 中 `!Visible` 分支已更新 config 坐标 |
| M-2 | EnumWindows 缺少超时 | ⏳ 待定 | 极低概率场景，暂不处理 |
| M-3 | 跨屏拖拽锚点使用左上角而非中心 | ✅ 已完成 | 基类 `CapturePositionAnchor` 已使用中心点；本次补齐 `WidgetWindow` 遮蔽版本及三个 `UpdateConfigBoundsFromPhysical` 实现 |
| M-4 | 三种窗口 RestoreBounds 不一致 | ⏳ 待定 | 代码重复，暂不紧急 |
| M-5 | ResizeGuide 跨屏 snap | ⏳ 待定 | 可接受当前行为 |
| D-1 | 拖拽中 DPI 变化与恢复逻辑冲突 | ✅ 已完成 | `SuppressRestore()/ResumeRestore()` 机制已实现 |
| D-2 | DPI 获取逻辑重复 | ⏳ 待定 | 代码质量改进，暂不紧急 |
| D-3 | 拖拽使用物理像素增量 | ✅ 无问题 | 物理像素到物理像素运算自洽 |
| D-4 | 天气动画 To 值未自适应高度 | ✅ 已完成 | 已使用 `_animationFallHeight` 动态适配，`SizeChanged` 实时更新 |
| D-5 | GetDpiScale 通过屏幕中心点查询 | ✅ 无问题 | Windows 11 同一显示器 DPI 一致 |
| D-6 | ResizeGuide HighlightThickness 固定 DIP | ✅ 无问题 | XAML 自动 DPI 缩放 |

---

## 一、性能占用

### 1.1 已有的良好实践

| 项目 | 说明 | 文件 |
|------|------|------|
| 图标缓存 | `IconHelper` 使用 `ConcurrentDictionary` 缓存 icon bytes 和 BitmapImage，限制 200 条上限并半量淘汰 | `Helpers/IconHelper.cs` |
| 图标加载并发控制 | `SemaphoreSlim(4, 4)` 限制并发 shell icon 提取 | `Helpers/IconHelper.cs:17` |
| 图片缩略图降采样 | `decodePixelWidth: 80` 避免大图全分辨率加载到内存 | `Helpers/IconHelper.cs:125` |
| 文件夹监听防抖 | `FolderWatcherService` 250ms 防抖 + 64 条变更上限触发全量刷新 | `Services/FolderWatcherService.cs` |
| Backdrop 刷新分代 | `QueueBackdropRefresh` 使用 generation token 防止重复刷新 | `Views/WidgetWindow.xaml.cs:1389` |
| 性能日志可选 | `PerformanceLogger` 通过环境变量 `DESKBOX_PERF_LOG` 启用，非启用时零开销 | `Services/PerformanceLogger.cs` |
| 托盘动画帧渲染 | `CompositionTarget.Rendering` 事件驱动，动画结束后自动取消订阅 | `Services/WidgetTrayAnimationController.cs:255-311` |

### 1.2 发现的问题

#### 问题 P-1：天气动画 Storyboard 永远运行，无可见性暂停（中等） ✅ 已完成

**位置**：`Controls/WidgetContents/WeatherWidgetContent.xaml.cs:80-183`

**状态**：已通过 `IsWidgetActive` 属性驱动动画生命周期解决。`WeatherWidgetViewModel.OnWindowVisibilityChanged(bool visible)` 控制动画启停和刷新定时器。`WeatherWidgetContent.UpdateAnimations()` 检查 `_viewModel.IsWidgetActive` 决定是否运行 Storyboard。

---

<details>
<summary>原始描述（已归档）</summary>

**描述**：雨/雪/雷电/晴天的 `Storyboard` 设置了 `RepeatBehavior = RepeatBehavior.Forever`。虽然 `Unloaded` 时会 `StopAllAnimations()`，但在以下场景动画仍然空转：

- 天气格子被 `HideWindow()` 隐藏（`Visible = false`）但 `Unloaded` 未触发时
- 用户切换到其他桌面 (Win+Tab) 但窗口未 Unload
- 格子被其他窗口完全遮挡

**影响**：每个天气格子 CPU 持续占用 ~1-3%（Storyboard 逐帧驱动 Canvas.Top）。

**建议**：
- 在 `WeatherWidgetViewModel` 中监听 `Visibility` 属性变化，不可见时暂停动画
- 或在 `WidgetWindow` 的 `Activated`/`Deactivated` 事件中转发可见性状态给天气内容控件

</details>

#### 问题 P-2：Backdrop 多阶段刷新定时器在频繁窗口操作时累积（低） ✅ 已完成

**状态**：`QueueBackdropRefresh` 已使用 generation token 去重 + `DispatcherQueueTimer` 分阶段刷新（`WidgetWindowBase.Backdrop.cs:571-617`）。拖拽/调整大小期间通过 `SuppressRestore/ResumeRestore` 机制延迟恢复操作，结束后统一刷新。

#### 问题 P-3：`FolderWatcherService` 每次 `QueueChange` 创建新 Task（低） ✅ 已完成

**状态**：已改为 `DispatcherQueueTimer` 250ms 防抖（`FolderWatcherService.cs:46-49`）。不再创建短命 Task。

#### 问题 P-4：`IconHelper` 缓存淘汰策略不够精确（低） ✅ 已完成

**状态**：缩略图缓存已实现 LRU 链表淘汰（`IconHelper.cs:25-27, 207-215`）。图标缓存上限设为 200 条，半量淘汰。`decodePixelWidth` 已设置为 48（图标）/ 96（缩略图）。

#### 问题 P-5：`_retiredWindows` 列表仅在 CloseAll 时清理（低） ✅ 已完成

**状态**：`_retiredWindows` 已移除，不再存在。

#### 问题 P-6：天气 `HttpClient` 每个 WeatherService 实例独立创建（低） ✅ 已完成

**状态**：已改为共享静态 `s_httpClient`（`WeatherService.cs:23-26`）。`Dispose()` 不释放共享实例。

---

## 二、多屏幕适配

### 2.1 已有的良好实践

| 项目 | 说明 | 文件 |
|------|------|------|
| 显示变更监听 | `WidgetDisplayChangeWatcher` 监听 `WM_DISPLAYCHANGE`、`WM_SETTINGCHANGE`、`WM_DPICHANGED`、`TaskbarCreated` 消息，触发 bounds 恢复 | `Services/WidgetDisplayChangeWatcher.cs` |
| 屏幕标识匹配 | `WidgetPositioningService.SelectWorkAreaCore` 通过设备名 → monitor key → 尺寸匹配三级回退定位目标屏幕 | `Services/WidgetPositioningService.cs:308-353` |
| 主屏智能跟随 | `ShouldFollowPrimaryMonitor` 支持配置格子跟随主屏移动 | `Services/WidgetPositioningService.cs:375-397` |
| 锚点系统 | `CaptureAnchor` 记录格子相对于屏幕边缘的锚点和边距，支持 LeftTop/RightTop/LeftBottom/RightBottom 四角锚定 | `Services/WidgetPositioningService.cs:186-215` |
| 可见性保证 | `EnsureVisible` 确保格子在屏幕变更后不会完全跑到屏幕外 | `Services/WidgetPositioningService.cs:245-269` |
| 重试机制 | `WidgetDisplayChangeWatcher` 最多重试 8 次（每次 180ms 间隔），应对显示器热插拔的延迟生效 | `Services/WidgetDisplayChangeWatcher.cs:14, 143-147` |
| 工作区缓存失效 | `WidgetLayerService.InvalidateDesktopIconViewCache` 在显示变更时清除桌面图标层缓存 | `Services/WidgetLayerService.cs:107-113` |

### 2.2 发现的问题

#### 问题 M-1：`RestoreBoundsAfterDisplayChange` 未在窗口隐藏时恢复（中等） ✅ 已完成

**状态**：`RestoreBoundsAfterDisplayChange`（`WidgetWindowBase.Bounds.cs:221-241`）中 `!Visible` 分支已更新 config 坐标：调用 `ResolveBoundsForCurrentTopology` + `CaptureAnchor` + `UpdateConfigFromPhysicalBounds` + `SaveDebounced`，不实际移动窗口。

#### 问题 M-2：`FindDesktopIconView` 的 `EnumWindows` 回调缺少错误处理（低）

**位置**：`Services/WidgetLayerService.cs:263-273`

**描述**：`EnumWindows` 遍历所有顶层窗口调用 `FindWindowEx` 查找 `SHELLDLL_DefView`。如果在此过程中某个窗口的消息处理阻塞或异常，可能导致枚举卡顿。当前没有超时或异常保护。

**影响**：极低概率场景，但可能导致桌面钉选模式初始化延迟。

**建议**：考虑添加 `SendMessageTimeout` 检查或限制枚举窗口数量上限。

#### 问题 M-3：跨屏拖拽时锚点更新使用 `DisplayArea.GetFromRect` 可能返回错误屏幕（低） ✅ 已完成

**状态**：基类 `WidgetWindowBase.Bounds.cs` 的 `CapturePositionAnchor` 已使用窗口中心点 `DisplayArea.GetFromPoint`（line 173-176）。本次补齐了 `WidgetWindow.xaml.cs` 中遮蔽基类的 `private CapturePositionAnchor` 和三个窗口类型的 `UpdateConfigBoundsFromPhysical` 实现，统一使用中心点确定所属屏幕。

---

<details>
<summary>原始描述（已归档）</summary>

**位置**：`Views/WidgetWindow.xaml.cs:1170`

```csharp
var workArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
```

**描述**：`GetFromRect` 以矩形的左上角为基准查找屏幕。当窗口横跨两个显示器（拖拽过程中）时，左上角可能在 A 屏，但大部分内容在 B 屏。锚点和 config 更新会基于 A 屏计算，但用户意图可能是放在 B 屏。

**影响**：拖拽释放在屏幕边界处可能记录错误屏幕的锚点。但由于 `EnsureVisible` 的保护，不会导致窗口消失。

**建议**：拖拽结束时使用窗口中心点而非左上角来确定所属屏幕。

</details>

#### 问题 M-4：`QuickCaptureWidgetWindow` 和 `ContentWidgetWindow` 的 `RestoreBoundsAfterDisplayChange` 处理不一致（低）

**位置**：
- `Views/QuickCaptureWidgetWindow.xaml.cs:642`
- `Views/ContentWidgetWindow.xaml.cs:472`

**描述**：三个窗口类型的 `RestoreBoundsAfterDisplayChange` 实现略有差异。`WidgetWindow` 在恢复成功后调用 `RestoreDesktopLayer(force: true)`，而 `QuickCaptureWidgetWindow` 和 `ContentWidgetWindow` 的实现可能缺少此步骤（需确认）。代码重复也增加了维护不一致的风险。

**建议**：将公共的 bounds 恢复逻辑提取到 `WidgetPositioningService` 或共享基类中。

#### 问题 M-5：ResizeGuide 的 snap 检测使用物理像素坐标，跨屏时 work area 计算可能不准（低）

**位置**：`Services/ResizeGuideOverlayService.cs:276-285, 308-318`

**描述**：`FindSnapEdgeX/Y` 通过 `Win32Helper.GetWindowRect` 获取当前调整中窗口的位置，再通过 `TryGetMonitorWorkArea` 获取 work area。如果调整过程中窗口跨屏，获取到的 work area 可能不是用户期望的参考屏幕。

**影响**：跨屏调整大小时 snap-to-edge 行为可能不符合预期。

**建议**：可以接受当前行为，因为跨屏调整是临时状态，释放后会重新计算。

---

## 三、DPI 缩放

### 3.1 已有的良好实践

| 项目 | 说明 | 文件 |
|------|------|------|
| PerMonitorV2 声明 | `app.manifest` 声明 `PerMonitorV2` DPI 感知 | `app.manifest:25` |
| 逻辑/物理坐标分离 | `WidgetConfig` 使用逻辑像素存储宽高，`BoundsCoordinateVersion` 标记版本 | `Models/WidgetConfig.cs` |
| DPI 感知的坐标转换 | `WidgetPositioningService` 的 `ToPhysicalPixels`/`ToLogicalPixels` 统一处理缩放 | `Services/WidgetPositioningService.cs:455-467` |
| 多显示器 DPI 查询 | `GetDpiScaleForPoint` 通过 `MonitorFromPoint` + `GetDpiForMonitor` 获取指定位置的 DPI | `Helpers/Win32Helper.cs:875-903` |
| 坐标版本迁移 | `EnsureCurrentBoundsCoordinateVersion` 支持旧版本物理坐标到逻辑坐标的自动迁移 | `Services/WidgetPositioningService.cs:154-179` |
| RasterizationScale 使用 | WidgetWindow 在坐标转换时使用 `RootGrid.XamlRoot.RasterizationScale` | `Views/WidgetWindow.xaml.cs:2807, 4354` |

### 3.2 发现的问题

#### 问题 D-1：WidgetWindow 缺少 `WM_DPICHANGED` 的主动响应（中等） ✅ 已完成

**状态**：`WidgetDisplayChangeWatcher` 已实现 `SuppressRestore()/ResumeRestore()` 机制（`WidgetDisplayChangeWatcher.cs:46-67`）。拖拽/调整大小开始时调用 `SuppressRestore()`，结束时调用 `ResumeRestore()`，丢弃挂起的恢复操作（因为拖拽结束 handler 已更新 config）。

#### 问题 D-2：SettingsWindow 和 OnboardingWindow 各自重复实现 `GetCurrentDpiScale`（低）

**位置**：
- `Views/SettingsWindow.xaml.cs:2269-2278`
- `Views/OnboardingWindow.xaml.cs:2283-2293`

**描述**：两个窗口各自实现了几乎相同的 `GetCurrentDpiScale` 方法和 `GetDpiForWindow` P/Invoke 声明。逻辑一致：先尝试 `XamlRoot.RasterizationScale`，回退到 `GetDpiForWindow`。

**影响**：代码重复，维护成本增加。

**建议**：将此逻辑提取到 `Win32Helper` 或 `DpiHelper` 中。

#### 问题 D-3：WidgetWindow 拖拽/调整大小时使用物理像素增量，未做 DPI 感知处理（无问题确认）

**位置**：`Views/WidgetWindow.xaml.cs:4599-4631, 5778-5825`

**描述**：拖拽和调整大小使用 `Win32Helper.GetCursorPos`（物理像素）计算增量，然后直接加到 `_initialWindowPos`（也是物理像素）上。因为 `AppWindow.Position` 和 `AppWindow.Size` 返回的是物理像素，`ApplyWindowBounds` 接收的也是物理像素，所以整个链路是自洽的。

**结论**：**无问题**。物理像素到物理像素的运算不需要 DPI 转换。

#### 问题 D-4：`UpdateAvailableSize` 传递的是 DIP 尺寸但天气动画使用硬编码物理像素偏移（低） ✅ 已完成

**状态**：动画 `To` 值已改为使用 `_animationFallHeight` 动态适配（`WeatherWidgetContent.xaml.cs:173, 201`）。`Loaded` 时从 `ActualHeight` 初始化（line 83），`SizeChanged` 时实时更新所有动画的 `To` 值（line 331-343）。

#### 问题 D-5：`WidgetPositioningService.GetDpiScale` 通过屏幕中心点查询 DPI（低）

**位置**：`Services/WidgetPositioningService.cs:431-439`

```csharp
int x = workArea.X + Math.Max(0, workArea.Width / 2);
int y = workArea.Y + Math.Max(0, workArea.Height / 2);
double scale = dpiScaleProvider?.Invoke(workArea) ?? Win32Helper.GetDpiScaleForPoint(x, y);
```

**描述**：通过 work area 中心点查询 DPI。在 Windows 11 中同一显示器不可能有不同 DPI（这是 per-monitor 而非 per-display-area），所以中心点查询是正确的。

**结论**：**无问题**。但 `GetDpiScaleForPoint` 在 `MonitorFromPoint` 返回 `IntPtr.Zero` 时回退到 1.0，这在某些远程桌面场景下可能不准确。

#### 问题 D-6：`ResizeGuideOverlayService` 的 `HighlightThickness` 使用固定 DIP 值（无问题确认）

**位置**：`Services/ResizeGuideOverlayService.cs:21`

```csharp
private const int HighlightThickness = 12; // DIPs
```

**描述**：高亮宽度为 12 DIP。由于 XAML 会自动处理 DPI 缩放，这个值在不同 DPI 下会自动缩放为 12×scale 物理像素。

**结论**：**无问题**。DIP 值由 XAML 自动缩放。

---

## 四、优先级排序汇总（2026-07-20 更新）

| 编号 | 标题 | 原优先级 | 状态 |
|------|------|----------|------|
| P-1 | 天气动画在不可见时未暂停 | 🔴 中 | ✅ 已完成 |
| M-1 | 隐藏窗口在显示变更时未更新坐标 | 🔴 中 | ✅ 已完成 |
| D-1 | 拖拽中 DPI 变化与恢复逻辑冲突 | 🔴 中 | ✅ 已完成 |
| P-2 | Backdrop 刷新在拖拽时累积 | 🟡 低 | ✅ 已完成 |
| P-3 | FolderWatcher 频繁创建 Task | 🟡 低 | ✅ 已完成 |
| P-5 | retiredWindows 引用延迟清理 | 🟡 低 | ✅ 已完成 |
| P-6 | WeatherService HttpClient 未共享 | 🟡 低 | ✅ 已完成 |
| M-3 | 跨屏拖拽锚点使用左上角而非中心 | 🟡 低 | ✅ 已完成 |
| D-4 | 天气动画 To 值未自适应高度 | 🟡 低 | ✅ 已完成 |
| P-4 | 图标缓存淘汰非 LRU | ⚪ 极低 | ✅ 已完成 |
| M-4 | 三种窗口 RestoreBounds 不一致 | 🟡 低 | ⏳ 待定 |
| D-2 | DPI 获取逻辑重复 | 🟡 低 | ⏳ 待定 |
| M-2 | EnumWindows 缺少超时 | ⚪ 极低 | ⏳ 待定 |
| M-5 | ResizeGuide 跨屏 snap | ⚪ 极低 | ⏳ 待定 |

---

## 五、后续优化路线图（2026-07-20 更新）

### 已完成项（第一至第三阶段全部完成）

所有 P-1 ～ P-6、M-1、M-3、D-1、D-4 均已实施。

### 待定项（按需进行）

1. **D-2**：提取 `GetCurrentDpiScale` 到共享 `Win32Helper` 或 `DpiHelper`（纯代码质量改进）
2. **M-4**：统一三种窗口的 `RestoreBoundsAfterDisplayChange` 逻辑（减少代码重复）
3. **M-2**：`EnumWindows` 添加超时保护（极低概率场景）
4. **M-5**：ResizeGuide 跨屏 snap 优化（可接受当前行为）
