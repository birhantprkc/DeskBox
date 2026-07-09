# DeskBox 原生材质与表面样式可行性分析

> 状态：分析文档（待确认）  
> 日期：2026-07-09  
> 躺平原则：优先使用 Windows/WinUI/.NET 原生能力，不引入第三方库

---

## 一、当前实现现状

### 1.1 格子窗口的"多层"结构

当前 Widget 窗口的视觉效果由以下层次叠加而成：

| 层次 | 实现方式 | 文件位置 | 说明 |
|------|----------|----------|------|
| **L0 系统背板** | Win32 `DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE)` | `Win32Helper.cs:553-557` | 设为 `DWMSBT_TRANSIENTWINDOW`（Acrylic-like），仅在 Controller 失败时作为 fallback |
| **L1 Win32 Accent 模糊** | `SetWindowCompositionAttribute(WCA_ACCENT_POLICY)` + `ACCENT_ENABLE_ACRYLICBLURBEHIND` | `Win32Helper.cs:587-640` | 作为 Acrylic Controller 的 fallback 路径，通过 GradientColor 控制透明度 |
| **L2 DesktopAcrylicController** | `Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController` (Kind=Thin) | `WidgetWindow.xaml.cs:1354-1378` | 主路径，通过 TintColor/TintOpacity/LuminosityOpacity 三参数控制 |
| **L3 BackgroundPlate 表面色** | `Border.Background = SolidColorBrush(BuildFrostedSurfaceColor)` | `WidgetWindow.xaml.cs:1460` | 在 Acrylic 之上再叠一层半透明"磨砂色" |
| **L4 边框** | `Border.BorderBrush = SolidColorBrush(borderColor)` | `WidgetWindow.xaml.cs:1461` | 自定义 alpha=0x18 的细边框 |
| **L5 DWM 窗口边框色** | `DwmSetWindowAttribute(DWMWA_BORDER_COLOR, 0xFFFFFFFE)` | `WidgetWindow.xaml.cs:255-256` | 硬编码为"无边框"(0xFFFFFFFE) |
| **L6 窗口圆角** | `DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE)` | `Win32Helper.cs:547` | 用户可选 Default/Square/Small/Round |
| **L7 分隔线** | `HeaderDivider.Background = SolidColorBrush(dividerColor)` | `WidgetWindow.xaml.cs:1462` | 标题栏与内容之间的细线 |
| **L8 格子内 item 边框** | `BorderThickness="0.8"` + `CardStrokeColorDefaultBrush` | `WidgetShell.xaml:150-152` | 每个文件格子的内边框 |
| **阴影** | **无** | — | 除了 MusicWidget 的 AlbumArtShadow 外，格子主窗口没有任何阴影 |

### 1.2 当前用户可配置项

| 配置项 | 设置路径 | 类型 | 当前范围 |
|--------|----------|------|----------|
| `WidgetOpacity` | 设置 > 外观 > 不透明度 | Slider 0.0-1.0 | 控制 L1/L2 的 TintOpacity + L3 的 materialOpacity |
| `WidgetCornerPreference` | 设置 > 外观 > 圆角 | ComboBox | Default/Square/Small/Round → DWMWA_WINDOW_CORNER_PREFERENCE |
| `WidgetTitleIconMode` | 设置 > 外观 > 标题图标 | ComboBox | FilledMono/LineMono/Color/Hidden/TextLabel |
| 主题色 (Accent) | 系统设置 | 跟随系统 | 影响 tintColor 和 surfaceColor 的混色 |
| 深色/浅色 | 系统设置 | 跟随系统 | 影响 baseColor 和所有 chromeAlpha |

### 1.3 核心问题总结

1. **层级过多**：L0-L4 四层叠加做"同一件事"（让窗口看起来半透明），导致：
   - 颜色计算复杂（`BuildFrostedSurfaceColor` + `BuildNativeBackdropTintColor` + `BuildAccentSurfaceColor` 三层混色函数）
   - 调整任何一个参数都需要同步修改多层，维护成本高
   - 透明度联动逻辑写死，用户无法独立控制"背板模糊"与"表面色调"

2. **缺少材质选择**：用户只能调透明度，无法选择 Mica / Acrylic / 纯色 / 全透明 等不同材质

3. **边框/阴影不可控**：DWM 边框色硬编码为无边框；XAML 边框 alpha 硬编码为 0x18；完全没有窗口级阴影

4. **Mica 未被使用**：Settings 和 Onboarding 窗口用了 `MicaBackdrop { Kind = BaseAlt }`，但 Widget 窗口没用

---

## 二、原生材质能力清单（微软文档 + API）

### 2.1 系统级背板材质（System Backdrop）

这些是 Windows 11 原生提供的窗口级材质，由 DWM 合成器渲染，性能最优：

| 材质 | API（XAML 简写） | API（Controller 精细控制） | DWM Win32 等价 | 特点 | 适用场景 |
|------|-------------------|---------------------------|-----------------|------|----------|
| **Mica** | `MicaBackdrop { Kind = Base }` | `MicaController` | `DWMSBT_MAINWINDOW` | 不透明，采样壁纸一次，跟随窗口移动微妙变化，失活时退化为纯色 | 长驻窗口的基底 |
| **Mica Alt** | `MicaBackdrop { Kind = BaseAlt }` | `MicaController` | `DWMSBT_MAINWINDOW`（+ 系统设置） | 比 Mica 着色更强，层次感更深 | 带标签栏的窗口 |
| **Desktop Acrylic** | `DesktopAcrylicBackdrop` | `DesktopAcrylicController { Kind = Thin/Base }` | `DWMSBT_TRANSIENTWINDOW` | 半透明毛玻璃，实时模糊窗口后方内容 | 瞬态/轻量级表面 |
| **Acrylic (Win32 Accent)** | — | `SetWindowCompositionAttribute(ACCENT_ENABLE_ACRYLICBLURBEHIND)` | 无 DWM 等价 | 最老的 Win32 模糊 API，兼容性最好但效果最粗糙 | Fallback |
| **Blur Behind (Win32)** | — | `ACCENT_ENABLE_BLURBEHIND` | 无 | 纯模糊无着色 | 透明窗口 |
| **纯色 (Solid)** | `SolidColorBrush` | — | `DWMSBT_NONE` + Accent `ACCENT_ENABLE_GRADIENT` | 无模糊，纯色背景 | 性能优先/高对比 |
| **全透明** | `Background="Transparent"` + 窗口扩展 | — | `DWMSBT_NONE` + Accent `ACCENT_DISABLED` | 完全透明，无任何背板 | 特殊视觉效果 |

**关键发现：当前代码只用了 Acrylic (Controller + Accent fallback)，完全没有用 Mica。**

#### Controller 精细控制参数

```csharp
// MicaController
micaController.TintColor = color;        // 着色色
micaController.TintOpacity = 0.0f;       // 着色不透明度 0-1
micaController.FallbackColor = color;    // 硬件不支持时的退化色

// DesktopAcrylicController
acrylicController.TintColor = color;         // 着色色
acrylicController.TintOpacity = 0.0f;        // 着色不透明度
acrylicController.LuminosityOpacity = 0.0f;  // 亮度层不透明度（控制"磨砂"感）
acrylicController.FallbackColor = color;     // 退化色
acrylicController.Kind = DesktopAcrylicKind.Thin | Base;  // Thin 更薄更通透
```

### 2.2 XAML 级表面材质（In-app Acrylic / Backdrop）

除了窗口级材质，WinUI 还提供元素级材质：

| 材质 | API | 说明 |
|------|-----|------|
| **In-app Acrylic** | `AcrylicBrush` (XAML Resource) | 应用于任意 XAML 元素的亚克力材质，`ThemeResource` 预设：`SystemControlAcrylicElementBrush`、`SystemControlTransientAcrylicElementBrush` 等 |
| **HostBackdrop Acrylic** | `AcrylicBrush { AlwaysUseFallback = false }` | 模糊窗口后方内容（与 Desktop Acrylic 类似但更轻量） |
| **系统主题色叠加** | `SolidColorBrush` + `ThemeResource AccentFillColorDefaultBrush` | 跟随系统主题色的半透明叠加 |

### 2.3 阴影 API

| API | 类型 | 控制粒度 | 当前使用 |
|-----|------|----------|----------|
| **ThemeShadow** | XAML (`Microsoft.UI.Xaml.Media.ThemeShadow`) | z-depth 自动计算阴影大小/柔度，保持跨应用一致性 | ❌ 未使用 |
| **DropShadow** | Composition (`Microsoft.UI.Composition.DropShadow`) | 完全自定义：半径、颜色、偏移、遮罩 | ❌ 未使用（仅 MusicWidget 的 AlbumArtShadow 是 Border 命名，实际不是阴影 API） |
| **DWM 窗口阴影** | Win32（自动） | 窗口级系统阴影，由 DWM 自动管理 | ✅ 隐式启用（但被 L5 边框色=无边框可能影响） |

### 2.4 DWM 边框控制

| DWM 属性 | 当前代码 | 可控制内容 |
|-----------|----------|------------|
| `DWMWA_BORDER_COLOR` (34) | 硬编码 `0xFFFFFFFE`（无边框） | 可设为任意 COLORREF 控制系统边框色 |
| `DWMWA_WINDOW_CORNER_PREFERENCE` (33) | ✅ 用户可选 | Default/DoNotRound/Round/RoundSmall |
| `DWMWA_USE_IMMERSIVE_DARK_MODE` (20) | ✅ 跟随主题 | 深色/浅色模式 |
| `DWMWA_SYSTEMBACKDROP_TYPE` (38) | ✅ 动态设置 | Auto/None/MainWindow(Mica)/TransientWindow(Acrylic)/TabbedWindow(Mica Alt) |
| `DWMWA_CAPTION_COLOR` (35) | ❌ 未使用 | 标题栏文字颜色 |
| `DWMWA_TEXT_COLOR` (36) | ❌ 未使用 | 标题栏文字颜色 |

### 2.5 当前未使用但可用的原生能力

| 能力 | API | 价值 |
|------|-----|------|
| **MicaController** | `Microsoft.UI.Composition.SystemBackdrops.MicaController` | 比 Acrylic 更省性能（只采样一次壁纸），更适合长驻 Widget |
| **MicaBackdrop (XAML)** | `Window.SystemBackdrop = new MicaBackdrop()` | 最简 API，一行代码启用 |
| **ThemeShadow** | `ThemeShadow` + `Translation` | 给 Widget 内部元素（如格子卡片）添加原生深度阴影 |
| **DropShadow (Composition)** | `Compositor.CreateDropShadow()` | 给 BackgroundPlate 添加可自定义的窗口级阴影 |
| **DWMWA_CAPTION_COLOR / TEXT_COLOR** | `DwmSetWindowAttribute` | 自定义标题栏颜色 |
| **DWMWA_BORDER_COLOR** (自定义值) | `DwmSetWindowAttribute` | 当前硬编码为无边框，可改为用户自定义颜色/透明度 |
| **In-app Acrylic (ThemeResource)** | `SystemControlAcrylicElementBrush` 等 | 给格子内部 item 使用系统预设亚克力，替代手动计算颜色 |
| **SystemBackdrop 应用到任意 XAML 元素** | ` Microsoft.UI.Composition.SystemBackdrops` + `DesktopAcrylicController.AddSystemBackdropTarget(visual)` | 不仅限于 Window，可给任意 Visual 施加系统背板 |

---

## 三、方案梳理

### 方案 A：材质类型选择器（新增 `WidgetMaterialType` 设置）

**目标**：让用户在设置中选择格子窗口的基底材质类型。

```
设置 > 外观 > 材质类型
├── 自动（推荐）     → 跟随系统性能，优先 Mica，fallback Acrylic
├── 云母 (Mica)      → MicaController，不透明，采样壁纸
├── 亚克力 (Acrylic) → DesktopAcrylicController (Thin)，半透明毛玻璃（当前行为）
├── 纯色             → DWMSBT_NONE + SolidBrush，无模糊
└── 全透明           → DWMSBT_NONE + Accent DISABLED，完全透明
```

**实现方式**：

```csharp
// 新增枚举
public enum WidgetMaterialType { Auto, Mica, Acrylic, Solid, Transparent }

// SettingsService.cs 新增字段
public WidgetMaterialType WidgetMaterialType { get; set; } = WidgetMaterialType.Acrylic;

// WidgetWindow.xaml.cs ApplyBackdropPreference() 重构
private void ApplyBackdropPreference()
{
    bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
    double surfaceOpacity = Math.Clamp(ViewModel.WidgetOpacity, 0.0, 1.0);
    
    switch (_settingsService.Settings.WidgetMaterialType)
    {
        case WidgetMaterialType.Mica:
            ApplyMicaController(isDark, surfaceOpacity);
            break;
        case WidgetMaterialType.Acrylic:
            ApplyAcrylicController(isDark, tintColor, surfaceOpacity); // 当前逻辑
            break;
        case WidgetMaterialType.Solid:
            ApplySolidBackdrop(isDark, surfaceOpacity);
            break;
        case WidgetMaterialType.Transparent:
            ApplyTransparentBackdrop();
            break;
        case WidgetMaterialType.Auto:
            // 优先 Mica，不支持则 fallback Acrylic
            if (!ApplyMicaController(isDark, surfaceOpacity))
                ApplyAcrylicController(isDark, tintColor, surfaceOpacity);
            break;
    }
    
    ApplySurfaceStyle();
}

// 新增 Mica 路径
private MicaController? _micaController;

private bool ApplyMicaController(bool isDark, double surfaceOpacity)
{
    if (!MicaController.IsSupported()) return false;
    
    _backdropTarget ??= this.As<ICompositionSupportsSystemBackdrop>();
    _backdropConfiguration ??= new SystemBackdropConfiguration { IsInputActive = true };
    _backdropConfiguration.Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
    
    _micaController ??= new MicaController();
    _micaController.Kind = MicaKind.Base;  // 或 BaseAlt
    _micaController.TintColor = BuildNativeBackdropTintColor(isDark);
    _micaController.TintOpacity = (float)Math.Clamp(surfaceOpacity * 0.5, 0.0, 1.0);
    _micaController.FallbackColor = isDark ? ColorHelper.FromArgb(0xFF, 0x20, 0x22, 0x26) : Colors.White;
    
    if (!_micaController.AddSystemBackdropTarget(_backdropTarget))
    {
        _micaController.Dispose();
        _micaController = null;
        return false;
    }
    _micaController.SetSystemBackdropConfiguration(_backdropConfiguration);
    
    // 同时设 DWM 背板为 Mica
    int backdropType = Win32Helper.DWMSBT_MAINWINDOW;
    Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
    Win32Helper.DisableAccentPolicy(_hWnd);
    
    DisposeAcrylicController();
    return true;
}
```

**可行性评估**：
- ✅ 原生 API，无需第三方库
- ✅ `MicaController.IsSupported()` 已在 WinAppSDK 中提供
- ✅ 代码结构清晰：现有 `ApplyAcrylicController` 模式可直接复用
- ⚠️ Mica 是不透明的，对于"全透明"需求无法满足，需要作为独立选项
- ⚠️ Mica 采样壁纸而非实时模糊，Widget 窗口移动时背景不会实时变化（但 Mica 本身会微妙变化）
- ✅ 性能更好：Mica 只采样一次壁纸，比 Acrylic 实时模糊更省 GPU

**用户体验影响**：
- Mica 模式下 `WidgetOpacity` 滑块仍然有效（控制 TintOpacity），但视觉上不是"透明度"而是"着色浓度"
- 纯色模式下 `WidgetOpacity` 控制 SolidBrush 的 Alpha
- 全透明模式下 `WidgetOpacity` 无效（或控制内部元素的透明度）

---

### 方案 B：边框自定义控制

**目标**：让用户可以控制格子窗口的边框开关和强度。

```
设置 > 外观 > 边框
├── 边框样式：无 / 细 / 中 / 粗
├── 边框颜色：自动（跟随主题） / 自定义颜色
└── （可选）边框透明度：Slider 0-100%
```

**实现方式**：

涉及两个层次的边框需要同步控制：

```csharp
// 新增设置
public enum WidgetBorderStyle { None, Thin, Medium, Thick }
public WidgetBorderStyle WidgetBorderStyle { get; set; } = WidgetBorderStyle.Thin;

// 在 ApplySurfaceStyle() 中
private void ApplySurfaceStyle()
{
    // ... 现有代码 ...
    
    // XAML 层边框（BackgroundPlate）
    var (borderThickness, borderAlpha) = _settingsService.Settings.WidgetBorderStyle switch
    {
        WidgetBorderStyle.None    => (0d, (byte)0),
        WidgetBorderStyle.Thin    => (0.8d, (byte)0x18),
        WidgetBorderStyle.Medium  => (1.2d, (byte)0x30),
        WidgetBorderStyle.Thick   => (1.6d, (byte)0x48),
        _ => (0.8d, (byte)0x18),
    };
    
    BackgroundPlate.BorderThickness = new Thickness(borderThickness);
    BackgroundPlate.BorderBrush = new SolidColorBrush(isDark
        ? ColorHelper.FromArgb(borderAlpha, 0xFF, 0xFF, 0xFF)
        : ColorHelper.FromArgb(borderAlpha, 0x00, 0x00, 0x00));
    
    // DWM 层边框色
    int dwmBorderColor = _settingsService.Settings.WidgetBorderStyle == WidgetBorderStyle.None
        ? unchecked((int)0xFFFFFFFE)  // 无边框
        : isDark
            ? (int)0xFF202020          // 深色边框
            : (int)0xFFE0E0E0;         // 浅色边框
    Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_BORDER_COLOR, ref dwmBorderColor, sizeof(int));
}
```

**可行性评估**：
- ✅ `DWMWA_BORDER_COLOR` 已在代码中定义（`Win32Helper.cs:546`），只是值被硬编码为无边框
- ✅ XAML 边框已在 `ApplySurfaceStyle()` 中动态设置
- ✅ 两个层次可以独立或联动控制
- ⚠️ DWM 边框色不支持 Alpha 透明度（COLORREF 是 0xRRGGBB 格式，无 Alpha 通道），所以 DWM 层只能控制颜色不能控制透明度
- ✅ XAML 层边框可以完全自定义 Alpha

---

### 方案 C：阴影控制

**目标**：给格子窗口添加可控的阴影效果。

有两个可选路径：

#### C1: ThemeShadow（原生推荐）

```xml
<!-- WidgetShell.xaml -->
<Border x:Name="BackgroundPlate" ...>
    <Border.Shadow>
        <ThemeShadow x:Name="BackgroundPlateShadow" />
    </Border.Shadow>
</Border>
```

```csharp
// 在代码中设置 z-depth
// 需要一个接收阴影的平面
// WinUI 3 中 ThemeShadow 需要手动设置 ShadowReceiver
// 这是 ThemeShadow 在 WinUI 3 中的限制：需要显式指定接收面
```

**问题**：ThemeShadow 在 WinUI 3 中需要 `ShadowReceiver`，且对于窗口级 Border（没有其他元素在下方接收阴影）来说不太适用。ThemeShadow 更适合 Flyout/Popup 等有明确 z-index 层级的场景。

#### C2: Composition DropShadow（更灵活）

```csharp
// 在 WidgetWindow 或 WidgetShell 中
private void ApplyShadowEffect(double intensity)
{
    if (intensity <= 0)
    {
        // 移除阴影
        ElementCompositionPreview.GetElementVisual(BackgroundPlate).Shadow = null;
        return;
    }
    
    var compositor = ElementCompositionPreview.GetElementVisual(BackgroundPlate).Compositor;
    var shadow = compositor.CreateDropShadow();
    shadow.BlurRadius = (float)(8 * intensity);      // 0-24
    shadow.Opacity = (float)(0.3 * intensity);        // 0-1
    shadow.Offset = new Vector3(0, (float)(2 * intensity), 0);
    shadow.Color = isDark ? Colors.Black : Windows.UI.Color.FromArgb(0x80, 0x00, 0x00, 0x00);
    shadow.Mask = BackgroundPlate.GetAlphaMask();     // 圆角遮罩
    
    var elementVisual = ElementCompositionPreview.GetElementVisual(BackgroundPlate);
    elementVisual.Shadow = shadow;
}
```

**问题**：
- ⚠️ `DropShadow` 的 `Mask` 需要 `GetAlphaMask()`，而 `BackgroundPlate` 是一个纯色 Border，AlphaMask 就是它的形状（含圆角），这是可行的
- ⚠️ 但 Composition Shadow 只能在 XAML 元素之间投射，**不会投射到窗口外部**（因为窗口是顶层窗口，没有东西在"下面"接收阴影）
- ❌ **关键限制**：Widget 窗口是顶层窗口，Composition DropShadow 只能在窗口内的元素之间投射，无法产生"窗口外的阴影"

#### C3: DWM 系统阴影（已有，可强化）

Windows 11 的顶层窗口默认由 DWM 管理系统阴影（窗口投影到桌面）。当前代码通过 `DWMWA_BORDER_COLOR = 0xFFFFFFFE` 设置为无边框，但这**不影响系统阴影**。

系统阴影由 `DWMWA_NCRENDERING_POLICY` 和窗口是否激活控制。当前代码已通过 `ApplyFullWindowFrame()` 扩展了 DWM Frame，系统阴影应该隐式存在。

**真正的可行方案**：

实际上对于顶层窗口，阴影就是 DWM 系统阴影，已经是原生的。问题在于当前 Widget 窗口使用了 `WS_EX_TOOLWINDOW` + 自定义无边框，可能某些情况下系统阴影不明显。可以：

1. 确保 `DWMWA_NCRENDERING_POLICY` 设为 `DWMNCRP_ENABLED`（让 DWM 渲染 NC 区域包括阴影）
2. 或者通过 `DwmExtendFrameIntoClientArea` 的 margin 来影响阴影表现

```csharp
// 方案：根据用户"阴影强度"调整 DWM Frame 扩展
public static void ApplyShadowIntensity(IntPtr hWnd, double intensity)
{
    // intensity 0 = 无阴影, 1 = 正常阴影
    if (intensity <= 0)
    {
        // 通过禁用 NC 渲染来移除阴影
        int policy = DWMNCRP_DISABLED;
        DwmSetWindowAttribute(hWnd, DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int));
    }
    else
    {
        int policy = DWMNCRP_ENABLED;
        DwmSetWindowAttribute(hWnd, DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int));
        // Frame 扩展量可以微调阴影
    }
}
```

**可行性评估**：
- ✅ DWM 系统阴影是原生能力
- ✅ 可以通过 `DWMWA_NCRENDERING_POLICY` 开关
- ⚠️ 无法精细控制阴影的模糊半径/颜色/偏移（DWM 系统阴影是固定样式）
- ⚠️ 当前窗口已经是自定义无边框 + 全 Frame 扩展，系统阴影行为可能已受影响，需要实测

**建议**：阴影控制提供"开/关"两档即可（不提供强度滑块），通过 DWM NC 渲染策略控制。

---

### 方案 D：简化层叠结构

**目标**：减少 L0-L4 的层级冗余，让材质控制更直接。

当前问题：Acrylic Controller（L2）已经提供了 TintColor + TintOpacity + LuminosityOpacity 三参数控制，但代码又在上面叠了 L3 的 `BuildFrostedSurfaceColor` 半透明色层。这导致：
- 用户调透明度时，两个层同时变化，效果难以预测
- 颜色计算经过 `BuildAccentSurfaceColor` → `BuildFrostedSurfaceColor` → `ApplySurfaceOpacity` 三层函数

**优化方向**：

```
当前：AcrylicController(TintColor, TintOpacity) 
    + BackgroundPlate.Background = FrostedSurfaceColor(materialOpacity)

优化：AcrylicController(TintColor, TintOpacity, LuminosityOpacity)  ← 唯一的"材质"层
    + BackgroundPlate.Background = null  ← 移除叠加色层（或仅在纯色模式下使用）
```

也就是说，当材质类型为 Acrylic/Mica 时，`BackgroundPlate` 应该完全透明，让系统背板材质透出来；当材质类型为 Solid 时，`BackgroundPlate` 才填充纯色。

```csharp
private void ApplySurfaceStyle()
{
    bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
    double surfaceOpacity = Math.Clamp(ViewModel.WidgetOpacity, 0.0, 1.0);
    var materialType = _settingsService.Settings.WidgetMaterialType;
    
    // 材质模式下，BackgroundPlate 不叠色
    if (materialType is WidgetMaterialType.Acrylic or WidgetMaterialType.Mica or WidgetMaterialType.Transparent)
    {
        BackgroundPlate.Background = null;  // 或 new SolidColorBrush(Colors.Transparent)
    }
    else if (materialType is WidgetMaterialType.Solid)
    {
        var solidColor = BuildFrostedSurfaceColor(isDark, accentColor, surfaceOpacity);
        BackgroundPlate.Background = new SolidColorBrush(solidColor);
    }
    
    // 边框、分隔线等仍正常设置
    // ...
}
```

**可行性评估**：
- ✅ 减少层级，颜色计算更直观
- ✅ 用户调透明度时只影响 AcrylicController 参数，效果可预测
- ⚠️ 需要重新调参：移除 L3 后，Acrylic 的 TintOpacity 需要补偿之前 L3 提供的遮盖力
- ⚠️ 可能导致内容区文字对比度变化（L3 之前提供了额外的底色），需要确保文字可读性

---

### 方案 E：格子内 item 卡片使用原生 ThemeResource

**目标**：格子内部的文件卡片（WidgetShell.xaml:150-152）当前使用 `{ThemeResource CardBackgroundFillColorDefaultBrush}` + `{ThemeResource CardStrokeColorDefaultBrush}`，这是正确的原生做法。但可以考虑提供更多原生预设。

当前：
```xml
<Border Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"
        BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}"
        BorderThickness="0.8" />
```

可选增强：
```xml
<!-- 预设1：更通透（亚克力卡片） -->
<Border Background="{ThemeResource SystemControlAcrylicElementBrush}"
        BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}"
        BorderThickness="0.8" />

<!-- 预设2：跟随主题色（Accent 半透明） -->
<Border Background="{ThemeResource AccentFillColorDefaultBrush}"
        BorderBrush="{ThemeResource AccentControlElevationBorderBrush}"
        BorderThickness="1" />

<!-- 预设3：无背景（仅悬停时显示） -->
<Border Background="Transparent"
        BorderBrush="Transparent"
        BorderThickness="0" />
```

**可行性评估**：
- ✅ 完全使用 `ThemeResource`，原生且跟随系统主题
- ✅ 不需要手动计算颜色
- ⚠️ `SystemControlAcrylicElementBrush` 是元素级亚克力（In-app Acrylic），不是窗口级，性能开销比纯色大
- ⚠️ 需要考虑与窗口背板材质的视觉协调

---

## 四、整合方案推荐

### 4.1 推荐实施优先级

| 优先级 | 方案 | 工作量 | 风险 | 价值 |
|--------|------|--------|------|------|
| P0 | **方案 A：材质类型选择器** | 中 | 低 | 高 — 给用户选择 Mica/Acrylic/纯色/透明的权力 |
| P0 | **方案 D：简化层叠结构** | 中 | 中 | 高 — 降低维护复杂度，提升调参直觉性 |
| P1 | **方案 B：边框自定义** | 低 | 低 | 中 — 当前边框硬编码，开放给用户 |
| P2 | **方案 C3：DWM 阴影开关** | 低 | 低 | 中 — 简单开/关即可 |
| P3 | **方案 E：item 卡片预设** | 低 | 低 | 低 — 当前已经用 ThemeResource，锦上添花 |

### 4.2 新增设置项一览

```
设置 > 外观
├── 材质类型          [ComboBox] 自动/云母/亚克力/纯色/全透明
├── 不透明度          [Slider]   0-100% （语义随材质类型变化）
├── 边框              [ComboBox] 无/细/中/粗
├── 窗口阴影          [Toggle]   开/关
├── 圆角              [ComboBox] Default/Square/Small/Round  ← 已有
├── 标题图标          [ComboBox] ← 已有
└── ...其他已有设置
```

### 4.3 兼容性保证

- ✅ 所有现有设置（`WidgetOpacity`、`WidgetCornerPreference`、`WidgetTitleIconMode`、主题色、深色/浅色）保持不变
- ✅ `WidgetOpacity` 的语义扩展：在 Acrylic/Mica 模式下控制 TintOpacity，在 Solid 模式下控制 Alpha，在 Transparent 模式下无效
- ✅ 默认材质类型设为 `Acrylic`（与当前行为一致），用户不主动切换则无感知变化
- ✅ `MicaController.IsSupported()` / `DesktopAcrylicController.IsSupported()` 做了 fallback，低版本 Windows 自动退化为 Win32 Accent

### 4.4 文件改动范围

| 文件 | 改动内容 |
|------|----------|
| `Services/SettingsService.cs` | 新增 `WidgetMaterialType`、`WidgetBorderStyle`、`WidgetShadowEnabled` 字段 |
| `Models/AppSettings.cs`（或等效） | 新增设置属性 |
| `ViewModels/SettingsViewModel.cs` | 新增绑定属性 |
| `Views/SettingsWindow.xaml` | 新增 3 个设置控件 |
| `Views/WidgetWindow.xaml.cs` | 重构 `ApplyBackdropPreference()`，新增 `ApplyMicaController()` |
| `Views/QuickCaptureWidgetWindow.xaml.cs` | 同步重构（同模式） |
| `Views/ContentWidgetWindow.xaml.cs` | 同步重构（同模式） |
| `Helpers/Win32Helper.cs` | 新增 `DWMWA_NCRENDERING_POLICY` 常量、阴影控制方法 |
| `Controls/WidgetShell.xaml` | BackgroundPlate 边框动态化 |
| `Strings/en-US/` + `Strings/zh-CN/` | 新增本地化字符串 |

---

## 五、关键风险与注意事项

### 5.1 Mica 与 Widget 的兼容性

- Mica 是为**长驻窗口**设计的，采样壁纸一次。Widget 窗口通常固定在桌面位置，Mica 的壁纸采样会基于窗口当前位置。
- 如果用户有多个显示器且壁纸不同，Mica 会在窗口移动时重新采样。
- **Mica 不透明**：用户无法通过 Mica 看到"窗口后面的东西"（不同于 Acrylic 的实时模糊）。这对 Widget 来说可能不是问题（Widget 通常不需要看到后面），但如果用户期望"透过格子看到桌面图标"，则 Mica 不合适。

### 5.2 性能对比

| 材质 | GPU 开销 | 内存开销 | 备注 |
|------|----------|----------|------|
| Mica | 最低（采样一次） | 最低 | 推荐 long-running 窗口 |
| Desktop Acrylic | 中（实时模糊） | 中 | 当前方案 |
| Win32 Accent Acrylic | 中 | 低 | Fallback 路径 |
| 纯色 | 最低 | 最低 | 无模糊 |
| 全透明 | 低 | 低 | 需注意文字可读性 |

### 5.3 简化层叠的风险

移除 L3（BackgroundPlate 叠色层）后：
- 内容区文字对比度可能下降（之前 L3 提供了额外的底色）
- 需要 Acrylic 的 `LuminosityOpacity` 参数补偿
- 建议在实施时做 A/B 对比测试，确保可读性不退化

### 5.4 DWM 边框色的限制

- `DWMWA_BORDER_COLOR` 接受 `COLORREF`（0x00BBGGRR），**无 Alpha 通道**
- 特殊值 `0xFFFFFFFE` = 无边框，`0xFFFFFFFF` = 自动
- 要实现"半透明边框"只能靠 XAML 层 Border，DWM 层做不到
- 建议：XAML 层控制边框视觉（有 Alpha），DWM 层只做"有无边框"的二选一

### 5.5 多窗口同步

当前有 3 种 Widget 窗口（`WidgetWindow`、`QuickCaptureWidgetWindow`、`ContentWidgetWindow`），它们各自有 `ApplyBackdropPreference()` 实现。任何材质改动都需要**同步到所有三个窗口**，否则会出现行为不一致。

---

## 六、总结

当前代码的材质实现已经相当完善（Acrylic Controller + Win32 fallback + 自定义混色），但存在两个主要改进空间：

1. **缺少材质选择**：用户只能调透明度，无法选择 Mica / Acrylic / 纯色 / 全透明。微软原生提供了 `MicaController`、`DesktopAcrylicController`、DWM `SYSTEMBACKDROP_TYPE` 等多种材质，完全可以开放给用户。

2. **层叠冗余**：L2 Acrylic Controller + L3 BackgroundPlate 叠色，两层做同一件事。可以简化为：材质模式下 BackgroundPlate 透明、纯色模式下 BackgroundPlate 填充。

3. **边框/阴影硬编码**：DWM 边框色写死为无边框，XAML 边框 alpha 写死为 0x18，阴影完全未控制。这些都是可以开放给用户的低成本改动。

所有方案均使用原生 API（`MicaController`、`DesktopAcrylicController`、`DwmSetWindowAttribute`、`ThemeShadow`/`DropShadow`），不引入任何第三方库，符合项目"尽可能使用原生组件"的原则。
