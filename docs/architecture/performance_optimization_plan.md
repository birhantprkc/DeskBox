# DeskBox 性能优化修复方案

> 创建时间：2026-07-09  
> 状态：待核对，未开始实施  
> 原则：全部使用 Windows / WinUI / .NET 原生 API，不引入任何第三方库  
> 核心约束：不降级现有视觉效果，不破坏现有设置项与自定义功能

---

## 一、问题总览

| 编号 | 问题 | 严重程度 | 涉及文件 |
|------|------|---------|---------|
| P0 | 动画出现/消失帧率低，高刷新率屏幕上明显卡顿 | 高 | `WidgetTrayAnimationController.cs` + 3 个窗口文件 |
| P1a | 鼠标滑过格子时轻微卡顿 | 中 | `WidgetWindow.xaml` / `WidgetWindow.xaml.cs` |
| P1b | Backdrop 刷新三重 Task.Delay 浪费 | 中 | `WidgetWindow.xaml.cs` / `QuickCaptureWidgetWindow.xaml.cs` |
| P1c | Music 可视化 33ms 定时器持续空转 | 中 | `MusicWidgetViewModel.cs` |
| P2a | ThemeService 颜色变化事件无 debounce | 低 | `ThemeService.cs` |
| P2b | IconHelper 缓存上限偏高 + 缺统一 DecodePixelWidth | 低 | `IconHelper.cs` |
| P2c | ApplyLegacyTitleActionButtonVisibility 无状态守卫 | 低 | `WidgetWindow.xaml.cs` |

---

## 二、P0：动画帧率优化

### 2.1 问题根因

文件：`src/DeskBox/Services/WidgetTrayAnimationController.cs`

当前动画使用 `DispatcherQueueTimer`（硬编码 16ms = 60fps 上限）驱动，每帧 Tick 中：

1. `_appWindow.Move(nextPosition)` — 移动窗口物理位置（Win32 `SetWindowPos`）
2. `visual.Opacity = currentOpacity` — 设置 Composition Visual 透明度
3. `visual.Scale = ...` — 设置 Composition Visual 缩放

问题：
- 16ms 硬编码 → 在 120Hz/144Hz/240Hz 屏幕上，动画只以 60fps 更新，帧间隔不匹配刷新率，肉眼可见卡顿
- `DispatcherQueueTimer` 不与 VSync 同步，即使在 60Hz 屏幕上帧间隔也不均匀
- `CompositionTarget.Rendering` 是原生 WinUI 事件，每帧由系统按显示器刷新率触发（VSync 同步）

### 2.2 修复方案

**将 `DispatcherQueueTimer` 替换为 `CompositionTarget.Rendering`，保留 `_appWindow.Move()` 窗口移动方式不变。**

这是用户明确要求的方案：视觉效果完全不变，只改驱动方式。

#### 改动范围

仅修改一个文件：`src/DeskBox/Services/WidgetTrayAnimationController.cs`

#### 改动内容

**删除：**
- `DispatcherQueueTimer? _timer` 字段
- `var timer = _dispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(16);` 创建逻辑
- `timer.Tick += ...` 回调注册
- `timer.Start()` / `timer.Stop()` 调用
- `Stop()` 方法中对 `_timer` 的处理

**新增：**
- `private bool _isAnimating` 字段
- `private double _animationDurationMs` 字段
- `private double _fromOffsetX, _toOffsetX` 等所有动画参数字段
- `private float _fromOpacity, _toOpacity` 等字段
- `private float _fromScale, _toScale` 字段
- `private bool _isShowing` 字段
- `private long _animationGeneration` 字段
- `private string _easingIntensity` 字段
- `private Action? _completedCallback` 字段
- `private Stopwatch _animationStopwatch` 字段（已有 `Stopwatch`，复用）

**`Animate()` 方法改为：**

```csharp
public void Animate(
    double fromOffsetX, double fromOffsetY,
    double toOffsetX, double toOffsetY,
    float fromOpacity, float toOpacity,
    float fromScale, float toScale,
    int durationMs,
    bool isShowing,
    long generation,
    string easingIntensity,
    Action completed)
{
    _log($"AnimateStart mode={(isShowing ? "show" : "hide")} gen={generation} durationMs={durationMs}");
    Stop();
    PrepareVisualState(fromOffsetX, fromOffsetY, fromOpacity, fromScale);

    if (durationMs <= 1)
    {
        CompleteAnimation(toOffsetX, toOffsetY, toOpacity, toScale, isShowing, generation, completed);
        return;
    }

    // 保存所有动画参数
    _fromOffsetX = fromOffsetX; _fromOffsetY = fromOffsetY;
    _toOffsetX = toOffsetX;     _toOffsetY = toOffsetY;
    _fromOpacity = fromOpacity;  _toOpacity = toOpacity;
    _fromScale = fromScale;      _toScale = toScale;
    _isShowing = isShowing;
    _animationGeneration = generation;
    _easingIntensity = easingIntensity;
    _completedCallback = completed;
    _animationDurationMs = durationMs;

    _animationStopwatch = Stopwatch.StartNew();
    _isAnimating = true;

    // 注册 CompositionTarget.Rendering（原生 WinUI 事件）
    CompositionTarget.Rendering -= OnRenderingFrame;
    CompositionTarget.Rendering += OnRenderingFrame;
}
```

**新增 `OnRenderingFrame` 方法：**

```csharp
private void OnRenderingFrame(object sender, object e)
{
    if (!_isAnimating || _animationGeneration != Generation)
    {
        StopRendering();
        return;
    }

    double rawProgress = Math.Clamp(
        _animationStopwatch.Elapsed.TotalMilliseconds / _animationDurationMs, 0.0, 1.0);
    double easedProgress = WidgetAnimationSettings.Ease(rawProgress, _easingIntensity, _isShowing);

    double currentOffsetX = Lerp(_fromOffsetX, _toOffsetX, easedProgress);
    double currentOffsetY = Lerp(_fromOffsetY, _toOffsetY, easedProgress);
    float currentOpacity = (float)Lerp(_fromOpacity, _toOpacity, easedProgress);
    float currentScale = (float)Lerp(_fromScale, _toScale, easedProgress);

    ApplyWindowOffset(currentOffsetX, currentOffsetY);
    ApplyOpacity(currentOpacity);
    ApplyScale(currentScale);

    if (rawProgress < 1.0)
    {
        return;
    }

    StopRendering();

    CompleteAnimation(
        _toOffsetX, _toOffsetY, _toOpacity, _toScale,
        _isShowing, _animationGeneration, _completedCallback);
}

private void StopRendering()
{
    _isAnimating = false;
    CompositionTarget.Rendering -= OnRenderingFrame;
}
```

**`Stop()` 方法改为：**

```csharp
public void Stop()
{
    StopRendering();

    if (_cachedRootVisual is { } visual)
    {
        StopVisualAnimations(visual);
    }
}
```

#### 冲突分析

逐项核对 `WidgetTrayAnimationController` 的所有调用者和功能交互：

| 功能 | 当前交互方式 | 改动后是否受影响 | 说明 |
|------|------------|----------------|------|
| **Generation 检查** | `timer.Tick` 内检查 `generation != Generation` | ✅ 不受影响 | `OnRenderingFrame` 内同样检查 `_animationGeneration != Generation` |
| **Stop() 中断** | `timer.Stop()` | ✅ 不受影响 | `StopRendering()` 取消事件注册，效果等同 |
| **PrepareVisualState** | 动画开始前调用 | ✅ 不受影响 | 仍在 `Animate()` 开头调用 |
| **PrepareHiddenState** | 透明度归零准备隐藏 | ✅ 不受影响 | 不经过 `Animate()`，直接设置 Visual |
| **CompleteAnimation** | 动画完成时调用 `completed` 回调 | ✅ 不受影响 | `OnRenderingFrame` 在 `rawProgress >= 1.0` 时调用 |
| **RestoreVisualState** | 完成回调内调用 | ✅ 不受影响 | 回调逻辑不变 |
| **RestoreWindowPosition** | 完成回调内调用 | ✅ 不受影响 | 同上 |
| **SetOffsetOverride** | `WidgetManager.ApplyTrayAnimationGroupOffset` 调用 | ✅ 不受影响 | 设置 `_offsetOverrideX/Y`，`ApplyWindowOffset` 内读取 |
| **ApplyWindowOffset** | 每帧调用 `_appWindow.Move()` | ✅ 不受影响 | 逻辑不变，只是驱动方式从 Timer → Rendering |
| **ApplyOpacity / ApplyScale** | 每帧设置 Visual 属性 | ✅ 不受影响 | 同上 |
| **GetCachedRootVisual** | 懒加载 Visual | ✅ 不受影响 | 不涉及驱动方式 |
| **GetOffscreenSlideOffsets** | 计算滑入偏移量 | ✅ 不受影响 | 纯计算，不涉及驱动 |
| **IsApplyingBounds** | `ApplyWindowOffset` 中设置 | ✅ 不受影响 | 逻辑不变 |

**三个窗口文件的调用路径核对：**

| 窗口 | 调用路径 | 是否受影响 |
|------|---------|-----------|
| `WidgetWindow` | `PrepareTrayShowAnimation` → `PlayTrayRaiseAnimation` → `_trayAnimation.Animate(...)` → 完成回调 | ✅ 不受影响 |
| `WidgetWindow` | `PrepareTrayHideAnimation` → `PlayTrayHideAnimation` → `_trayAnimation.Animate(...)` → 完成回调 | ✅ 不受影响 |
| `WidgetWindow` | `CompleteTrayShowWithoutAnimation` → `_trayAnimation.Stop()` + `RestoreVisualState` + `RestoreWindowPosition` | ✅ 不受影响 |
| `WidgetWindow` | `CompleteTrayHideAnimation` → 同上 | ✅ 不受影响 |
| `QuickCaptureWidgetWindow` | 同上 4 条路径 | ✅ 不受影响 |
| `ContentWidgetWindow` | 同上 4 条路径 | ✅ 不受影响 |

**ItemContainerTransitions 抑制/恢复交互：**

- `PrepareTrayShowAnimation` 中调用 `SuppressItemContainerTransitions()`
- 动画完成回调中调用 `QueueItemContainerTransitionRestore(animationGeneration)`
- 这两个调用都在窗口文件中，不在 `WidgetTrayAnimationController` 中
- 改动只影响 `WidgetTrayAnimationController` 内部驱动方式，不影响窗口文件中的调用顺序

**NativeBackdrop 抑制交互：**

- `SuppressNativeBackdropForTrayReveal()` 方法存在于 `WidgetWindow` 和 `QuickCaptureWidgetWindow` 中
- **经代码搜索确认：该方法从未被调用（死代码）**
- `_isNativeBackdropSuppressedForTrayReveal` 标志始终为 false
- 因此不存在冲突

**需添加的 using：**
- `using Microsoft.UI.Xaml.Media;`（`CompositionTarget` 所在命名空间）
- 项目中已有 `using Microsoft.UI.Xaml.Hosting;`（`ElementCompositionPreview`）

#### 需要测试的回归项

- [ ] F7 批量显示/隐藏所有小组件（含多显示器分组偏移）
- [ ] 托盘左键切换显示/隐藏
- [ ] 托盘右键菜单 → 各创建项
- [ ] 各动画效果：None / Fade / SlideLeft / SlideRight / SlideUp / SlideDown / ScaleFade / Zoom / SlideUpFade / SlideDownFade / SlideLeftFade / SlideRightFade / SlideFade / ScaleSlide
- [ ] 各动画速度：VeryFast(120ms) / Fast(220ms) / Standard(240ms) / Relaxed(520ms) / Slow(680ms)
- [ ] 各缓动强度：None / Light / Standard / Strong
- [ ] 动画过程中快速切换（show 中途 hide / hide 中途 show）→ Generation 检查
- [ ] 多窗口分组动画（同一显示器上的窗口从同一边滑入）
- [ ] 动画完成后窗口位置精确恢复
- [ ] 动画完成后 Acrylic 背景正常显示

---

## 三、P1a：鼠标滑过格子卡顿

### 3.1 问题根因

文件：`src/DeskBox/Views/WidgetWindow.xaml` + `WidgetWindow.xaml.cs`

1. 每个格子 Border（`Tag="InteractiveSurface"`）注册了 13 个事件，其中 `WidgetItemSurface_PointerMoved`（第 3782-3784 行）是**空方法**
2. `RootGrid_PointerEntered/Exited` 每次都调用 `ApplyLegacyTitleActionButtonVisibility`，该方法无条件 `Stop()` 两个 Storyboard 再重新设置属性
3. `ApplyWidgetItemSurfaceState` 每次都设置 `Background`、`BorderBrush`、`BorderThickness`、`Opacity` 四个依赖属性，即使值未变

### 3.2 修复方案

**路径 A（推荐）：纯代码清理 + 状态守卫，不改动 XAML 模板结构**

#### 改动 1：移除空的 PointerMoved 事件

文件：`WidgetWindow.xaml`（第 297 行和第 374 行）

```xml
<!-- 删除以下两行 -->
PointerMoved="WidgetItemSurface_PointerMoved"
```

文件：`WidgetWindow.xaml.cs`（第 3782-3784 行）

```csharp
// 删除整个空方法
private void WidgetItemSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
{
}
```

#### 改动 2：ApplyLegacyTitleActionButtonVisibility 加状态守卫

文件：`WidgetWindow.xaml.cs`（第 1657-1667 行）

```csharp
private bool _lastShowButtonsState;

private void ApplyLegacyTitleActionButtonVisibility(WidgetChromeMode chromeMode)
{
    bool showButtons = _settingsService.Settings.ShowHoverButtons &&
        chromeMode is not (WidgetChromeMode.Overlay or WidgetChromeMode.Hidden);

    // 状态守卫：如果目标状态与当前一致，跳过
    if (showButtons == _lastShowButtonsState)
    {
        return;
    }
    _lastShowButtonsState = showButtons;

    _showButtonsStoryboard?.Stop();
    _hideButtonsStoryboard?.Stop();

    RightActionButtons.Opacity = showButtons ? 1 : 0;
    RightActionButtons.IsHitTestVisible = showButtons;
    RightButtonsTransform.X = showButtons ? 0 : 12;
}
```

需要同步更新所有直接设置 `RightActionButtons.Opacity` 的地方，确保 `_lastShowButtonsState` 一致：
- `ApplyChromeMode` 内的 `ApplyActionButtonVisibility`（WidgetShell.xaml.cs 第 464-474 行）——这是 WidgetShell 自己的逻辑，不涉及 WidgetWindow 的 `_lastShowButtonsState`
- 需要在 `OnSettingsChanged` 中重置 `_lastShowButtonsState = false` 强制刷新一次

#### 改动 3：ApplyWidgetItemSurfaceState 加值守卫

文件：`WidgetWindow.xaml.cs`（第 1687-1719 行）

```csharp
private void ApplyWidgetItemSurfaceState(Border border, ItemSurfaceState state)
{
    if (ReferenceEquals(border, _folderDropTarget) && state != ItemSurfaceState.DropTarget)
    {
        state = ItemSurfaceState.DropTarget;
    }

    // 值守卫：如果状态未变，跳过
    if (border.GetValue(ItemSurfaceStateProperty) is ItemSurfaceState currentState &&
        currentState == state)
    {
        return;
    }
    border.SetValue(ItemSurfaceStateProperty, state);

    // ... 后续逻辑不变 ...
}
```

需新增一个附加属性用于缓存当前状态：
```csharp
private static readonly DependencyProperty ItemSurfaceStateProperty =
    DependencyProperty.RegisterAttached(
        "_ItemSurfaceState",
        typeof(ItemSurfaceState),
        typeof(WidgetWindow),
        new PropertyMetadata(ItemSurfaceState.Normal));
```

#### 冲突分析

| 功能 | 冲突风险 | 说明 |
|------|---------|------|
| **自定义 hover/pressed/selected 颜色** | ❌ 无冲突 | `EnsureItemSurfaceBrushCache` 仍正常工作，颜色计算逻辑不变 |
| **DropTarget 状态** | ❌ 无冲突 | `ReferenceEquals(border, _folderDropTarget)` 检查仍在方法开头 |
| **Cut 剪贴板半透明** | ❌ 无冲突 | `border.Opacity = isCut ? 0.58 : 1.0` 仍每次执行 |
| **选中态切换** | ❌ 无冲突 | `ApplySelectionState` → `UpdateInteractiveSurfaces` → `ApplyWidgetItemSurfaceState(border, Normal)` 会重置状态 |
| **主题切换** | ❌ 无冲突 | `OnSettingsChanged` → `UpdateInteractiveSurfaces` 需要强制刷新，需要在此处重置状态缓存 |
| **透明度设置** | ❌ 无冲突 | 透明度影响的是 Backdrop，不是格子表面 |
| **拖拽排序** | ❌ 无冲突 | `DragStarting` / `DropCompleted` 不经过 `ApplyWidgetItemSurfaceState` |
| **ShowHoverButtons 设置** | ⚠️ 需注意 | 设置变更时需重置 `_lastShowButtonsState`；在 `OnSettingsChanged` 中添加 `_lastShowButtonsState = false` |
| **ChromeMode（Overlay/Hidden）** | ❌ 无冲突 | `ApplyChromeMode` 在 WidgetShell 中独立管理，`ApplyLegacyTitleActionButtonVisibility` 只处理 File 窗口的旧标题栏按钮 |

#### 需要测试的回归项

- [ ] 鼠标在格子上 hover → 颜色变化正常
- [ ] 鼠标按下 → pressed 颜色正常
- [ ] Ctrl+Click 多选 → selected 颜色正常
- [ ] Ctrl+Click 取消选中 → 恢复 normal 颜色
- [ ] 拖文件到格子上 → DropTarget 边框高亮正常
- [ ] 剪贴板剪切文件 → 格子半透明（0.58）
- [ ] 主题切换（深色/浅色）→ 格子颜色刷新正常
- [ ] 强调色切换 → 格子颜色刷新正常
- [ ] 透明度滑块调整 → 格子表面不受影响
- [ ] 设置中开关 ShowHoverButtons → 标题栏按钮显示/隐藏正常
- [ ] Overlay 模式 → 按钮隐藏正常
- [ ] 文字大小调整 → 格子布局刷新正常
- [ ] 图标大小调整 → 格子布局刷新正常

---

## 四、P1b：Backdrop 刷新优化

### 4.1 问题根因

文件：`WidgetWindow.xaml.cs`（第 1265-1277 行）和 `QuickCaptureWidgetWindow.xaml.cs`（第 4260-4266 行）

每次 `QueueBackdropRefresh()` 创建 3 个 `Task.Delay` + 3 次可能的跨线程 `DispatcherQueue.TryEnqueue`。托盘批量显示 N 个窗口时，产生 3N 个延迟任务。

### 4.2 修复方案

**用单个 `DispatcherQueueTimer` 替代三重 `Task.Delay`。**

#### 改动范围

- `WidgetWindow.xaml.cs`
- `QuickCaptureWidgetWindow.xaml.cs`

（`ContentWidgetWindow` 不存在此问题——没有 `QueueBackdropRefresh` 的三重延迟）

#### 改动内容

**新增字段：**
```csharp
private DispatcherQueueTimer? _backdropRefreshTimer;
private int _backdropRefreshStep;
```

**替换 `QueueBackdropRefresh`：**
```csharp
private void QueueBackdropRefresh()
{
    if (!DispatcherQueue.HasThreadAccess)
    {
        DispatcherQueue.TryEnqueue(QueueBackdropRefresh);
        return;
    }

    long generation = ++_backdropRefreshGeneration;
    _backdropRefreshStep = 0;

    _backdropRefreshTimer ??= DispatcherQueue.CreateTimer();
    _backdropRefreshTimer.IsRepeating = true;
    _backdropRefreshTimer.Interval = TimeSpan.FromMilliseconds(80);
    _backdropRefreshTimer.Tick -= OnBackdropRefreshTick;
    _backdropRefreshTimer.Tick += OnBackdropRefreshTick;
    _backdropRefreshTimer.Start();
}

private void OnBackdropRefreshTick(DispatcherQueueTimer sender, object args)
{
    if (sender != _backdropRefreshTimer)
    {
        return;
    }

    RefreshBackdropIfCurrent(_backdropRefreshGeneration);

    _backdropRefreshStep++;
    // 3 个时间点：80ms, 320ms(80+240), 900ms(320+580)
    _backdropRefreshTimer.Interval = _backdropRefreshStep switch
    {
        1 => TimeSpan.FromMilliseconds(240), // 下次在 320ms
        2 => TimeSpan.FromMilliseconds(580), // 下次在 900ms
        _ => StopBackdropRefreshTimer(),      // 完成
    };
}

private TimeSpan StopBackdropRefreshTimer()
{
    _backdropRefreshTimer?.Stop();
    return TimeSpan.Zero;
}
```

**删除：**
- `RefreshBackdropAfterDelayAsync` 方法
- `_ = RefreshBackdropAfterDelayAsync(generation, 80/320/900)` 三行调用

**保留不变：**
- `RefreshBackdropIfCurrent` 方法（逻辑不变）
- `_backdropRefreshGeneration` 机制（不变）

#### 冲突分析

| 功能 | 冲突风险 | 说明 |
|------|---------|------|
| **窗口 Activate 时刷新** | ❌ 无冲突 | `Activate()` 调用 `QueueBackdropRefresh()`，逻辑不变 |
| **窗口 Hide 时跳过** | ❌ 无冲突 | `RefreshBackdropIfCurrent` 内检查 `!Visible \|\| _isHideAnimationRunning` |
| **设置变更时刷新** | ❌ 无冲突 | `OnSettingsChanged` 调用 `QueueBackdropRefresh()` |
| **Generation 机制** | ❌ 无冲突 | 多次调用 `QueueBackdropRefresh` 会 `++_backdropRefreshGeneration` 并重置 step |
| **动画完成回调** | ❌ 无冲突 | `CompleteTrayShowWithoutAnimation` 调用 `RestoreNativeBackdropAfterTrayReveal` → `ApplyBackdropPreference`，不走 `QueueBackdropRefresh` |

#### 需要测试的回归项

- [ ] 窗口首次显示 → Acrylic 背景在 ~80ms 后出现，~320ms 和 ~900ms 后稳定
- [ ] 批量显示 5+ 个窗口 → 无明显 UI 卡顿
- [ ] 透明度滑块调整 → 背景跟随刷新
- [ ] 主题切换 → 背景色切换正常
- [ ] 窗口隐藏时 → 不触发刷新

---

## 五、P1c：Music 可视化定时器优化

### 5.1 问题根因

文件：`src/DeskBox/ViewModels/MusicWidgetViewModel.cs`

- `_visualizerTimer`：140ms 间隔，播放时运行
- `_visualizerTransitionTimer`：33ms 间隔（≈30fps），过渡时运行

`UpdateVisualizerTimer()` 方法（第 1277-1294 行）已经有播放状态守卫：
- `IsPlaying && ShowRhythmBars` → 启动 `_visualizerTimer`，停止 `_visualizerTransitionTimer`
- `!IsPlaying && ShowRhythmBars` → 停止 `_visualizerTimer`，启动过渡到 idle
- `!ShowRhythmBars` → 两者都停止

**但存在一个缺口：`_visualizerTransitionTimer` 在过渡完成后可能未被停止。**

### 5.2 修复方案

**确保 `_visualizerTransitionTimer` 在过渡完成后停止。**

#### 改动范围

- `MusicWidgetViewModel.cs`

#### 需要先确认的点

需要读取 `VisualizerTransitionTimer_Tick` 方法，确认过渡完成时是否停止了 timer。如果已停止，则此项无需改动。

如果过渡完成后 timer 仍在运行（每 33ms 空转），则需要在过渡完成的条件分支中添加 `_visualizerTransitionTimer.Stop()`。

#### 冲突分析

| 功能 | 冲突风险 | 说明 |
|------|---------|------|
| **播放中可视化** | ❌ 无冲突 | `_visualizerTimer` 140ms 仍正常运行 |
| **暂停过渡动画** | ❌ 无冲突 | 过渡期间 `_visualizerTransitionTimer` 正常运行 |
| **ShowRhythmBars 开关** | ❌ 无冲突 | `UpdateVisualizerTimer` 逻辑不变 |
| **RhythmStyle 切换** | ❌ 无冲突 | 切换时调用 `UpdateVisualizerTimer`，逻辑不变 |
| **封面悬停动画** | ❌ 无冲突 | `EnableCoverHoverMotion` 不经过这两个 timer |

#### 需要测试的回归项

- [ ] 音乐播放 → 可视化柱状图正常跳动
- [ ] 音乐暂停 → 柱状图平滑过渡到 idle 状态后停止
- [ ] 关闭 ShowRhythmBars → 柱状图消失，定时器停止
- [ ] 切换 RhythmStyle → 过渡动画正常
- [ ] Music 组件关闭/重新打开 → 定时器正确启停

---

## 六、P2a：ThemeService debounce

### 6.1 问题根因

文件：`src/DeskBox/Services/ThemeService.cs`（第 25-30 行）

`UISettings.ColorValuesChanged` 在系统主题/强调色变化时可能连续触发多次，每次都 dispatch 到 UI 线程并 `ApplyToAllWindows`。

### 6.2 修复方案

**用 `DispatcherQueueTimer` 做 200ms debounce。**

#### 改动范围

- `ThemeService.cs`

#### 改动内容

```csharp
private DispatcherQueueTimer? _appearanceDebounceTimer;

public ThemeService(SettingsService settingsService)
{
    _settingsService = settingsService;
    _uiSettings.ColorValuesChanged += (_, _) =>
    {
        App.UiDispatcherQueue?.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
            ScheduleAppearanceRefresh);
    };
}

private void ScheduleAppearanceRefresh()
{
    _appearanceDebounceTimer ??= App.UiDispatcherQueue.CreateTimer();
    _appearanceDebounceTimer.IsRepeating = false;
    _appearanceDebounceTimer.Interval = TimeSpan.FromMilliseconds(200);
    _appearanceDebounceTimer.Tick -= OnAppearanceDebounceTick;
    _appearanceDebounceTimer.Tick += OnAppearanceDebounceTick;
    _appearanceDebounceTimer.Start();
}

private void OnAppearanceDebounceTick(DispatcherQueueTimer sender, object args)
{
    _appearanceDebounceTimer?.Stop();
    RefreshAppearance();
}
```

#### 冲突分析

| 功能 | 冲突风险 | 说明 |
|------|---------|------|
| **手动 SetTheme/SetAccentMode** | ❌ 无冲突 | 这些方法直接调用 `RefreshAppearance()`，不经过 debounce |
| **AppearanceChanged 事件** | ⚠️ 延迟 200ms | 托盘图标更新会延迟 200ms，可接受 |
| **TrackWindow 窗口列表** | ❌ 无冲突 | `_trackedWindows` 列表管理不受影响 |
| **Settings 变更触发** | ❌ 无冲突 | Settings 变更不经过 `ColorValuesChanged` |

#### 需要测试的回归项

- [ ] 系统深色/浅色切换 → 所有窗口主题跟随（延迟 ~200ms 可接受）
- [ ] 系统强调色切换 → 所有窗口颜色更新
- [ ] 应用内主题切换 → 立即生效（不走 debounce）
- [ ] 应用内强调色切换 → 立即生效

---

## 七、P2b：IconHelper 缓存优化

### 7.1 问题根因

文件：`src/DeskBox/Helpers/IconHelper.cs`

- `MaxCacheEntries = 500`，每个条目含 `byte[]`（PNG 图标数据）+ `BitmapImage`，大量图标时内存占用高
- `LoadBitmapImageAsync` 路径创建 `BitmapImage` 时未设置 `DecodePixelWidth`，图标以原始 32x32 加载，但 PNG 数据可能更大

### 7.2 修复方案

#### 改动 1：降低缓存上限

```csharp
private const int MaxCacheEntries = 250; // 从 500 降到 250
```

#### 改动 2：统一 DecodePixelWidth

在 `CreateBitmapImageOnUiThreadAsync` 方法中（第 336-347 行），给所有 `BitmapImage` 设置 `DecodePixelWidth`：

```csharp
private static async Task<BitmapImage?> CreateBitmapImageOnUiThreadAsync(byte[] bytes)
{
    var bmp = new BitmapImage
    {
        DecodePixelWidth = 48  // 图标显示尺寸通常 ≤48px，统一限制解码尺寸
    };
    // ... 后续不变 ...
}
```

注意：`CreateImageThumbnailAsync` 中已有 `DecodePixelWidth = 80`（第 128 行），保持不变。

#### 冲突分析

| 功能 | 冲突风险 | 说明 |
|------|---------|------|
| **图标显示尺寸设置** | ❌ 无冲突 | `ApplyWidgetItemLayout` 设置的是 `Image.Width/Height`（显示尺寸），`DecodePixelWidth` 是解码尺寸，两者独立 |
| **图片缩略图** | ❌ 无冲突 | 缩略图走独立路径，`DecodePixelWidth = 80` |
| **缓存淘汰** | ❌ 无冲突 | `EvictCachesIfNeeded` 逻辑不变，只是阈值降低 |
| **ClearIconCache** | ❌ 无冲突 | 清除逻辑不变 |
| **快捷方式图标** | ❌ 无冲突 | `.lnk` 解析走 `ResolveIconSource`，缓存 key 逻辑不变 |

#### 需要测试的回归项

- [ ] 文件格内 100+ 文件 → 图标正常显示
- [ ] 大图标模式 → 图标清晰（检查 48px 解码是否足够）
- [ ] 小图标模式 → 图标清晰
- [ ] 图片文件 → 缩略图正常
- [ ] 快捷方式 → 图标正常
- [ ] 添加/删除文件后 → 图标缓存正确更新
- [ ] 频繁切换文件夹 → 缓存淘汰正常，无内存泄漏

---

## 八、P2c：ApplyLegacyTitleActionButtonVisibility 状态守卫

此项已包含在 P1a 改动 2 中，不单独列出。

---

## 九、实施顺序与风险评估

| 顺序 | 优化项 | 改动文件数 | 风险 | 预计工作量 |
|------|--------|-----------|------|-----------|
| 1 | P0：动画 Rendering 驱动 | 1 | 中 | 2h |
| 2 | P1b：Backdrop 单 Timer | 2 | 低 | 1h |
| 3 | P1a：格子事件清理 + 守卫 | 2 | 低 | 1.5h |
| 4 | P1c：Music 定时器确认/修复 | 0-1 | 低 | 0.5h |
| 5 | P2a：ThemeService debounce | 1 | 低 | 0.5h |
| 6 | P2b：IconHelper 缓存 | 1 | 低 | 0.5h |

建议每完成一项后进行回归测试，确认无问题再进行下一项。

---

## 十、不优化的项目（经评估后排除）

| 项目 | 排除原因 |
|------|---------|
| 共享 DesktopAcrylicController | 收益有限（~2-3MB/窗口），且共享后需处理不同窗口的主题/透明度差异，复杂度高 |
| Z-order 批量 SetWindowPos | 当前调用频率不高（仅在窗口激活/失活时），优化收益小 |
| FolderWatcherService 复用 | 多窗口映射同一文件夹的场景极少，不值得增加共享逻辑的复杂度 |
| 引入 MemoryCache 替代 ConcurrentDictionary | 为单个缓存引入 `Microsoft.Extensions` 依赖链不划算 |
| 用 AnimationBuilder（CommunityToolkit）做动画 | 用户要求尽量原生，Composition API 本身就是原生的 |
| 恢复 GridViewItem/ListViewItem 的 VisualStateManager | 项目有大量自定义选中/拖拽/DropTarget 逻辑，迁移风险高，收益主要是减少事件注册而非解决卡顿 |
