# DeskBox 性能 / 多屏 / DPI 缩放审计报告

> 审计日期：2026-07-10
> 范围：`src/DeskBox` 全部窗口类型（WidgetWindow、QuickCaptureWidgetWindow、ContentWidgetWindow、SettingsWindow、OnboardingWindow）及相关服务

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

#### 问题 P-1：天气动画 Storyboard 永远运行，无可见性暂停（中等）

**位置**：`Controls/WidgetContents/WeatherWidgetContent.xaml.cs:80-183`

**描述**：雨/雪/雷电/晴天的 `Storyboard` 设置了 `RepeatBehavior = RepeatBehavior.Forever`。虽然 `Unloaded` 时会 `StopAllAnimations()`，但在以下场景动画仍然空转：

- 天气格子被 `HideWindow()` 隐藏（`Visible = false`）但 `Unloaded` 未触发时
- 用户切换到其他桌面 (Win+Tab) 但窗口未 Unload
- 格子被其他窗口完全遮挡

**影响**：每个天气格子 CPU 持续占用 ~1-3%（Storyboard 逐帧驱动 Canvas.Top）。

**建议**：
- 在 `WeatherWidgetViewModel` 中监听 `Visibility` 属性变化，不可见时暂停动画
- 或在 `WidgetWindow` 的 `Activated`/`Deactivated` 事件中转发可见性状态给天气内容控件

#### 问题 P-2：Backdrop 多阶段刷新定时器在频繁窗口操作时累积（低）

**位置**：`Views/WidgetWindow.xaml.cs:1389-1435`

**描述**：`QueueBackdropRefresh` 在窗口 Loaded、Activated、SettingsChanged、恢复后等处被频繁调用。虽然使用 generation token 去重，但 3 阶段刷新（80ms → 240ms → 580ms）在快速连续触发时仍会重复执行 `ApplyBackdropPreference()`，涉及 Mica/Acrylic controller 的属性设置。

**影响**：拖拽窗口时的额外 CPU 消耗，但不严重。

**建议**：在拖拽/调整大小期间（`_isDragging || _isResizing`）跳过 `QueueBackdropRefresh` 调用，操作结束后统一刷新一次。

#### 问题 P-3：`FolderWatcherService.ScheduleDispatch` 每次 `QueueChange` 创建新 Task（低）

**位置**：`Services/FolderWatcherService.cs:159-183`

**描述**：每次文件变更事件都会 `Task.Run(async () => { await Task.Delay(...); ... })`。虽然有防抖 token 机制取消前一个 Task，但被取消的 Task 仍占用线程池调度开销。高频文件操作（如编译输出、git checkout）下会创建大量短命 Task。

**影响**：在极端场景下（如大量文件复制到格子目录）可能产生短暂的线程池压力。

**建议**：考虑使用 `DispatcherQueueTimer` 替代 `Task.Delay` 方案，避免线程池调度。

#### 问题 P-4：`IconHelper` 缓存淘汰策略不够精确（低）

**位置**：`Helpers/IconHelper.cs:456-471`

**描述**：`EvictCachesIfNeeded` 使用 `s_iconBytesCache.Keys.Take(...)` 进行淘汰，但 `ConcurrentDictionary.Keys` 的枚举顺序不保证是插入顺序（实际上是哈希桶顺序），因此淘汰是随机的而非 LRU。可能导致频繁使用的图标被误淘汰。

**影响**：缓存命中率略低，但不影响正确性。

**建议**：如果缓存命中率成为瓶颈，考虑实现简单的 LRU 淘汰策略。当前 200 条上限对于桌面格子场景一般足够。

#### 问题 P-5：`_retiredWindows` 列表仅在 CloseAll 时清理（低）

**位置**：`Services/WidgetManager.cs:98, 2372`

**描述**：`_retiredWindows` 用于保存已关闭但可能还在做动画的窗口引用。这些引用只在 `CloseAllWidgetsAsync` 中清理。如果用户不执行全部关闭操作，这些窗口引用可能长时间保留。

**影响**：内存占用增加（每个 Window 对象约几十 KB），但不会造成泄漏因为最终会被 GC 回收。

**建议**：考虑在 `Closed` 事件的延迟回调中从 `_retiredWindows` 移除引用，或使用 `WeakReference`。

#### 问题 P-6：天气 `HttpClient` 每个 WeatherService 实例独立创建（低）

**位置**：`Services/WeatherService.cs:31-34`

**描述**：每个 `WeatherService` 实例都 `new HttpClient()`。虽然目前只有一个天气格子，但如果未来扩展多个天气格子实例，会创建多个 HttpClient，导致 socket 耗尽风险。

**建议**：使用 `IHttpClientFactory` 或共享静态 `HttpClient` 实例。

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

#### 问题 M-1：`RestoreBoundsAfterDisplayChange` 未在窗口隐藏时恢复（中等）

**位置**：`Views/WidgetWindow.xaml.cs:394-403`

```csharp
private bool RestoreBoundsAfterDisplayChange()
{
    bool restored = TryRestoreBoundsForCurrentTopology(allowHidden: false);
    // ...
}
```

**描述**：`allowHidden: false` 意味着如果窗口当前不可见，则跳过恢复。但显示配置变更时，隐藏的格子的配置坐标可能已失效。下次显示时 `TryRestoreBoundsForCurrentTopology` 才会重新计算，但此时如果原屏幕已断开，格子的 `config.X/Y` 可能指向不存在的屏幕。

**影响**：用户在关闭某显示器后重新显示隐藏的格子，可能出现在错误位置或回退到偏移位置。

**建议**：显示变更时也应以 `allowHidden: true` 更新隐藏窗口的 config 坐标（不实际移动窗口），确保下次显示时坐标正确。

#### 问题 M-2：`FindDesktopIconView` 的 `EnumWindows` 回调缺少错误处理（低）

**位置**：`Services/WidgetLayerService.cs:263-273`

**描述**：`EnumWindows` 遍历所有顶层窗口调用 `FindWindowEx` 查找 `SHELLDLL_DefView`。如果在此过程中某个窗口的消息处理阻塞或异常，可能导致枚举卡顿。当前没有超时或异常保护。

**影响**：极低概率场景，但可能导致桌面钉选模式初始化延迟。

**建议**：考虑添加 `SendMessageTimeout` 检查或限制枚举窗口数量上限。

#### 问题 M-3：跨屏拖拽时锚点更新使用 `DisplayArea.GetFromRect` 可能返回错误屏幕（低）

**位置**：`Views/WidgetWindow.xaml.cs:1170`

```csharp
var workArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
```

**描述**：`GetFromRect` 以矩形的左上角为基准查找屏幕。当窗口横跨两个显示器（拖拽过程中）时，左上角可能在 A 屏，但大部分内容在 B 屏。锚点和 config 更新会基于 A 屏计算，但用户意图可能是放在 B 屏。

**影响**：拖拽释放在屏幕边界处可能记录错误屏幕的锚点。但由于 `EnsureVisible` 的保护，不会导致窗口消失。

**建议**：拖拽结束时使用窗口中心点而非左上角来确定所属屏幕。

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

#### 问题 D-1：WidgetWindow 缺少 `WM_DPICHANGED` 的主动响应（中等）

**位置**：`Services/WidgetDisplayChangeWatcher.cs:61`

**描述**：`WidgetDisplayChangeWatcher` 监听 `WM_DPICHANGED` 并触发 `QueueRestore`，但恢复操作 `RestoreBoundsAfterDisplayChange` → `TryRestoreBoundsForCurrentTopology` → `ResolveBoundsCore` 中：

```csharp
int width = ResolvePhysicalWidth(config, scale);  // 使用新 DPI 重新计算物理宽度
int height = ResolvePhysicalHeight(config, scale);
```

这会重新缩放窗口尺寸。但 `AppWindow_Changed` 中的 `UpdateConfigBoundsFromPhysical` 在 `_isDragging || _isResizing` 为 false 时直接返回：

```csharp
if (_isApplyingBounds || _trayAnimation.IsApplyingBounds || (!_isDragging && !_isResizing))
{
    return;
}
```

**关键问题**：DPI 变化触发的 `MoveAndResize` 不在拖拽/调整大小期间，因此 `AppWindow_Changed` 会直接返回，不会更新 config。但 `TryRestoreBoundsForCurrentTopology` 内部通过 `ApplyWindowBounds`（设置 `_isApplyingBounds = true`）应用新 bounds，之后 `CapturePositionAnchor` + `UpdateConfigBoundsFromPhysical` 会正确更新 config。

**实际影响**：经仔细分析，逻辑是正确的——`ApplyWindowBounds` 内部的 `CapturePositionAnchor` 和 `UpdateConfigBoundsFromPhysical` 已经正确处理了 DPI 变化后的坐标更新。**但**如果 DPI 变化恰好发生在 `_isDragging || _isResizing` 期间（例如用户拖拽到另一个 DPI 不同的显示器），`AppWindow_Changed` 会正确更新 config，但 `WidgetDisplayChangeWatcher` 的 `QueueRestore` 会在拖拽结束后触发恢复，可能与用户的拖拽意图冲突。

**建议**：在 `_isDragging || _isResizing` 期间延迟 `WidgetDisplayChangeWatcher` 的恢复操作。

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

#### 问题 D-4：`UpdateAvailableSize` 传递的是 DIP 尺寸但天气动画使用硬编码物理像素偏移（低）

**位置**：
- `Controls/WidgetContents/WeatherWidgetContent.xaml.cs:90-91, 97, 117` — `Canvas.SetLeft(drop, 20 + i * 25)`
- `Controls/WidgetContents/WeatherWidgetContent.xaml.cs:93-96` — `From = -20, To = 400`

**描述**：天气动画的雨滴/雪花位置使用硬编码的 DIP 值（如 `To = 400`），但不同 DPI 下格子实际尺寸不同。在 150% DPI 下，一个 200x200 逻辑像素的格子实际渲染为 300x300 物理像素，但动画 `To = 400` 仍然以 DIP 为单位，XAML 会自动缩放，所以逻辑上是正确的。

**但**：动画的 `To = 400` 是固定值，在 Mini 布局（200x200 DIP）下意味着雨滴会跑到格子底部之外很远才重置，实际不影响视觉效果（因为 Canvas 裁剪），但可能产生不必要的渲染开销。

**建议**：根据 `UserControl_SizeChanged` 传入的实际高度动态设置动画的 `To` 值。

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

## 四、优先级排序汇总

| 优先级 | 编号 | 标题 | 影响范围 |
|--------|------|------|----------|
| 🔴 中 | P-1 | 天气动画在不可见时未暂停 | CPU 占用 |
| 🔴 中 | M-1 | 隐藏窗口在显示变更时未更新坐标 | 多屏 |
| 🔴 中 | D-1 | 拖拽中 DPI 变化与恢复逻辑冲突 | DPI |
| 🟡 低 | P-2 | Backdrop 刷新在拖拽时累积 | CPU 占用 |
| 🟡 低 | P-3 | FolderWatcher 频繁创建 Task | 线程池 |
| 🟡 低 | P-5 | retiredWindows 引用延迟清理 | 内存 |
| 🟡 低 | P-6 | WeatherService HttpClient 未共享 | Socket |
| 🟡 低 | M-3 | 跨屏拖拽锚点使用左上角而非中心 | 多屏 |
| 🟡 低 | M-4 | 三种窗口 RestoreBounds 不一致 | 多屏 |
| 🟡 低 | D-2 | DPI 获取逻辑重复 | 代码质量 |
| 🟡 低 | D-4 | 天气动画 To 值未自适应高度 | 渲染 |
| ⚪ 极低 | P-4 | 图标缓存淘汰非 LRU | 缓存命中 |
| ⚪ 极低 | M-2 | EnumWindows 缺少超时 | 稳定性 |
| ⚪ 极低 | M-5 | ResizeGuide 跨屏 snap | 交互 |

---

## 五、建议的优化路线图

### 第一阶段：快速修复（预计 2-3 小时）

1. **P-1**：在 `WeatherWidgetContent` 中监听 `IsVisible` 状态，不可见时 `StopAllAnimations()`
2. **P-2**：在 `QueueBackdropRefresh` 中添加 `_isDragging || _isResizing` 守卫
3. **M-1**：修改 `RestoreBoundsAfterDisplayChange` 为 `allowHidden: true`（仅更新 config，不移动窗口）

### 第二阶段：中期优化（预计 4-6 小时）

4. **D-1**：在 `WidgetDisplayChangeWatcher` 中添加拖拽/调整大小期间的延迟恢复
5. **M-3**：拖拽结束时使用窗口中心点确定所属屏幕
6. **D-2**：提取 `GetCurrentDpiScale` 到共享 helper
7. **M-4**：统一三种窗口的 bounds 恢复逻辑

### 第三阶段：长期优化（按需进行）

8. **P-3**：FolderWatcher 改用 DispatcherQueueTimer
9. **P-4**：IconHelper 实现 LRU 淘汰
10. **P-5**：retiredWindows 改用 WeakReference 或延迟清理
11. **P-6**：WeatherService 使用共享 HttpClient
12. **D-4**：天气动画 To 值自适应控件高度
