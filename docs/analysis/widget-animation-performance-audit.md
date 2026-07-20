# DeskBox 格子动画性能排查报告

> 日期：2026-07-20  
> 状态：分析报告（待决策）

---

## 一、问题现象

无论选择哪种动画效果（Fade / Slide / ScaleFade / Zoom 等），格子（Widget）在出现/消失时都感觉「卡卡的」、帧数不够、有卡顿感。但部分电脑正常（如当前开发机），部分电脑卡顿（如酷睿 U9 处理器的电脑），都是 Win11。

---

## 二、动画实现架构分析

### 2.1 动画驱动方式：`CompositionTarget.Rendering` 逐帧手动插值

当前动画**没有使用 Composition API 的 KeyFrame 动画**（让 GPU 合成器独立驱动），而是使用了 `CompositionTarget.Rendering` 回调在 CPU 端逐帧手动计算插值：

```
WidgetTrayAnimationController.Animate()
  → 注册 CompositionTarget.Rendering += OnRenderingFrame
  → 每帧 CPU 端计算:
      - Stopwatch 计时 → rawProgress
      - 手动 Easing 函数 → easedProgress
      - 手动 Lerp 插值 offset/opacity/scale
      → ApplyWindowOffset()  ← 移动原生窗口
      → ApplyOpacity()       ← 设置 Visual.Opacity
      → ApplyScale()         ← 设置 Visual.Scale
```

**这是最核心的性能瓶颈。** `CompositionTarget.Rendering` 回调运行在 UI 线程上，每一帧都要：
1. CPU 计算
2. 调用 `_appWindow.Move()` 移动原生窗口（Win32 调用）
3. 设置 Visual 属性

如果 UI 线程有任何阻塞（GC、布局、绑定更新、文件系统事件），就会掉帧。

### 2.2 窗口移动方式：`AppWindow.Move()` 每帧调用

每帧调用 `_appWindow.Move(position)` 来移动原生窗口位置。这是一个同步 Win32 调用，涉及：
- DWM 合成器重新合成窗口
- 系统重新计算窗口区域
- 触发 `WM_WINDOWPOSCHANGING` / `WM_WINDOWPOSCHANGED` 消息

**每次 Move 都会触发 DWM 合成**，在低性能 GPU 上这是巨大的开销。

### 2.3 材质（Backdrop）在动画期间的状态

通过代码分析发现一个关键问题：

```csharp
// WidgetWindowBase.Backdrop.cs
protected bool IsBackdropSuppressedForTrayReveal =>
    _isNativeBackdropSuppressedForTrayReveal;
```

但在 `WidgetTrayAnimationController` 中，`SuppressNativeBackdropForTrayReveal()` 方法**从未被调用**（死代码，见 `performance_optimization_plan.md` 中的确认）。这意味着：

**动画期间，Acrylic/Mica Controller 仍然活跃**，每帧都在：
- 采样窗口后方内容
- 实时模糊（Acrylic）
- 随窗口移动重新采样壁纸（Mica）

这导致动画期间 GPU 负载极高，特别是在 Acrylic 模式下。

### 2.4 ItemContainerTransitions 的抑制/恢复

动画期间正确地抑制了 `ItemContainerTransitions`（`SuppressItemContainerTransitions`），这减少了 GridView/ListView 内部的过渡动画开销。**这部分处理是正确的。**

### 2.5 ContentReady 帧等待机制

```csharp
public void PlayAfterContentReady(Action action)
{
    _contentReadyRenderingHandler = OnContentReadyRenderingFrame;
    CompositionTarget.Rendering += _contentReadyRenderingHandler;
}
```

动画启动前等待 2 帧（`_contentReadyFrameCount < 2`）才开始播放。这增加了一感知延迟（~33ms@60Hz），但本身不会导致卡顿。

---

## 三、各功能模块对动画性能的影响

### 3.1 材质类型（影响：极高）

| 材质 | 动画期间开销 | 原因 |
|------|------------|------|
| **Acrylic (Thin/Base)** | **极高** | 每帧实时模糊窗口后方内容，窗口移动时模糊区域不断变化 |
| **Mica** | 中 | 采样壁纸一次，但窗口移动时可能重新采样 |
| **Solid** | 低 | 无模糊，纯色背景 |
| **Accent Blur (Fallback)** | 高 | Win32 `SetWindowCompositionAttribute` 每帧重新设置 |

**关键发现**：动画期间没有抑制 Backdrop Controller，Acrylic 的实时模糊与窗口移动叠加是最大的 GPU 负载来源。

### 3.2 胶囊模式 / Compact 模式（影响：低）

胶囊模式使用 XAML Storyboard 动画（`DoubleAnimation`），运行在独立于 `CompositionTarget.Rendering` 的路径上。胶囊的 `CompactTextHost`、`CompactActionHost` 的 Opacity 动画开销极小，**不影响主动画性能**。

### 3.3 文件堆栈 / Stacks（影响：中）

文件堆栈展开时使用 Composition API（`CreateScalarKeyFrameAnimation` / `CreateVector3KeyFrameAnimation`），这些是 GPU 合成器驱动的动画，性能良好。但如果动画期间堆栈正在进行展开/收起动画，会叠加 GPU 负载。

### 3.4 多窗口同时动画（影响：高）

F7 批量显示/隐藏时，所有 Widget 窗口同时执行 `CompositionTarget.Rendering` 驱动的动画。每个窗口每帧都调用 `AppWindow.Move()`，如果同时有 5-10 个窗口在移动，DWM 合成器负载成倍增加。

### 3.5 透明度滑块设置（影响：中）

`WidgetOpacity` 越低（用户看到的透明度越高），Acrylic 的 `TintOpacity` 和 `LuminosityOpacity` 越低，模糊效果越明显，GPU 开销越大。

---

## 四、硬件兼容性分析

### 4.1 为什么有的电脑卡、有的不卡？

当前开发机正常的可能原因：
- GPU 性能足够强（独显或高性能集显），能跟上每帧的 DWM 合成 + Acrylic 模糊
- DWM 合成器调度良好，`AppWindow.Move()` 能在 16ms 内完成

卡顿电脑（酷睿 U9）的可能原因：

#### 4.1.1 集成显卡性能差异

酷睿 U9（Core Ultra 9）的集成显卡（Intel Arc Graphics）虽然理论上性能不弱，但：

- **驱动版本差异**：Intel 集显驱动对 DWM 合成器的优化程度参差不齐，特别是窗口频繁移动时
- **GPU 调度器差异**：Win11 的硬件加速 GPU 调度（HAGS）在不同驱动版本上表现不同
- **共享内存带宽**：集显使用系统内存，带宽远低于独显显存，Acrylic 实时模糊对带宽敏感

#### 4.1.2 DWM 合成器帧率限制

Win11 22H2+ 引入了 DWM 帧率限制机制：
- 对于非聚焦窗口，DWM 可能降低合成帧率
- 对于频繁移动的窗口，某些驱动实现会触发「合成器降频」保护机制
- 这导致 `CompositionTarget.Rendering` 回调频率不稳定（可能从 60Hz 降到 30Hz 甚至更低）

#### 4.1.3 Acrylic Controller 的硬件加速路径

`DesktopAcrylicController` 在不同硬件上有不同的渲染路径：
- **高性能 GPU**：使用硬件加速的 Gaussian Blur，开销小
- **低性能/兼容性差的 GPU**：回退到软件模糊或低效路径，帧率骤降
- Intel 集显的驱动实现中，Acrylic 模糊可能没有走最优的 GPU 管线

#### 4.1.4 Windows 系统版本差异

即使都是 Win11，不同版本对 DWM 合成器的优化不同：
- Win11 23H2 vs 24H2 vs 25H2 的 DWM 实现有差异
- 24H2 引入了新的合成器调度逻辑，可能对频繁移动的窗口处理不同
- 系统设置中的「透明效果」和「动画效果」开关状态也影响行为

#### 4.1.5 显示器刷新率差异

- 60Hz 显示器：每帧 16.67ms，容错空间小
- 高刷新率显示器（120Hz/144Hz）：每帧 8.33ms/6.94ms，对 CPU 端逐帧计算的要求更高
- 如果 `CompositionTarget.Rendering` 在高刷新率下触发更频繁，但 `AppWindow.Move()` 的开销不变，反而更容易暴露卡顿

---

## 五、根因总结

### 主要根因（按影响排序）

| 排名 | 根因 | 影响程度 | 修复难度 |
|------|------|---------|---------|
| **1** | 动画使用 `CompositionTarget.Rendering` + `AppWindow.Move()` 逐帧 CPU 驱动，而非 Composition API 的 GPU 驱动动画 | 极高 | 高 |
| **2** | 动画期间未抑制 Acrylic/Mica Backdrop Controller，实时模糊与窗口移动叠加 | 极高 | 低 |
| **3** | `AppWindow.Move()` 每帧调用，触发 DWM 同步重合成 | 高 | 中 |
| **4** | 多窗口同时动画时，多个 `CompositionTarget.Rendering` 回调竞争 UI 线程 | 中 | 中 |
| **5** | 硬件/驱动对 DWM 频繁移动窗口 + Acrylic 模糊的优化不足 | 中 | 无法直接修复 |

### 为什么不影响所有动画效果？

所有动画效果都走同一个 `CompositionTarget.Rendering` 驱动路径，所以所有效果都会卡。即使是纯 Fade（只改 Opacity 不移窗口），由于 Backdrop Controller 仍然活跃，Acrylic 模糊区域变化仍会导致 GPU 负载。

---

## 六、优化方向

### 方向 A：动画期间抑制 Backdrop（推荐优先，改动最小，效果最显著）

**思路**：在动画开始前调用 `SuppressNativeBackdropForTrayReveal()`（已有但从未被调用的方法），切换到纯色背景；动画完成后调用 `RestoreNativeBackdropAfterTrayReveal()` 恢复材质。

**改动范围**：
- `WidgetWindow.TrayLifecycle.cs`：在 `PrepareTrayShowAnimation` 和 `PrepareTrayHideAnimation` 中调用 suppress
- `QuickCaptureWidgetWindow.xaml.cs`：同上
- `ContentWidgetWindow.TrayAnimations.cs`：同上
- 复活已有的 `SuppressNativeBackdropForTrayReveal` / `RestoreNativeBackdropAfterTrayReveal` 方法

**风险**：动画期间窗口变成纯色，视觉上有突变。可以通过渐变到 FallbackColor 来缓解。

**预期效果**：消除动画期间最大的 GPU 开销（实时模糊），预计可解决 80% 的卡顿问题。

**与功能冲突**：无。方法已存在只是未调用。

---

### 方向 B：用 Composition 隐式动画替代 `CompositionTarget.Rendering`（中等改动，效果最好）

**思路**：不再用 CPU 逐帧计算插值，改用 `Compositor.CreateScalarKeyFrameAnimation` / `CreateVector3KeyFrameAnimation` 让 GPU 合成器独立驱动动画。

**改动范围**：
- 重写 `WidgetTrayAnimationController.Animate()` 方法
- 位移动画改为 `Visual.StartAnimation("Offset", ...)` 而非 `AppWindow.Move()`
- 透明度动画改为 `Visual.StartAnimation("Opacity", ...)`
- 缩放动画改为 `Visual.StartAnimation("Scale", ...)`
- Easing 函数改用 `Compositor.CreateCubicBezierEasingFunction()`

**关键变化**：窗口位移不再通过 `AppWindow.Move()` 移动原生窗口，而是通过 `Visual.Offset` 移动 Visual 层。DWM 合成器只需合成一次，然后 GPU 独立插值。

**风险**：
- 需要处理窗口边界检测（Visual.Offset 不改变实际窗口位置，不影响 hit-testing）
- 多显示器场景下，Visual 偏移可能跨出工作区边界
- 需要保留 `AppWindow.Move()` 作为动画结束后的最终位置设置

**预期效果**：动画完全由 GPU 驱动，不受 UI 线程阻塞影响，在任何硬件上都能达到 60fps。

**与功能冲突**：如果使用 `Visual.Offset` 做位移，窗口实际位置不变，拖拽/点击的 hit-test 区域会偏移。需要在动画期间禁用交互（已有 `IsApplyingBounds` 标志可复用）。

---

### 方向 C：优化 `AppWindow.Move` 调用频率（低改动，中等效果）

**思路**：减少每帧 Move 的开销，而非完全消除。

**方案**：
- 在动画期间使用 `SetWindowPos` 的 `SWP_NOREDRAW` 标志抑制重绘
- 或使用 `DwmSetWindowAttribute(DWMWA_CLOAK)` 先 cloak 窗口，移动完毕再 uncloak（但已有 cloak 逻辑用于 Tray Show）
- 或降低 Move 频率：隔帧 Move（30fps），中间帧只改 Visual 属性

**风险**：隔帧 Move 会导致位移不流畅；`SWP_NOREDRAW` 可能导致残留画面。

**预期效果**：中等改善，可能不足以完全解决卡顿。

---

### 方向 D：多窗口动画批处理（中等改动，解决批量场景）

**思路**：F7 批量显示/隐藏时，不用每个窗口独立注册 `CompositionTarget.Rendering`，而是用一个全局 Rendering 回调统一驱动所有窗口。

**改动范围**：
- 新增 `WidgetAnimationBatchController` 管理多窗口动画
- 各窗口的 `WidgetTrayAnimationController` 注册到 batch controller
- 一个 `CompositionTarget.Rendering` 回调统一更新所有窗口

**预期效果**：减少 UI 线程回调数量，减少 GC 压力。

---

### 方向 E：硬件检测 + 自适应降级（防御性，解决兼容性）

**思路**：检测 GPU 性能等级，在低性能设备上自动降级。

**方案**：
- 检测是否为集成显卡（`AdapterInfo`）
- 检测 DWM 帧率（通过 `DwmGetCompositionTimingInfo`）
- 在低性能设备上：
  - 自动将动画效果降为 None 或 Fade（不做位移）
  - 动画期间强制切换到 Solid 材质
  - 降低动画持续时间

**预期效果**：在低性能设备上保证流畅，代价是牺牲视觉效果。

---

## 七、推荐优先级

| 优先级 | 方向 | 预期投入 | 预期效果 |
|--------|------|---------|---------|
| **P0** | A：动画期间抑制 Backdrop | 0.5 天 | 解决 80% 卡顿 |
| **P1** | B：Composition API 驱动动画 | 2-3 天 | 彻底解决，所有硬件流畅 |
| **P2** | E：硬件检测 + 自适应降级 | 1 天 | 兜底保障 |
| **P3** | D：多窗口批处理 | 1 天 | 改善 F7 批量场景 |
| **P4** | C：优化 Move 频率 | 0.5 天 | 中等改善 |

---

## 八、建议

**最小改动方案**：先做 A（抑制 Backdrop），这是已有代码的复活，改动极小但效果显著。如果 A 后仍有卡顿，再做 B。

**彻底解决方案**：A + B 组合。A 解决 GPU 负载，B 解决 CPU 逐帧驱动。两者结合可以从根本上消除动画卡顿，无论什么硬件配置。

**硬件兼容性**：U9 集显的卡顿很可能是 Acrylic 实时模糊 + 频繁窗口移动的叠加效应。方向 A 可以直接验证这个假设——如果抑制 Backdrop 后 U9 上不卡了，就确认了根因。
